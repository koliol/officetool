using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Controllers;

/// <summary>系统配置、健康检查、元数据同步与操作日志（设计文档 §7.5、§12）。</summary>
[ApiController]
[Route("api")]
public sealed class SystemController(
    AppDbContext db,
    StorageOptions storage,
    UploadOptions upload,
    DesktopPluginOptions plugin,
    IFileStore store,
    ContentCatalogService content,
    CredentialProtector protector) : ControllerBase
{
    private readonly AppDbContext _db = db;
    private readonly StorageOptions _storage = storage;
    private readonly UploadOptions _upload = upload;
    private readonly DesktopPluginOptions _plugin = plugin;
    private readonly IFileStore _store = store;
    private readonly ContentCatalogService _content = content;
    private readonly CredentialProtector _protector = protector;

    /// <summary>
    /// 存活探针。**必须匿名可达**。
    ///
    /// 它同时被三处使用：Dockerfile 的 HEALTHCHECK、反向代理/负载均衡的探活、
    /// 以及部署脚本的就绪等待。一旦要求认证，容器会永远处于 unhealthy，
    /// compose 的 depends_on: service_healthy 会直接卡死，反代也不会转发流量。
    ///
    /// 因此这里的出参刻意保持最小：只有存活状态、服务器本地时间和存储可用性，
    /// 不含任何路径、项目名或配置内容。
    /// </summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public IActionResult Health() => Ok(new
    {
        status = "ok",
        time = DateTime.Now,
        storeAvailable = _store.IsAvailable,
    });

    [HttpGet("system/config")]
    public ActionResult<SystemConfigDto> Config() => Ok(new SystemConfigDto(
        _storage.Mode,
        _storage.TemplatesRoot,
        _storage.DataRoot,
        _storage.ClientTemplatesRoot,
        _storage.ClientDataRoot,
        _plugin.Protocol,
        _plugin.DownloadUrl,
        _upload.AllowedExtensions,
        _upload.MaxSizeMB,
        FileStoreIsRemoteUnc: _store is not OfficeTool.Core.Storage.LocalFileStore,
        FileStoreAvailable: _store.IsAvailable,
        AttachmentsRoot: _storage.AttachmentsRoot,
        ClientAttachmentsRoot: _storage.ClientAttachmentsRoot,
        AttachmentExtensions: _upload.AttachmentExtensions,
        RulesRoot: _storage.RulesRoot,
        ClientRulesRoot: _storage.ClientRulesRoot,
        RuleExtensions: _upload.RuleExtensions,
        ExtractionAvailable: AttachmentsController.ExtractionAvailable));

    /// <summary>扫描 SMB 目录修正数据库索引（设计文档 §6.2）。</summary>
    [HttpPost("system/sync")]
    public Task<SyncResult> Sync(CancellationToken ct) => _content.SyncMetadataAsync(ct);

    [HttpGet("logs")]
    public async Task<ActionResult<PagedResult<OperationLogDto>>> Logs(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 500 ? 50 : pageSize;

        var total = await _db.OperationLogs.CountAsync(ct);
        var items = await _db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new OperationLogDto(
                l.Id, l.Action, l.TargetPath, l.Ip, l.UserAgent, l.Result, l.Message, l.CreatedAt))
            .ToListAsync(ct);

        return Ok(new PagedResult<OperationLogDto>(items, total, page, pageSize));
    }

    /// <summary>
    /// 把明文密码转换为可写入 appsettings.json 的密文（生产部署用，设计文档 §7.2）。
    /// 该端点只做加解密，不落库、不写文件。
    /// </summary>
    [HttpPost("system/protect")]
    public ActionResult<object> Protect([FromBody] ProtectRequest request)
    {
        if (string.IsNullOrEmpty(request.PlainText))
        {
            throw new BadRequestException("plainText 不能为空。");
        }

        return Ok(new { encrypted = _protector.Protect(request.PlainText) });
    }

    public sealed record ProtectRequest(string PlainText);
}
