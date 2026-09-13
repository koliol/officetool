using System.Text.Json;

namespace OfficeTool.Desktop;

/// <summary>
/// 插件配置，来自 <c>%APPDATA%\OfficeTool\plugin.json</c>。
/// 文件不存在、内容为空或格式非法时，一律回退到默认共享根目录。
/// </summary>
internal sealed class PluginConfig
{
    /// <summary>默认共享根目录。</summary>
    public const string DefaultShareRoot = @"\\mynas\OfficeDocs";

    /// <summary>共享根目录，例如 <c>\\mynas\OfficeDocs</c>。</summary>
    public string ShareRoot { get; set; } = DefaultShareRoot;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // 允许手写配置时使用 shareRoot / ShareRoot 等不同大小写，也容忍注释与多余逗号。
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>配置文件目录：<c>%APPDATA%\OfficeTool</c>。</summary>
    public static string ConfigDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OfficeTool");

    /// <summary>配置文件路径：<c>%APPDATA%\OfficeTool\plugin.json</c>。</summary>
    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "plugin.json");

    /// <summary>
    /// 读取配置。任何异常都被吞掉并回退到默认值——插件必须能启动，
    /// 不能因为一个手写坏的 json 就打不开文件。
    /// </summary>
    public static PluginConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                AppLog.Info($"未找到配置文件，使用默认共享根：{DefaultShareRoot}");
                return new PluginConfig();
            }

            var json = File.ReadAllText(ConfigFilePath);
            var loaded = JsonSerializer.Deserialize<PluginConfig>(json, SerializerOptions);

            if (loaded is null || string.IsNullOrWhiteSpace(loaded.ShareRoot))
            {
                AppLog.Info($"配置文件缺少 ShareRoot，使用默认共享根：{DefaultShareRoot}");
                return new PluginConfig();
            }

            var config = new PluginConfig { ShareRoot = loaded.ShareRoot.Trim() };
            AppLog.Info($"已读取配置，共享根：{config.ShareRoot}");
            return config;
        }
        catch (Exception ex)
        {
            AppLog.Error($"读取 {ConfigFilePath} 失败，改用默认配置", ex);
            return new PluginConfig();
        }
    }
}
