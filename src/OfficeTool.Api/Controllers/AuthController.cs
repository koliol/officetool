using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// 认证入口。三条通道：
/// <list type="number">
///   <item><c>GET /api/auth/sso</c> —— Windows 集成认证（Kerberos/Negotiate），成功后签发应用 Cookie</item>
///   <item><c>POST /api/auth/login</c> —— 本地账户密码登录</item>
///   <item>鉴权总开关关闭时，<c>/api/auth/me</c> 直接返回全权限</item>
/// </list>
///
/// 关键设计：SSO 端点拿到 Negotiate 身份后<strong>转签应用 Cookie</strong>，
/// 后续请求只认 Cookie。否则每个请求都要走一次 401 握手 + LDAP 查组，延迟不可接受。
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(
    AppDbContext db,
    AuthOptions options,
    UserDirectoryService directory,
    IAccessControlService access,
    ApiTokenService tokens,
    ILogger<AuthController> logger) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly AuthOptions _options = options;
    private readonly UserDirectoryService _directory = directory;
    private readonly IAccessControlService _access = access;
    private readonly ApiTokenService _tokens = tokens;
    private readonly ILogger<AuthController> _logger = logger;

    /// <summary>
    /// 取出当前请求对应的用户实体。
    ///
    /// Bearer 通道（外部脚本）与 Cookie 通道都走这里：<see cref="ApiTokenAuthenticationHandler"/>
    /// 签发的 principal 与 Cookie 通道用同一套 ClaimTypes.Name，无需区分来源。
    /// </summary>
    private async Task<User?> CurrentUserAsync(CancellationToken ct)
    {
        var raw = User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var name = AccessControlService.NormalizeAccountName(raw);
        return await _db.Users.FirstOrDefaultAsync(x => x.UserName == name, ct);
    }

    /// <summary>
    /// 当前身份。前端启动第一个请求就打这里：
    /// 200 → 直接用；401 → 试 <c>/api/auth/sso</c>；再 401 → 显示本地登录页。
    /// </summary>
    [HttpGet("me")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthMeDto>> Me(CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Ok(new AuthMeDto
            {
                AuthEnabled = false,
                Authenticated = true,
                UserName = "(anonymous)",
                DisplayName = "内网可信模式",
                Source = "None",
                IsSystemAdmin = true,
            });
        }

        var raw = User?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(raw) || User?.Identity?.IsAuthenticated != true)
        {
            return Unauthorized(new AuthMeDto { AuthEnabled = true, Authenticated = false });
        }

        var name = AccessControlService.NormalizeAccountName(raw);
        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.UserName == name, ct);

        return Ok(new AuthMeDto
        {
            AuthEnabled = true,
            Authenticated = true,
            UserName = name,
            DisplayName = user?.DisplayName ?? name,
            Source = user?.Source.ToString() ?? UserSource.Ad.ToString(),
            IsSystemAdmin = user?.IsSystemAdmin ?? false,
            MustChangePassword = user?.MustChangePassword ?? false,
        });
    }

    /// <summary>
    /// Windows 集成认证入口。
    ///
    /// 不使用 <c>[AllowAnonymous]</c> —— 它需要由 Negotiate 方案来发起 401 挑战，
    /// 浏览器（IE/Edge/Chrome 在内网区域）会自动完成握手，用户全程无感。
    /// 认证成功后转签应用 Cookie 并跳回首页。
    /// </summary>
    [HttpGet("sso")]
    [Authorize(AuthenticationSchemes = NegotiateDefaults.AuthenticationScheme)]
    public async Task<IActionResult> Sso(CancellationToken ct)
    {
        var identity = User;
        var raw = identity?.Identity?.Name;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return Unauthorized();
        }

        var groups = UserDirectoryService.ExtractGroupIdentifiers(identity!);
        var user = await _directory.UpsertAdUserAsync(raw, groups, ct);

        _logger.LogInformation(
            "域用户 {UserName} 通过 SSO 登录，同步到 {GroupCount} 个组。",
            user.UserName, groups.Count);

        await SignInAsync(user);

        // 浏览器直连跳转，用户无感回到应用
        return Redirect("~/");
    }

    /// <summary>本地账户登录。</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<ActionResult<AuthMeDto>> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (!_options.Enabled)
        {
            return Ok(new AuthMeDto { AuthEnabled = false, Authenticated = true, IsSystemAdmin = true });
        }

        if (string.IsNullOrWhiteSpace(request.UserName) || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { error = "invalid_request", message = "用户名与密码不能为空。" });
        }

        var user = await _directory.ValidateLocalAsync(request.UserName, request.Password, ct);
        if (user is null)
        {
            // 刻意不区分「用户不存在」与「密码错误」，避免账号枚举
            _logger.LogWarning("本地账户登录失败：{UserName}", request.UserName);
            return Unauthorized(new { error = "invalid_credentials", message = "用户名或密码错误。" });
        }

        await SignInAsync(user);

        return Ok(new AuthMeDto
        {
            AuthEnabled = true,
            Authenticated = true,
            UserName = user.UserName,
            DisplayName = user.DisplayName,
            Source = user.Source.ToString(),
            IsSystemAdmin = user.IsSystemAdmin,
            MustChangePassword = user.MustChangePassword,
        });
    }

    /// <summary>修改本地账户密码（引导出来的管理员首次登录会走这里）。</summary>
    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);

        if (user is null || user.Source != UserSource.Local)
        {
            return BadRequest(new { error = "not_local_account", message = "域账户请在域控制器上修改密码。" });
        }

        if (!PasswordHasher.Verify(user.PasswordHash, request.OldPassword))
        {
            return BadRequest(new { error = "bad_old_password", message = "原密码不正确。" });
        }

        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
        {
            return BadRequest(new { error = "weak_password", message = "新密码至少 8 位。" });
        }

        user.PasswordHash = PasswordHasher.Hash(request.NewPassword);
        user.MustChangePassword = false;
        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }

    // ── 外部工具 / 脚本的访问令牌 ─────────────────────────────────────
    //
    // 开启鉴权后，非浏览器的调用方没有任何登录态：拿不到 Cookie，
    // 也不在浏览器上下文里因而没有 Kerberos 票据。部署验证脚本
    // （scripts/api-smoke.sh）与自动化任务都要靠这里的令牌取得身份。
    //
    // 注意：桌面插件不在此列 —— 它只处理 officetool:// 协议、不调用接口。

    /// <summary>当前用户的令牌列表。不含明文——明文无法找回。</summary>
    [HttpGet("tokens")]
    public async Task<ActionResult<IReadOnlyList<ApiTokenDto>>> ListTokens(CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null)
        {
            return Unauthorized();
        }

        var tokens = await _tokens.ListAsync(user.Id, ct);

        return Ok(tokens.Select(x => new ApiTokenDto
        {
            Id = x.Id,
            Name = x.Name,
            CreatedAt = x.CreatedAt,
            LastUsedAt = x.LastUsedAt,
            ExpiresAt = x.ExpiresAt,
            IsRevoked = x.IsRevoked,
        }).ToList());
    }

    /// <summary>
    /// 颁发新令牌。<see cref="CreateTokenResponse.Token"/> 只在这一次返回，
    /// 之后无论谁都读不出来（库里只有 SHA-256 哈希）。
    /// </summary>
    [HttpPost("tokens")]
    public async Task<ActionResult<CreateTokenResponse>> CreateToken(
        [FromBody] CreateTokenRequest request,
        CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null)
        {
            return Unauthorized();
        }

        var name = string.IsNullOrWhiteSpace(request.Name) ? "外部工具" : request.Name.Trim();

        if (request.ExpiresAt is { } expiry && expiry <= DateTime.Now)
        {
            return BadRequest(new { error = "invalid_expiry", message = "过期时间必须晚于当前时间。" });
        }

        var (_, plain) = await _tokens.IssueAsync(user, name, request.ExpiresAt, ct);
        var created = await _db.ApiTokens
            .AsNoTracking()
            .Where(x => x.UserId == user.Id)
            .OrderByDescending(x => x.Id)
            .FirstAsync(ct);

        return Ok(new CreateTokenResponse
        {
            Id = created.Id,
            Name = created.Name,
            Token = plain,
            CreatedAt = created.CreatedAt,
            ExpiresAt = created.ExpiresAt,
        });
    }

    /// <summary>吊销令牌。保留记录以便审计，不做物理删除。</summary>
    [HttpDelete("tokens/{id:int}")]
    public async Task<IActionResult> RevokeToken(int id, CancellationToken ct)
    {
        var user = await CurrentUserAsync(ct);
        if (user is null)
        {
            return Unauthorized();
        }

        var ok = await _tokens.RevokeAsync(user.Id, id, ct);

        return ok ? NoContent() : NotFound(new { error = "token_not_found", message = "令牌不存在。" });
    }

    /// <summary>
    /// 查询当前用户在某个 项目/检项 上的有效权限，供前端按权限渲染按钮。
    /// 前端<strong>只能据此决定显示与否</strong>——真正的拦截始终在服务端。
    /// </summary>
    [HttpGet("rights")]
    public async Task<ActionResult<EffectiveRightsDto>> Rights(
        [FromQuery] string project,
        [FromQuery] string? check,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            return BadRequest(new { error = "invalid_request", message = "project 不能为空。" });
        }

        var access = await _access.ResolveAsync(User, ct);

        if (access.IsSystemAdmin)
        {
            return Ok(new EffectiveRightsDto
            {
                Visible = true,
                Level = AccessLevel.RuleManage.ToString(),
                CanRead = true,
                CanWrite = true,
                CanManage = true,
                CanManageRules = true,
            });
        }

        int? checkId = null;

        if (!string.IsNullOrWhiteSpace(check))
        {
            checkId = await _db.Checks
                .AsNoTracking()
                .Where(c => c.Project!.Name == project && c.Name == check)
                .Select(c => (int?)c.Id)
                .FirstOrDefaultAsync(ct);
        }
        else
        {
            // 只给了项目：取该项目下任一检项有权限即视为项目可见
            var ids = await _db.Checks
                .AsNoTracking()
                .Where(c => c.Project!.Name == project)
                .Select(c => c.Id)
                .ToListAsync(ct);

            var visible = ids.Any(id => access.IsVisible(id));
            var level = ids.Count == 0 ? AccessLevel.None : ids.Max(id => access.LevelOf(id));

            return Ok(new EffectiveRightsDto
            {
                Visible = visible,
                Level = level.ToString(),
                CanRead = visible,
                CanWrite = level >= AccessLevel.Write,
                CanManage = level >= AccessLevel.Manage,
                CanManageRules = level >= AccessLevel.RuleManage,
            });
        }

        var effective = access.LevelOf(checkId);

        return Ok(new EffectiveRightsDto
        {
            Visible = effective > AccessLevel.None,
            Level = effective.ToString(),
            CanRead = effective >= AccessLevel.Read,
            CanWrite = effective >= AccessLevel.Write,
            CanManage = effective >= AccessLevel.Manage,
            CanManageRules = effective >= AccessLevel.RuleManage,
        });
    }

    /// <summary>
    /// 当前用户在全部可见检项上的有效等级。前端<b>一次</b>拉全量，之后在内存里查表。
    ///
    /// 为什么不复用 <c>/api/auth/rights</c>：那个是按 项目/检项 单点询问，
    /// 左侧树有几十个节点，每次切范围都往返一次会明显卡顿。
    /// 检项总量远小于文件数，整表载入可接受。
    /// </summary>
    [HttpGet("rights/map")]
    public async Task<ActionResult<AccessMapDto>> RightsMap(CancellationToken ct)
    {
        var access = await _access.ResolveAsync(User, ct);

        var query =
            from c in _db.Checks.AsNoTracking()
            select new
            {
                c.Id,
                c.ProjectId,
                Project = c.Project!.Name,
                c.Name,
            };

        // 超管与「鉴权关闭」形态下 NeedsCheckFilter 为 false，此时可见集合是空集，
        // 直接拿去 Contains 会把所有行滤掉——必须先判这个开关。
        if (access.NeedsCheckFilter)
        {
            var visible = access.LevelByCheckId
                .Where(x => x.Value > AccessLevel.None)
                .Select(x => x.Key)
                .ToList();

            query = query.Where(c => visible.Contains(c.Id));
        }

        var rows = await query.ToListAsync(ct);

        return Ok(new AccessMapDto
        {
            AuthEnabled = _options.Enabled,
            IsSystemAdmin = access.IsSystemAdmin,
            Checks =
            [
                .. rows.Select(r => new CheckAccessDto
                {
                    CheckId = r.Id,
                    ProjectId = r.ProjectId,
                    Project = r.Project,
                    Check = r.Name,
                    // LevelOf 对超管恒为 RuleManage，前端无需再分叉
                    Level = access.LevelOf(r.Id).ToString(),
                }),
            ],
        });
    }

    /// <summary>把用户档案转成应用 Cookie 身份。</summary>
    private async Task SignInAsync(User user)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.UserName),
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
        };

        if (user.IsSystemAdmin)
        {
            claims.Add(new Claim(ClaimTypes.Role, "SystemAdmin"));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            principal,
            new AuthenticationProperties
            {
                IsPersistent = true,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(Math.Max(1, _options.CookieHours)),
            });
    }
}
