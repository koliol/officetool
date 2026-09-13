using Microsoft.Win32;
using OfficeTool.Desktop.Core;
using System.Runtime.InteropServices;

namespace OfficeTool.Desktop;

/// <summary>
/// 在 <c>HKEY_CURRENT_USER</c> 下注册/注销 <c>officetool://</c> 协议。
/// </summary>
/// <remarks>
/// 刻意只写 HKCU 而不是 HKLM / HKCR：
/// HKCR 是 HKLM\Software\Classes 与 HKCU\Software\Classes 的合并视图，
/// 写 HKCU 既能被系统识别，又完全不需要管理员权限。
/// </remarks>
internal static class ProtocolRegistrar
{
    /// <summary>Shell 关联已变更事件，通知资源管理器/浏览器重新读取协议处理程序。</summary>
    private const int ShcneAssocChanged = 0x08000000;

    private const uint ShcnfIdList = 0x0000;

    /// <summary>
    /// 注册协议，并把打开命令指向 <paramref name="exePath"/>。
    /// </summary>
    public static void Register(string exePath)
    {
        using var protocolKey = Registry.CurrentUser.CreateSubKey(RegistryCommandBuilder.ProtocolKeyPath, true);

        // 协议默认值：显示在「设置 → 默认应用」等处的友好名称。
        protocolKey.SetValue(string.Empty, RegistryCommandBuilder.ProtocolFriendlyName, RegistryValueKind.String);

        // 关键字段：这个值必须存在（内容允许为空字符串），Windows 才会把它当成 URL 协议。
        protocolKey.SetValue("URL Protocol", string.Empty, RegistryValueKind.String);

        using (var iconKey = protocolKey.CreateSubKey("DefaultIcon", true))
        {
            iconKey.SetValue(string.Empty, RegistryCommandBuilder.BuildDefaultIconValue(exePath), RegistryValueKind.String);
        }

        using (var commandKey = protocolKey.CreateSubKey(@"shell\open\command", true))
        {
            // 浏览器会把完整地址（officetool://...）作为第一个参数追加到这一行后面。
            commandKey.SetValue(string.Empty, RegistryCommandBuilder.BuildProtocolOpenCommand(exePath), RegistryValueKind.String);
        }

        NotifyShellAssociationChanged();
    }

    /// <summary>注销协议。不存在时静默返回，方便重复执行。</summary>
    public static void Unregister()
    {
        Registry.CurrentUser.DeleteSubKeyTree(RegistryCommandBuilder.ProtocolKeyPath, throwOnMissingSubKey: false);
        NotifyShellAssociationChanged();
    }

    /// <summary>协议是否已注册。</summary>
    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryCommandBuilder.ProtocolKeyPath);
        return key?.GetValue("URL Protocol") is not null;
    }

    /// <summary>读取当前注册的打开命令，用于判断是否需要重新注册。</summary>
    public static string? GetRegisteredCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryCommandBuilder.ProtocolCommandKeyPath);
        return key?.GetValue(string.Empty) as string;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);

    /// <summary>
    /// 通知 Shell 关联已变更。失败不影响注册结果，因此吞掉异常。
    /// </summary>
    private static void NotifyShellAssociationChanged()
    {
        try
        {
            SHChangeNotify(ShcneAssocChanged, ShcnfIdList, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            AppLog.Error("通知 Shell 刷新协议关联失败（可忽略）", ex);
        }
    }
}
