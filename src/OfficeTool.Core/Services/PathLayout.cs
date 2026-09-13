using OfficeTool.Core.Options;

namespace OfficeTool.Core.Services;

/// <summary>
/// 目录布局（设计文档 §4）。项目 / 检项两级，
/// 模板库与数据目录平行：Templates\{项目}\{检项}、Data\{项目}\{检项}。
/// </summary>
public sealed class PathLayout(StorageOptions storageOptions)
{
    private readonly StorageOptions _storage = storageOptions;

    public string TemplatesRoot => PathGuard.NormalizeRoot(_storage.TemplatesRoot);

    public string DataRoot => PathGuard.NormalizeRoot(_storage.DataRoot);

    /// <summary>附件库根。与 Templates / Data 平级，单独存放。</summary>
    public string AttachmentsRoot => PathGuard.NormalizeRoot(_storage.AttachmentsRoot);

    /// <summary>提取规则库根。与 Templates / Data / Attachments 平级。</summary>
    public string RulesRoot => PathGuard.NormalizeRoot(_storage.RulesRoot);

    /// <summary>回收站根。与业务根同级，删除时文件移入此处。</summary>
    public string TrashRoot => PathGuard.NormalizeRoot(_storage.TrashRoot);

    /// <summary>Templates\{项目} 或 Templates\{项目}\{检项}</summary>
    public string TemplateDirectory(string? project, string? check = null)
    {
        var segments = BuildSegments(project, check);
        return PathGuard.CombineUnderRoot(TemplatesRoot, segments);
    }

    /// <summary>Attachments\{项目} 或 Attachments\{项目}\{检项}</summary>
    public string AttachmentDirectory(string? project, string? check = null)
    {
        var segments = BuildSegments(project, check);
        return PathGuard.CombineUnderRoot(AttachmentsRoot, segments);
    }

    /// <summary>ExtractionRules\{项目} 或 ExtractionRules\{项目}\{检项}</summary>
    public string RuleDirectory(string? project, string? check = null)
    {
        var segments = BuildSegments(project, check);
        return PathGuard.CombineUnderRoot(RulesRoot, segments);
    }

    /// <summary>Data\{项目} 或 Data\{项目}\{检项}</summary>
    public string DataDirectory(string? project, string? check = null)
    {
        var segments = BuildSegments(project, check);
        return PathGuard.CombineUnderRoot(DataRoot, segments);
    }

    public string TemplateRelativePath(string project, string check, string fileName) =>
        PathGuard.ToRelative(TemplatesRoot, Path.Combine(TemplateDirectory(project, check), fileName));

    public string DataRelativePath(string project, string check, string fileName) =>
        PathGuard.ToRelative(DataRoot, Path.Combine(DataDirectory(project, check), fileName));

    public string AttachmentRelativePath(string project, string check, string fileName) =>
        PathGuard.ToRelative(AttachmentsRoot, Path.Combine(AttachmentDirectory(project, check), fileName));

    public string RuleRelativePath(string project, string check, string fileName) =>
        PathGuard.ToRelative(RulesRoot, Path.Combine(RuleDirectory(project, check), fileName));

    /// <summary>
    /// 回收站内某次删除的目标目录：<c>_trash/{Kind}/{yyyyMMdd}</c>。
    /// Kind 使用业务根名（Templates/Data/Attachments/ExtractionRules），与共享盘上的平行结构一致。
    /// </summary>
    public string TrashKindDirectory(string kind, DateTime deletedAt)
    {
        if (string.IsNullOrWhiteSpace(kind) || kind.IndexOfAny(['/', '\\']) >= 0 || kind.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException("回收站类别名非法。", nameof(kind));
        }

        var day = deletedAt.ToString("yyyyMMdd");
        return PathGuard.CombineUnderRoot(TrashRoot, kind, day);
    }

    public string TrashRelativePath(string kind, DateTime deletedAt, string fileName) =>
        PathGuard.ToRelative(TrashRoot, Path.Combine(TrashKindDirectory(kind, deletedAt), fileName));

    /// <summary>把业务相对路径转成回收站里的唯一文件名，避免跨项目/检项同名冲突。</summary>
    public static string BuildTrashFileName(string originalRelativePath, string fileName)
    {
        var safe = originalRelativePath.Replace('\\', '_').Replace('/', '_');
        return $"{Guid.NewGuid():N}_{safe}";
    }

    private static string[] BuildSegments(string? project, string? check)
    {
        if (string.IsNullOrWhiteSpace(project))
        {
            throw new ArgumentException("项目编码不能为空。", nameof(project));
        }

        var segments = new List<string> { NameValidator.ValidateCode(project, "项目编码") };

        if (!string.IsNullOrWhiteSpace(check))
        {
            segments.Add(NameValidator.ValidateCode(check, "检项编码"));
        }

        return [.. segments];
    }
}
