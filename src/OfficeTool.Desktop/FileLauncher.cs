using System.Diagnostics;

namespace OfficeTool.Desktop;

/// <summary>用系统默认程序打开文件/目录/URL 的统一入口。</summary>
internal static class FileLauncher
{
    /// <summary>
    /// 用系统默认程序打开 <paramref name="target"/>。
    /// </summary>
    /// <remarks>
    /// 必须把 <see cref="ProcessStartInfo.UseShellExecute"/> 设为 <c>true</c>
    /// （.NET Core 起默认是 <c>false</c>），否则 UNC 路径和自定义协议都不会交给 Shell 处理。
    /// 这里不做 File.Exists 预检：共享目录不可达时预检会长时间阻塞，
    /// 而「被浏览器唤起」这条路径对响应速度很敏感，交给 Shell 快速失败并提示更合适。
    /// </remarks>
    public static bool TryOpen(string target, out string? error)
    {
        error = null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Error($"打开失败：{target}", ex);
            return false;
        }
    }
}
