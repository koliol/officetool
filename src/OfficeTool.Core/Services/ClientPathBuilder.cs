namespace OfficeTool.Core.Services;

/// <summary>
/// 生成「客户端可访问路径」。
///
/// 群晖 NAS 部署下这是最容易搞错的一环：
/// - 服务端（容器）看到的是 <c>/data/Data/QLS2409/SEC/20260912.docx</c>
/// - 客户端（Windows 资源管理器 / 桌面插件）需要 <c>\\NAS\OfficeDocs\Data\QLS2409\SEC\20260912.docx</c>
///
/// 同一份数据，两个视角。本类负责按配置把「相对存储根的路径」拼成后者。
/// 未配置客户端根时回退为服务端物理路径（开发环境可接受）。
/// </summary>
public static class ClientPathBuilder
{
    /// <param name="clientRoot">客户端视角的根，如 \\NAS\OfficeDocs\Data。留空则用 serverRoot。</param>
    /// <param name="serverRoot">服务端视角的根，如 /data/Data。</param>
    /// <param name="relativePath">相对根的路径，反斜杠形式，如 QLS2409\SEC\20260912.docx。</param>
    public static string Build(string? clientRoot, string serverRoot, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var root = string.IsNullOrWhiteSpace(clientRoot) ? serverRoot : clientRoot;
        var normalized = PathGuard.NormalizeRoot(root);

        // 客户端根是 UNC / Windows 形式 → 直接拼反斜杠。
        // 注意：不要用 Path.Combine —— 在 Linux 上它会把反斜杠当普通字符，
        // 生成 "\\NAS\OfficeDocs\Data/QLS2409\SEC\x.docx" 这种混合分隔符的怪路径。
        if (normalized.Contains('\\', StringComparison.Ordinal))
        {
            var relative = relativePath.Replace('/', '\\').TrimStart('\\');
            return $"{normalized}\\{relative}";
        }

        // 回退到服务端路径（Linux 开发环境）
        return Path.Combine(normalized, relativePath.Replace('\\', Path.DirectorySeparatorChar));
    }
}
