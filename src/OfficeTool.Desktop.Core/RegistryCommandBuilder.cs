namespace OfficeTool.Desktop.Core;

/// <summary>
/// 生成要写进注册表的字符串。全部是纯函数，不接触真实注册表，因此可以在 Linux 上直接测试。
/// </summary>
/// <remarks>
/// 注册表结构（写在 HKCU 下，无需管理员权限；HKCR 只是 HKLM\Software\Classes 与
/// HKCU\Software\Classes 的合并视图，所以优先写 HKCU）：
/// <code>
/// HKEY_CURRENT_USER
///   Software\Classes\officetool
///     (默认)        = "URL:Office 文档管理插件"
///     URL Protocol  = ""                     ← 值必须存在（内容为空），浏览器才认它是协议
///     DefaultIcon
///       (默认)      = "C:\...\OfficeToolPlugin.exe",0
///     shell\open\command
///       (默认)      = "C:\...\OfficeToolPlugin.exe" "%1"
/// </code>
/// </remarks>
public static class RegistryCommandBuilder
{
    /// <summary>协议名。</summary>
    public const string UriScheme = ProtocolUriParser.Scheme;

    /// <summary>命令行里代表被打开地址的占位符，由 Windows 替换。</summary>
    public const string ArgumentPlaceholder = "%1";

    /// <summary>写入协议项默认值的友好名称。</summary>
    public const string ProtocolFriendlyName = "URL:Office 文档管理插件";

    /// <summary>开机自启在 Run 键里使用的值名。</summary>
    public const string AutoStartValueName = "OfficeToolPlugin";

    /// <summary>协议项路径（相对 HKCU）。</summary>
    public const string ProtocolKeyPath = @"Software\Classes\" + UriScheme;

    /// <summary>协议命令行项路径（相对 HKCU）。</summary>
    public const string ProtocolCommandKeyPath = ProtocolKeyPath + @"\shell\open\command";

    /// <summary>协议图标项路径（相对 HKCU）。</summary>
    public const string ProtocolIconKeyPath = ProtocolKeyPath + @"\DefaultIcon";

    /// <summary>开机自启项路径（相对 HKCU）。</summary>
    public const string AutoStartKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 把 exe 路径包成带双引号的命令行片段，避免路径里有空格时被截断。
    /// </summary>
    public static string QuoteExecutablePath(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new ArgumentException("exe 路径不能为空。", nameof(exePath));
        }

        if (exePath.IndexOf('"') >= 0)
        {
            throw new ArgumentException("exe 路径不能包含双引号。", nameof(exePath));
        }

        return "\"" + exePath + "\"";
    }

    /// <summary>
    /// 生成 <c>shell\open\command</c> 的值，例如
    /// <c>"C:\OfficeTool\OfficeToolPlugin.exe" "%1"</c>。
    /// 这是浏览器唤起插件时真正执行的命令行，<c>"%1"</c> 必须带引号，
    /// 否则路径含空格时会被拆成多个参数。
    /// </summary>
    public static string BuildProtocolOpenCommand(string exePath) =>
        QuoteExecutablePath(exePath) + " \"" + ArgumentPlaceholder + "\"";

    /// <summary>
    /// 生成开机自启的值。只带 exe 路径：无参数启动即进入托盘常驻。
    /// </summary>
    public static string BuildAutoStartCommand(string exePath) => QuoteExecutablePath(exePath);

    /// <summary>
    /// 生成 <c>DefaultIcon</c> 的值，例如 <c>"C:\OfficeTool\OfficeToolPlugin.exe",0</c>
    /// （0 表示取第 1 个图标资源）。
    /// </summary>
    public static string BuildDefaultIconValue(string exePath) => QuoteExecutablePath(exePath) + ",0";
}
