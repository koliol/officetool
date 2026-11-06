using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Data;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Services;

/// <summary>
/// 用户档案的维护：域用户同步、本地账户校验、首次部署的管理员引导。
///
/// 与 <see cref="AccessControlService"/> 的分工：
/// 这里只管「档案与组关系怎么落库」，那边只管「档案能推出什么权限」。
/// </summary>
public sealed class UserDirectoryService(
    AppDbContext db,
    AuthOptions options,
    IAccessControlService access,
    ILogger<UserDirectoryService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly AuthOptions _options = options;
    private readonly IAccessControlService _access = access;
    private readonly ILogger<UserDirectoryService> _logger = logger;

    /// <summary>
    /// 域用户首次登录时建档，并<strong>整体覆盖</strong>其组关系。
    ///
    /// 必须整体覆盖而不是增量合并：域里把某人移出组，本系统必须同步失去对应权限。
    /// 增量合并会让「已移除的权限」残留，这是权限系统里最危险的一类静默失效。
    ///
    /// <paramref name="groupIdentifiers"/> 里的值可能是 sAMAccountName，也可能是 SID
    /// （<c>S-1-5-21-...</c>）——取决于 Negotiate 的 EnableLdap 给出哪种声明。
    /// 两者都原样存进 <c>Groups.Name</c>，管理员可通过 <c>DisplayName</c> 补一个易读标签。
    /// </summary>
    public async Task<User> UpsertAdUserAsync(
        string userName,
        IEnumerable<string> groupIdentifiers,
        CancellationToken ct = default)
    {
        var name = AccessControlService.NormalizeAccountName(userName);

        // 同名账户必须区分来源：本地账户与域账户共用一张表，如果只按名字查，
        // 域内任何人只要 sAMAccountName 撞上本地账户（默认引导账户就叫 admin），
        // 就能被签发该账户的会话 —— 等于不用密码接管超管。这里必须显式拒绝。
        var user = await _db.Users.FirstOrDefaultAsync(
            x => EF.Functions.Collate(x.UserName, "NOCASE") == name, ct);

        if (user is not null && user.Source == UserSource.Local)
        {
            _logger.LogWarning(
                "拒绝 SSO 登录：{UserName} 与本地账户同名，二者必须使用不同登录名。", name);
            throw new AccessDeniedException($"登录名 {name} 已被本地账户占用，无法用域账户登录。");
        }

        if (user is not null && !user.IsEnabled)
        {
            _logger.LogWarning("拒绝 SSO 登录：{UserName} 已被停用。", name);
            throw new AccessDeniedException("该账户已被停用，请联系管理员。");
        }

        if (user is null)
        {
            user = new User
            {
                UserName = name,
                DisplayName = name,
                Source = UserSource.Ad,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
            };

            _db.Users.Add(user);
            await _db.SaveChangesAsync(ct);

            _logger.LogInformation("域用户 {UserName} 首次登录，已自动建档。", name);
        }

        var identifiers = groupIdentifiers
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 只覆盖**域组**关系。本地组是管理员手工维护的（例如「把这个人归拢到某个
        // 本地组」），以域为准去删它们会把人工授权在下次登录时静默抹掉。
        var existing = await (
            from ug in _db.UserGroups
            join g in _db.Groups on ug.GroupId equals g.Id
            where ug.UserId == user.Id && g.Source == UserSource.Ad
            select ug).ToListAsync(ct);

        // 拿不到域组（LDAP 未配 / 查询失败 / 域里确实没有组）时保持现状，
        // 不能把它当成「域里一个组都没有」而清空，否则撤权与失权都无法区分。
        if (identifiers.Count == 0)
        {
            _logger.LogWarning(
                "域用户 {UserName} 本次未返回任何组标识，跳过组关系覆盖（保持现有授权）。", name);
        }
        else if (existing.Count > 0)
        {
            // 先删后插：保证「以域为准」，不残留已被移除的关系
            _db.UserGroups.RemoveRange(existing);
        }

        foreach (var identifier in identifiers)
        {
            var group = await _db.Groups.FirstOrDefaultAsync(g => g.Name == identifier, ct);
            if (group is null)
            {
                group = new Group
                {
                    Name = identifier,
                    DisplayName = identifier,
                    Source = UserSource.Ad,
                    IsEnabled = true,
                    CreatedAt = DateTime.UtcNow,
                };

                _db.Groups.Add(group);
                await _db.SaveChangesAsync(ct);
            }

            _db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        // 组关系变了，权限缓存必须失效，否则用户要等缓存过期（默认 5 分钟）才看到变化。
        // 注释写在这里却没有调用，等于撤权不生效 —— 这是权限系统里最危险的一类静默失效。
        await _access.InvalidateAsync([user.UserName], ct);
        return user;
    }

    /// <summary>本地账户密码校验。失败一律返回 null，不区分「用户不存在」与「密码错」。</summary>
    public async Task<User?> ValidateLocalAsync(string userName, string password, CancellationToken ct = default)
    {
        var name = userName.Trim();
        var user = await _db.Users.FirstOrDefaultAsync(x => x.UserName == name, ct);

        if (user is null || user.Source != UserSource.Local)
        {
            return null;
        }

        if (!user.IsEnabled)
        {
            return null;
        }

        if (!PasswordHasher.Verify(user.PasswordHash, password))
        {
            return null;
        }

        user.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return user;
    }

    /// <summary>
    /// Users 表为空时创建本地管理员，返回其明文初始密码（仅此一次可见）。
    ///
    /// 为什么必须有一个逃生舱：ACL 一旦配错（例如把所有授权都删了），
    /// 没有超管就再也进不去管理界面修。这是权限系统上线时最容易把自己锁死的环节。
    /// </summary>
    public async Task<(User Admin, string Password)?> EnsureBootstrapAdminAsync(CancellationToken ct = default)
    {
        if (!_options.BootstrapLocalAdmin)
        {
            return null;
        }

        if (await _db.Users.AnyAsync(ct))
        {
            return null;
        }

        var password = GeneratePassword();
        var admin = new User
        {
            UserName = _options.BootstrapAdminName,
            DisplayName = "系统管理员",
            Source = UserSource.Local,
            PasswordHash = PasswordHasher.Hash(password),
            IsEnabled = true,
            IsSystemAdmin = true,
            MustChangePassword = true,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Users.Add(admin);
        await _db.SaveChangesAsync(ct);

        _logger.LogWarning(
            "已创建本地超级管理员 {AdminName}，初始密码：{Password}（首次登录将强制修改，请立即登录并妥善保存）",
            admin.UserName, password);

        return (admin, password);
    }

    /// <summary>
    /// 生成可用于人工转述的随机密码。
    ///
    /// 去掉了 0/O、1/l/I 这类易混淆字符：初始密码与重置后的密码都要由管理员
    /// 口头或书面转述一次，拼错的成本远高于少几个字符带来的熵损失。
    /// </summary>
    public static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var chars = new char[16];

        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = alphabet[bytes[i] % alphabet.Length];
        }

        return new string(chars);
    }

    /// <summary>
    /// 从已认证身份里抽取组标识。
    ///
    /// Negotiate 的 <c>EnableLdap</c> 在不同环境下给出的声明类型不一致：
    /// 可能是 <see cref="ClaimTypes.Role"/>、<see cref="ClaimTypes.GroupSid"/>，
    /// 也可能是自定义的 "groups"。这里都收，避免依赖某一种实现细节。
    /// </summary>
    public static IReadOnlyList<string> ExtractGroupIdentifiers(ClaimsPrincipal principal)
    {
        var result = new List<string>();

        foreach (var claim in principal.Claims)
        {
            if (claim.Type is ClaimTypes.Role or ClaimTypes.GroupSid
                or "groups" or "http://schemas.microsoft.com/ws/2008/06/identity/claims/groupsid")
            {
                if (!string.IsNullOrWhiteSpace(claim.Value))
                {
                    result.Add(claim.Value);
                }
            }
        }

        return result;
    }
}
