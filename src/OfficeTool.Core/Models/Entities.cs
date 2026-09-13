namespace OfficeTool.Core.Models;

/// <summary>项目（设计文档 §6.1 Projects）。</summary>
public class Project
{
    public int Id { get; set; }

    /// <summary>项目编码，如 QLS2409。唯一。</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }

    public List<CheckItem> Checks { get; set; } = [];
}

/// <summary>检项（设计文档 §6.1 Checks。类名加 Item 以避开 BCL 语义歧义，表名仍为 Checks）。</summary>
public class CheckItem
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    /// <summary>检项编码，如 SEC。</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }

    public Project? Project { get; set; }
}

/// <summary>模板（设计文档 §6.1 Templates）。</summary>
public class Template
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int CheckId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对 TemplatesRoot 的路径，如 QLS2409\SEC\检验记录模板.docx</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? UploadedByIp { get; set; }
}

/// <summary>文档副本（设计文档 §6.1 Documents）。</summary>
public class Document
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int CheckId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对 DataRoot 的路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>来源模板的相对路径（相对 TemplatesRoot）。</summary>
    public string SourceTemplatePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }
}

/// <summary>
/// 提取规则的备注（网页上可直接修改）。
///
/// 为什么备注进数据库、规则本体进文件系统：
/// 规则本体是使用者要看的文件，放在共享盘上人和 Windows 都能直接看到、直接替换；
/// 而备注是纯应用侧元数据，放进共享盘会多出用户看不懂的杂项文件。
/// 按「相对 RulesRoot 的路径」关联，复制规则时顺带把备注一起带过去。
///
/// 注意：这张表是后加的，用 EnsureCreated 的库不会自动补建，
/// 启动时会额外执行一次 CREATE TABLE IF NOT EXISTS（见 Program.cs）。
/// </summary>
public class RuleNote
{
    public int Id { get; set; }

    /// <summary>相对 RulesRoot 的规则文件路径，如 QLS2409\SEC\标准字段.json。唯一。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 回收站条目。删除模板/文档/附件/规则时，文件移入共享盘上的 <c>_trash</c> 目录，
/// 本表登记原位置与回收站位置，供恢复与彻底删除使用。
/// </summary>
public class TrashItem
{
    public int Id { get; set; }

    /// <summary>Template | Document | Attachment | Rule</summary>
    public string Kind { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string Check { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对原业务根的路径（Templates/Data/Attachments/Rules 根）。</summary>
    public string OriginalRelativePath { get; set; } = string.Empty;

    /// <summary>相对 TrashRoot 的路径。</summary>
    public string TrashRelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime DeletedAt { get; set; }

    public string? DeletedByIp { get; set; }
}

/// <summary>操作日志（设计文档 §6.1 OperationLogs）。</summary>
public class OperationLog
{
    public int Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? Ip { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>成功 / 失败。</summary>
    public string Result { get; set; } = "成功";

    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; }
}
