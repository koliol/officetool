namespace OfficeTool.Api.Contracts;

// ── 请求 ────────────────────────────────────────────────────────────────

public sealed record CreateProjectRequest(string Name);

public sealed record CreateCheckRequest(string Name);

/// <summary>
/// 复制模板。
///
/// <paramref name="TargetProject"/> / <paramref name="TargetCheck"/> 留空时表示**同目录复制**，
/// 沿用旧行为（必然重名，因此总是追加「_副本」）；给了目标则表示跨目录复制，
/// 与文档复制保持一致的语义（沿用原名，撞名才加「_副本」）。
///
/// 目标只接受项目/检项编码 —— 路径由服务端拼装，前端无法构造越界路径。
/// </summary>
public sealed record CopyTemplateRequest(
    string Project,
    string Check,
    string SourceFileName,
    string? NewFileName,
    string? TargetProject = null,
    string? TargetCheck = null);

public sealed record RenameRequest(string Project, string Check, string FileName, string NewFileName);

public sealed record CreateDocumentRequest(string Project, string Check, string TemplateFileName);

/// <summary>
/// 「提取」请求：把某个附件按指定规则提取，回填到目标文档。
/// </summary>
/// <param name="Project">项目编码</param>
/// <param name="Check">检项编码</param>
/// <param name="AttachmentFileName">附件文件名（位于附件库中）</param>
/// <param name="RuleFileName">提取规则文件名，来自 GET /api/extraction-rules?project=&amp;check=</param>
/// <param name="TargetDocumentFileName">目标文档文件名（新建文档后由前端传入）</param>
public sealed record ExtractRequest(
    string Project,
    string Check,
    string AttachmentFileName,
    string RuleFileName,
    string? TargetDocumentFileName);

// ── 响应 ────────────────────────────────────────────────────────────────

public sealed record ProjectDto(int Id, string Name, DateTime CreatedAt, int CheckCount);

public sealed record CheckDto(int Id, string Name, int ProjectId, DateTime CreatedAt);

/// <summary>
/// <paramref name="AccessPath"/> 是**客户端视角**的路径（群晖部署下形如 \\NAS\OfficeDocs\...），
/// 用于展示给用户复制到资源管理器，以及桌面插件调用本机 Office。
/// <paramref name="RelativePath"/> 是相对存储根的路径，用于定位与排查。
/// </summary>
public sealed record TemplateDto(
    int Id,
    string FileName,
    string Extension,
    long Size,
    DateTime ModifiedAt,
    DateTime CreatedAt,
    string Project,
    string Check,
    string RelativePath,
    string AccessPath);

public sealed record DocumentDto(
    int Id,
    string FileName,
    string Extension,
    long Size,
    DateTime CreatedAt,
    string Project,
    string Check,
    string SourceTemplatePath,
    string RelativePath,
    string AccessPath);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

/// <summary>
/// 附件。与模板/文档同样给出「服务端相对路径」与「客户端访问路径」两个视角。
/// </summary>
public sealed record AttachmentDto(
    string FileName,
    string Extension,
    long Size,
    DateTime CreatedAt,
    string Project,
    string Check,
    string RelativePath,
    string AccessPath);

/// <summary>
/// 提取规则**文件**（含备注）。
/// 与模板/文档/附件一样给出服务端相对路径与客户端访问路径两个视角。
/// </summary>
public sealed record RuleDto(
    string FileName,
    string Extension,
    long Size,
    DateTime ModifiedAt,
    DateTime CreatedAt,
    string Project,
    string Check,
    string RelativePath,
    string AccessPath,
    /// <summary>网页上可直接编辑的备注（存在数据库，不在共享盘上）。</summary>
    string Note,
    DateTime? NoteUpdatedAt);

/// <summary>在网页上直接修改某条规则的备注。备注传空串即清除。</summary>
public sealed record UpdateRuleNoteRequest(string Project, string Check, string FileName, string? Note);

/// <summary>
/// 把若干条规则复制到另一个项目/检项。
/// 目标只给**编码**，不给路径 —— 路径一律由服务端按配置拼装，前端无法构造越界路径。
/// </summary>
public sealed record CopyRulesRequest(
    string Project,
    string Check,
    IReadOnlyList<string> FileNames,
    string TargetProject,
    string TargetCheck);

/// <summary>
/// 把文档复制到另一个项目/检项（设计上刻意不在当前文件夹直接生成副本）。
/// </summary>
/// <param name="NewFileName">留空则沿用源文件名；重名时自动加「_副本」序号。</param>
public sealed record CopyDocumentRequest(
    string Project,
    string Check,
    string FileName,
    string TargetProject,
    string TargetCheck,
    string? NewFileName);

/// <summary>
/// 复制结果。批量复制时允许「部分成功」：目标已存在同名文件的跳过，
/// 并把跳过的清单明确回传，而不是整体失败或静默覆盖。
/// </summary>
public sealed record CopyResultDto(
    IReadOnlyList<string> Copied,
    IReadOnlyList<string> Skipped,
    string TargetProject,
    string TargetCheck);

public sealed record OperationLogDto(
    int Id, string Action, string TargetPath, string? Ip, string? UserAgent,
    string Result, string? Message, DateTime CreatedAt);

public sealed record SystemConfigDto(
    string Mode,
    string TemplatesRoot,
    string DataRoot,
    string ClientTemplatesRoot,
    string ClientDataRoot,
    string Protocol,
    string PluginDownloadUrl,
    string[] AllowedExtensions,
    int MaxSizeMB,
    bool FileStoreIsRemoteUnc,
    bool FileStoreAvailable,
    string AttachmentsRoot,
    string ClientAttachmentsRoot,
    string[] AttachmentExtensions,
    string RulesRoot,
    string ClientRulesRoot,
    string[] RuleExtensions,
    /// <summary>
    /// 提取功能是否已可用。当前恒为 false（脚本引擎尚未实现），
    /// 前端据此禁用「提取」按钮并显示说明，避免给出「点了没反应」的操作。
    /// 实现后由 <c>AttachmentsController.ExtractionAvailable</c> 一处放开。
    /// </summary>
    bool ExtractionAvailable);

/// <summary>统一错误响应体。</summary>
public sealed record ApiError(string Code, string Message, string? Detail = null);

/// <summary>回收站中的一条记录（文件已移入 _trash，本条是索引）。</summary>
public sealed record TrashItemDto(
    int Id,
    string Kind,
    string Project,
    string Check,
    string FileName,
    string OriginalRelativePath,
    string TrashRelativePath,
    long Size,
    DateTime DeletedAt);

/// <summary>项目树节点（含检项），前端一次拉全，避免 N+1。</summary>
public sealed record ProjectTreeNodeDto(
    int Id,
    string Name,
    DateTime CreatedAt,
    IReadOnlyList<CheckDto> Checks);
