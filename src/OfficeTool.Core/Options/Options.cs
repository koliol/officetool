namespace OfficeTool.Core.Options;

/// <summary>文件存储模式。</summary>
public enum StorageMode
{
    /// <summary>
    /// 本地/容器路径。群晖 NAS（bind mount 共享文件夹）与开发环境均用此模式，
    /// 无需任何凭据模拟——挂载进来的就是普通本地路径。
    /// </summary>
    Local = 0,

    /// <summary>Windows UNC 远程共享 + 凭据模拟（仅 Windows 主机可用）。</summary>
    Unc = 1,
}

/// <summary>
/// 存储与访问配置。
///
/// 关键区分（NAS 部署必须理解）：
/// - <c>TemplatesRoot</c> / <c>DataRoot</c>：**服务端（容器）视角**的路径，应用真正读写的位置。
///   容器里形如 <c>/data/Templates</c>。
/// - <c>ClientTemplatesRoot</c> / <c>ClientDataRoot</c>：**客户端视角**的路径，用于界面上展示给用户
///   去 Explorer 打开，也用于桌面插件。形如 <c>\\NAS\OfficeDocs\Templates</c>。
///
/// 两者指向同一份数据，只是从不同角度看。不配置 Client* 时直接回退展示服务端路径。
/// </summary>
public sealed class StorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Local（默认）或 Unc。</summary>
    public string Mode { get; set; } = nameof(StorageMode.Local);

    /// <summary>服务端视角：模板库根。</summary>
    public string TemplatesRoot { get; set; } = string.Empty;

    /// <summary>服务端视角：数据目录根。</summary>
    public string DataRoot { get; set; } = string.Empty;

    /// <summary>
    /// 服务端视角：附件库根。附件与模板**分开存放**（设计上互不干扰，
    /// 模板是可复用的样板，附件是一次性的待提取材料）。
    /// 留空时自动推导为 TemplatesRoot 的同级 Attachments 目录。
    /// </summary>
    public string AttachmentsRoot { get; set; } = string.Empty;

    /// <summary>客户端视角：模板库根（留空则回退 TemplatesRoot）。</summary>
    public string ClientTemplatesRoot { get; set; } = string.Empty;

    /// <summary>客户端视角：数据目录根（留空则回退 DataRoot）。</summary>
    public string ClientDataRoot { get; set; } = string.Empty;

    /// <summary>客户端视角：附件库根（留空则回退 AttachmentsRoot）。</summary>
    public string ClientAttachmentsRoot { get; set; } = string.Empty;

    /// <summary>
    /// 服务端视角：提取规则库根。
    ///
    /// 提取规则与模板/文档/附件一样是「按 项目/检项 两级存放的文件」，
    /// 建项目/检项时同步创建目录。留空时自动推导为 TemplatesRoot 的同级 ExtractionRules 目录。
    /// </summary>
    public string RulesRoot { get; set; } = string.Empty;

    /// <summary>客户端视角：提取规则库根（留空则回退 RulesRoot）。</summary>
    public string ClientRulesRoot { get; set; } = string.Empty;

    /// <summary>
    /// 服务端视角：回收站根。删除的文件物理移入此处，可恢复。
    /// 留空时自动推导为 TemplatesRoot 同级的 <c>_trash</c>。
    /// 必须与业务根处于同一挂载卷（群晖上同为 /data 下），否则跨设备 Move 会失败。
    /// </summary>
    public string TrashRoot { get; set; } = string.Empty;

    // ── 仅 Mode=Unc 使用（Windows UNC + 凭据模拟）─────────────────────

    public string Domain { get; set; } = string.Empty;

    public string UserName { get; set; } = string.Empty;

    /// <summary>加密后的密码（ASP.NET Core Data Protection）。</summary>
    public string EncryptedPassword { get; set; } = string.Empty;

    /// <summary>明文密码，仅运行期注入，不来自配置文件。</summary>
    public string? DecryptedPassword { get; set; }

    public bool IsUnc => string.Equals(Mode, nameof(StorageMode.Unc), StringComparison.OrdinalIgnoreCase);

    /// <summary>绑定后的规整与默认值填充。</summary>
    public StorageOptions WithDefaults()
    {
        if (!Enum.TryParse<StorageMode>(Mode, ignoreCase: true, out var parsed))
        {
            throw new InvalidOperationException(
                $"Storage:Mode 取值非法：{Mode}。允许 Local 或 Unc。");
        }

        Mode = parsed.ToString();

        if (string.IsNullOrWhiteSpace(TemplatesRoot) || string.IsNullOrWhiteSpace(DataRoot))
        {
            throw new InvalidOperationException("Storage:TemplatesRoot 与 Storage:DataRoot 不能为空。");
        }

        if (string.IsNullOrWhiteSpace(ClientTemplatesRoot))
        {
            ClientTemplatesRoot = TemplatesRoot;
        }

        if (string.IsNullOrWhiteSpace(ClientDataRoot))
        {
            ClientDataRoot = DataRoot;
        }

        // 附件库默认与模板库同级：/data/Templates → /data/Attachments
        if (string.IsNullOrWhiteSpace(AttachmentsRoot))
        {
            AttachmentsRoot = SiblingOf(TemplatesRoot, "Attachments");
        }

        if (string.IsNullOrWhiteSpace(ClientAttachmentsRoot))
        {
            ClientAttachmentsRoot = SiblingOf(ClientTemplatesRoot, "Attachments");
        }

        // 提取规则库同样与模板库同级
        if (string.IsNullOrWhiteSpace(RulesRoot))
        {
            RulesRoot = SiblingOf(TemplatesRoot, "ExtractionRules");
        }

        if (string.IsNullOrWhiteSpace(ClientRulesRoot))
        {
            ClientRulesRoot = SiblingOf(ClientTemplatesRoot, "ExtractionRules");
        }

        if (string.IsNullOrWhiteSpace(TrashRoot))
        {
            TrashRoot = SiblingOf(TemplatesRoot, "_trash");
        }

        return this;
    }

    /// <summary>
    /// 把路径的最后一段换成 <paramref name="leaf"/>，并保持原有的分隔符风格
    /// （UNC 路径用反斜杠，Linux 路径用正斜杠）。
    /// </summary>
    /// <remarks>
    /// 不能用 <see cref="Path.GetDirectoryName(string)"/>：在 Linux 上无法正确处理
    /// <c>\\NAS\OfficeDocs\Templates</c> 这种反斜杠路径。
    /// </remarks>
    private static string SiblingOf(string path, string leaf)
    {
        var useBackslash = path.Contains('\\', StringComparison.Ordinal);
        var normalized = path.Replace('\\', '/').TrimEnd('/');
        var index = normalized.LastIndexOf('/');

        var sibling = index <= 0 ? leaf : $"{normalized[..index]}/{leaf}";
        return useBackslash ? sibling.Replace('/', '\\') : sibling;
    }
}

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    /// <summary>
    /// 未配置时的默认白名单（设计文档 §5.3）。
    /// </summary>
    public static readonly string[] DefaultAllowedExtensions =
        [".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"];

    /// <summary>
    /// 附件的默认白名单：比模板宽。
    /// 附件是「待提取的原始材料」，实际业务里大量是扫描件、PDF、图片、导出报表，
    /// 所以刻意不限于 Office 格式。可在配置里收窄。
    /// </summary>
    public static readonly string[] DefaultAttachmentExtensions =
        [".pdf", ".ofd", ".docx", ".doc", ".xlsx", ".xls", ".pptx", ".ppt",
         ".txt", ".csv", ".xml", ".json", ".png", ".jpg", ".jpeg", ".bmp", ".tif", ".tiff"];

    /// <summary>
    /// 注意：初值必须为空数组。配置绑定对数组是「追加」语义，
    /// 若这里预置默认值，绑定后会出现重复项（实测会变成 12 项）。
    /// </summary>
    public string[] AllowedExtensions { get; set; } = [];

    /// <summary>附件白名单（留空取 <see cref="DefaultAttachmentExtensions"/>）。</summary>
    public string[] AttachmentExtensions { get; set; } = [];

    /// <summary>
    /// 提取规则的默认白名单。
    ///
    /// 规则的具体格式取决于后续编写的提取脚本，现在无法确定，因此给得比较宽：
    /// 配置类（json/xml/yaml/csv/txt）、脚本类（js/py/ps1）、表格类（xlsx）、文档类（docx/pdf）。
    /// ⚠️ 规则文件**只存储、不执行** —— 上传脚本不会带来执行风险，真正执行要等提取功能实现。
    /// 可用配置收窄到实际采用的格式。
    /// </summary>
    public static readonly string[] DefaultRuleExtensions =
        [".json", ".xml", ".yaml", ".yml", ".csv", ".txt", ".md",
         ".js", ".py", ".ps1", ".xlsx", ".docx", ".pdf"];

    /// <summary>提取规则白名单（留空取 <see cref="DefaultRuleExtensions"/>）。</summary>
    public string[] RuleExtensions { get; set; } = [];

    public int MaxSizeMB { get; set; } = 100;

    public long MaxSizeBytes => (long)MaxSizeMB * 1024 * 1024;

    /// <summary>应用默认值。绑定完成后调用。</summary>
    public UploadOptions WithDefaults()
    {
        if (AllowedExtensions.Length == 0)
        {
            AllowedExtensions = DefaultAllowedExtensions;
        }

        if (AttachmentExtensions.Length == 0)
        {
            AttachmentExtensions = DefaultAttachmentExtensions;
        }

        if (RuleExtensions.Length == 0)
        {
            RuleExtensions = DefaultRuleExtensions;
        }

        if (MaxSizeMB <= 0)
        {
            MaxSizeMB = 100;
        }

        return this;
    }
}

public sealed class DesktopPluginOptions
{
    public const string SectionName = "DesktopPlugin";

    public string DownloadUrl { get; set; } = string.Empty;

    public string Protocol { get; set; } = "officetool";
}

/// <summary>
/// 提取规则**不再来自配置**。
///
/// 早期版本把规则做成 <c>Extraction:Rules</c> 配置数组，但实际需求是：
/// 规则要能上传、能有备注、能在网页上按文件夹管理并复制到别的文件夹 ——
/// 这些都是「文件 + 目录」的形态，配置数组无法承载。
///
/// 现在规则是存放在 <c>ExtractionRules\{项目}\{检项}\</c> 下的**文件**
/// （见 <c>StorageOptions.RulesRoot</c> 与 <c>PathLayout.RuleDirectory</c>），
/// 由 <c>RuleCatalogService</c> 负责，备注存在数据库的 <c>RuleNotes</c> 表。
///
/// ⚠️ 规则文件**只存储、不执行**。真正的执行要等提取脚本实现，
/// 接入点是 <c>AttachmentService.ExtractAsync</c>。
/// </summary>
internal static class ExtractionFeatureNotes
{
    // 仅作为设计说明的载体，无成员。
}
