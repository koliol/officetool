using System.Text.RegularExpressions;
using OfficeTool.Core.Exceptions;

namespace OfficeTool.Core.Services;

/// <summary>
/// 项目 / 检项编码与文件名校验（设计文档 §5.1、§5.3、§7.3、§11）。
/// </summary>
public static partial class NameValidator
{
    public const int MaxCodeLength = 64;

    public const int MaxFileNameLength = 200;

    [GeneratedRegex(@"^[A-Za-z0-9_-]+$")]
    private static partial Regex CodePattern();

    private static readonly HashSet<string> ReservedWindowsNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>项目 / 检项编码：仅字母、数字、下划线、短横线。</summary>
    public static bool TryValidateCode(string? value, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "编码不能为空。";
            return false;
        }

        if (value.Length > MaxCodeLength)
        {
            error = $"编码长度不能超过 {MaxCodeLength} 个字符。";
            return false;
        }

        if (!CodePattern().IsMatch(value))
        {
            error = "编码只允许字母、数字、下划线、短横线，不允许空格与路径特殊字符。";
            return false;
        }

        if (value is "." or "..")
        {
            error = "编码不允许为 . 或 ..";
            return false;
        }

        if (ReservedWindowsNames.Contains(value))
        {
            error = $"编码为系统保留名：{value}";
            return false;
        }

        return true;
    }

    public static string ValidateCode(string? value, string fieldName)
    {
        if (!TryValidateCode(value, out var error))
        {
            throw new InvalidNameException($"{fieldName}不合法：{error}");
        }

        return value!;
    }

    /// <summary>上传 / 重命名时的文件名：不允许路径分隔符与穿越。</summary>
    public static bool TryValidateFileName(string? value, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "文件名不能为空。";
            return false;
        }

        if (value.Length > MaxFileNameLength)
        {
            error = $"文件名长度不能超过 {MaxFileNameLength} 个字符。";
            return false;
        }

        if (value.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
        {
            error = "文件名不允许包含 \\ / : * ? \" < > | 等字符。";
            return false;
        }

        if (value.Contains("..", StringComparison.Ordinal))
        {
            error = "文件名不允许包含 ..";
            return false;
        }

        if (value is "." or "..")
        {
            error = "文件名不允许为 . 或 ..";
            return false;
        }

        if (value.EndsWith('.') || value.EndsWith(' '))
        {
            error = "文件名不允许以点或空格结尾。";
            return false;
        }

        return true;
    }

    public static string ValidateFileName(string? value)
    {
        if (!TryValidateFileName(value, out var error))
        {
            throw new InvalidNameException(error);
        }

        return value!;
    }

    /// <summary>
    /// 扩展名白名单校验（设计文档 §5.3、§11）。大小写不敏感地匹配白名单，
    /// 返回值补上前导点并 <b>保留原始大小写</b>——因为设计文档 §4.4 要求
    /// 「扩展名完全跟随模板」，模板是 .DOCX 时副本也应是 .DOCX。
    /// </summary>
    public static string EnsureAllowedExtension(string? extension, IEnumerable<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ExtensionNotAllowedException("(空)");
        }

        var normalized = extension.StartsWith('.') ? extension : "." + extension;
        var allowedSet = new HashSet<string>(
            allowed.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        if (!allowedSet.Contains(normalized))
        {
            throw new ExtensionNotAllowedException(normalized);
        }

        return normalized;
    }
}
