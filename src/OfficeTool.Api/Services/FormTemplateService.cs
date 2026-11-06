using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// ELN 表单模板。
///
/// 权限口径：模板是**全公司共享的标准**，改一份所有人填的记录都会受影响，
/// 所以写操作要求「规则管理」级权限（与旧版规则维护同一档），
/// 而不是"谁都能建"。读只要登录即可 —— 选模板填记录是所有人的日常。
/// </summary>
public sealed partial class FormTemplateService(
    AppDbContext db,
    RequestContext context,
    IAccessControlService access,
    OperationLogService logs,
    IHttpContextAccessor accessor)
{
    private readonly AppDbContext _db = db;
    private readonly RequestContext _context = context;
    private readonly IAccessControlService _access = access;
    private readonly OperationLogService _logs = logs;
    private readonly IHttpContextAccessor _accessor = accessor;

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{2,63}$")]
    private static partial Regex CodePattern();

    public async Task<IReadOnlyList<FormTemplateListItemDto>> ListAsync(
        string? keyword,
        string? category,
        bool includeDisabled,
        CancellationToken ct = default)
    {
        var query = _db.FormTemplates.AsNoTracking();

        if (!includeDisabled)
        {
            query = query.Where(t => t.IsEnabled);
        }

        if (!string.IsNullOrWhiteSpace(category))
        {
            query = query.Where(t => t.Category == category);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLower();
            query = query.Where(t =>
                t.Name.ToLower().Contains(kw) ||
                t.Code.ToLower().Contains(kw) ||
                t.Description.ToLower().Contains(kw));
        }

        var templates = await query.OrderBy(t => t.Category).ThenBy(t => t.Name).ToListAsync(ct);

        // 记录数一次查完：模板数量是几十量级，按模板逐个 count 会变成 N+1
        var recordCounts = await _db.FormRecords.AsNoTracking()
            .GroupBy(r => r.TemplateId)
            .Select(g => new { TemplateId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TemplateId, x => x.Count, ct);

        return [.. templates.Select(t => new FormTemplateListItemDto(
            t.Id,
            t.Code,
            t.Name,
            t.Category,
            t.Description,
            t.Version,
            t.IsEnabled,
            t.IsBuiltIn,
            CountFields(t.SchemaJson),
            recordCounts.GetValueOrDefault(t.Id),
            t.UpdatedAt,
            t.UpdatedBy))];
    }

    public async Task<FormTemplateDto> GetAsync(int id, CancellationToken ct = default)
    {
        var template = await _db.FormTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw new NotFoundException($"模板不存在：{id}");

        return ToDto(template);
    }

    public async Task<FormTemplateDto> CreateAsync(SaveFormTemplateRequest request, CancellationToken ct = default)
    {
        var actor = await RequireTemplateMaintainerAsync("新建模板", ct);

        var code = NormalizeCode(request.Code, request.Name);
        var name = RequireName(request.Name);

        if (await _db.FormTemplates.AnyAsync(t => t.Code == code, ct))
        {
            throw new ConflictException($"模板编码已被占用：{code}");
        }

        var schemaJson = NormalizeSchemaJson(request.Schema);

        var validation = FormSchema.Validate(schemaJson);
        if (!validation.IsValid)
        {
            throw new BadRequestException("表单定义不合法：" + string.Join("；", validation.Errors));
        }

        var now = DateTime.Now;

        var template = new FormTemplate
        {
            Code = code,
            Name = name,
            Category = request.Category?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            Version = 1,
            SchemaJson = schemaJson,
            IsEnabled = request.IsEnabled ?? true,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actor,
        };

        _db.FormTemplates.Add(template);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("新建表单模板", $"{template.Code}（{template.Name}）", success: true, ct: ct);

        return ToDto(template);
    }

    public async Task<FormTemplateDto> UpdateAsync(int id, SaveFormTemplateRequest request, CancellationToken ct = default)
    {
        var actor = await RequireTemplateMaintainerAsync("修改模板", ct);

        var template = await _db.FormTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw new NotFoundException($"模板不存在：{id}");

        var name = RequireName(request.Name);
        var schemaJson = NormalizeSchemaJson(request.Schema);

        var validation = FormSchema.Validate(schemaJson);
        if (!validation.IsValid)
        {
            throw new BadRequestException("表单定义不合法：" + string.Join("；", validation.Errors));
        }

        // 只有结构真的变了才升版本：改名/改描述不该让所有旧记录"跨版本"
        var schemaChanged = !JsonNode.DeepEquals(
            JsonNode.Parse(template.SchemaJson),
            JsonNode.Parse(schemaJson));

        template.Name = name;
        template.Category = request.Category?.Trim() ?? template.Category;
        template.Description = request.Description?.Trim() ?? template.Description;
        template.UpdatedAt = DateTime.Now;
        template.UpdatedBy = actor;

        if (request.IsEnabled is { } enabled)
        {
            template.IsEnabled = enabled;
        }

        if (schemaChanged)
        {
            template.SchemaJson = schemaJson;
            template.Version += 1;
        }

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(
            "修改表单模板",
            $"{template.Code} v{template.Version}{(schemaChanged ? "（结构变更，版本+1）" : "")}",
            success: true,
            ct: ct);

        return ToDto(template);
    }

    public async Task<FormTemplateDto> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        var actor = await RequireTemplateMaintainerAsync(enabled ? "启用模板" : "停用模板", ct);

        var template = await _db.FormTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw new NotFoundException($"模板不存在：{id}");

        template.IsEnabled = enabled;
        template.UpdatedAt = DateTime.Now;
        template.UpdatedBy = actor;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(enabled ? "启用表单模板" : "停用表单模板", template.Code, success: true, ct: ct);

        return ToDto(template);
    }

    public async Task<FormTemplateDto> CopyAsync(int id, CopyFormTemplateRequest request, CancellationToken ct = default)
    {
        var actor = await RequireTemplateMaintainerAsync("复制模板", ct);

        var source = await _db.FormTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct)
                     ?? throw new NotFoundException($"模板不存在：{id}");

        var code = NormalizeCode(request.Code, request.Name ?? (source.Name + "（副本）"));
        var name = RequireName(request.Name ?? (source.Name + "（副本）"));

        if (await _db.FormTemplates.AnyAsync(t => t.Code == code, ct))
        {
            throw new ConflictException($"模板编码已被占用：{code}");
        }

        var now = DateTime.Now;

        var copy = new FormTemplate
        {
            Code = code,
            Name = name,
            Category = source.Category,
            Description = source.Description,
            Version = 1,
            SchemaJson = source.SchemaJson,
            IsEnabled = true,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actor,
        };

        _db.FormTemplates.Add(copy);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("复制表单模板", $"{source.Code} → {copy.Code}", success: true, ct: ct);

        return ToDto(copy);
    }

    public async Task DeleteAsync(int id, CancellationToken ct = default)
    {
        await RequireTemplateMaintainerAsync("删除模板", ct);

        var template = await _db.FormTemplates.FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw new NotFoundException($"模板不存在：{id}");

        if (template.IsBuiltIn)
        {
            throw new ConflictException("内置模板不能删除；如需停用请把状态改为停用。");
        }

        // 记录挂了模板，删模板等于删掉历史 —— 用停用来代替
        var used = await _db.FormRecords.CountAsync(r => r.TemplateId == id, ct);
        if (used > 0)
        {
            throw new ConflictException($"该模板已被 {used} 条记录使用，不能删除。请改为停用。");
        }

        _db.FormTemplates.Remove(template);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除表单模板", template.Code, success: true, ct: ct);
    }

    // ── 内部 ────────────────────────────────────────────────────────

    private async Task<string> RequireTemplateMaintainerAsync(string action, CancellationToken ct)
    {
        var current = await _access.ResolveAsync(_accessor.HttpContext?.User, ct);

        if (current.IsSystemAdmin ||
            current.LevelByCheckId.Values.Any(level => level >= AccessLevel.RuleManage))
        {
            return _context.UserName ?? "(unknown)";
        }

        throw new AccessDeniedException($"{action}需要「规则管理」级别权限，请联系管理员授权。");
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new BadRequestException("模板名称不能为空。");
        }

        if (trimmed.Length > 128)
        {
            throw new BadRequestException("模板名称不能超过 128 字。");
        }

        return trimmed;
    }

    /// <summary>编码规范：大写字母开头，只允许大写字母/数字/下划线。留空则按名称生成。</summary>
    private static string NormalizeCode(string? code, string? fallbackName)
    {
        var raw = code?.Trim();

        if (string.IsNullOrWhiteSpace(raw))
        {
            // 中文名称没法直接转编码：用时间戳兜底，用户可随后改
            raw = "TPL_" + DateTime.Now.ToString("yyyyMMddHHmmss");
        }

        raw = raw.ToUpperInvariant();

        if (!CodePattern().IsMatch(raw))
        {
            throw new BadRequestException("模板编码不合法：需以大写字母开头，只含大写字母、数字、下划线，长度 3-64。");
        }

        return raw;
    }

    private static string NormalizeSchemaJson(JsonElement schema)
    {
        if (schema.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new BadRequestException("表单定义不能为空。");
        }

        return schema.ValueKind == JsonValueKind.String
            ? schema.GetString() ?? string.Empty
            : schema.GetRawText();
    }

    private static int CountFields(string schemaJson)
    {
        var validation = FormSchema.Validate(schemaJson);
        return validation.Schema?.Sections.Sum(s => s.Fields.Count) ?? 0;
    }

    private static FormTemplateDto ToDto(FormTemplate template)
    {
        JsonElement schema;

        try
        {
            using var document = JsonDocument.Parse(template.SchemaJson);
            schema = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            // 库里存了坏 JSON 也不能让模板页整页 500：给一个空骨架，前端会提示结构异常
            schema = JsonSerializer.SerializeToElement(new { sections = Array.Empty<object>() });
        }

        return new FormTemplateDto(
            template.Id,
            template.Code,
            template.Name,
            template.Category,
            template.Description,
            template.Version,
            template.IsEnabled,
            template.IsBuiltIn,
            schema,
            template.CreatedAt,
            template.UpdatedAt,
            template.UpdatedBy);
    }
}
