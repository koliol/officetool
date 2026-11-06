namespace OfficeTool.Core.Models;

// ═══════════════════════════════════════════════════════════════════════════
// ELN（电子实验记录）模型
//
// 与 Entities.cs 里的「模板/文档文件管理」是两套东西，刻意不复用 Template/Document：
// 那两张表的语义是「共享盘上的一个文件」，而记录是「一条有状态、有审计、
// 由表单结构约束的数据」。混在一张表里，两边都说不清。
//
// 旧表保留原样，仅作为「历史文件」只读展示 —— 已经存在的客户文件不能因为
// 产品形态改变而消失。
// ═══════════════════════════════════════════════════════════════════════════

/// <summary>
/// ELN 表单模板：一份**在线定义**的记录格式（分区 + 字段 + 表格列），存在数据库里。
///
/// 为什么内联 schema 而不是拆成 Field 表：表单结构总是整体读写（打开设计器 → 保存整份），
/// 拆表会让「保存一次」变成几十次 diff，而查询需求只有「取整份」。
/// 追溯问题由「版本号 + 记录里存快照」解决。
/// </summary>
public class FormTemplate
{
    public int Id { get; set; }

    /// <summary>模板编码。唯一，字母/数字/下划线，如 INSOLUBLE_PARTICLES。</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>分类，如「理化检验」「微生物检验」。用于模板库分组。</summary>
    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    /// <summary>结构版本。schema 内容变化时自增，记录里保存的是当时那一版。</summary>
    public int Version { get; set; } = 1;

    /// <summary>表单定义 JSON（分区 / 字段 / 表格列），格式见 <c>FormSchema</c>。</summary>
    public string SchemaJson { get; set; } = "{}";

    /// <summary>停用后不再出现在「新建记录」的模板选择里，但不影响已建的记录。</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>随代码发布的内置模板（可停用、可复制后改，不建议删除）。</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }
}

/// <summary>记录状态。ELN 的核心约束：提交后数据不可改，只能作废重开。</summary>
public enum RecordStatus
{
    /// <summary>草稿。可反复保存。</summary>
    Draft = 0,

    /// <summary>已提交。数据冻结，作为正式记录。</summary>
    Submitted = 1,

    /// <summary>已作废。保留留痕，不再计入统计。</summary>
    Voided = 2,
}

/// <summary>
/// 一条实验记录：某人在某项目/检项下，按某个模板填的一次内容。
///
/// <see cref="TemplateSchemaJson"/> 与 <see cref="TemplateVersion"/> 是**创建时快照**：
/// 模板后来改了字段，旧记录仍按当时的结构渲染与校验。否则"打开三年前的记录"
/// 会因为模板升级变成另一份表格，这在受监管场景里不可接受。
/// </summary>
public class FormRecord
{
    public int Id { get; set; }

    public int TemplateId { get; set; }

    public string TemplateCode { get; set; } = string.Empty;

    public string TemplateName { get; set; } = string.Empty;

    public int TemplateVersion { get; set; }

    /// <summary>创建时的 schema 快照。</summary>
    public string TemplateSchemaJson { get; set; } = "{}";

    public int ProjectId { get; set; }

    public int CheckId { get; set; }

    /// <summary>记录号。唯一，形如 QLS2409-SEC-20261106-001。</summary>
    public string RecordNo { get; set; } = string.Empty;

    /// <summary>标题。默认「模板名-批号」，用户可改。</summary>
    public string Title { get; set; } = string.Empty;

    public RecordStatus Status { get; set; } = RecordStatus.Draft;

    /// <summary>填写内容 JSON：字段 key → 值；表格字段是对象数组。</summary>
    public string DataJson { get; set; } = "{}";

    public string? CreatedBy { get; set; }

    public string? UpdatedBy { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public DateTime? SubmittedAt { get; set; }

    public string? SubmittedBy { get; set; }

    public DateTime? VoidedAt { get; set; }

    public string? VoidReason { get; set; }

    public FormTemplate? Template { get; set; }

    public Project? Project { get; set; }

    public CheckItem? Check { get; set; }

    public List<RecordFile> Files { get; set; } = [];
}

/// <summary>记录关联文件的种类。</summary>
public enum RecordFileKind
{
    /// <summary>原始材料（仪器导出的 PDF、扫描件等），解析的数据来源。</summary>
    Source = 0,

    /// <summary>产出物（后续生成的报告）。目前预留。</summary>
    Report = 1,
}

/// <summary>挂在记录上的文件，落盘在 Records\{项目}\{检项}\{记录号}\ 下。</summary>
public class RecordFile
{
    public int Id { get; set; }

    public int RecordId { get; set; }

    public RecordFileKind Kind { get; set; } = RecordFileKind.Source;

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对 RecordsRoot 的路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long Size { get; set; }

    public string ContentType { get; set; } = string.Empty;

    public DateTime UploadedAt { get; set; }

    public string? UploadedBy { get; set; }

    public FormRecord? Record { get; set; }
}

/// <summary>
/// 提取规则定义（系统内置，由我们训练好后随版本发布）。
///
/// 与旧 RuleCatalogService 的区别：旧的是「用户上传到共享盘的规则文件」；
/// 这一份是**产品内置资产** —— 随代码/数据库发布，管理员只能启用与划范围，不能上传。
/// 规则质量由我们控制，用户侧只做「选哪条」。
/// </summary>
public class ExtractionRuleDefinition
{
    public int Id { get; set; }

    /// <summary>规则编码。唯一，如 insoluble_particles。</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>适用类别，如「理化检验」。</summary>
    public string Category { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public int Version { get; set; } = 1;

    /// <summary>规则本体 JSON（目标字段 + 抽取器），格式见 <c>ExtractionRuleSet</c>。</summary>
    public string RuleJson { get; set; } = "{}";

    public bool IsEnabled { get; set; } = true;

    /// <summary>内置规则：允许停用；删除前需确认没有解析记录引用。</summary>
    public bool IsBuiltIn { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string? UpdatedBy { get; set; }

    public List<ExtractionRuleScope> Scopes { get; set; } = [];
}

/// <summary>
/// 规则的生效范围。管理员在这里决定「哪些项目/检项的人能看到这条规则」。
///
/// <see cref="CheckId"/> 为 null 表示整个项目生效 —— 与 AclEntry 同一套约定，
/// 避免出现"项目级和检项级两套语义"。
/// </summary>
public class ExtractionRuleScope
{
    public int Id { get; set; }

    public int RuleId { get; set; }

    public int ProjectId { get; set; }

    public int? CheckId { get; set; }

    public DateTime CreatedAt { get; set; }

    public ExtractionRuleDefinition? Rule { get; set; }

    public Project? Project { get; set; }

    public CheckItem? Check { get; set; }
}

/// <summary>解析运行的结果状态。</summary>
public enum ExtractionRunStatus
{
    /// <summary>抽出至少一个字段。</summary>
    Succeeded = 0,

    /// <summary>规则执行了但一个字段都没匹配到（多半选错了规则，或报告版式对不上）。</summary>
    NoMatch = 1,

    /// <summary>失败：PDF 无文本层（扫描件）、文件损坏、规则非法等。</summary>
    Failed = 2,
}

/// <summary>
/// 一次解析运行的审计记录。
///
/// 为什么必须落库：记录里的数字是**从 PDF 抽出来再人工确认**的，
/// 出问题时第一个被问的是「当时抽到的是什么、原文长什么样」。
/// 只留结果、不留原文与命中片段，等于把追溯能力丢掉。
/// </summary>
public class ExtractionRun
{
    public int Id { get; set; }

    public int RecordId { get; set; }

    public int RuleDefinitionId { get; set; }

    public string RuleCode { get; set; } = string.Empty;

    public int RuleVersion { get; set; }

    public int? RecordFileId { get; set; }

    public string SourceFileName { get; set; } = string.Empty;

    public ExtractionRunStatus Status { get; set; }

    /// <summary>抽出的字段数（表格字段按 1 个字段计）。</summary>
    public int MatchedFieldCount { get; set; }

    /// <summary>抽取结果 JSON：字段 key → { value, snippet, confidence }。</summary>
    public string? ResultJson { get; set; }

    /// <summary>PDF 全文（截断到上限）。用于回答"当时原文是什么样"。</summary>
    public string? RawText { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedBy { get; set; }
}
