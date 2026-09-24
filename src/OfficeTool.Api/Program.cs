using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Controllers;
using OfficeTool.Api.Data;
using OfficeTool.Api.Middleware;
using OfficeTool.Api.Services;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── 日志（设计文档 §12：文件日志按天滚动） ─────────────────────────────
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .WriteTo.File(
        Path.Combine(AppContext.BaseDirectory, "logs", "officetool-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 31));

// ── 配置绑定 ───────────────────────────────────────────────────────────
var storageOptions = new StorageOptions();
builder.Configuration.GetSection(StorageOptions.SectionName).Bind(storageOptions);
storageOptions.WithDefaults();

var uploadOptions = new UploadOptions();
builder.Configuration.GetSection(UploadOptions.SectionName).Bind(uploadOptions);
uploadOptions.WithDefaults();

var pluginOptions = new DesktopPluginOptions();
builder.Configuration.GetSection(DesktopPluginOptions.SectionName).Bind(pluginOptions);

var authOptions = new AuthOptions();
builder.Configuration.GetSection(AuthOptions.SectionName).Bind(authOptions);

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(uploadOptions);
builder.Services.AddSingleton(pluginOptions);
builder.Services.AddSingleton(authOptions);

// ── 请求体大小上限（上传 100MB，留出余量） ────────────────────────────
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = uploadOptions.MaxSizeBytes + (16L * 1024 * 1024);
});

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = uploadOptions.MaxSizeBytes + (16L * 1024 * 1024);
});

// ── 服务注册 ───────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddHttpContextAccessor();
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddMemoryCache();

// 转发用 scheme 名。只在这个文件里用，不对外暴露。
const string AdaptiveScheme = "Adaptive";

// ── 鉴权 ──────────────────────────────────────────────────────────────
//
// 四条通道：
//   1. Cookie（浏览器默认）—— SSO 与本地账户最终都落到这张票，后续请求只认它
//   2. Negotiate —— 仅挂在 GET /api/auth/sso 上，成功后转签 Cookie
//   3. ApiToken —— Bearer 令牌，供外部工具 / 脚本使用（桌面插件不需要）
//   4. 全部关闭 —— Auth:Enabled=false，回到内网可信形态
//
// 为什么不让 Negotiate 做默认方案：它每个请求都要 401 握手，
// 且 EnableLdap 的组查询会随嵌套组数量放大延迟。
//
// 默认 scheme 是一个转发器：带 Authorization 头的请求交给 ApiToken，
// 其余交给 Cookie。二者的 principal 用同一套 ClaimTypes.Name，
// 下游 IAccessControlService 无需区分请求来自浏览器还是插件。
builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultScheme = AdaptiveScheme;
        options.DefaultChallengeScheme = AdaptiveScheme;
    })
    .AddPolicyScheme(AdaptiveScheme, "Cookie 或 ApiToken", options =>
    {
        options.ForwardDefaultSelector = context =>
            context.Request.Headers.ContainsKey(Microsoft.Net.Http.Headers.HeaderNames.Authorization)
                ? ApiTokenDefaults.Scheme
                : CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
        ApiTokenDefaults.Scheme, options => { })
    .AddCookie(options =>
    {
        options.Cookie.Name = "OfficeTool.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(Math.Max(1, authOptions.CookieHours));

        // 接口性质：未认证返回 401，而不是重定向到登录页（前端自己决定跳哪）
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddNegotiate(options =>
    {
        // Linux 上的 Kerberos 不返回任何角色/组信息，必须显式查 LDAP 才能拿到组。
        // 这是能否实现「按域分组授权」的前提，漏掉则所有人都没有任何授权。
        if (OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(authOptions.Domain))
        {
            return;
        }

        // 一律用 settings 重载：IgnoreNestedGroups 只在 LdapSettings 上，
        // 简单的 EnableLdap(domain) 重载没有它。
        options.EnableLdap(settings =>
        {
            settings.Domain = authOptions.Domain;

            // 域很大或嵌套很深时，递归解析嵌套组会显著拖慢登录
            settings.IgnoreNestedGroups = authOptions.IgnoreNestedGroups;

            // 不配机器账号时用已认证用户自身的上下文查 LDAP（多数域够用）。
            // 域禁用了匿名/用户上下文查询时才需要显式机器账号。
            if (!string.IsNullOrWhiteSpace(authOptions.LdapMachineAccountName))
            {
                settings.MachineAccountName = authOptions.LdapMachineAccountName;
                settings.MachineAccountPassword = authOptions.LdapMachineAccountPassword;
            }
        });
    });

builder.Services.AddAuthorization(options =>
{
    // 它只是粗筛：真正的判定在 AdminService.RequireAdminAsync 里读数据库。
    // 声明在签发时就固定了，管理员刚改完别人的超管位时声明不会同步变化。
    if (authOptions.Enabled)
    {
        options.AddPolicy(AdminPolicy.Name, policy => policy.RequireRole(AdminPolicy.Role));
    }
    else
    {
        // 鉴权关闭时必须让请求<strong>穿过</strong>策略到达 AdminService，
        // 由它返回「鉴权未启用」的 409 提示。否则管理员只会看到一个
        // 没有任何解释的 401 —— 而在这个形态下没有人能通过认证。
        options.AddPolicy(AdminPolicy.Name, policy => policy.RequireAssertion(_ => true));
    }

    if (!authOptions.Enabled)
    {
        // 内网可信形态：不设 FallbackPolicy，等价于全部放行。
        // 适用未接入域、且确认只有可信内网可达的部署；关闭后任何人都是管理员，
        // 前提是该部署绝不暴露到公网。
        return;
    }

    // 默认要求已认证。/api/auth/* 由各自的 [AllowAnonymous] 放行，静态资源在认证之前处理。
    // 不加 FallbackPolicy 的话，忘记标 [Authorize] 的新控制器会静默裸奔。
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

builder.Services.AddScoped<IAccessControlService, AccessControlService>();
builder.Services.AddScoped<UserDirectoryService>();
builder.Services.AddScoped<ApiTokenService>();
builder.Services.AddScoped<AdminService>();

// Data Protection：Docker/群晖部署时必须把密钥环放到持久卷上，
// 否则容器重建后 Smb/Storage:EncryptedPassword 将无法解密。
var keysPath = builder.Configuration["DataProtection:KeysPath"];
var dataProtection = builder.Services.AddDataProtection().SetApplicationName("OfficeTool");

if (!string.IsNullOrWhiteSpace(keysPath))
{
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Default") ?? "Data Source=officetool.db";
    options.UseSqlite(connectionString);
});

// 文件存储：Mode=Unc 且主机为 Windows → UncFileStore（UNC + 凭据模拟）；
// 其余情况（群晖 bind mount、本地开发）→ LocalFileStore，直接读写挂载进来的路径。
builder.Services.AddSingleton<LocalFileStore>();
builder.Services.AddSingleton<IFileStore>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();

    if (!storageOptions.IsUnc)
    {
        logger.LogInformation(
            "存储模式 Local：模板根={TemplatesRoot}，数据根={DataRoot}",
            storageOptions.TemplatesRoot, storageOptions.DataRoot);

        return sp.GetRequiredService<LocalFileStore>();
    }

    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformUnsupportedException(
            "Storage:Mode=Unc 需要 Windows 主机（依赖 mpr.dll）。" +
            "群晖/Linux 部署请使用 Mode=Local 并把共享文件夹 bind mount 进容器。");
    }

    logger.LogInformation(
        "存储模式 Unc：共享账号={Domain}\\{UserName}，模板根={TemplatesRoot}",
        storageOptions.Domain, storageOptions.UserName, storageOptions.TemplatesRoot);

    return new UncFileStore(storageOptions, sp.GetRequiredService<LocalFileStore>());
});

builder.Services.AddSingleton<PathLayout>(sp => new PathLayout(sp.GetRequiredService<StorageOptions>()));
builder.Services.AddSingleton<DocumentNamingService>();

builder.Services.AddScoped<RequestContext>();
builder.Services.AddScoped<CredentialProtector>();
builder.Services.AddScoped<OperationLogService>();
builder.Services.AddScoped<ProjectCatalogService>();
builder.Services.AddScoped<ContentCatalogService>();
builder.Services.AddScoped<AttachmentService>();
builder.Services.AddScoped<RuleCatalogService>();
builder.Services.AddScoped<TrashService>();

// 开发期内网联调放行所有来源（设计文档 §11 可选 IP 白名单留待二期）
builder.Services.AddCors(options => options.AddPolicy("internal", policy => policy
    .AllowAnyOrigin()
    .AllowAnyHeader()
    .AllowAnyMethod()));

var app = builder.Build();

// ── 启动期：解密凭据、建库、校验存储根 ────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var protector = scope.ServiceProvider.GetRequiredService<CredentialProtector>();
    storageOptions.DecryptedPassword = CredentialProtector.ResolvePassword(storageOptions, protector, app.Logger);

    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // EF Migrations + 旧 EnsureCreated 库 Baseline（替代原先的 EnsureCreated + 手写补表）
    await DatabaseInitializer.InitializeAsync(db, app.Logger);

    // 首次部署引导：Users 表为空时创建本地超管。
    // 必须有这个逃生舱——ACL 配错导致全员无权限时，仍要有人能进去修。
    if (authOptions.Enabled)
    {
        var directory = scope.ServiceProvider.GetRequiredService<UserDirectoryService>();
        var bootstrapped = await directory.EnsureBootstrapAdminAsync();

        if (bootstrapped is not null)
        {
            app.Logger.LogWarning(
                "首次部署：本地超级管理员已创建。初始密码仅在本次启动日志中出现一次，请立即登录并修改。");
        }
    }

    // 主动校验存储根是否可见/可写。群晖上最常见的事故就是卷没映射对，
    // 与其等用户上传时报错，不如启动就喊出来。
    var fileStore = scope.ServiceProvider.GetRequiredService<IFileStore>();
    foreach (var (label, root) in new[]
             {
                 ("模板根", storageOptions.TemplatesRoot),
                 ("数据根", storageOptions.DataRoot),
                 ("附件根", storageOptions.AttachmentsRoot),
                 ("规则根", storageOptions.RulesRoot),
                 ("回收站根", storageOptions.TrashRoot),
             })
    {
        try
        {
            fileStore.CreateDirectory(root);
            var probe = Path.Combine(root, $".officetool-write-probe-{Guid.NewGuid():N}");

            using (fileStore.CreateNew(probe))
            {
                // 仅探测可写性
            }

            fileStore.DeleteFile(probe);
            app.Logger.LogInformation("{Label} 校验通过（可读写）：{Root}", label, root);
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex,
                "{Label} 不可用：{Root}（当前运行用户 {User}）。\n" +
                "  群晖上最常见的原因：共享文件夹用的是 Synology ACL（ls 里的 '+'），\n" +
                "  容器运行用户没有 ACL 身份。解决：SSH 执行 `id 你的用户名` 拿到 uid:gid，\n" +
                "  写入 .env 的 PUID / PGID，再 `docker compose up -d`。\n" +
                "  其次检查卷映射与容器内路径是否一致（应挂到 /data）。",
                label, root, DescribeCurrentUser());
        }
    }
}

app.UseSerilogRequestLogging();

// 注意：异常中间件必须在显式 UseRouting 之前，
// 否则路由阶段的异常（如端点歧义）会绕过它、直接落到开发者异常页。
app.UseMiddleware<ApiExceptionMiddleware>();
app.UseRouting();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors("internal");

// 单机部署时直接托管前端构建产物（wwwroot）
app.UseDefaultFiles();
app.UseStaticFiles();

// 认证/授权放在静态文件之后：SPA 的 index.html 与静态资源必须匿名可取，
// 否则未登录时连登录页都打不开。/api/* 不受影响，仍会被 FallbackPolicy 拦截。
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// 未知的 /api/* 必须返回 JSON 404，不能被 SPA 回退吞成 HTML
app.MapFallback("/api/{**path}", () =>
    Results.Json(new OfficeTool.Api.Contracts.ApiError("not_found", "接口不存在。"), statusCode: 404));

// SPA 回退：非 /api 的非文件请求交回 index.html
app.MapFallbackToFile("index.html");

app.Logger.LogInformation(
    "OfficeTool 启动完成。存储模式={Mode}，存储类型={StoreType}\n" +
    "  服务端模板根={TemplatesRoot}\n  服务端数据根={DataRoot}\n  服务端附件根={AttachmentsRoot}\n  服务端规则根={RulesRoot}\n" +
    "  客户端模板根={ClientTemplatesRoot}\n  客户端数据根={ClientDataRoot}\n  客户端附件根={ClientAttachmentsRoot}\n  客户端规则根={ClientRulesRoot}",
    storageOptions.Mode,
    app.Services.GetRequiredService<IFileStore>().GetType().Name,
    storageOptions.TemplatesRoot,
    storageOptions.DataRoot,
    storageOptions.AttachmentsRoot,
    storageOptions.RulesRoot,
    storageOptions.ClientTemplatesRoot,
    storageOptions.ClientDataRoot,
    storageOptions.ClientAttachmentsRoot,
    storageOptions.ClientRulesRoot);

app.Run();

/// <summary>
/// 描述当前进程的运行用户，用于存储根不可写时的诊断日志。
/// Linux 下读 /proc/self/status 拿到真实 uid —— 群晖上这个值就是排错的关键。
/// </summary>
static string DescribeCurrentUser()
{
    try
    {
        if (!OperatingSystem.IsWindows() && File.Exists("/proc/self/status"))
        {
            foreach (var line in File.ReadLines("/proc/self/status"))
            {
                if (line.StartsWith("Uid:", StringComparison.Ordinal))
                {
                    var parts = line.Split('\t', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length > 1)
                    {
                        return $"uid={parts[1].Trim()}";
                    }
                }
            }
        }
    }
    catch (IOException)
    {
        // 读不到就退回用户名
    }

    return Environment.UserName;
}

/// <summary>供集成测试引用。</summary>
public partial class Program;
