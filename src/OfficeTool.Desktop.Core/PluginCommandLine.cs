namespace OfficeTool.Desktop.Core;

/// <summary>插件的启动模式。</summary>
public enum PluginMode
{
    /// <summary>无参数：注册协议（如需）并进入托盘常驻。</summary>
    Tray,

    /// <summary><c>--install</c>：注册协议 + 开机自启，然后退出。</summary>
    Install,

    /// <summary><c>--uninstall</c>：注销协议 + 取消开机自启，然后退出。</summary>
    Uninstall,

    /// <summary>被浏览器通过 officetool:// 唤起：打开文件后立即退出。</summary>
    Open,
}

/// <summary>
/// 把命令行参数归类成启动模式。放在 Core 里是为了让「被浏览器唤起」这条主路径
/// 也能在 Linux 上跑单元测试。
/// </summary>
public sealed class PluginCommandLine
{
    /// <summary>安装开关。</summary>
    public const string InstallSwitch = "--install";

    /// <summary>卸载开关。</summary>
    public const string UninstallSwitch = "--uninstall";

    private PluginCommandLine(PluginMode mode, string? rawUri, ProtocolParseResult? protocolResult)
    {
        Mode = mode;
        RawUri = rawUri;
        ProtocolResult = protocolResult;
    }

    /// <summary>本次启动的模式。</summary>
    public PluginMode Mode { get; }

    /// <summary>原始协议地址（仅 <see cref="PluginMode.Open"/> 有值）。</summary>
    public string? RawUri { get; }

    /// <summary>协议解析结果（仅 <see cref="PluginMode.Open"/> 有值，失败时也可能带错误信息）。</summary>
    public ProtocolParseResult? ProtocolResult { get; }

    /// <summary>
    /// 解析命令行。优先级：<c>--install</c> &gt; <c>--uninstall</c> &gt; 协议地址 &gt; 托盘。
    /// </summary>
    public static PluginCommandLine Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new PluginCommandLine(PluginMode.Tray, null, null);
        }

        foreach (var arg in args)
        {
            if (IsSwitch(arg, InstallSwitch))
            {
                return new PluginCommandLine(PluginMode.Install, null, null);
            }
        }

        foreach (var arg in args)
        {
            if (IsSwitch(arg, UninstallSwitch))
            {
                return new PluginCommandLine(PluginMode.Uninstall, null, null);
            }
        }

        foreach (var arg in args)
        {
            if (ProtocolUriParser.IsProtocolUri(arg))
            {
                return new PluginCommandLine(PluginMode.Open, arg, ProtocolUriParser.Parse(arg));
            }
        }

        return new PluginCommandLine(PluginMode.Tray, null, null);
    }

    private static bool IsSwitch(string value, string name) =>
        value.Equals(name, StringComparison.OrdinalIgnoreCase);
}
