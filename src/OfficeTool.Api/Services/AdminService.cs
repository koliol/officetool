using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Services;

/// <summary>
/// 权限管理（用户 / 组 / 授权条目）的业务逻辑。
///
/// 三条贯穿全局的约束，每条都对应一类真实事故：
/// <list type="number">
///   <item>
///     <term>管理动作不由 ACL 授权</term>
///     只看 <see cref="User.IsSystemAdmin"/>。若把「谁能改权限」也做成 ACL，
///     就会陷入「管理权限也要被管理」的循环，且配错时无法自救。
///   </item>
///   <item>
///     <term>写操作必须失效权限缓存</term>
///     否则管理员改完当场看不到效果，用户要等缓存过期才生效。
///   </item>
///   <item>
///     <term>不允许把最后一个超管锁死</term>
///     禁用/降级/删除自己且系统里没有其他可用超管时一律拒绝。
///     这是权限系统唯一无法从界面内部修复的故障，必须在写入前拦住。
///   </item>
/// </list>
/// </summary>
public sealed class AdminService(
    AppDbContext db,
    AuthOptions options,
    RequestContext request,
    IAccessControlService access,
    OperationLogService logs,
    ILogger<AdminService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly AuthOptions _options = options;
    private readonly RequestContext _request = request;
    private readonly IAccessControlService _access = access;
    private readonly OperationLogService _logs = logs;
    private readonly ILogger<AdminService> _logger = logger;

    // ══════════════════════════════════════════════════════════════
    // 用户
    // ══════════════════════════════════════════════════════════════

    public async Task<PagedResult<UserDto>> ListUsersAsync(
        string? keyword, string? source, bool? enabled, int page, int pageSize, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 500 ? 50 : pageSize;

        IQueryable<User> q = _db.Users.AsNoTracking();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var k = keyword.Trim();
            q = q.Where(x => x.UserName.Contains(k) || x.DisplayName.Contains(k));
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            Enum.TryParse<UserSource>(source, true, out var parsed) &&
            Enum.IsDefined(parsed))
        {
            q = q.Where(x => x.Source == parsed);
        }

        if (enabled is { } flag)
        {
            q = q.Where(x => x.IsEnabled == flag);
        }

        var total = await q.CountAsync(ct);

        var items = await q
            .OrderBy(x => x.UserName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<UserDto>(await WithGroupsAsync(items, ct), total, page, pageSize);
    }

    public async Task<UserDto> GetUserAsync(int id, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
                   ?? throw new NotFoundException($"用户不存在：{id}");

        var groupNames = await LoadGroupsAsync([user.Id], ct);

        return ToDto(user, groupNames.GetValueOrDefault(user.Id) ?? []);
    }

    /// <summary>
    /// 创建本地账户，返回明文初始密码（唯一一次）。
    ///
    /// 域账户<strong>不需要也不能</strong>在这里创建：它们由首次 SSO 登录自动建档，
    /// 密码在域控制器上。在这里预先建一个同名 User 反而会在同步时造成歧义。
    /// </summary>
    public async Task<CreateUserResponse> CreateUserAsync(CreateUserRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var userName = ValidateUserName(dto.UserName);
        var displayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? userName : dto.DisplayName.Trim();

        if (await _db.Users.AnyAsync(x => x.UserName == userName, ct))
        {
            throw new ConflictException($"登录名已存在：{userName}");
        }

        var password = UserDirectoryService.GeneratePassword();

        var user = new User
        {
            UserName = userName,
            DisplayName = displayName,
            Source = UserSource.Local,
            PasswordHash = PasswordHasher.Hash(password),
            IsEnabled = true,
            IsSystemAdmin = dto.IsSystemAdmin,
            MustChangePassword = dto.MustChangePassword,
            CreatedAt = DateTime.Now,
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(
            "新建本地账户",
            dto.IsSystemAdmin ? $"{userName}（系统管理员）" : userName,
            success: true,
            ct: ct);

        _logger.LogInformation(
            "管理员 {Admin} 创建了本地账户 {UserName}{AdminFlag}。",
            me.UserName, userName, dto.IsSystemAdmin ? "（系统管理员）" : string.Empty);

        return new CreateUserResponse { User = ToDto(user, []), Password = password };
    }

    public async Task<UserDto> UpdateUserAsync(int id, UpdateUserRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var user = await _db.Users.Include(x => x.UserGroups).FirstOrDefaultAsync(x => x.Id == id, ct)
                   ?? throw new NotFoundException($"用户不存在：{id}");

        if (dto.DisplayName is { } name && !string.IsNullOrWhiteSpace(name))
        {
            user.DisplayName = name.Trim();
        }

        if (dto.IsEnabled == false)
        {
            await RefuseIfSelfLockoutAsync(me, id, "禁用自己的账户", ct);
            user.IsEnabled = false;
        }
        else if (dto.IsEnabled == true)
        {
            user.IsEnabled = true;
        }

        if (dto.IsSystemAdmin == false)
        {
            await RefuseIfSelfLockoutAsync(me, id, "取消自己的系统管理员身份", ct);
            user.IsSystemAdmin = false;
        }
        else if (dto.IsSystemAdmin == true)
        {
            user.IsSystemAdmin = true;
        }

        if (dto.MustChangePassword is { } flag)
        {
            user.MustChangePassword = flag;
        }

        await _db.SaveChangesAsync(ct);

        // 禁用 / 改超管位都会改变可见集合与判定结果，缓存必须立刻失效
        await _access.InvalidateAsync([user.UserName], ct);

        await _logs.WriteAsync("修改用户", user.UserName, success: true, ct: ct);

        var groupNames = await LoadGroupsAsync([user.Id], ct);
        return ToDto(user, groupNames.GetValueOrDefault(user.Id) ?? []);
    }

    /// <summary>
    /// 重置本地账户密码。<see cref="ResetPasswordResponse.Password"/> 只返回这一次。
    ///
    /// 强制 <c>MustChangePassword</c>：密码要由管理员转述给本人，中途一定被第三方看到过。
    /// </summary>
    public async Task<ResetPasswordResponse> ResetPasswordAsync(int id, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
                   ?? throw new NotFoundException($"用户不存在：{id}");

        if (user.Source != UserSource.Local)
        {
            throw new BadRequestException(
                $"{user.UserName} 是域账户，密码请在域控制器上重置。");
        }

        var password = UserDirectoryService.GeneratePassword();
        user.PasswordHash = PasswordHasher.Hash(password);
        user.MustChangePassword = true;

        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("管理员 {Admin} 重置了 {UserName} 的密码。", me.UserName, user.UserName);
        await _logs.WriteAsync("重置密码", user.UserName, success: true, ct: ct);

        return new ResetPasswordResponse { Password = password };
    }

    public async Task DeleteUserAsync(int id, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var user = await _db.Users.FirstOrDefaultAsync(x => x.Id == id, ct)
                   ?? throw new NotFoundException($"用户不存在：{id}");

        await RefuseIfSelfLockoutAsync(me, id, "删除自己的账户", ct);

        if (user.Source == UserSource.Ad)
        {
            // 域用户删掉后下次 SSO 会重新建档，等于什么都没做还丢了历史；
            // 真正想阻止一个域账户登录，应该是禁用它，而不是删除。
            throw new ConflictException(
                $"{user.UserName} 是域账户，删除后其下次登录会自动重建。" +
                "如需阻止其登录请改为「禁用」。");
        }

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);

        await _access.InvalidateAsync([user.UserName], ct);

        _logger.LogInformation("管理员 {Admin} 删除了本地账户 {UserName}。", me.UserName, user.UserName);
        await _logs.WriteAsync("删除用户", user.UserName, success: true, ct: ct);
    }

    // ══════════════════════════════════════════════════════════════
    // 组
    // ══════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var groups = await _db.Groups.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

        var counts = await _db.UserGroups
            .AsNoTracking()
            .GroupBy(x => x.GroupId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return [.. groups.Select(g => new GroupDto
        {
            Id = g.Id,
            Name = g.Name,
            DisplayName = g.DisplayName,
            Source = g.Source.ToString(),
            IsEnabled = g.IsEnabled,
            CreatedAt = g.CreatedAt,
            MemberCount = counts.GetValueOrDefault(g.Id),
        })];
    }

    public async Task<GroupDetailDto> GetGroupAsync(int id, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        var members = await (
            from ug in _db.UserGroups.AsNoTracking()
            join u in _db.Users.AsNoTracking() on ug.UserId equals u.Id
            where ug.GroupId == id
            orderby u.UserName
            select u).ToListAsync(ct);

        return new GroupDetailDto
        {
            Id = group.Id,
            Name = group.Name,
            DisplayName = group.DisplayName,
            Source = group.Source.ToString(),
            IsEnabled = group.IsEnabled,
            CreatedAt = group.CreatedAt,
            Members = [.. members.Select(m => new UserBriefDto
            {
                Id = m.Id,
                UserName = m.UserName,
                DisplayName = m.DisplayName,
                Source = m.Source.ToString(),
            })],
        };
    }

    /// <summary>创建<strong>本地</strong>组。域组由 LDAP 同步自动产生。</summary>
    public async Task<GroupDto> CreateGroupAsync(CreateGroupRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var name = ValidateGroupName(dto.Name);
        var displayName = string.IsNullOrWhiteSpace(dto.DisplayName) ? name : dto.DisplayName.Trim();

        if (await _db.Groups.AnyAsync(x => x.Name == name, ct))
        {
            throw new ConflictException($"组名已存在：{name}");
        }

        var group = new Group
        {
            Name = name,
            DisplayName = displayName,
            Source = UserSource.Local,
            IsEnabled = true,
            CreatedAt = DateTime.Now,
        };

        _db.Groups.Add(group);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("管理员 {Admin} 创建了本地组 {GroupName}。", me.UserName, name);
        await _logs.WriteAsync("新建组", name, success: true, ct: ct);

        return new GroupDto
        {
            Id = group.Id,
            Name = group.Name,
            DisplayName = group.DisplayName,
            Source = group.Source.ToString(),
            IsEnabled = group.IsEnabled,
            CreatedAt = group.CreatedAt,
        };
    }

    public async Task<GroupDto> UpdateGroupAsync(int id, UpdateGroupRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        if (dto.DisplayName is { } name && !string.IsNullOrWhiteSpace(name))
        {
            group.DisplayName = name.Trim();
        }

        if (dto.IsEnabled is { } flag)
        {
            group.IsEnabled = flag;
        }

        await _db.SaveChangesAsync(ct);

        // 组的启停直接影响 <see cref="AccessControlService"/> 是否采信它的授权条目
        await _access.InvalidateGroupAsync(group.Id, ct);

        await _logs.WriteAsync("修改组", group.Name, success: true, ct: ct);
        _logger.LogInformation("管理员 {Admin} 修改了组 {GroupName}。", me.UserName, group.Name);

        var count = await _db.UserGroups.CountAsync(x => x.GroupId == id, ct);

        return new GroupDto
        {
            Id = group.Id,
            Name = group.Name,
            DisplayName = group.DisplayName,
            Source = group.Source.ToString(),
            IsEnabled = group.IsEnabled,
            CreatedAt = group.CreatedAt,
            MemberCount = count,
        };
    }

    public async Task DeleteGroupAsync(int id, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        if (group.Source == UserSource.Ad)
        {
            throw new ConflictException(
                $"{group.Name} 是域组，删除后其成员下次登录会被自动重建。" +
                "如需停用该组的全部授权，请改为「禁用」。");
        }

        await _access.InvalidateGroupAsync(id, ct);

        _db.Groups.Remove(group);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("管理员 {Admin} 删除了本地组 {GroupName}。", me.UserName, group.Name);
        await _logs.WriteAsync("删除组", group.Name, success: true, ct: ct);
    }

    /// <summary>
    /// 整体覆盖组内成员。
    ///
    /// 只允许本地组：域组的成员必须以 LDAP 为准，
    /// 手工增删会在该成员下次 SSO 登录时被<strong>整体覆盖</strong>掉，
    /// 表现为「当时生效、第二天失效」这类最难排查的故障。
    /// 干脆在入口拒绝，而不是写一个注定会被冲掉的功能。
    /// </summary>
    public async Task<GroupDetailDto> SetMembersAsync(int id, SetMembersRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.Include(x => x.UserGroups).FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        RequireLocalGroup(group, "调整成员");

        var userIds = dto.UserIds.Distinct().ToList();

        var existing = await _db.UserGroups.Where(x => x.GroupId == id).ToListAsync(ct);
        var before = existing.Select(x => x.UserId).ToHashSet();

        if (existing.Count > 0)
        {
            _db.UserGroups.RemoveRange(existing);
        }

        foreach (var userId in userIds)
        {
            _db.UserGroups.Add(new UserGroup { UserId = userId, GroupId = id });
        }

        await _db.SaveChangesAsync(ct);

        var changed = userIds.Union(before).Distinct().ToList();
        await _access.InvalidateAsync(await UserNamesOfAsync(changed, ct), ct);

        _logger.LogInformation(
            "管理员 {Admin} 将组 {GroupName} 的成员调整为 {Count} 人。", me.UserName, group.Name, userIds.Count);

        await _logs.WriteAsync("调整组成员", group.Name, success: true, ct: ct);

        return await GetGroupAsync(id, ct);
    }

    public async Task<GroupDetailDto> AddMemberAsync(int id, int userId, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        RequireLocalGroup(group, "添加成员");

        var exists = await _db.Users.AnyAsync(x => x.Id == userId, ct);
        if (!exists)
        {
            throw new NotFoundException($"用户不存在：{userId}");
        }

        if (await _db.UserGroups.AnyAsync(x => x.GroupId == id && x.UserId == userId, ct))
        {
            return await GetGroupAsync(id, ct);
        }

        _db.UserGroups.Add(new UserGroup { UserId = userId, GroupId = id });
        await _db.SaveChangesAsync(ct);

        await _access.InvalidateAsync(await UserNamesOfAsync([userId], ct), ct);
        await _logs.WriteAsync("添加组成员", $"{group.Name} ← {userId}", success: true, ct: ct);
        _logger.LogInformation("管理员 {Admin} 将用户 {UserId} 加入组 {GroupName}。", me.UserName, userId, group.Name);

        return await GetGroupAsync(id, ct);
    }

    public async Task RemoveMemberAsync(int id, int userId, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"组不存在：{id}");

        RequireLocalGroup(group, "移除成员");

        var link = await _db.UserGroups.FirstOrDefaultAsync(x => x.GroupId == id && x.UserId == userId, ct);
        if (link is null)
        {
            throw new NotFoundException($"用户 {userId} 不在组 {group.Name} 中。");
        }

        _db.UserGroups.Remove(link);
        await _db.SaveChangesAsync(ct);

        await _access.InvalidateAsync(await UserNamesOfAsync([userId], ct), ct);
        await _logs.WriteAsync("移除组成员", $"{group.Name} ← {userId}", success: true, ct: ct);
        _logger.LogInformation("管理员 {Admin} 将用户 {UserId} 移出组 {GroupName}。", me.UserName, userId, group.Name);
    }

    // ══════════════════════════════════════════════════════════════
    // 授权条目
    // ══════════════════════════════════════════════════════════════

    public async Task<IReadOnlyList<AclEntryDto>> ListAclAsync(
        int? groupId, int? projectId, int? checkId, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        IQueryable<AclEntry> q = _db.AclEntries.AsNoTracking();

        if (groupId is { } g)
        {
            q = q.Where(x => x.GroupId == g);
        }

        if (projectId is { } p)
        {
            q = q.Where(x => x.ProjectId == p);
        }

        if (checkId is { } c)
        {
            q = q.Where(x => x.CheckId == c);
        }

        var items = await (
            from e in q
            join grp in _db.Groups.AsNoTracking() on e.GroupId equals grp.Id
            join prj in _db.Projects.AsNoTracking() on e.ProjectId equals prj.Id
            from chk in _db.Checks.AsNoTracking().Where(x => x.Id == e.CheckId).DefaultIfEmpty()
            orderby prj.Name, chk.Name, grp.Name
            select new { e, GroupName = grp.Name, ProjectName = prj.Name, CheckName = (string?)chk.Name }
        ).ToListAsync(ct);

        return [.. items.Select(x => new AclEntryDto
        {
            Id = x.e.Id,
            GroupId = x.e.GroupId,
            GroupName = x.GroupName,
            ProjectId = x.e.ProjectId,
            ProjectName = x.ProjectName,
            CheckId = x.e.CheckId,
            CheckName = x.CheckName,
            Level = x.e.Level.ToString(),
            CreatedAt = x.e.CreatedAt,
        })];
    }

    /// <summary>新增授权。同一 (组, 项目, 检项) 已存在时返回 409——刻意不做静默覆盖。</summary>
    public async Task<AclEntryDto> CreateAclAsync(CreateAclRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dto.GroupId, ct)
                    ?? throw new NotFoundException($"组不存在：{dto.GroupId}");

        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dto.ProjectId, ct)
                      ?? throw new NotFoundException($"项目不存在：{dto.ProjectId}");

        string? checkName = null;

        if (dto.CheckId is { } checkId)
        {
            var check = await _db.Checks.AsNoTracking()
                            .FirstOrDefaultAsync(x => x.Id == checkId && x.ProjectId == project.Id, ct)
                        ?? throw new NotFoundException(
                            $"检项不存在，或不属于项目 {project.Name}：{checkId}");

            checkName = check.Name;
        }

        var exists = await _db.AclEntries.AnyAsync(
            x => x.GroupId == group.Id && x.ProjectId == project.Id && x.CheckId == dto.CheckId, ct);

        if (exists)
        {
            throw new ConflictException(
                $"组「{group.Name}」在 {project.Name}/{checkName ?? "整个项目"} 上已有授权，请直接修改其等级。");
        }

        var entry = new AclEntry
        {
            GroupId = group.Id,
            ProjectId = project.Id,
            CheckId = dto.CheckId,
            Level = ParseLevel(dto.Level),
            CreatedAt = DateTime.Now,
        };

        _db.AclEntries.Add(entry);
        await _db.SaveChangesAsync(ct);

        await _access.InvalidateGroupAsync(group.Id, ct);

        await _logs.WriteAsync(
            "新增授权",
            $"{group.Name} → {project.Name}/{checkName ?? "整个项目"} = {entry.Level}",
            success: true,
            ct: ct);

        _logger.LogInformation(
            "管理员 {Admin} 授权：组 {GroupName} → {Target} = {Level}。",
            me.UserName, group.Name, $"{project.Name}/{checkName ?? "整个项目"}", entry.Level);

        return new AclEntryDto
        {
            Id = entry.Id,
            GroupId = group.Id,
            GroupName = group.Name,
            ProjectId = project.Id,
            ProjectName = project.Name,
            CheckId = entry.CheckId,
            CheckName = checkName,
            Level = entry.Level.ToString(),
            CreatedAt = entry.CreatedAt,
        };
    }

    public async Task<AclEntryDto> UpdateAclAsync(int id, UpdateAclRequest dto, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var entry = await _db.AclEntries.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"授权条目不存在：{id}");

        var level = ParseLevel(dto.Level);
        entry.Level = level;

        await _db.SaveChangesAsync(ct);
        await _access.InvalidateGroupAsync(entry.GroupId, ct);

        await _logs.WriteAsync("修改授权", $"条目 {id} → {level}", success: true, ct: ct);
        _logger.LogInformation("管理员 {Admin} 将授权条目 {EntryId} 改为 {Level}。", me.UserName, id, level);

        var group = await _db.Groups.AsNoTracking().FirstOrDefaultAsync(x => x.Id == entry.GroupId, ct);
        var project = await _db.Projects.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == entry.ProjectId, ct);

        string? checkName = null;
        if (entry.CheckId is { } cid)
        {
            checkName = await _db.Checks.AsNoTracking()
                .Where(x => x.Id == cid)
                .Select(x => (string?)x.Name)
                .FirstOrDefaultAsync(ct);
        }

        return new AclEntryDto
        {
            Id = entry.Id,
            GroupId = entry.GroupId,
            GroupName = group?.Name ?? string.Empty,
            ProjectId = entry.ProjectId,
            ProjectName = project?.Name ?? string.Empty,
            CheckId = entry.CheckId,
            CheckName = checkName,
            Level = entry.Level.ToString(),
            CreatedAt = entry.CreatedAt,
        };
    }

    public async Task DeleteAclAsync(int id, CancellationToken ct = default)
    {
        var me = await RequireAdminAsync(ct);

        var entry = await _db.AclEntries.FirstOrDefaultAsync(x => x.Id == id, ct)
                    ?? throw new NotFoundException($"授权条目不存在：{id}");

        // 必须在物理删除<strong>之前</strong>取组 id 并失效缓存
        await _access.InvalidateGroupAsync(entry.GroupId, ct);

        _db.AclEntries.Remove(entry);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除授权", $"条目 {id}", success: true, ct: ct);
        _logger.LogInformation("管理员 {Admin} 删除授权条目 {EntryId}。", me.UserName, id);
    }

    /// <summary>
    /// 展开某个用户的有效权限，并注明每条来自哪个组的哪一条授权。
    ///
    /// 存在的意义是排查：当有人报告「我看不到某某项目」时，管理员需要看到
    /// 系统<em>实际算出</em>的结果与它的来源，而不是凭印象猜配置项哪里错了。
    /// </summary>
    public async Task<EffectiveAccessDto> EffectiveAccessAsync(string userName, CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var name = userName.Trim();
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.UserName == name, ct)
                   ?? throw new NotFoundException($"用户不存在：{name}");

        var groupNames = await (
            from ug in _db.UserGroups.AsNoTracking()
            join g in _db.Groups.AsNoTracking() on ug.GroupId equals g.Id
            where ug.UserId == user.Id
            orderby g.Name
            select new { g.Id, g.Name, g.IsEnabled }
        ).ToListAsync(ct);

        var access = await _access.ResolveByUserNameAsync(name, ct);

        if (user.IsSystemAdmin)
        {
            return new EffectiveAccessDto
            {
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                IsSystemAdmin = true,
                Groups = [.. groupNames.Select(x => x.Name)],
                Checks = [],
            };
        }

        var groupIds = groupNames.Where(x => x.IsEnabled).Select(x => x.Id).ToList();
        var nameById = groupNames.ToDictionary(x => x.Id, x => x.Name);

        // 一次性取出该用户所有相关授权条目——量级只有几十条，不必逐检项查
        var entries = await _db.AclEntries
            .AsNoTracking()
            .Where(x => groupIds.Contains(x.GroupId))
            .Select(x => new { x.GroupId, x.ProjectId, x.CheckId, x.Level })
            .ToListAsync(ct);

        var checks = await (
            from c in _db.Checks.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on c.ProjectId equals p.Id
            orderby p.Name, c.Name
            select new { c.Id, c.ProjectId, c.Name, ProjectName = p.Name }
        ).ToListAsync(ct);

        var result = new List<EffectiveCheckAccessDto>();

        foreach (var c in checks)
        {
            var checkEntries = entries.Where(x => x.CheckId == c.Id).ToList();
            var projectEntries = entries
                .Where(x => x.CheckId == null && x.ProjectId == c.ProjectId)
                .ToList();

            string level;
            string grantedBy;

            if (checkEntries.Count > 0)
            {
                // 检项级覆盖项目级，这是 <see cref="AccessControlService.BuildAsync"/> 的既定语义
                var top = checkEntries.OrderByDescending(x => x.Level).First();
                level = top.Level.ToString();
                grantedBy = $"组「{GroupNameOf(top.GroupId, nameById)}」→ 本检项";
            }
            else if (projectEntries.Count > 0)
            {
                var top = projectEntries.OrderByDescending(x => x.Level).First();
                level = top.Level.ToString();
                grantedBy = $"组「{GroupNameOf(top.GroupId, nameById)}」→ 整个项目";
            }
            else
            {
                level = nameof(AccessLevel.None);
                grantedBy = "未授权";
            }

            // 与运行时判定一致性自校验：这条万一不成立，说明两处算法已经漂移
            if (access.LevelOf(c.Id).ToString() != level)
            {
                _logger.LogWarning(
                    "权限展开与运行时判定不一致：user={UserName} check={Check} 展开={Computed} 运行时={Runtime}。",
                    name, $"{c.ProjectName}/{c.Name}", level, access.LevelOf(c.Id));
            }

            result.Add(new EffectiveCheckAccessDto
            {
                Project = c.ProjectName,
                Check = c.Name,
                Level = level,
                GrantedBy = grantedBy,
            });
        }

        return new EffectiveAccessDto
        {
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            IsSystemAdmin = false,
            Groups = [.. groupNames.Select(x => x.Name)],
            Checks = result,
        };
    }

    /// <summary>
    /// 权限配置自检。重点是找出「没有任何人能管理」的项目——
    /// 这类空洞不会报错，只会在某天需要删除或同步元数据时让人束手无策。
    /// </summary>
    public async Task<AclHealthDto> HealthAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var adminCount = await _db.Users.CountAsync(x => x.IsSystemAdmin && x.IsEnabled, ct);
        var groupCount = await _db.Groups.CountAsync(ct);
        var aclCount = await _db.AclEntries.CountAsync(ct);

        var checks = await (
            from c in _db.Checks.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on c.ProjectId equals p.Id
            select new { c.Id, c.ProjectId, ProjectName = p.Name }
        ).ToListAsync(ct);

        var entries = await _db.AclEntries
            .AsNoTracking()
            .Select(x => new { x.GroupId, x.ProjectId, x.CheckId, x.Level })
            .ToListAsync(ct);

        // EF Core 没有 ToHashSetAsync，先落地成 List 再建集合。
        // 数据量是「组」的量级，没必要为此引入 AsEnumerable 的流式处理。
        var enabledGroupIds = (await _db.Groups
            .AsNoTracking()
            .Where(x => x.IsEnabled)
            .Select(x => x.Id)
            .ToListAsync(ct)).ToHashSet();

        // 项目级 Manage 及以上 → 整个项目都算「有人管」
        var managedProjectIds = entries
            .Where(x => x.CheckId == null && x.Level >= AccessLevel.Manage && enabledGroupIds.Contains(x.GroupId))
            .Select(x => x.ProjectId)
            .ToHashSet();

        var grantedCheckIds = new HashSet<int>();
        foreach (var e in entries.Where(x => x.CheckId != null && enabledGroupIds.Contains(x.GroupId)))
        {
            if (e.Level >= AccessLevel.Manage)
            {
                grantedCheckIds.Add(e.CheckId!.Value);
            }
        }

        var unmanaged = new List<UnmanagedProjectDto>();
        foreach (var group in checks.GroupBy(x => new { x.ProjectId, x.ProjectName }))
        {
            if (managedProjectIds.Contains(group.Key.ProjectId))
            {
                continue;
            }

            var all = group.Select(x => x.Id).ToList();
            var granted = all.Count(id => grantedCheckIds.Contains(id));

            if (granted < all.Count)
            {
                unmanaged.Add(new UnmanagedProjectDto
                {
                    ProjectId = group.Key.ProjectId,
                    ProjectName = group.Key.ProjectName,
                    CheckCount = all.Count,
                    GrantedCheckCount = granted,
                });
            }
        }

        var warnings = new List<string>();

        if (adminCount == 0)
        {
            warnings.Add("系统中没有任何启用状态的系统管理员，权限将无法被修改。");
        }

        if (adminCount == 1)
        {
            warnings.Add("仅有一名系统管理员，建议再增加一名以免单点失效。");
        }

        if (aclCount == 0 && adminCount > 0)
        {
            warnings.Add("尚未配置任何授权条目，除系统管理员外所有人都看不到任何内容。");
        }

        return new AclHealthDto
        {
            AdminCount = adminCount,
            GroupCount = groupCount,
            AclEntryCount = aclCount,
            UnmanagedProjects = unmanaged,
            Warnings = warnings,
        };
    }

    // ══════════════════════════════════════════════════════════════
    // 内部：校验与工具
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 管理接口的统一入口校验。
    ///
    /// 刻意以<strong>数据库里的 User 实体</strong>为准，而不是 Cookie 里的 Role 声明：
    /// 声明在签发时就固定了，管理员刚给别人加上超管位（或刚摘掉）时不会同步变化，
    /// 于是会出现「刚授权的 admin 仍然 403」这种难以理解的现象。
    /// 这条路径只在 /api/admin/* 上走，多一次查询的代价可以接受。
    /// </summary>
    private async Task<User> RequireAdminAsync(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            throw new ConflictException(
                "鉴权未启用（Auth:Enabled=false）。当前为内网可信形态，" +
                "全部用户以匿名身份拥有完整权限，授权数据不参与判定，因此无法管理。");
        }

        var name = _request.UserName;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new AccessDeniedException("未认证。");
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.UserName == name, ct);

        if (user is null || !user.IsEnabled)
        {
            throw new AccessDeniedException($"当前账户不可用：{name}");
        }

        if (!user.IsSystemAdmin)
        {
            throw new AccessDeniedException($"仅系统管理员可执行该操作：{name}");
        }

        return user;
    }

    /// <summary>
    /// 拒绝「把自己锁在门外」的写操作。
    ///
    /// 触发条件：操作对象是操作者自己，且系统里再没有第二个可用的系统管理员。
    /// 这个故障一旦发生，只能直接改数据库才能恢复，界面内没有任何自救手段。
    /// </summary>
    private async Task RefuseIfSelfLockoutAsync(User me, int targetUserId, string action, CancellationToken ct)
    {
        if (me.Id != targetUserId)
        {
            return;
        }

        var hasOther = await _db.Users.AnyAsync(
            x => x.IsSystemAdmin && x.IsEnabled && x.Id != me.Id, ct);

        if (hasOther)
        {
            return;
        }

        throw new ConflictException(
            $"无法{action}：您是系统中唯一可用的系统管理员。" +
            "请先新建或升级另一名系统管理员，否则将再也无法进入权限管理。");
    }

    private static void RequireLocalGroup(Group group, string action)
    {
        if (group.Source == UserSource.Ad)
        {
            throw new ConflictException(
                $"{group.Name} 是域组，成员由域同步决定，无法{action}。" +
                "请把需要手工归拢的人放进本地组后再授权。");
        }
    }

    /// <summary>
    /// 解析权限等级。
    ///
    /// <c>Enum.TryParse</c> 对纯数字字符串（如 "999"）也会返回 true，
    /// 必须再用 <c>Enum.IsDefined</c> 兜一层——否则非法输入会被静默接受成一个不存在的枚举值，
    /// 存进 SQLite 后表现为一条行为不可预测的授权。
    /// </summary>
    public static AccessLevel ParseLevel(string? level)
    {
        if (string.IsNullOrWhiteSpace(level))
        {
            throw new BadRequestException("权限等级不能为空。");
        }

        if (!Enum.TryParse<AccessLevel>(level.Trim(), true, out var parsed) || !Enum.IsDefined(parsed))
        {
            throw new BadRequestException(
                $"无效的权限等级：{level}。可选：None / Read / Write / Manage / RuleManage。");
        }

        return parsed;
    }

    /// <summary>登录名校验。允许字母数字与 <c>. _ - @</c>——域 UPN 形态也留了余量。</summary>
    private static string ValidateUserName(string raw)
    {
        var value = (raw ?? string.Empty).Trim();

        if (value.Length is 0 or > 128)
        {
            throw new BadRequestException("登录名不能为空，且长度不得超过 128 个字符。");
        }

        if (!value.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '@'))
        {
            throw new BadRequestException(
                "登录名只能包含字母、数字与 . _ - @ 四种符号。");
        }

        return value;
    }

    private static string ValidateGroupName(string raw)
    {
        var value = (raw ?? string.Empty).Trim();

        if (value.Length is 0 or > 128)
        {
            throw new BadRequestException("组名不能为空，且长度不得超过 128 个字符。");
        }

        // 允许空格与反斜杠：域组名同步进来时可能是 "DOMAIN\GroupName" 形态
        if (value.Any(char.IsControl))
        {
            throw new BadRequestException("组名不能包含控制字符。");
        }

        return value;
    }

    private async Task<IReadOnlyList<string>> UserNamesOfAsync(
        IEnumerable<int> userIds, CancellationToken ct) =>
        await _db.Users.AsNoTracking()
            .Where(x => userIds.Contains(x.Id))
            .Select(x => x.UserName)
            .ToListAsync(ct);

    private async Task<List<UserDto>> WithGroupsAsync(List<User> users, CancellationToken ct)
    {
        var map = await LoadGroupsAsync(users.Select(x => x.Id).ToList(), ct);

        return [.. users.Select(u => ToDto(u, map.GetValueOrDefault(u.Id) ?? []))];
    }

    /// <summary>一趟查完这批用户各自的组名，避免逐个用户回查（N+1）。</summary>
    private async Task<Dictionary<int, List<string>>> LoadGroupsAsync(
        List<int> userIds, CancellationToken ct)
    {
        var map = new Dictionary<int, List<string>>();

        if (userIds.Count == 0)
        {
            return map;
        }

        var rows = await (
            from ug in _db.UserGroups.AsNoTracking()
            join g in _db.Groups.AsNoTracking() on ug.GroupId equals g.Id
            where userIds.Contains(ug.UserId)
            orderby g.Name
            select new { ug.UserId, g.Name }
        ).ToListAsync(ct);

        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.UserId, out var list))
            {
                list = [];
                map[row.UserId] = list;
            }

            list.Add(row.Name);
        }

        return map;
    }

    private static string GroupNameOf(int groupId, Dictionary<int, string> nameById) =>
        nameById.TryGetValue(groupId, out var n) ? n : groupId.ToString();

    private static UserDto ToDto(User user, IReadOnlyList<string> groups) => new()
    {
        Id = user.Id,
        UserName = user.UserName,
        DisplayName = user.DisplayName,
        Source = user.Source.ToString(),
        IsEnabled = user.IsEnabled,
        IsSystemAdmin = user.IsSystemAdmin,
        MustChangePassword = user.MustChangePassword,
        LastLoginAt = user.LastLoginAt,
        CreatedAt = user.CreatedAt,
        Groups = groups,
    };
}
