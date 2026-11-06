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

    /// <summary>判断 <paramref name="candidate"/> 是否位于 <paramref name="root"/> 之内（不抛异常）。</summary>
    public static bool IsUnderRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(NormalizeRoot(root));
        var full = Path.GetFullPath(candidate);
        var rootWithSeparator = fullRoot + Path.DirectorySeparatorChar;

        return full.StartsWith(rootWithSeparator, PathComparison)
               || string.Equals(full, fullRoot, PathComparison);
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
    ///
    /// 这一层之外还有第二道闸：<see cref="EnsureNoLinkEscape"/> ——
    /// 字符串层面的前缀校验挡不住符号链接/目录联接：
    /// 共享盘对所有人都可写时，"Templates\QLS2409\SEC → C:\Windows" 这种链接
    /// 能让上传写进系统目录。链接检查需要 IO，放在同一条必经之路上。
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

        EnsureNoLinkEscape(fullRoot, full);

        return full;
    }

    /// <summary>
    /// 逐段检查 <paramref name="fullPath"/> 的目录链上是否有「重解析点指向根外」。
    ///
    /// 为什么逐段而不是只查最后一段：攻击面在中间层 ——
    /// <c>Templates\QLS2409</c> 本身是链接、后面再拼什么都逃出去了。
    /// 逐段解析代价是每段一次 <c>ResolveLinkTarget</c>，上传本身的 IO 远大于此。
    ///
    /// 指回根内部的链接**放行**：合法运维会用 junction 整理目录；
    /// 我们只拦"逃出根"的那一种。
    /// </summary>
    private static void EnsureNoLinkEscape(string fullRoot, string fullPath)
    {
        // 只对存在的部分检查；不存在的段下面不可能再有链接
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        if (relative is "." or "")
        {
            return;
        }

        var current = fullRoot;

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo? finalTarget = null;

            if (Directory.Exists(current))
            {
                finalTarget = Directory.ResolveLinkTarget(current, returnFinalTarget: true);
            }
            else if (File.Exists(current))
            {
                finalTarget = File.ResolveLinkTarget(current, returnFinalTarget: true);
            }
            else
            {
                // 该段尚不存在，其后更不可能存在链接，提前结束
                return;
            }

            if (finalTarget is null)
            {
                continue;   // 不是链接
            }

            var targetPath = finalTarget.FullName;

            if (!IsUnderRootPath(fullRoot, targetPath))
            {
                throw new PathEscapeException(
                    $"路径中包含指向共享根之外的链接：{current} → {targetPath}。" +
                    "共享盘目录必须为普通目录；如需整理结构请把链接目标移入根内。");
            }
        }
    }

    private static bool IsUnderRootPath(string fullRoot, string candidate) =>
        IsUnderRoot(fullRoot, candidate);

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
