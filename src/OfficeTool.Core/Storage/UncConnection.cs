using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using OfficeTool.Core.Exceptions;

namespace OfficeTool.Core.Storage;

/// <summary>
/// SMB 凭据模拟（设计文档 §7.2）。
/// 主方案：NuGet 封装库（Vshed.IO.UncShare 或同类）。
/// 降级方案：直接 P/Invoke <c>WNetAddConnection2</c>，即本类实现——不引入第三方依赖，
/// 避免设计文档 §15 风险表里「Vshed.IO.UncShare 兼容性不确定」的风险。
/// </summary>
[SupportedOSPlatform("windows")]
public static class UncConnection
{
    private const int ResourceTypeDisk = 0x00000001;

    /// <summary>禁止系统弹出凭据输入框，凭据不对就直接返回错误码。</summary>
    private const int ConnectInteractive = 0x00000008;

    private static readonly object Gate = new();

    /// <summary>已建立连接的共享根 → 建立时间。到期后重新探测，**只增不减**的旧实现在这里踩过坑：
    /// 共享服务器重启/网络闪断后，缓存里仍是"已连接"，之后所有 IO 一直失败到进程重启。</summary>
    private static readonly Dictionary<string, DateTime> Connected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>超过这个时长后，下一次访问会先探测共享是否仍然可达。</summary>
    private static readonly TimeSpan RevalidateAfter = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 确保到 <paramref name="uncPath"/> 所在共享的连接可用。
    /// </summary>
    /// <param name="uncPath">UNC 路径，如 \\server\share\OfficeDocs\Data</param>
    public static void EnsureConnected(string uncPath, string? userName, string? password, string? domain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformUnsupportedException(
                "SMB 凭据模拟依赖 Windows mpr.dll，当前主机不是 Windows，无法建立 UNC 连接。");
        }

        var shareRoot = GetShareRoot(uncPath);

        lock (Gate)
        {
            var known = Connected.TryGetValue(shareRoot, out var connectedAt);

            if (known && DateTime.UtcNow - connectedAt < RevalidateAfter)
            {
                return;
            }

            // 缓存过期：先探测共享是否仍然可达。探测成功就刷新时间戳，
            // 不必每次都打 Win32（WNetAddConnection2 对已连接共享可能返回 1219）。
            if (known && Directory.Exists(shareRoot))
            {
                Connected[shareRoot] = DateTime.UtcNow;
                return;
            }

            if (string.IsNullOrWhiteSpace(userName))
            {
                // 未配置凭据：依赖进程身份（域账号或 ApplicationPoolIdentity）直连。
                // 这里不能只记状态不做检查 —— 探测失败要立刻给出可读错误，
                // 否则用户看到的是几分钟后某个 IO 操作里一句没头没尾的异常。
                if (!Directory.Exists(shareRoot))
                {
                    throw new IOException(
                        $"无法访问共享 {shareRoot}（进程身份直连）。请确认共享可达、当前账号有权限。");
                }

                Connected[shareRoot] = DateTime.UtcNow;
                return;
            }

            var qualifiedUser = string.IsNullOrWhiteSpace(domain)
                ? userName
                : $"{domain}\\{userName}";

            var resource = new NetResource
            {
                dwType = ResourceTypeDisk,
                lpRemoteName = shareRoot,
            };

            var result = WNetAddConnection2(ref resource, password, qualifiedUser, ConnectInteractive);
            if (result != 0)
            {
                // 失败时清掉缓存条目：让下一次请求重新尝试，而不是继续信旧状态
                Connected.Remove(shareRoot);

                throw new IOException(
                    $"连接 SMB 共享失败：{shareRoot}，Win32 错误码 {result}（{DescribeError(result)}）。" +
                    "请检查专用账号、密码、共享路径与网络连通性。");
            }

            Connected[shareRoot] = DateTime.UtcNow;
        }
    }

    /// <summary>主动失效某个共享的连接状态。检测到 IO 异常时可调用，下一次访问会重新探测。</summary>
    public static void Invalidate(string uncPath)
    {
        lock (Gate)
        {
            Connected.Remove(GetShareRoot(uncPath));
        }
    }

    /// <summary>从 UNC 路径提取共享根 \\server\share。</summary>
    public static string GetShareRoot(string uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath))
        {
            throw new ArgumentException("UNC 路径不能为空。", nameof(uncPath));
        }

        var normalized = uncPath.Replace('/', '\\').TrimEnd('\\');
        if (!normalized.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ArgumentException($"不是合法的 UNC 路径：{uncPath}", nameof(uncPath));
        }

        var parts = normalized[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new ArgumentException($"UNC 路径至少应包含服务器与共享名：{uncPath}", nameof(uncPath));
        }

        return $@"\\{parts[0]}\{parts[1]}";
    }

    private static string DescribeError(int code) => code switch
    {
        5 => "拒绝访问，账号或密码错误",
        53 => "找不到网络路径",
        67 => "找不到网络名",
        86 => "指定的网络密码不正确",
        1219 => "同一服务器已有冲突的已连接凭据，需先断开旧连接",
        1326 => "用户名或密码错误",
        _ => "未知错误",
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource
    {
        public int dwScope;
        public int dwType;
        public int dwDisplayType;
        public int dwUsage;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpLocalName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpRemoteName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpComment;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpProvider;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int WNetAddConnection2(
        ref NetResource lpNetResource,
        string? lpPassword,
        string? lpUsername,
        int dwFlags);
}
