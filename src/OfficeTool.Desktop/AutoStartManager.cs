using Microsoft.Win32;
using OfficeTool.Desktop.Core;

namespace OfficeTool.Desktop;

/// <summary>
/// 开机自启管理，写的是当前用户的 Run 键，同样不需要管理员权限。
/// </summary>
internal static class AutoStartManager
{
    /// <summary>开启开机自启。</summary>
    public static void Enable(string exePath)
    {
        using var runKey = Registry.CurrentUser.CreateSubKey(RegistryCommandBuilder.AutoStartKeyPath, true);
        runKey.SetValue(
            RegistryCommandBuilder.AutoStartValueName,
            RegistryCommandBuilder.BuildAutoStartCommand(exePath),
            RegistryValueKind.String);
    }

    /// <summary>关闭开机自启。不存在时静默返回，方便重复执行。</summary>
    public static void Disable()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RegistryCommandBuilder.AutoStartKeyPath, true);
        runKey?.DeleteValue(RegistryCommandBuilder.AutoStartValueName, throwOnMissingValue: false);
    }

    /// <summary>当前是否已开启开机自启。</summary>
    public static bool IsEnabled()
    {
        using var runKey = Registry.CurrentUser.OpenSubKey(RegistryCommandBuilder.AutoStartKeyPath);
        return runKey?.GetValue(RegistryCommandBuilder.AutoStartValueName) is string value && value.Length > 0;
    }
}
