using OfficeTool.Desktop.Core;

namespace OfficeTool.Desktop;

/// <summary>
/// 插件入口。四种启动方式：
/// <list type="bullet">
///   <item>无参数：自动注册协议（如需）后进入托盘常驻</item>
///   <item><c>--install</c>：注册协议 + 开启开机自启，然后退出</item>
///   <item><c>--uninstall</c>：注销协议 + 关闭开机自启，然后退出</item>
///   <item><c>officetool://open?path=...</c>：被浏览器唤起，打开文件后立刻退出（最常见）</item>
/// </list>
/// </summary>
internal static class Program
{
    /// <summary>界面标题，也是托盘提示文字。</summary>
    internal const string DisplayName = "Office 文档管理插件";

    /// <summary>托盘常驻实例的互斥体名（<c>Local\</c> 只在本机会话内生效）。</summary>
    private const string TrayMutexName = @"Local\OfficeTool.Desktop.Tray";

    [STAThread]
    private static int Main(string[] args)
    {
        // 这三句必须在创建任何窗口之前调用。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        _ = Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        AppLog.Info($"启动，命令行参数：[{string.Join(" ", args)}]");

        var commandLine = PluginCommandLine.Parse(args);
        var exePath = CurrentExePath();

        try
        {
            switch (commandLine.Mode)
            {
                case PluginMode.Install:
                    return RunInstall(exePath);
                case PluginMode.Uninstall:
                    return RunUninstall();
                case PluginMode.Open:
                    // Parse 在 Open 模式下必定带上解析结果（成功或失败）。
                    return RunOpen(commandLine.ProtocolResult!);
                default:
                    return RunTray(exePath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("未处理的异常", ex);
            MessageBox.Show(
                $"插件运行出错：{ex.Message}",
                DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return 1;
        }
    }

    /// <summary>
    /// 最常见、也最要求响应速度的路径：浏览器唤起 → 打开文件 → 立刻退出。
    /// </summary>
    private static int RunOpen(ProtocolParseResult result)
    {
        if (!result.Success)
        {
            AppLog.Error($"协议地址解析失败：{result.Error}");
            MessageBox.Show(
                $"无法识别协议地址：{result.Error}",
                DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
            return 2;
        }

        var path = result.Path!;
        AppLog.Info($"打开文件：{path}");

        if (FileLauncher.TryOpen(path, out var error))
        {
            return 0;
        }

        MessageBox.Show(
            "无法打开文件：" + Environment.NewLine + path + Environment.NewLine +
            Environment.NewLine + $"系统返回：{error}" + Environment.NewLine +
            "请确认已连接内网，且该文件所在的共享目录可访问。",
            DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
        return 3;
    }

    /// <summary>注册协议 + 开机自启。</summary>
    private static int RunInstall(string exePath)
    {
        ProtocolRegistrar.Register(exePath);
        AutoStartManager.Enable(exePath);
        AppLog.Info($"已注册协议并开启开机自启：{exePath}");

        MessageBox.Show(
            "注册完成。" + Environment.NewLine + Environment.NewLine +
            $"协议：officetool:// → {exePath}" + Environment.NewLine +
            "开机自启：已开启" + Environment.NewLine + Environment.NewLine +
            "以上信息写在 HKEY_CURRENT_USER 下，未使用管理员权限。",
            DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>注销协议 + 关闭开机自启。</summary>
    private static int RunUninstall()
    {
        ProtocolRegistrar.Unregister();
        AutoStartManager.Disable();
        AppLog.Info("已注销协议并关闭开机自启。");

        MessageBox.Show(
            "已清理注册信息。" + Environment.NewLine + Environment.NewLine +
            "协议：officetool:// 已注销" + Environment.NewLine +
            "开机自启：已关闭" + Environment.NewLine + Environment.NewLine +
            "（程序文件本身与 %APPDATA%\\OfficeTool\\ 下的配置、日志未删除）",
            DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
        return 0;
    }

    /// <summary>进入托盘常驻。</summary>
    private static int RunTray(string exePath)
    {
        // 只允许一个托盘实例，否则通知区域会出现多个图标。
        using var mutex = new Mutex(initiallyOwned: true, TrayMutexName, out var isFirstInstance);

        // 首次运行（或 exe 被移动、协议被清理）时自动补注册，用户不必手动执行 --install。
        EnsureProtocolRegistered(exePath);

        if (!isFirstInstance)
        {
            AppLog.Info("检测到已有托盘实例在运行，本次启动直接退出。");
            MessageBox.Show(
                "Office 文档管理插件已经在运行，请查看任务栏通知区域。",
                DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return 0;
        }

        using var context = new TrayApplicationContext(PluginConfig.Load());
        Application.Run(context);
        return 0;
    }

    /// <summary>
    /// 协议未注册、或注册的命令行没有指向当前 exe 时，重新注册。
    /// </summary>
    private static void EnsureProtocolRegistered(string exePath)
    {
        try
        {
            var expected = RegistryCommandBuilder.BuildProtocolOpenCommand(exePath);
            if (string.Equals(ProtocolRegistrar.GetRegisteredCommand(), expected, StringComparison.OrdinalIgnoreCase))
            {
                AppLog.Info("协议已注册且指向当前 exe，跳过注册。");
                return;
            }

            ProtocolRegistrar.Register(exePath);
            AppLog.Info($"已自动注册协议：{expected}");
        }
        catch (Exception ex)
        {
            // 注册失败不应该阻止托盘启动。
            AppLog.Error("自动注册协议失败（不影响本次运行）", ex);
        }
    }

    /// <summary>
    /// 当前 exe 的完整路径。单文件发布时 <see cref="Environment.ProcessPath"/>
    /// 指向 apphost 本体，正是注册表命令行需要的路径。
    /// </summary>
    internal static string CurrentExePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? Application.ExecutablePath : path;
    }
}
