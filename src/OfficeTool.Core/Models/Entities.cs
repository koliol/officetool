namespace OfficeTool.Core.Models;

/// <summary>
/// 用户来源：域账户（经 Negotiate + LDAP 同步）或应用本地账户。
/// </summary>
public enum UserSource
{
    /// <summary>域账户。密码不在本系统内，由 Kerberos/LDAP 校验。</summary>
    Ad = 0,

    /// <summary>应用本地账户。密码哈希存 <see cref="User.PasswordHash"/>。</summary>
    Local = 1,
}

/// <summary>
/// 权限等级。**递进**关系：高级别自动包含低级别的全部能力。
///
/// <code>规则管理 ⊃ 管理 ⊃ 编辑 ⊃ 只读</code>
///
/// 与位标志方案的取舍：位标志能表达「能维护规则但不能删文档」这类组合，
/// 但实际授权里这类组合几乎不存在，代价却是授权页要四个 checkbox、
/// 且容易配出「能删但不能看」的怪组合。递进等级只需一个下拉，
/// 语义直观、不易配错，多数场景够用。
///
/// 若将来确实需要混合编排，再引入位标志并保留 <see cref="AccessLevel"/> 作为预设即可，
/// 届时需要在 AclEntry 上补一列，属于独立改动。
/// </summary>
public enum AccessLevel
{
    /// <summary>无任何权限（用于「可见但不可操作」的显式授予场景）。</summary>
    None = 0,

    /// <summary>只读：列表、详情、复制路径、打开文件、下载。</summary>
    Read = 1,

    /// <summary>编辑：上传模板、新建文档、复制（含跨目录）、上传附件。</summary>
    Write = 2,

    /// <summary>管理：删除（进回收站）、回收站恢复/彻底删除、建删项目检项、同步元数据。</summary>
    Manage = 3,

    /// <summary>规则管理：提取规则的上传 / 删除 / 备注 / 跨目录复制。</summary>
    RuleManage = 4,
}

/// <summary>
/// 用户档案。
///
/// 域用户与本地账户共用一张表：授权层只认 <see cref="User.Id"/>，
/// 不需要区分来源——来源只决定「密码怎么校验」与「组关系怎么同步」。
///
/// 域用户在其首次成功 SSO 时自动建档（upsert），不需要管理员预先录入。
/// </summary>
public class User
{
    public int Id { get; set; }

    /// <summary>登录名。域用户存 sAMAccountName（不含域前缀），本地账户即登录名。唯一。</summary>
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public UserSource Source { get; set; }

    /// <summary>
    /// PBKDF2 密码哈希（ASP.NET Core PasswordHasher 格式）。
    /// 仅 <see cref="UserSource.Local"/> 使用；域用户恒为 null——
    /// 域密码绝不落本库，否则等于在应用侧复制了一份域凭据。
    /// </summary>
    public string? PasswordHash { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// 系统管理员。绕过全部 ACL，用于系统初始化与故障自救。
    /// 该位是刻意的「逃生舱」：ACL 配错导致全员无权限时，仍需有人能进去修。
    /// </summary>
    public bool IsSystemAdmin { get; set; }

    /// <summary>首次登录是否必须改密（引导创建的本地管理员用）。</summary>
    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<UserGroup> UserGroups { get; set; } = [];
}

/// <summary>
/// 授权主体（组）。域组与本地组共用一张表。
///
/// 域组由 LDAP 同步时按名字自动创建；本地组由管理员建立，
/// 用于把「域里没有对应组」的一类人（外包、临时账号）归拢起来授权。
/// </summary>
public class Group
{
    public int Id { get; set; }

    /// <summary>组名。域组存 sAMAccountName。唯一。</summary>
    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public UserSource Source { get; set; }

    /// <summary>域组被禁用后仍保留记录，避免历史授权关系被静默清空。</summary>
    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public List<UserGroup> UserGroups { get; set; } = [];
}

/// <summary>
/// 用户 ↔ 组的关联。
///
/// 域用户的这份关系由 LDAP 同步时**整体覆盖**（先删后插），
/// 保证域里被移出组的人在本系统里同步失去权限——这是「以域为准」的硬要求。
/// 本地账户的这份关系由管理员手工维护，不做覆盖。
/// </summary>
public class UserGroup
{
    public int UserId { get; set; }

    public int GroupId { get; set; }

    public User? User { get; set; }

    public Group? Group { get; set; }
}

/// <summary>
/// 访问控制条目：把「组」授权到「项目/检项」。
///
/// <see cref="CheckId"/> 为 null 时表示对该项目下**所有检项**（含将来新建的）生效。
/// 之所以允许 null：按检项逐个授权在检项很多时是沉重的管理负担，
/// 「给整个项目授权 + 个别检项例外覆盖」是实际最常用的形态。
///
/// 同一检项被多条条目命中时权限**取并集**（不取最小），
/// 因为「一个人属于两个组」应该获得两个组的权限之和。
/// </summary>
public class AclEntry
{
    public int Id { get; set; }

    public int GroupId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>为 null 表示覆盖整个项目下所有检项。</summary>
    public int? CheckId { get; set; }

    /// <summary>
    /// 授权等级。同一检项被多条条目命中时<strong>取最高</strong>，不是取最低也不是取并集。
    /// </summary>
    public AccessLevel Level { get; set; } = AccessLevel.Read;

    public DateTime CreatedAt { get; set; }

    public Group? Group { get; set; }

    public Project? Project { get; set; }

    public CheckItem? Check { get; set; }
}

/// <summary>
/// 外部工具 / 脚本用的长期访问令牌。
///
/// 存在的理由：开启鉴权后，非浏览器的调用方<strong>没有任何登录态可用</strong> ——
/// 拿不到 Cookie，也不在浏览器上下文里因而没有 Kerberos 票据。
/// 典型调用方是部署验证脚本（<c>scripts/api-smoke.sh</c>）与后续的自动化任务。
///
/// <strong>桌面插件（<c>officetool://</c>）不需要令牌</strong>：它只解析协议地址、
/// 把 UNC 路径交给系统 Shell 打开，全程不发 HTTP 请求。这一条曾经是个误解，
/// 保留说明以免后人再按错误的理由去"修"插件。
///
/// 令牌由用户在网页端显式生成。明文仅在创建时返回一次，
/// 库里只存 SHA-256 哈希——应用管理员也不应该能读出别人的令牌。
/// </summary>
public class ApiToken
{
    public int Id { get; set; }

    public int UserId { get; set; }

    /// <summary>用户可读备注，如「部署冒烟脚本」。用于管理页区分多条令牌。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>SHA-256 哈希（十六进制小写）。明文不落库。</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>最近一次验过令牌的请求时间，用于判断令牌是否还在被使用。</summary>
    public DateTime? LastUsedAt { get; set; }

    /// <summary>过期时间。可为 null 表示长期有效——内网工具硬性过期会让插件毫无征兆地失效。</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>吊销。保留记录以便审计「谁在什么时候吊销了哪条」。</summary>
    public bool IsRevoked { get; set; }

    public User? User { get; set; }
}

/// <summary>项目（设计文档 §6.1 Projects）。</summary>
public class Project
{
    public int Id { get; set; }

    /// <summary>项目编码，如 QLS2409。唯一。</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }

    public List<CheckItem> Checks { get; set; } = [];
}

/// <summary>检项（设计文档 §6.1 Checks。类名加 Item 以避开 BCL 语义歧义，表名仍为 Checks）。</summary>
public class CheckItem
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    /// <summary>检项编码，如 SEC。</summary>
    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }

    public Project? Project { get; set; }
}

/// <summary>模板（设计文档 §6.1 Templates）。</summary>
public class Template
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int CheckId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对 TemplatesRoot 的路径，如 QLS2409\SEC\检验记录模板.docx</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime ModifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? UploadedByIp { get; set; }
}

/// <summary>文档副本（设计文档 §6.1 Documents）。</summary>
public class Document
{
    public int Id { get; set; }

    public int ProjectId { get; set; }

    public int CheckId { get; set; }

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对 DataRoot 的路径。</summary>
    public string RelativePath { get; set; } = string.Empty;

    /// <summary>来源模板的相对路径（相对 TemplatesRoot）。</summary>
    public string SourceTemplatePath { get; set; } = string.Empty;

    public string Extension { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime CreatedAt { get; set; }

    public string? CreatedByIp { get; set; }
}

/// <summary>
/// 提取规则的备注（网页上可直接修改）。
///
/// 为什么备注进数据库、规则本体进文件系统：
/// 规则本体是使用者要看的文件，放在共享盘上人和 Windows 都能直接看到、直接替换；
/// 而备注是纯应用侧元数据，放进共享盘会多出用户看不懂的杂项文件。
/// 按「相对 RulesRoot 的路径」关联，复制规则时顺带把备注一起带过去。
///
/// 注意：这张表是后加的，用 EnsureCreated 的库不会自动补建，
/// 启动时会额外执行一次 CREATE TABLE IF NOT EXISTS（见 Program.cs）。
/// </summary>
public class RuleNote
{
    public int Id { get; set; }

    /// <summary>相对 RulesRoot 的规则文件路径，如 QLS2409\SEC\标准字段.json。唯一。</summary>
    public string RelativePath { get; set; } = string.Empty;

    public string Note { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 回收站条目。删除模板/文档/附件/规则时，文件移入共享盘上的 <c>_trash</c> 目录，
/// 本表登记原位置与回收站位置，供恢复与彻底删除使用。
/// </summary>
public class TrashItem
{
    public int Id { get; set; }

    /// <summary>Template | Document | Attachment | Rule</summary>
    public string Kind { get; set; } = string.Empty;

    public string Project { get; set; } = string.Empty;

    public string Check { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    /// <summary>相对原业务根的路径（Templates/Data/Attachments/Rules 根）。</summary>
    public string OriginalRelativePath { get; set; } = string.Empty;

    /// <summary>相对 TrashRoot 的路径。</summary>
    public string TrashRelativePath { get; set; } = string.Empty;

    public long Size { get; set; }

    public DateTime DeletedAt { get; set; }

    public string? DeletedByIp { get; set; }
}

/// <summary>操作日志（设计文档 §6.1 OperationLogs）。</summary>
public class OperationLog
{
    public int Id { get; set; }

    public string Action { get; set; } = string.Empty;

    public string TargetPath { get; set; } = string.Empty;

    public string? Ip { get; set; }

    /// <summary>
    /// 操作人登录名。鉴权上线后这是唯一能把日志对到「人」的字段。
    /// 为可空：未开启鉴权（<c>Auth:Enabled=false</c>）的部署形态下无人可记。
    /// </summary>
    public string? UserName { get; set; }

    public string? UserAgent { get; set; }

    /// <summary>成功 / 失败。</summary>
    public string Result { get; set; } = "成功";

    public string? Message { get; set; }

    public DateTime CreatedAt { get; set; }
}
