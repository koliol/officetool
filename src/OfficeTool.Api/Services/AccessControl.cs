using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Services;

/// <summary>
/// 一次解析出来的「这个人能看什么、能做什么」。
///
/// 之所以预先展开成集合而不是每次查库：列表查询要在 SQL 里按 CheckId 过滤，
/// 展开好的 id 集合可以直接翻译成 <c>Contains</c>，把权限过滤留在数据库层。
/// </summary>
public sealed record UserAccess(
    string UserName,
    int? UserId,
    bool IsSystemAdmin,
    IReadOnlySet<int> AllowedCheckIds,
    IReadOnlyDictionary<int, AccessLevel> LevelByCheckId,
    IReadOnlySet<string> AllowedScopes)
{
    /// <summary>未开启鉴权时的形态：全权限、无身份。</summary>
    public static UserAccess Unrestricted { get; } = new(
        "(anonymous)",
        null,
        true,
        new HashSet<int>(),
        new Dictionary<int, AccessLevel>(),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>已认证但没有任何授权条目。</summary>
    public static UserAccess None(string userName, int? userId) => new(
        userName,
        userId,
        false,
        new HashSet<int>(),
        new Dictionary<int, AccessLevel>(),
        new HashSet<string>(StringComparer.Ordinal));

    /// <summary>
    /// 是否需要在列表查询里加检项过滤。
    ///
    /// 这是个容易被踩的坑：超管与关闭鉴权时 <see cref="AllowedCheckIds"/> 是<strong>空集合</strong>，
    /// 直接拿它做 <c>Contains</c> 会过滤掉<emphasis>所有</emphasis>行。
    /// 调用方必须先判断这个值，为 false 时完全不加 Where。
    /// </summary>
    public bool NeedsCheckFilter => !IsSystemAdmin;

    /// <summary>该检项的有效等级。未授权返回 <see cref="AccessLevel.None"/>。</summary>
    public AccessLevel LevelOf(int? checkId)
    {
        if (IsSystemAdmin)
        {
            return AccessLevel.RuleManage;
        }

        if (checkId is null)
        {
            return AccessLevel.None;
        }

        return LevelByCheckId.TryGetValue(checkId.Value, out var level) ? level : AccessLevel.None;
    }

    /// <summary>
    /// 校验对某个检项是否具备指定等级。递进语义：<c>实际等级 &gt;= 要求等级</c> 即通过。
    /// </summary>
    public bool Has(int? checkId, AccessLevel required) => LevelOf(checkId) >= required;

    /// <summary>检项是否可见（等级高于 None 即视为可见）。</summary>
    public bool IsVisible(int? checkId) => IsSystemAdmin
                                           || (checkId is not null && LevelOf(checkId) > AccessLevel.None);

    /// <summary>
    /// 规则 / 附件 / 回收站的可见性判定。
    ///
    /// 这三类资源的实体里<strong>没有 ProjectId/CheckId 外键</strong>（只有路径或名称字符串），
    /// 无法走 id 索引，只能按「项目名 + 检项名」匹配。这是既有建模的限制，
    /// 数据量在几千条量级时无感知，若后续增长应给它们补外键。
    /// </summary>
    public bool IsScopeAllowed(string project, string check) =>
        IsSystemAdmin || AllowedScopes.Contains(ScopeKey(project, check));

    /// <summary>是否需要对上述按名称匹配的资源做过滤。</summary>
    public bool NeedsScopeFilter => !IsSystemAdmin;

    public static string ScopeKey(string project, string check) => $"{project}\u001f{check}";
}

/// <summary>
/// 权限校验的统一入口。
///
/// 刻意放在<strong>服务层</strong>而不是控制器：控制器在这个项目里只做参数转发，
/// 真正的业务入口是服务。若判在控制器，每新增一个端点都要记得加一层判断，
/// 漏一个就是一个越权漏洞；判在服务层则天然覆盖所有调用方。
/// </summary>
public static class AccessGuard
{
    /// <summary>
    /// 读权限不足 → 抛 404 而不是 403。
    ///
    /// 理由：403 会证实「这个资源的确存在」，等于把项目清单开放给无权用户枚举。
    /// 内网工具的路径里通常含客户名与项目代号，属于不该泄露的信息。
    /// 代价是用户看到「找不到」而非「没权限」，对只读场景这个误导可以接受。
    /// </summary>
    public static void RequireRead(this UserAccess access, int? checkId, string target)
    {
        if (!access.Has(checkId, AccessLevel.Read))
        {
            throw new NotFoundException($"资源不存在或无权访问：{target}");
        }
    }

    /// <summary>
    /// 写入类操作的权限不足 → 抛 403。
    /// 与读不同，这里明说「无权限」：用户已经看见了资源，含糊的失败会让人反复重试。
    /// </summary>
    public static void Require(this UserAccess access, int? checkId, AccessLevel level, string action, string target)
    {
        if (access.Has(checkId, level))
        {
            return;
        }

        throw new AccessDeniedException($"无权{action}：{target}（要求权限：{Describe(level)}）");
    }

    public static string Describe(AccessLevel level) => level switch
    {
        AccessLevel.Read => "只读",
        AccessLevel.Write => "编辑",
        AccessLevel.Manage => "管理",
        AccessLevel.RuleManage => "规则管理",
        _ => "无",
    };
}

public static class RequestAccessExtensions
{
    /// <summary>服务里的一行式权限入口。</summary>
    public static Task<UserAccess> ResolveAccessAsync(
        this IAccessControlService access,
        RequestContext context,
        CancellationToken ct = default) => access.ResolveAsync(context.User, ct);
}

/// <summary>访问控制解析。</summary>
public interface IAccessControlService
{
    /// <summary>按当前请求的身份解析权限。</summary>
    Task<UserAccess> ResolveAsync(ClaimsPrincipal? principal, CancellationToken ct = default);

    /// <summary>按登录名解析权限（登录流程内部使用，此时还没有 ClaimsPrincipal）。</summary>
    Task<UserAccess> ResolveByUserNameAsync(string userName, CancellationToken ct = default);

    /// <summary>
    /// 使指定用户的权限缓存失效。
    ///
    /// 任何会改变「这个人能看什么」的写操作之后都必须调用它，
    /// 否则用户要等 <c>Auth:AclCacheMinutes</c> 过期才看到变化——
    /// 在管理员眼里就是「改了没生效」，且极易被误判成 bug。
    /// </summary>
    Task InvalidateAsync(IEnumerable<string> userNames, CancellationToken ct = default);

    /// <summary>使某个组<strong>全体成员</strong>的权限缓存失效。授权变更后必须调用。</summary>
    Task InvalidateGroupAsync(int groupId, CancellationToken ct = default);
}

public sealed class AccessControlService(
    AppDbContext db,
    IMemoryCache cache,
    AuthOptions options,
    ILogger<AccessControlService> logger) : IAccessControlService
{
    private readonly AppDbContext _db = db;
    private readonly IMemoryCache _cache = cache;
    private readonly AuthOptions _options = options;
    private readonly ILogger<AccessControlService> _logger = logger;

    public Task<UserAccess> ResolveAsync(ClaimsPrincipal? principal, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(UserAccess.Unrestricted);
        }

        var raw = principal?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return Task.FromResult(UserAccess.None(string.Empty, null));
        }

        return ResolveByUserNameAsync(NormalizeAccountName(raw), ct);
    }

    public async Task<UserAccess> ResolveByUserNameAsync(string userName, CancellationToken ct = default)
    {
        if (!_options.Enabled)
        {
            return UserAccess.Unrestricted;
        }

        var key = CacheKey(userName);
        if (_cache.TryGetValue<UserAccess>(key, out var cached) && cached is not null)
        {
            return cached;
        }

        var user = await _db.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.UserName == userName, ct);

        if (user is null)
        {
            return UserAccess.None(userName, null);
        }

        if (!user.IsEnabled)
        {
            _logger.LogWarning("用户 {UserName} 已被禁用，拒绝解析权限。", userName);
            return UserAccess.None(userName, user.Id);
        }

        var access = await BuildAsync(user, ct);

        _cache.Set(key, access, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(Math.Max(1, _options.AclCacheMinutes)),
        });

        return access;
    }

    public Task InvalidateAsync(IEnumerable<string> userNames, CancellationToken ct = default)
    {
        foreach (var name in userNames)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _cache.Remove(CacheKey(name));
            }
        }

        return Task.CompletedTask;
    }

    public async Task InvalidateGroupAsync(int groupId, CancellationToken ct = default)
    {
        var names = await _db.UserGroups
            .AsNoTracking()
            .Where(x => x.GroupId == groupId)
            .Select(x => x.User!.UserName)
            .ToListAsync(ct);

        await InvalidateAsync(names, ct);
    }

    /// <summary>缓存键。写入与失效必须走同一个函数，否则大小写差异会让失效静默失败。</summary>
    private static string CacheKey(string userName) => $"acl:{userName.ToLowerInvariant()}";

    private async Task<UserAccess> BuildAsync(User user, CancellationToken ct)
    {
        if (user.IsSystemAdmin)
        {
            return new UserAccess(
                user.UserName, user.Id, true,
                new HashSet<int>(),
                new Dictionary<int, AccessLevel>(),
                new HashSet<string>(StringComparer.Ordinal));
        }

        // 组被禁用时必须连同其授权一起失效，否则管理页上的「停用该组」
        // 会变成一个看起来生效、实则毫无作用的开关——比没有这个开关更糟。
        var groupIds = await (
            from ug in _db.UserGroups.AsNoTracking()
            join g in _db.Groups.AsNoTracking() on ug.GroupId equals g.Id
            where ug.UserId == user.Id && g.IsEnabled
            select g.Id
        ).ToListAsync(ct);

        if (groupIds.Count == 0)
        {
            return UserAccess.None(user.UserName, user.Id);
        }

        // 检项级条目
        var checkLevel = await _db.AclEntries
            .AsNoTracking()
            .Where(x => groupIds.Contains(x.GroupId) && x.CheckId != null)
            .Select(x => new { x.CheckId, x.Level })
            .ToListAsync(ct);

        // 项目级条目（CheckId == null）→ 展开到该项目下所有检项
        var projectLevel = await _db.AclEntries
            .AsNoTracking()
            .Where(x => groupIds.Contains(x.GroupId) && x.CheckId == null)
            .Select(x => new { x.ProjectId, x.Level })
            .ToListAsync(ct);

        // 同一个检项被多条授权条目命中时取**最高**等级：
        // 一个人属于两个组，就该享受较高那组的待遇。
        var levelByCheck = new Dictionary<int, AccessLevel>();
        foreach (var entry in checkLevel)
        {
            var id = entry.CheckId!.Value;
            levelByCheck[id] = levelByCheck.TryGetValue(id, out var acc)
                ? (AccessLevel)Math.Max((int)acc, (int)entry.Level)
                : entry.Level;
        }

        if (projectLevel.Count > 0)
        {
            var projectIds = projectLevel.Select(x => x.ProjectId).Distinct().ToList();

            // 只取需要的字段，避免把整个 Checks 表拉进内存
            var checks = await _db.Checks
                .AsNoTracking()
                .Where(c => projectIds.Contains(c.ProjectId))
                .Select(c => new { c.Id, c.ProjectId })
                .ToListAsync(ct);

            var levelByProject = new Dictionary<int, AccessLevel>();
            foreach (var entry in projectLevel)
            {
                levelByProject[entry.ProjectId] = levelByProject.TryGetValue(entry.ProjectId, out var acc)
                    ? (AccessLevel)Math.Max((int)acc, (int)entry.Level)
                    : entry.Level;
            }

            foreach (var c in checks)
            {
                // 检项级条目优先于项目级，包括「降级」的情形：
                //   · 项目授权管理 + 某检项显式只读 → 该检项只读
                //   · 项目授权管理 + 某检项显式 None → 该检项不可见（黑名单例外）
                // 若取 Max，第二种情形会被静默抬高成「管理」，例外就配不出来。
                // 具体条目覆盖笼统条目，是 ACL 的通用原则。
                if (levelByCheck.ContainsKey(c.Id))
                {
                    continue;
                }

                if (levelByProject.TryGetValue(c.ProjectId, out var pr))
                {
                    levelByCheck[c.Id] = pr;
                }
            }
        }

        if (levelByCheck.Count == 0)
        {
            return UserAccess.None(user.UserName, user.Id);
        }

        var allowedCheckIds = levelByCheck.Keys.Where(k => levelByCheck[k] > AccessLevel.None).ToHashSet();

        // 规则/附件/回收站按名称匹配，需要 id → 名称 的映射
        var scopes = await _db.Checks
            .AsNoTracking()
            .Where(c => allowedCheckIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, ProjectName = c.Project!.Name })
            .ToListAsync(ct);

        var scopeSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in scopes)
        {
            scopeSet.Add(UserAccess.ScopeKey(s.ProjectName, s.Name));
        }

        return new UserAccess(
            user.UserName, user.Id, false,
            allowedCheckIds, levelByCheck, scopeSet);
    }

    /// <summary>
    /// 归一化登录名。
    /// Linux 上 Negotiate 给的是 <c>user@DOMAIN</c>（UPN），Windows 上是 <c>DOMAIN\user</c>；
    /// 两者在本系统里都按 sAMAccountName 存储，即不含域前缀。
    /// </summary>
    public static string NormalizeAccountName(string raw)
    {
        var value = raw.Trim();

        var at = value.IndexOf('@');
        if (at > 0)
        {
            value = value[..at];
        }

        var slash = value.LastIndexOf('\\');
        if (slash >= 0)
        {
            value = value[(slash + 1)..];
        }

        return value;
    }
}

/// <summary>
/// PBKDF2 密码哈希。
///
/// 刻意不引入 Microsoft.AspNetCore.Identity：只需要一个哈希函数，
/// 为它拖进一整套 Identity 抽象不值得。格式与 ASP.NET Identity 的 V3 格式不兼容，
/// 但本系统的本地账户不从别处迁移，没有兼容负担。
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 100_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, Iterations, HashAlgorithmName.SHA256);
        var key = pbkdf2.GetBytes(KeySize);

        return $"{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    /// <summary>校验。哈希格式不认识时返回 false，不抛异常——避免把内部细节变成 500。</summary>
    public static bool Verify(string? stored, string password)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('.');
        if (parts.Length != 3 || !int.TryParse(parts[0], out var iterations))
        {
            return false;
        }

        byte[] salt;
        byte[] expected;

        try
        {
            salt = Convert.FromBase64String(parts[1]);
            expected = Convert.FromBase64String(parts[2]);
        }
        catch (FormatException)
        {
            return false;
        }

        using var pbkdf2 = new Rfc2898DeriveBytes(password, salt, iterations, HashAlgorithmName.SHA256);
        var actual = pbkdf2.GetBytes(expected.Length);

        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
