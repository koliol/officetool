using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;

namespace OfficeTool.Api.Services;

/// <summary>
/// 记录上的「上传 PDF → 解析 → 得到候选字段」。
///
/// 全流程刻意是**建议式**的：解析只产出候选值与命中片段，最终写进记录的数据
/// 必须由人确认。仪器报告的版式千差万别，让机器直接落库一个看不见来源的数字，
/// 在受监管场景里是不可接受的 —— 这也是每次解析都要留审计记录的原因。
/// </summary>
public sealed class ExtractionService(
    AppDbContext db,
    PathLayout layout,
    IFileStore store,
    FormRecordService records,
    RequestContext context,
    OperationLogService logs)
{
    /// <summary>原文入库的截断长度。留够追溯用，又不至于把库撑爆。</summary>
    private const int RawTextLimit = 20_000;

    private const int PreviewLimit = 1_500;

    private readonly AppDbContext _db = db;
    private readonly PathLayout _layout = layout;
    private readonly IFileStore _store = store;
    private readonly FormRecordService _records = records;
    private readonly RequestContext _context = context;
    private readonly OperationLogService _logs = logs;

    /// <summary>当前用户在**这条记录所属范围**上可用的规则。</summary>
    public async Task<IReadOnlyList<ExtractionRuleListItemDto>> ListRulesForRecordAsync(
        int recordId,
        CancellationToken ct = default)
    {
        var current = await _records.CurrentAccessAsync(ct);
        var record = await _records.LoadRecordAsync(recordId, ct);

        FormRecordService.RequireLevel(current, record.CheckId, AccessLevel.Read, "查看可用规则");

        var rules = await LoadVisibleRulesAsync(current, record, ct);

        return [.. rules.Select(r => new ExtractionRuleListItemDto(
            r.Id,
            r.Code,
            r.Name,
            r.Category,
            r.Description,
            r.Version,
            ReadTemplateCode(r.RuleJson)))];
    }

    /// <summary>解析一次。返回候选值，不直接改记录数据。</summary>
    public async Task<ExtractRecordResultDto> RunAsync(
        int recordId,
        ExtractRecordRequest request,
        CancellationToken ct = default)
    {
        var current = await _records.CurrentAccessAsync(ct);
        var record = await _records.LoadRecordAsync(recordId, ct);

        FormRecordService.RequireLevel(current, record.CheckId, AccessLevel.Write, "执行解析");

        var rule = await _db.ExtractionRuleDefinitions.AsNoTracking()
                       .FirstOrDefaultAsync(r => r.Id == request.RuleId, ct)
                   ?? throw new NotFoundException($"规则不存在：{request.RuleId}");

        if (!rule.IsEnabled)
        {
            throw new ConflictException($"规则「{rule.Name}」已停用。");
        }

        var visible = await LoadVisibleRulesAsync(current, record, ct);
        if (visible.All(r => r.Id != rule.Id))
        {
            throw new AccessDeniedException($"规则「{rule.Name}」未授权给本记录所属的项目/检项。");
        }

        var file = await ResolveSourceFileAsync(record.Id, request.FileId, ct);

        var fileName = file.FileName;
        var fullPath = PathGuard.FromRelative(_layout.RecordsRoot, file.RelativePath);

        ExtractionRunStatus status;
        string? error = null;
        var fields = new List<ExtractedField>();
        var warnings = new List<string>();
        var rawText = string.Empty;

        try
        {
            if (!_store.FileExists(fullPath))
            {
                throw new NotFoundException($"原始文件已不在磁盘上：{fileName}");
            }

            byte[] bytes;
            using (var stream = _store.OpenRead(fullPath))
            using (var buffer = new MemoryStream())
            {
                await stream.CopyToAsync(buffer, ct);
                bytes = buffer.ToArray();
            }

            rawText = PdfTextReader.ReadText(bytes);

            if (string.IsNullOrWhiteSpace(rawText))
            {
                status = ExtractionRunStatus.Failed;
                error = "这份 PDF 没有可提取的文本层（多半是扫描件/图片）。请改用文本型 PDF，或手工填写。";
            }
            else
            {
                var ruleSet = ExtractionRule.Parse(rule.RuleJson);
                var outcome = ExtractionEngine.Run(ruleSet, rawText);

                fields = [.. outcome.Fields];
                warnings = [.. outcome.Warnings];

                // 只保留模板里真实存在的字段：规则可能比模板新，
                // 把多余的字段回给前端会让用户看到"填不进去的值"
                var schemaFields = FormSchema.Validate(record.TemplateSchemaJson).Schema?.FieldIndex()
                                   ?? new Dictionary<string, FormField>(StringComparer.OrdinalIgnoreCase);

                var unknown = fields.Where(f => !schemaFields.ContainsKey(f.FieldKey)).ToList();
                foreach (var item in unknown)
                {
                    warnings.Add($"规则抽到了模板里不存在的字段「{item.FieldLabel}」（{item.FieldKey}），已忽略。");
                }

                fields = [.. fields.Where(f => schemaFields.ContainsKey(f.FieldKey))];

                status = fields.Count > 0 ? ExtractionRunStatus.Succeeded : ExtractionRunStatus.NoMatch;

                if (status == ExtractionRunStatus.NoMatch)
                {
                    error = "规则执行完成，但没有匹配到任何字段。请确认报告版式与所选规则一致。";
                }
            }
        }
        catch (InvalidRuleException ex)
        {
            status = ExtractionRunStatus.Failed;
            error = $"规则内容非法：{ex.Message}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            status = ExtractionRunStatus.Failed;
            error = $"读取 PDF 失败：{ex.Message}";
        }

        var run = new ExtractionRun
        {
            RecordId = record.Id,
            RuleDefinitionId = rule.Id,
            RuleCode = rule.Code,
            RuleVersion = rule.Version,
            RecordFileId = file.Id,
            SourceFileName = fileName,
            Status = status,
            MatchedFieldCount = fields.Count,
            ResultJson = fields.Count == 0
                ? null
                : JsonSerializer.Serialize(fields.Select(f => new
                {
                    field = f.FieldKey,
                    label = f.FieldLabel,
                    value = f.Value,
                    snippet = f.Snippet,
                    confidence = f.Confidence,
                })),
            RawText = rawText.Length == 0 ? null : Truncate(rawText, RawTextLimit),
            ErrorMessage = error,
            CreatedAt = DateTime.Now,
            CreatedBy = _context.UserName,
        };

        _db.ExtractionRuns.Add(run);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(
            "解析记录",
            $"{record.RecordNo}/{fileName} ← {rule.Name}（{fields.Count} 个字段）",
            success: status != ExtractionRunStatus.Failed,
            message: error,
            ct: ct);

        return new ExtractRecordResultDto(
            run.Id,
            status.ToString(),
            fields.Count,
            [.. fields.Select(f => new ExtractionFieldDto(f.FieldKey, f.FieldLabel, f.Value, f.Snippet, f.Confidence))],
            warnings,
            error,
            rawText.Length <= PreviewLimit ? rawText : Truncate(rawText, PreviewLimit));
    }

    public async Task<IReadOnlyList<ExtractionRunDto>> ListRunsAsync(int recordId, CancellationToken ct = default)
    {
        var current = await _records.CurrentAccessAsync(ct);
        var record = await _records.LoadRecordAsync(recordId, ct);

        FormRecordService.RequireLevel(current, record.CheckId, AccessLevel.Read, "查看解析记录");

        return await _db.ExtractionRuns.AsNoTracking()
            .Where(r => r.RecordId == recordId)
            .OrderByDescending(r => r.Id)
            .Select(r => new ExtractionRunDto(
                r.Id,
                r.RecordId,
                r.RuleCode,
                r.RuleVersion,
                r.SourceFileName,
                r.Status.ToString(),
                r.MatchedFieldCount,
                r.ErrorMessage,
                r.CreatedAt,
                r.CreatedBy))
            .ToListAsync(ct);
    }

    // ── 内部 ────────────────────────────────────────────────────────

    private async Task<RecordFile> ResolveSourceFileAsync(int recordId, int? fileId, CancellationToken ct)
    {
        if (fileId is { } id)
        {
            return await _db.RecordFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id && f.RecordId == recordId, ct)
                   ?? throw new NotFoundException($"记录上没有这个附件：{id}");
        }

        // 没指定文件时用最近上传的那份：用户的预期就是"解析我刚传的"
        return await _db.RecordFiles.AsNoTracking()
                   .Where(f => f.RecordId == recordId)
                   .OrderByDescending(f => f.Id)
                   .FirstOrDefaultAsync(ct)
               ?? throw new BadRequestException("这条记录还没有上传任何原始文件。");
    }

    /// <summary>
    /// 规则可见性：启用 + （超管看全部 / 非超管要求范围命中）。
    /// 范围命中 = 记录所在检项被单独授权，或整个项目被授权。
    /// </summary>
    private async Task<List<ExtractionRuleDefinition>> LoadVisibleRulesAsync(
        UserAccess current,
        FormRecord record,
        CancellationToken ct)
    {
        var enabled = await _db.ExtractionRuleDefinitions.AsNoTracking()
            .Where(r => r.IsEnabled)
            .OrderBy(r => r.Category)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        if (current.IsSystemAdmin)
        {
            return enabled;
        }

        var scopedRuleIds = await _db.ExtractionRuleScopes.AsNoTracking()
            .Where(s => s.ProjectId == record.ProjectId &&
                        (s.CheckId == null || s.CheckId == record.CheckId))
            .Select(s => s.RuleId)
            .Distinct()
            .ToListAsync(ct);

        return [.. enabled.Where(r => scopedRuleIds.Contains(r.Id))];
    }

    private static string ReadTemplateCode(string ruleJson)
    {
        try
        {
            using var document = JsonDocument.Parse(ruleJson);

            return document.RootElement.TryGetProperty("templateCode", out var code) &&
                   code.ValueKind == JsonValueKind.String
                ? code.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : text[..limit] + "\n…（已截断）";
}
