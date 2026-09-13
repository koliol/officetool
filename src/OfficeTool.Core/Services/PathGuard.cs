using OfficeTool.Core.Exceptions;

namespace OfficeTool.Core.Services;

/// <summary>
/// 路径安全（设计文档 §7.3）：所有路径必须基于配置的 SMB 根，
/// 规范化后校验前缀，禁止穿越。
/// </summary>
public static class PathGuard
{
    private static readonly char[] Separators = ['/', '\\'];

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>去掉尾部路径分隔符。</summary>
    public static string NormalizeRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return root.TrimEnd(Separators);
    }

    /// <summary>
    /// 在根目录下拼接若干段。每一段都必须是单一名称（不含分隔符、不含 ..），
    /// 从源头杜绝穿越。
    /// </summary>
    public static string CombineUnderRoot(string root, params string[] segments)
    {
        var normalizedRoot = NormalizeRoot(root);
        var current = normalizedRoot;

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new InvalidNameException("路径段不能为空。");
            }

            if (segment.IndexOfAny(Separators) >= 0 || segment.Contains("..", StringComparison.Ordinal))
            {
                throw new InvalidNameException($"路径段含非法字符：{segment}");
            }

            current = Path.Combine(current, segment);
        }

        return EnsureUnderRoot(normalizedRoot, current);
    }

    /// <summary>
    /// 规范化 <paramref name="candidatePath"/> 并确认其位于 <paramref name="root"/> 之下。
    /// 返回规范化后的绝对路径。
    /// </summary>
    public static string EnsureUnderRoot(string root, string candidatePath)
    {
        var normalizedRoot = NormalizeRoot(root);
        var fullRoot = Path.GetFullPath(normalizedRoot);
        var full = Path.GetFullPath(candidatePath);

        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;

        var isUnderRoot =
            full.StartsWith(rootWithSeparator, PathComparison)
            || string.Equals(full, fullRoot, PathComparison);

        if (!isUnderRoot)
        {
            throw new PathEscapeException($"路径超出允许的根目录：{candidatePath}（根：{root}）");
        }

        return full;
    }

    /// <summary>把绝对路径转换为相对根的路径，统一使用反斜杠，便于展示与入库。</summary>
    public static string ToRelative(string root, string fullPath)
    {
        var normalizedRoot = NormalizeRoot(root);
        var ensured = EnsureUnderRoot(normalizedRoot, fullPath);
        var fullRoot = Path.GetFullPath(normalizedRoot);

        var relative = Path.GetRelativePath(fullRoot, ensured);
        return relative.Replace('/', '\\');
    }

    /// <summary>把入库的相对路径（反斜杠形式）还原为绝对路径，并再次校验未越界。</summary>
    public static string FromRelative(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var normalizedRoot = NormalizeRoot(root);
        var segments = relativePath.Split(Separators, StringSplitOptions.RemoveEmptyEntries);

        return CombineUnderRoot(normalizedRoot, segments);
    }
}
