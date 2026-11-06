using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;

namespace OfficeTool.Api.Services;

/// <summary>
/// ELN 记录：创建、填草稿、提交、作废、挂原始文件。
///
/// 状态机（这是 ELN 与普通表单最大的区别）：
/// <code>
/// Draft --提交--> Submitted --作废--> Voided
///   ^                                   |
///   +----------- 作废后不可复活 ---------+
/// </code>
/// 提交后数据**不可再改**：要改只能作废重开。这一条不能靠前端拦，
/// 记录一旦被引用（审计、报告、放行结论），"事后改数"就是最严重的问题。
/// </summary>
public sealed class FormRecordService(
    AppDbContext db,
    ProjectCatalogService catalog,
    PathLayout layout,
    IFileStore store,
    UploadOptions upload,
    RequestContext context,
    IAccessControlService access,
    OperationLogService logs,
    IHttpContextAccessor accessor)
{
    /// <summary>记录内容上限。正常表单远小于此值；超了多半是粘贴了整篇文档。</summary>
    private const int MaxDataBytes = 1024 * 1024;

    /// <summary>记录附件的扩展名白名单：原始报告常见形态。</summary>
    private static readonly string[] AllowedFileExtensions =
        [".pdf", ".jpg", ".jpeg", ".png", ".tif", ".tiff"];

    private readonly AppDbContext _db = db;
    private readonly ProjectCatalogService _catalog = catalog;
    private readonly PathLayout _layout = layout;
    private readonly IFileStore _store = store;
    private readonly UploadOptions _upload = upload;
    private readonly RequestContext _context = context;
    private readonly IAccessControlService _access = access;
    private readonly OperationLogService _logs = logs;
    private readonly IHttpContextAccessor _accessor = accessor;

    // ── 查询 ────────────────────────────────────────────────────────

    public async Task<PagedResult<FormRecordListItemDto>> ListAsync(
        string? project,
        string? check,
        string? status,
        string? keyword,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);

        var rows =
            from r in _db.FormRecords.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on r.ProjectId equals p.Id
            join c in _db.Checks.AsNoTracking() on r.CheckId equals c.Id
            select new
            {
                Record = r,
                Project = p.Name,
                Check = c.Name,
            };

        // 可见性下推 SQL：超管/关闭鉴权时 NeedsCheckFilter=false，完全不加条件
        if (current.NeedsCheckFilter)
        {
            var allowed = current.AllowedCheckIds.ToList();
            rows = rows.Where(x => allowed.Contains(x.Record.CheckId));
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            var name = project.Trim().ToLower();
            rows = rows.Where(x => x.Project.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(check))
        {
            var name = check.Trim().ToLower();
            rows = rows.Where(x => x.Check.ToLower() == name);
        }

        if (!string.IsNullOrWhiteSpace(status) &&
            Enum.TryParse<RecordStatus>(status, ignoreCase: true, out var parsedStatus))
        {
            rows = rows.Where(x => x.Record.Status == parsedStatus);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            rows = rows.Where(x =>
                x.Record.RecordNo.ToLower().Contains(kw) ||
                x.Record.Title.ToLower().Contains(kw) ||
                x.Record.TemplateName.ToLower().Contains(kw) ||
                x.Record.CreatedBy!.ToLower().Contains(kw));
        }

        var total = await rows.CountAsync(ct);
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 20 : pageSize;

        var items = await rows
            .OrderByDescending(x => x.Record.CreatedAt)
            .ThenBy(x => x.Record.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new
            {
                x.Record,
                x.Project,
                x.Check,
                FileCount = _db.RecordFiles.Count(f => f.RecordId == x.Record.Id),
            })
            .ToListAsync(ct);

        var dtos = items.Select(x => new FormRecordListItemDto(
            x.Record.Id,
            x.Record.RecordNo,
            x.Record.Title,
            x.Record.TemplateCode,
            x.Record.TemplateName,
            x.Record.TemplateVersion,
            x.Record.ProjectId,
            x.Project,
            x.Record.CheckId,
            x.Check,
            x.Record.Status.ToString(),
            x.Record.CreatedBy,
            x.Record.CreatedAt,
            x.Record.UpdatedAt,
            x.Record.SubmittedAt,
            x.FileCount)).ToList();

        return new PagedResult<FormRecordListItemDto>(dtos, total, page, pageSize);
    }

    public async Task<FormRecordDto> GetAsync(int id, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);

        RequireLevel(current, record.CheckId, AccessLevel.Read, "查看记录");

        var project = await _db.Projects.AsNoTracking().FirstAsync(p => p.Id == record.ProjectId, ct);
        var check = await _db.Checks.AsNoTracking().FirstAsync(c => c.Id == record.CheckId, ct);

        var files = await _db.RecordFiles.AsNoTracking()
            .Where(f => f.RecordId == id)
            .OrderBy(f => f.Id)
            .ToListAsync(ct);

        return new FormRecordDto(
            record.Id,
            record.RecordNo,
            record.Title,
            record.TemplateCode,
            record.TemplateName,
            record.TemplateVersion,
            record.ProjectId,
            project.Name,
            record.CheckId,
            check.Name,
            record.Status.ToString(),
            ParseJson(record.TemplateSchemaJson),
            ParseJson(record.DataJson),
            record.CreatedBy,
            record.UpdatedBy,
            record.CreatedAt,
            record.UpdatedAt,
            record.SubmittedAt,
            record.SubmittedBy,
            record.VoidedAt,
            record.VoidReason,
            [.. files.Select(ToFileDto)]);
    }

    // ── 写 ──────────────────────────────────────────────────────────

    public async Task<FormRecordDto> CreateAsync(CreateFormRecordRequest request, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);

        RequireLevel(current, check.Id, AccessLevel.Write, "新建记录");

        var template = await _db.FormTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId, ct)
                       ?? throw new NotFoundException($"模板不存在：{request.TemplateId}");

        if (!template.IsEnabled)
        {
            throw new ConflictException($"模板「{template.Name}」已停用，不能用于新建记录。");
        }

        var actor = _context.UserName;
        var now = DateTime.Now;
        var title = string.IsNullOrWhiteSpace(request.Title) ? template.Name : request.Title.Trim();

        // 记录号在并发下可能撞车，交给唯一索引兜底并重试
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var record = new FormRecord
            {
                TemplateId = template.Id,
                TemplateCode = template.Code,
                TemplateName = template.Name,
                TemplateVersion = template.Version,
                TemplateSchemaJson = template.SchemaJson,
                ProjectId = project.Id,
                CheckId = check.Id,
                RecordNo = await NextRecordNoAsync(project.Name, check.Name, ct),
                Title = title,
                Status = RecordStatus.Draft,
                DataJson = "{}",
                CreatedBy = actor,
                UpdatedBy = actor,
                CreatedAt = now,
                UpdatedAt = now,
            };

            _db.FormRecords.Add(record);

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (attempt < 4)
            {
                // 记录号撞车（并发创建）：丢回本地实体，重算号再试
                _db.Entry(record).State = EntityState.Detached;
                continue;
            }

            await _logs.WriteAsync("新建记录", $"{record.RecordNo}（{template.Name}）", success: true, ct: ct);

            return await GetAsync(record.Id, ct);
        }

        throw new ConflictException("生成记录号连续冲突，请稍后重试。");
    }

    public async Task<FormRecordDto> SaveDraftAsync(int id, JsonElement data, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);
        var level = RequireLevel(current, record.CheckId, AccessLevel.Write, "编辑记录");

        EnsureEditable(record, level);

        var json = NormalizeData(data);
        var schema = FormSchema.Validate(record.TemplateSchemaJson).Schema;

        // 草稿只校验"填了的部分必须合法"，允许半成品
        var errors = RecordDataValidator.Validate(schema, data, requireComplete: false);

        if (errors.Count > 0)
        {
            throw new BadRequestException(string.Join("；", errors));
        }

        record.DataJson = json;
        record.UpdatedAt = DateTime.Now;
        record.UpdatedBy = _context.UserName;

        await _db.SaveChangesAsync(ct);

        return await GetAsync(id, ct);
    }

    public async Task<FormRecordDto> SubmitAsync(int id, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);
        var level = RequireLevel(current, record.CheckId, AccessLevel.Write, "提交记录");

        EnsureEditable(record, level);

        var schema = FormSchema.Validate(record.TemplateSchemaJson).Schema;

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(record.DataJson) ? "{}" : record.DataJson);
        var errors = RecordDataValidator.Validate(schema, document.RootElement, requireComplete: true);

        if (errors.Count > 0)
        {
            throw new BadRequestException("提交前请先补全：" + string.Join("；", errors));
        }

        record.Status = RecordStatus.Submitted;
        record.SubmittedAt = DateTime.Now;
        record.SubmittedBy = _context.UserName;
        record.UpdatedAt = DateTime.Now;
        record.UpdatedBy = _context.UserName;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("提交记录", record.RecordNo, success: true, ct: ct);

        return await GetAsync(id, ct);
    }

    public async Task<FormRecordDto> VoidAsync(int id, string reason, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);

        // 作废是不可逆的严肃动作：要求管理级权限，且必须写原因
        RequireLevel(current, record.CheckId, AccessLevel.Manage, "作废记录");

        if (record.Status == RecordStatus.Voided)
        {
            throw new ConflictException("该记录已经作废。");
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new BadRequestException("作废必须填写原因。");
        }

        if (reason.Length > 500)
        {
            throw new BadRequestException("作废原因不能超过 500 字。");
        }

        record.Status = RecordStatus.Voided;
        record.VoidedAt = DateTime.Now;
        record.VoidReason = reason.Trim();
        record.UpdatedAt = DateTime.Now;
        record.UpdatedBy = _context.UserName;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("作废记录", $"{record.RecordNo}：{record.VoidReason}", success: true, ct: ct);

        return await GetAsync(id, ct);
    }

    // ── 记录附件 ────────────────────────────────────────────────────

    public async Task<RecordFileDto> AddFileAsync(
        int id,
        string fileName,
        Stream content,
        long declaredSize,
        string? contentType,
        CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);
        var level = RequireLevel(current, record.CheckId, AccessLevel.Write, "上传记录附件");

        EnsureEditable(record, level);

        var safeName = NameValidator.ValidateFileName(Path.GetFileName(fileName));
        var extension = Path.GetExtension(safeName);

        if (!AllowedFileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            // 抛"扩展名不允许"而不是通用 400：前端要能据此提示"这个格式不行"
            throw new ExtensionNotAllowedException(
                $"{extension}（记录附件只接受 {string.Join(" / ", AllowedFileExtensions)}）");
        }

        if (declaredSize > _upload.MaxSizeBytes)
        {
            throw new PayloadTooLargeException($"文件超过大小上限 {_upload.MaxSizeMB}MB。");
        }

        var project = await _db.Projects.AsNoTracking().FirstAsync(p => p.Id == record.ProjectId, ct);
        var check = await _db.Checks.AsNoTracking().FirstAsync(c => c.Id == record.CheckId, ct);

        var directory = _layout.RecordDirectory(project.Name, check.Name, record.RecordNo);
        _store.CreateDirectory(directory);

        var targetName = UniqueFileName(directory, safeName);
        var fullPath = PathGuard.CombineUnderRoot(directory, targetName);

        var meta = await StagedUpload.WriteAsync(
            _store,
            directory,
            fullPath,
            content,
            _upload.MaxSizeBytes,
            $"文件超过大小上限 {_upload.MaxSizeMB}MB。",
            ct);

        var file = new RecordFile
        {
            RecordId = record.Id,
            Kind = RecordFileKind.Source,
            FileName = targetName,
            RelativePath = PathGuard.ToRelative(_layout.RecordsRoot, fullPath),
            Extension = extension,
            Size = meta.Size,
            ContentType = contentType ?? string.Empty,
            UploadedAt = DateTime.Now,
            UploadedBy = _context.UserName,
        };

        _db.RecordFiles.Add(file);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("上传记录附件", $"{record.RecordNo}/{targetName}", success: true, ct: ct);

        return ToFileDto(file);
    }

    public async Task DeleteFileAsync(int id, int fileId, CancellationToken ct = default)
    {
        var current = await CurrentAccessAsync(ct);
        var record = await LoadRecordAsync(id, ct);
        var level = RequireLevel(current, record.CheckId, AccessLevel.Write, "删除记录附件");

        EnsureEditable(record, level);

        var file = await _db.RecordFiles.FirstOrDefaultAsync(f => f.Id == fileId && f.RecordId == id, ct)
                   ?? throw new NotFoundException($"附件不存在：{fileId}");

        var fullPath = PathGuard.FromRelative(_layout.RecordsRoot, file.RelativePath);

        _db.RecordFiles.Remove(file);
        await _db.SaveChangesAsync(ct);

        try
        {
            if (_store.FileExists(fullPath))
            {
                _store.DeleteFile(fullPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 文件删不掉不影响业务：留下的是可清理的孤儿文件，不该让用户看到 500
            await _logs.WriteAsync("删除记录附件（文件残留）", file.RelativePath, success: false, message: ex.Message, ct: ct);
        }

        await _logs.WriteAsync("删除记录附件", $"{record.RecordNo}/{file.FileName}", success: true, ct: ct);
    }

    // ── 内部 ────────────────────────────────────────────────────────

    internal async Task<FormRecord> LoadRecordAsync(int id, CancellationToken ct)
    {
        return await _db.FormRecords.FirstOrDefaultAsync(r => r.Id == id, ct)
               ?? throw new NotFoundException($"记录不存在：{id}");
    }

    internal async Task<UserAccess> CurrentAccessAsync(CancellationToken ct) =>
        await _access.ResolveAsync(_accessor.HttpContext?.User, ct);

    /// <summary>校验权限并返回实际等级（调用方常需要判断是否 Manage）。</summary>
    internal static AccessLevel RequireLevel(UserAccess current, int checkId, AccessLevel required, string action)
    {
        var level = current.IsSystemAdmin ? AccessLevel.RuleManage : current.LevelOf(checkId);

        if (level < required)
        {
            throw new AccessDeniedException($"{action}需要「{required}」级别权限，当前为「{level}」。");
        }

        return level;
    }

    /// <summary>
    /// 只有草稿能改。管理级权限也不能改已提交的记录 ——
    /// 否则"提交"这个动作就没有意义了，要改只能作废重开。
    /// </summary>
    private static void EnsureEditable(FormRecord record, AccessLevel level)
    {
        if (record.Status == RecordStatus.Draft)
        {
            return;
        }

        if (record.Status == RecordStatus.Voided)
        {
            throw new ConflictException("记录已作废，不能再修改。");
        }

        throw new ConflictException(
            level >= AccessLevel.Manage
                ? "记录已提交，数据不可修改。如需更正，请作废后重新创建。"
                : "记录已提交，不能修改。请联系有管理权限的同事处理。");
    }

    private static string NormalizeData(JsonElement data)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return "{}";
        }

        if (data.ValueKind != JsonValueKind.Object)
        {
            throw new BadRequestException("记录内容必须是 JSON 对象。");
        }

        var json = data.GetRawText();

        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxDataBytes)
        {
            throw new BadRequestException($"记录内容过大（上限 {MaxDataBytes / 1024} KB）。");
        }

        return json;
    }

    private static JsonElement ParseJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return JsonSerializer.SerializeToElement(new { });
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new { });
        }
    }

    private async Task<string> NextRecordNoAsync(string project, string check, CancellationToken ct)
    {
        var prefix = $"{project}-{check}-{DateTime.Now:yyyyMMdd}";

        var last = await _db.FormRecords.AsNoTracking()
            .Where(r => r.RecordNo.StartsWith(prefix))
            .OrderByDescending(r => r.RecordNo)
            .Select(r => r.RecordNo)
            .FirstOrDefaultAsync(ct);

        var next = 1;

        if (last is not null)
        {
            var tail = last[(last.LastIndexOf('-') + 1)..];
            if (int.TryParse(tail, out var parsed))
            {
                next = parsed + 1;
            }
        }

        return $"{prefix}-{next:000}";
    }

    private string UniqueFileName(string directory, string fileName)
    {
        if (!FileNames.ExistsIgnoringCase(_store, directory, fileName))
        {
            return fileName;
        }

        var baseName = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var index = 2; index < 999; index++)
        {
            var candidate = $"{baseName}_{index}{extension}";

            if (!FileNames.ExistsIgnoringCase(_store, directory, candidate))
            {
                return candidate;
            }
        }

        throw new ConflictException($"无法为 {fileName} 生成唯一文件名。");
    }

    private static RecordFileDto ToFileDto(RecordFile file) => new(
        file.Id,
        file.FileName,
        file.Extension,
        file.Size,
        file.Kind.ToString(),
        file.UploadedAt,
        file.UploadedBy);
}
