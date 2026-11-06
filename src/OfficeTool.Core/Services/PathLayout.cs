using System.Text;
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

    /// <summary>
    /// 把业务相对路径转成回收站里的唯一文件名，避免跨项目/检项同名冲突。
    ///
    /// 名字里带着原路径 + GUID 前缀，很容易超过单个路径段 255 字节的上限：
    /// 项目 64 + 检项 64 + 文件名 200（汉字还要 ×3）+ 32 位 GUID，一算就爆。
    /// 一旦爆了，删除动作会在 MoveFile 阶段失败，用户看到的是 502 而不是「名字太长」。
    /// 所以这里按字节预算截断：**保留 GUID 前缀与扩展名**，只收缩中间那一段 ——
    /// 扩展名丢了文件就再也认不回来，而中间那段只是给人看的线索。
    /// </summary>
    public static string BuildTrashFileName(string originalRelativePath, string fileName)
    {
        var prefix = Guid.NewGuid().ToString("N");
        var extension = Path.GetExtension(fileName);

        // 拆成「目录线索」+「文件名」两段：目录（项目/检项）是 ASCII 编码、长度可控，
        // 真正可能爆掉的是用户自己起的文件名，所以只截断后者，并把扩展名完整保留。
        var segments = originalRelativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var folder = segments.Length > 1 ? string.Join('_', segments[..^1]) : string.Empty;
        var baseName = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrEmpty(baseName))
        {
            baseName = "file";
        }

        var fixedBytes = prefix.Length
                         + 1
                         + Encoding.UTF8.GetByteCount(extension)
                         + (folder.Length > 0 ? Encoding.UTF8.GetByteCount(folder) + 1 : 0);

        var budget = Math.Max(8, NameValidator.MaxSegmentBytes - fixedBytes);
        baseName = TruncateToUtf8Bytes(baseName, budget);

        var middle = folder.Length > 0 ? $"{folder}_{baseName}" : baseName;
        return $"{prefix}_{middle}{extension}";
    }

    /// <summary>
    /// 按 UTF-8 字节数截断，且不切断代理对（否则会留下半个字符，Windows 上直接抛异常）。
    /// </summary>
    private static string TruncateToUtf8Bytes(string value, int maxBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var used = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > maxBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }

        return builder.ToString();
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
