using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
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

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(uploadOptions);
builder.Services.AddSingleton(pluginOptions);

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
