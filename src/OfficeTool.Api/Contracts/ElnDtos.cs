using System.Text.Json;

namespace OfficeTool.Api.Contracts;

// ── ELN：表单模板 ────────────────────────────────────────────────────────

/// <summary>模板列表项。列表不带 schema（体积大且列表用不到）。</summary>
public sealed record FormTemplateListItemDto(
    int Id,
    string Code,
    string Name,
    string Category,
    string Description,
    int Version,
    bool IsEnabled,
    bool IsBuiltIn,
    int FieldCount,
    int RecordCount,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>模板详情。schema 已解析成对象，前端不必再 JSON.parse 一次字符串。</summary>
public sealed record FormTemplateDto(
    int Id,
    string Code,
    string Name,
    string Category,
    string Description,
    int Version,
    bool IsEnabled,
    bool IsBuiltIn,
    JsonElement Schema,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? UpdatedBy);

/// <summary>新建 / 更新模板。Code 仅在新建时有意义。</summary>
public sealed record SaveFormTemplateRequest(
    string? Code,
    string Name,
    string? Category,
    string? Description,
    JsonElement Schema,
    bool? IsEnabled);

public sealed record CopyFormTemplateRequest(string? Code, string? Name);

// ── ELN：记录 ────────────────────────────────────────────────────────────

public sealed record CreateFormRecordRequest(int TemplateId, string Project, string Check, string? Title);

/// <summary>保存草稿：整份数据覆盖写（表单总是整体提交，不做字段级 diff）。</summary>
public sealed record SaveRecordDataRequest(JsonElement Data);

public sealed record VoidRecordRequest(string Reason);

public sealed record RecordFileDto(
    int Id,
    string FileName,
    string Extension,
    long Size,
    string Kind,
    DateTime UploadedAt,
    string? UploadedBy);

public sealed record FormRecordListItemDto(
    int Id,
    string RecordNo,
    string Title,
    string TemplateCode,
    string TemplateName,
    int TemplateVersion,
    int ProjectId,
    string Project,
    int CheckId,
    string Check,
    string Status,
    string? CreatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SubmittedAt,
    int FileCount);

/// <summary>
/// 记录详情。schema 用的是**记录创建时的快照**，不是模板当前版本 ——
/// 模板后续改字段，旧记录仍然按当时的表格呈现。
/// </summary>
public sealed record FormRecordDto(
    int Id,
    string RecordNo,
    string Title,
    string TemplateCode,
    string TemplateName,
    int TemplateVersion,
    int ProjectId,
    string Project,
    int CheckId,
    string Check,
    string Status,
    JsonElement Schema,
    JsonElement Data,
    string? CreatedBy,
    string? UpdatedBy,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    DateTime? SubmittedAt,
    string? SubmittedBy,
    DateTime? VoidedAt,
    string? VoidReason,
    IReadOnlyList<RecordFileDto> Files);

// ── ELN：提取 ────────────────────────────────────────────────────────────

public sealed record ExtractRecordRequest(int RuleId, int? FileId);

public sealed record ExtractionFieldDto(
    string Field,
    string Label,
    JsonElement Value,
    string Snippet,
    double Confidence);

public sealed record ExtractRecordResultDto(
    int RunId,
    string Status,
    int MatchedFieldCount,
    IReadOnlyList<ExtractionFieldDto> Fields,
    IReadOnlyList<string> Warnings,
    string? Error,
    string TextPreview);

public sealed record ExtractionRunDto(
    int Id,
    int RecordId,
    string RuleCode,
    int RuleVersion,
    string SourceFileName,
    string Status,
    int MatchedFieldCount,
    string? ErrorMessage,
    DateTime CreatedAt,
    string? CreatedBy);

// ── ELN：提取规则（用户侧）──────────────────────────────────────────────

public sealed record ExtractionRuleListItemDto(
    int Id,
    string Code,
    string Name,
    string Category,
    string Description,
    int Version,
    string TemplateCode);

/// <summary>用户侧规则详情：只返回自己能用的字段，不暴露内部规则 JSON。</summary>
public sealed record ExtractionRuleForUserDto(
    int Id,
    string Code,
    string Name,
    string Category,
    string Description,
    int Version,
    string TemplateCode,
    IReadOnlyList<ExtractionTargetPreviewDto> Targets);

public sealed record ExtractionTargetPreviewDto(string Field, string Label, bool IsTable);

// ── ELN：提取规则（管理侧）──────────────────────────────────────────────

public sealed record AdminExtractionRuleDto(
    int Id,
    string Code,
    string Name,
    string Category,
    string Description,
    int Version,
    bool IsEnabled,
    bool IsBuiltIn,
    JsonElement Rule,
    IReadOnlyList<RuleScopeDto> Scopes,
    DateTime UpdatedAt,
    string? UpdatedBy);

public sealed record RuleScopeDto(int Id, int ProjectId, string Project, int? CheckId, string? Check);

public sealed record SaveRuleDefinitionRequest(
    string Name,
    string? Category,
    string? Description,
    JsonElement Rule,
    bool? IsEnabled);

/// <summary>新增规则（编码在新建时给，之后不可改 —— 编码是记录里的追溯锚点）。</summary>
public sealed record CreateRuleRequest(string Code, SaveRuleDefinitionRequest Definition);

public sealed record RuleScopeInput(int ProjectId, int? CheckId);

public sealed record SaveRuleScopesRequest(IReadOnlyList<RuleScopeInput> Scopes);
