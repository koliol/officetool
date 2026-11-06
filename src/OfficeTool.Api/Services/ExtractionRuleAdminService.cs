using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;

namespace OfficeTool.Api.Services;

/// <summary>
/// 提取规则的**管理侧**：维护内置规则的版本、启停，以及"生效范围"。
///
/// 为什么规则不接受用户上传：规则是产品资产（我们训练/验证后发布），
/// 用户上传自己的规则会立刻带来三个问题——版本无人管、失败无法复现、
/// 一条烂规则能让所有人解析出错。所以这里的写操作只开放给系统管理员。
/// </summary>
public sealed class ExtractionRuleAdminService(
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

    public async Task<IReadOnlyList<AdminExtractionRuleDto>> ListAsync(CancellationToken ct = default)
    {
        await RequireAdminAsync(ct);

        var rules = await _db.ExtractionRuleDefinitions.AsNoTracking()
            .OrderBy(r => r.Category)
            .ThenBy(r => r.Name)
            .ToListAsync(ct);

        var scopes = await (
            from s in _db.ExtractionRuleScopes.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on s.ProjectId equals p.Id
            join c in _db.Checks.AsNoTracking() on s.CheckId equals c.Id into checks
            from c in checks.DefaultIfEmpty()
            select new
            {
                s.RuleId,
                s.Id,
                s.ProjectId,
                Project = p.Name,
                s.CheckId,
                Check = c == null ? null : c.Name,
            }).ToListAsync(ct);

        var scopeLookup = scopes.GroupBy(s => s.RuleId).ToDictionary(g => g.Key, g => g.ToList());

        return [.. rules.Select(rule => ToDto(
            rule,
            scopeLookup.TryGetValue(rule.Id, out var list)
                ? [.. list.Select(s => new RuleScopeDto(s.Id, s.ProjectId, s.Project, s.CheckId, s.Check))]
                : []))];
    }

    public async Task<AdminExtractionRuleDto> CreateAsync(
        string code,
        SaveRuleDefinitionRequest request,
        CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);

        var normalized = code?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > 64)
        {
            throw new BadRequestException("规则编码不能为空且不超过 64 字符。");
        }

        if (await _db.ExtractionRuleDefinitions.AnyAsync(r => r.Code == normalized, ct))
        {
            throw new ConflictException($"规则编码已存在：{normalized}");
        }

        var ruleJson = NormalizeRuleJson(request.Rule);
        _ = ExtractionRule.Parse(ruleJson);   // 非法规则不进库

        var now = DateTime.Now;

        var rule = new ExtractionRuleDefinition
        {
            Code = normalized,
            Name = RequireName(request.Name),
            Category = request.Category?.Trim() ?? string.Empty,
            Description = request.Description?.Trim() ?? string.Empty,
            Version = 1,
            RuleJson = ruleJson,
            IsEnabled = request.IsEnabled ?? true,
            IsBuiltIn = false,
            CreatedAt = now,
            UpdatedAt = now,
            UpdatedBy = actor,
        };

        _db.ExtractionRuleDefinitions.Add(rule);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("新建提取规则", rule.Code, success: true, ct: ct);

        return ToDto(rule, []);
    }

    public async Task<AdminExtractionRuleDto> UpdateAsync(
        int id,
        SaveRuleDefinitionRequest request,
        CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);

        var rule = await _db.ExtractionRuleDefinitions.Include(r => r.Scopes)
                       .FirstOrDefaultAsync(r => r.Id == id, ct)
                   ?? throw new NotFoundException($"规则不存在：{id}");

        var ruleJson = NormalizeRuleJson(request.Rule);
        _ = ExtractionRule.Parse(ruleJson);

        var changed = !string.Equals(
            Canonical(rule.RuleJson),
            Canonical(ruleJson),
            StringComparison.Ordinal);

        rule.Name = RequireName(request.Name);
        rule.Category = request.Category?.Trim() ?? rule.Category;
        rule.Description = request.Description?.Trim() ?? rule.Description;
        rule.UpdatedAt = DateTime.Now;
        rule.UpdatedBy = actor;

        if (request.IsEnabled is { } enabled)
        {
            rule.IsEnabled = enabled;
        }

        if (changed)
        {
            rule.RuleJson = ruleJson;
            rule.Version += 1;
        }

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(
            "修改提取规则",
            $"{rule.Code} v{rule.Version}{(changed ? "（内容变更）" : "")}",
            success: true,
            ct: ct);

        return ToDto(rule, await ScopesOfAsync(rule.Id, ct));
    }

    public async Task<AdminExtractionRuleDto> SetEnabledAsync(int id, bool enabled, CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);

        var rule = await _db.ExtractionRuleDefinitions.FirstOrDefaultAsync(r => r.Id == id, ct)
                   ?? throw new NotFoundException($"规则不存在：{id}");

        rule.IsEnabled = enabled;
        rule.UpdatedAt = DateTime.Now;
        rule.UpdatedBy = actor;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(enabled ? "启用提取规则" : "停用提取规则", rule.Code, success: true, ct: ct);

        return ToDto(rule, await ScopesOfAsync(rule.Id, ct));
    }

    /// <summary>整体替换生效范围。前端每次提交完整集合，服务端做全量覆盖。</summary>
    public async Task<AdminExtractionRuleDto> ReplaceScopesAsync(
        int id,
        SaveRuleScopesRequest request,
        CancellationToken ct = default)
    {
        var actor = await RequireAdminAsync(ct);

        var rule = await _db.ExtractionRuleDefinitions.FirstOrDefaultAsync(r => r.Id == id, ct)
                   ?? throw new NotFoundException($"规则不存在：{id}");

        var inputs = (request.Scopes ?? [])
            .GroupBy(s => (s.ProjectId, s.CheckId))
            .Select(g => g.First())
            .ToList();

        // 先校验再删：写一半失败会留下"范围被清空"的中间态
        foreach (var input in inputs)
        {
            var projectExists = await _db.Projects.AnyAsync(p => p.Id == input.ProjectId, ct);

            if (!projectExists)
            {
                throw new NotFoundException($"项目不存在：{input.ProjectId}");
            }

            if (input.CheckId is { } checkId)
            {
                var belongs = await _db.Checks.AnyAsync(c => c.Id == checkId && c.ProjectId == input.ProjectId, ct);

                if (!belongs)
                {
                    throw new BadRequestException($"检项 {checkId} 不属于项目 {input.ProjectId}。");
                }
            }
        }

        var existing = await _db.ExtractionRuleScopes.Where(s => s.RuleId == id).ToListAsync(ct);

        _db.ExtractionRuleScopes.RemoveRange(existing);

        var now = DateTime.Now;

        foreach (var input in inputs)
        {
            _db.ExtractionRuleScopes.Add(new ExtractionRuleScope
            {
                RuleId = id,
                ProjectId = input.ProjectId,
                CheckId = input.CheckId,
                CreatedAt = now,
            });
        }

        rule.UpdatedAt = now;
        rule.UpdatedBy = actor;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync(
            "调整提取规则范围",
            $"{rule.Code} → {inputs.Count} 处范围",
            success: true,
            ct: ct);

        return ToDto(rule, await ScopesOfAsync(rule.Id, ct));
    }

    // ── 内部 ────────────────────────────────────────────────────────

    private async Task<string> RequireAdminAsync(CancellationToken ct)
    {
        var current = await _access.ResolveAsync(_accessor.HttpContext?.User, ct);

        if (!current.IsSystemAdmin)
        {
            throw new AccessDeniedException("提取规则的维护仅限系统管理员。");
        }

        return _context.UserName ?? "(unknown)";
    }

    private async Task<IReadOnlyList<RuleScopeDto>> ScopesOfAsync(int ruleId, CancellationToken ct)
    {
        var rows = await (
            from s in _db.ExtractionRuleScopes.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on s.ProjectId equals p.Id
            join c in _db.Checks.AsNoTracking() on s.CheckId equals c.Id into checks
            from c in checks.DefaultIfEmpty()
            where s.RuleId == ruleId
            select new
            {
                s.Id,
                s.ProjectId,
                Project = p.Name,
                s.CheckId,
                Check = c == null ? null : c.Name,
            }).ToListAsync(ct);

        return [.. rows.Select(s => new RuleScopeDto(s.Id, s.ProjectId, s.Project, s.CheckId, s.Check))];
    }

    private static string RequireName(string? name)
    {
        var trimmed = name?.Trim();

        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new BadRequestException("规则名称不能为空。");
        }

        if (trimmed.Length > 128)
        {
            throw new BadRequestException("规则名称不能超过 128 字。");
        }

        return trimmed;
    }

    private static string NormalizeRuleJson(JsonElement rule)
    {
        if (rule.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new BadRequestException("规则内容不能为空。");
        }

        return rule.ValueKind == JsonValueKind.String
            ? rule.GetString() ?? string.Empty
            : rule.GetRawText();
    }

    private static string Canonical(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return "{}";
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static AdminExtractionRuleDto ToDto(ExtractionRuleDefinition rule, IReadOnlyList<RuleScopeDto> scopes)
    {
        JsonElement ruleJson;

        try
        {
            using var document = JsonDocument.Parse(rule.RuleJson);
            ruleJson = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            ruleJson = JsonSerializer.SerializeToElement(new { targets = Array.Empty<object>() });
        }

        return new AdminExtractionRuleDto(
            rule.Id,
            rule.Code,
            rule.Name,
            rule.Category,
            rule.Description,
            rule.Version,
            rule.IsEnabled,
            rule.IsBuiltIn,
            ruleJson,
            scopes,
            rule.UpdatedAt,
            rule.UpdatedBy);
    }
}
