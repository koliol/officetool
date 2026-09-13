namespace OfficeTool.Desktop;

/// <summary>
/// 极简日志：写到 <c>%APPDATA%\OfficeTool\plugin.log</c>。
/// </summary>
/// <remarks>
/// 插件大多是被浏览器静默唤起的（没有窗口、立刻退出），出问题时几乎无法排查，
/// 因此把关键步骤记到文件里。日志功能本身出错绝不能影响主流程，故所有异常都被吞掉。
/// </remarks>
internal static class AppLog
{
    /// <summary>超过这个大小就清空重来，避免长期驻留把日志撑大。</summary>
    private const long MaxLogBytes = 1024 * 1024;

    private static readonly object Gate = new();

    /// <summary>日志文件路径。</summary>
    public static string LogFilePath => Path.Combine(PluginConfig.ConfigDirectory, "plugin.log");

    /// <summary>记录一条普通信息。</summary>
    public static void Info(string message) => Write("INFO ", message);

    /// <summary>记录一条错误。</summary>
    public static void Error(string message, Exception? exception = null) =>
        Write("ERROR", exception is null ? message : $"{message} :: {exception.GetType().Name}: {exception.Message}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(PluginConfig.ConfigDirectory);

                var file = new FileInfo(LogFilePath);
                if (file.Exists && file.Length > MaxLogBytes)
                {
                    file.Delete();
                }

                File.AppendAllText(
                    LogFilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败不影响插件功能。
        }
    }
}
