using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;

namespace OfficeTool.Api.Services;

public static class ApiTokenDefaults
{
    /// <summary>方案名。与 Cookie / Negotiate 并列。</summary>
    public const string Scheme = "ApiToken";

    /// <summary>
    /// 明文令牌前缀。不参与计算，纯粹为了让管理员一眼看出这串是什么，
    /// 也便于将来用 DLP 规则扫描内网里散落的令牌。
    /// </summary>
    public const string Prefix = "otk_";
}

/// <summary>外部工具 / 脚本访问令牌的颁发与管理。</summary>
public sealed class ApiTokenService(
    AppDbContext db,
    IMemoryCache cache,
    ILogger<ApiTokenService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly IMemoryCache _cache = cache;
    private readonly ILogger<ApiTokenService> _logger = logger;

    /// <summary>验证结果缓存时长。见 <see cref="TryResolveAsync"/> 关于吊销延迟的说明。</summary>
    private static readonly TimeSpan VerifyCacheTtl = TimeSpan.FromMinutes(2);

    /// <summary>LastUsedAt 写入节流窗口。见 <see cref="TouchUsage"/>。</summary>
    private static readonly TimeSpan UsageWriteThrottle = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 颁发一条令牌。
    ///
    /// 明文<strong>只在此处返回一次</strong>：库里只有 SHA-256 哈希，丢失无法找回，只能吊销重发。
    /// 这是刻意的——应用管理员也不该能读出别人的令牌。
    /// </summary>
    public async Task<(User User, string PlainToken)> IssueAsync(
        User user,
        string name,
        DateTime? expiresAt,
        CancellationToken ct = default)
    {
        var plain = ApiTokenDefaults.Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        var token = new ApiToken
        {
            UserId = user.Id,
            Name = name,
            TokenHash = Hash(plain),
            CreatedAt = DateTime.Now,
            ExpiresAt = expiresAt,
        };

        _db.ApiTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation("用户 {UserName} 颁发了访问令牌 {TokenId}（{TokenName}）。",
            user.UserName, token.Id, name);

        return (user, plain);
    }

    public async Task<IReadOnlyList<ApiToken>> ListAsync(int userId, CancellationToken ct = default) =>
        await _db.ApiTokens
            .AsNoTracking()
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

    public async Task<bool> RevokeAsync(int userId, int tokenId, CancellationToken ct = default)
    {
        var token = await _db.ApiTokens.FirstOrDefaultAsync(x => x.UserId == userId && x.Id == tokenId, ct);
        if (token is null)
        {
            return false;
        }

        token.IsRevoked = true;
        await _db.SaveChangesAsync(ct);

        // 立刻清掉正向缓存。没有这一步，正常吊销也要等 VerifyCacheTtl 才生效。
        _cache.Remove(VerifyCacheKey(token.TokenHash));

        return true;
    }

    /// <summary>
    /// 校验明文令牌并返回身份。
    ///
    /// 结果缓存 2 分钟的取舍：插件每打开一次文档都要打一次 API，
    /// 不缓存意味着每个请求至少一次 SQL 查询。代价是<strong>强制失效最长延迟 2 分钟</strong>；
    /// 吊销走 <see cref="RevokeAsync"/> 会立即清缓存，所以正常吊销是即时的，
    /// 2 分钟窗口只覆盖「用户在 AD 里被禁用」「令牌过期」这类非主动操作。
    /// </summary>
    public async Task<VerifiedTokenIdentity?> TryResolveAsync(string plainToken, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(plainToken))
        {
            return null;
        }

        var hash = Hash(plainToken);
        var key = VerifyCacheKey(hash);

        if (_cache.TryGetValue<TokenCacheEntry>(key, out var cached) && cached is not null)
        {
            if (cached.UserId == 0)
            {
                return null;
            }

            TouchUsage(cached.UserId);
            return new VerifiedTokenIdentity(cached.UserId, cached.UserName, cached.IsSystemAdmin);
        }

        var token = await _db.ApiTokens
            .Include(x => x.User)
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.TokenHash == hash, ct);

        if (token is null || token.IsRevoked || token.User is null || !token.User.IsEnabled)
        {
            _cache.Set(key, new TokenCacheEntry(0, string.Empty, false), VerifyCacheTtl);
            return null;
        }

        if (token.ExpiresAt is { } expiry && expiry <= DateTime.Now)
        {
            _cache.Set(key, new TokenCacheEntry(0, string.Empty, false), VerifyCacheTtl);
            _logger.LogWarning("访问令牌 {TokenId} 已过期，拒绝使用。", token.Id);
            return null;
        }

        _cache.Set(
            key,
            new TokenCacheEntry(token.UserId, token.User.UserName, token.User.IsSystemAdmin),
            VerifyCacheTtl);

        TouchUsage(token.UserId);

        return new VerifiedTokenIdentity(token.UserId, token.User.UserName, token.User.IsSystemAdmin);
    }

    /// <summary>
    /// 更新该用户令牌的 LastUsedAt。
    ///
    /// 刻意做成「一次 SQL + 节流」而不是精确时间戳：它能回答的问题只有一个
    /// 「这条令牌最近还在被用吗」，为准确性付出每次请求一次写库的代价不划算。
    /// 且这里还在认证路径上，写库失败也不能影响登录，异常一律吞掉。
    /// </summary>
    private void TouchUsage(int userId)
    {
        var key = $"apitoken-usage:{userId}";
        if (_cache.TryGetValue<bool>(key, out _))
        {
            return;
        }

        _cache.Set(key, true, UsageWriteThrottle);

        var db = _db;
        try
        {
            db.ApiTokens
                .Where(x => x.UserId == userId && x.LastUsedAt == null)
                .ExecuteUpdate(s => s.SetProperty(x => x.LastUsedAt, DateTime.Now));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "更新令牌最近使用时间失败，不影响本次请求。");
        }
    }

    /// <summary>SHA-256 十六进制。</summary>
    public static string Hash(string plainToken)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plainToken));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string VerifyCacheKey(string hash) => $"apitoken:{hash}";

    private sealed record TokenCacheEntry(int UserId, string UserName, bool IsSystemAdmin);
}

/// <summary>令牌验出来的身份。刻意不带 User 实体，避免调用方误以为它附着在某次请求上。</summary>
public sealed record VerifiedTokenIdentity(int UserId, string UserName, bool IsSystemAdmin);

/// <summary>
/// 从 <c>Authorization: Bearer &lt;token&gt;</c> 建立身份的认证处理器。
///
/// 面向没有浏览器登录态的调用方（部署验证脚本、自动化任务）：
/// 它们既没有 Cookie，也不在浏览器上下文里因而拿不到 Kerberos 票据，
/// 只能靠长期令牌证明身份。
/// </summary>
public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder,
    IServiceScopeFactory scopeFactory)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, loggerFactory, encoder)
{
    private readonly IServiceScopeFactory _scopeFactory = scopeFactory;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();

        if (string.IsNullOrEmpty(header) || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var raw = header["Bearer ".Length..].Trim();
        if (raw.Length == 0)
        {
            return AuthenticateResult.NoResult();
        }

        // 处理器按 singleton 注册，AppDbContext 按 scoped —— 必须自建 scope，不能直接注入。
        using var scope = _scopeFactory.CreateScope();
        var tokens = scope.ServiceProvider.GetRequiredService<ApiTokenService>();
        var identity = await tokens.TryResolveAsync(raw, Context.RequestAborted);

        if (identity is null)
        {
            return AuthenticateResult.Fail("令牌无效、已吊销或已过期。");
        }

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, identity.UserName),
            new(ClaimTypes.NameIdentifier, identity.UserId.ToString()),
        };

        if (identity.IsSystemAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "SystemAdmin"));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiTokenDefaults.Scheme));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiTokenDefaults.Scheme));
    }
}
