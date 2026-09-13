namespace OfficeTool.Desktop;

/// <summary>
/// 托盘常驻上下文。没有主窗体，所有交互都通过 <see cref="NotifyIcon"/> 完成。
/// </summary>
internal sealed class TrayApplicationContext : ApplicationContext
{
    private readonly PluginConfig _config;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _autoStartItem;

    /// <summary>勾选「开机自启」会触发 CheckedChanged，用它避免回滚状态时递归。</summary>
    private bool _suppressAutoStartToggle;

    private bool _disposed;

    /// <summary>构建托盘图标与右键菜单。</summary>
    public TrayApplicationContext(PluginConfig config)
    {
        _config = config;

        _autoStartItem = new ToolStripMenuItem("开机自启")
        {
            CheckOnClick = true,
            Checked = SafeIsAutoStartEnabled(),
        };
        _autoStartItem.CheckedChanged += OnAutoStartChanged;

        _menu = new ContextMenuStrip();
        _menu.Items.Add(new ToolStripMenuItem("打开共享文件夹", null, OnOpenShareFolder));
        _menu.Items.Add(_autoStartItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("重新注册协议", null, OnReRegister));
        _menu.Items.Add(new ToolStripMenuItem("关于", null, OnAbout));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("退出", null, OnExit));

        _notifyIcon = new NotifyIcon
        {
            // SystemIcons 返回的是共享实例，NotifyIcon 释放时会连带释放它，
            // 所以这里克隆一份，避免影响到进程内其他使用者。
            Icon = (Icon)SystemIcons.Application.Clone(),
            // NotifyIcon.Text 有 63 字符上限，这里是 9 个汉字，安全。
            Text = Program.DisplayName,
            ContextMenuStrip = _menu,
            Visible = true,
        };

        // 双击托盘图标等价于弹出菜单。
        _notifyIcon.DoubleClick += OnTrayDoubleClick;

        ShowBalloon("插件已启动", $"共享目录：{_config.ShareRoot}");
    }

    /// <summary>释放托盘资源，防止退出后通知区域残留「幽灵图标」。</summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;

            try
            {
                _notifyIcon.Visible = false;
            }
            catch (Exception ex)
            {
                AppLog.Error("隐藏托盘图标失败", ex);
            }

            _notifyIcon.Dispose();
            _menu.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OnOpenShareFolder(object? sender, EventArgs e) => OpenShareFolder();

    private void OnTrayDoubleClick(object? sender, EventArgs e) => ShowMenu();

    private void OnExit(object? sender, EventArgs e) => ExitApplication();

    /// <summary>用资源管理器打开配置里的共享根目录。</summary>
    private void OpenShareFolder()
    {
        var root = _config.ShareRoot;
        AppLog.Info($"打开共享文件夹：{root}");

        if (FileLauncher.TryOpen(root, out var error))
        {
            return;
        }

        MessageBox.Show(
            "无法打开共享文件夹：" + Environment.NewLine + root + Environment.NewLine +
            Environment.NewLine + $"系统返回：{error}" + Environment.NewLine +
            "请确认已连接内网，且该共享目录可访问。",
            Program.DisplayName,
            MessageBoxButtons.OK,
            MessageBoxIcon.Warning);
    }

    /// <summary>切换开机自启：同步写注册表，失败则回滚勾选状态。</summary>
    private void OnAutoStartChanged(object? sender, EventArgs e)
    {
        if (_suppressAutoStartToggle)
        {
            return;
        }

        var enabled = _autoStartItem.Checked;

        try
        {
            if (enabled)
            {
                AutoStartManager.Enable(Program.CurrentExePath());
            }
            else
            {
                AutoStartManager.Disable();
            }

            AppLog.Info(enabled ? "已开启开机自启。" : "已关闭开机自启。");
            ShowBalloon("开机自启", enabled ? "已开启" : "已关闭");
        }
        catch (Exception ex)
        {
            AppLog.Error("修改开机自启失败", ex);
            MessageBox.Show(
                $"修改开机自启失败：{ex.Message}",
                Program.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);

            // 回滚到注册表里的真实状态，避免界面与系统不一致。
            _suppressAutoStartToggle = true;
            try
            {
                _autoStartItem.Checked = SafeIsAutoStartEnabled();
            }
            finally
            {
                _suppressAutoStartToggle = false;
            }
        }
    }

    /// <summary>重新注册协议（比如 exe 被挪了目录之后）。</summary>
    private void OnReRegister(object? sender, EventArgs e)
    {
        var exePath = Program.CurrentExePath();

        try
        {
            ProtocolRegistrar.Register(exePath);
            AppLog.Info($"已重新注册协议：{exePath}");
            ShowBalloon("协议已重新注册", $"officetool:// → {exePath}");
        }
        catch (Exception ex)
        {
            AppLog.Error("重新注册协议失败", ex);
            MessageBox.Show(
                $"重新注册协议失败：{ex.Message}",
                Program.DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
    }

    private void OnAbout(object? sender, EventArgs e)
    {
        var version = typeof(Program).Assembly.GetName().Version?.ToString() ?? "1.0.0";

        var protocolState = "读取失败";
        try
        {
            protocolState = ProtocolRegistrar.IsRegistered() ? "已注册" : "未注册";
        }
        catch (Exception ex)
        {
            AppLog.Error("读取协议状态失败", ex);
        }

        var text = string.Join(Environment.NewLine, new[]
        {
            Program.DisplayName,
            $"版本：{version}",
            string.Empty,
            $"程序路径：{Program.CurrentExePath()}",
            $"共享根目录：{_config.ShareRoot}",
            $"配置文件：{PluginConfig.ConfigFilePath}",
            $"协议状态：{protocolState}（officetool://，注册于 HKEY_CURRENT_USER）",
            $"运行日志：{AppLog.LogFilePath}",
        });

        MessageBox.Show(text, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowMenu()
    {
        try
        {
            _menu.Show(Cursor.Position);
        }
        catch (Exception ex)
        {
            AppLog.Error("弹出托盘菜单失败", ex);
        }
    }

    private void ShowBalloon(string title, string text)
    {
        try
        {
            _notifyIcon.BalloonTipTitle = title;
            _notifyIcon.BalloonTipText = text;
            _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
            _notifyIcon.ShowBalloonTip(3000);
        }
        catch (Exception ex)
        {
            AppLog.Error("显示气泡提示失败", ex);
        }
    }

    private void ExitApplication()
    {
        AppLog.Info("用户从托盘菜单退出。");
        _notifyIcon.Visible = false;
        ExitThread();
    }

    private static bool SafeIsAutoStartEnabled()
    {
        try
        {
            return AutoStartManager.IsEnabled();
        }
        catch (Exception ex)
        {
            AppLog.Error("读取开机自启状态失败", ex);
            return false;
        }
    }
}
