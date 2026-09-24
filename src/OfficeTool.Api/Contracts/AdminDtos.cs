using OfficeTool.Core.Models;

namespace OfficeTool.Api.Contracts;

/// <summary>
/// 管理端的「谁有权限做什么」的读模型。
///
/// 全部放在 <c>/api/admin/*</c> 之下，只有系统管理员可达。
/// 这里的读写<strong>不经过 ACL 本身</strong>——管理动作由 <c>User.IsSystemAdmin</c> 授权，
/// 否则会陷入「管理权限也要被管理」的循环依赖。
/// </summary>
public sealed class UserDto
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>Ad</c> / <c>Local</c>。</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public bool IsSystemAdmin { get; set; }

    public bool MustChangePassword { get; set; }

    public DateTime? LastLoginAt { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>所属组名。列表接口也带上，省得前端再逐项查一次。</summary>
    public IReadOnlyList<string> Groups { get; set; } = [];
}

public sealed class CreateUserRequest
{
    public string UserName { get; set; } = string.Empty;

    /// <summary>留空则用登录名。</summary>
    public string? DisplayName { get; set; }

    public bool IsSystemAdmin { get; set; }

    /// <summary>是否要求首次登录改密。默认 true——管理员要转述初始密码，</summary>
    public bool MustChangePassword { get; set; } = true;
}

/// <summary>
/// 创建用户的响应。<see cref="Password"/> 是明文，<strong>唯一一次</strong>出现。
/// 之后连管理员自己都读不出来（库里只有 PBKDF2 哈希）。
/// </summary>
public sealed class CreateUserResponse
{
    public UserDto User { get; set; } = new();

    public string Password { get; set; } = string.Empty;
}

public sealed class UpdateUserRequest
{
    public string? DisplayName { get; set; }

    public bool? IsEnabled { get; set; }

    public bool? IsSystemAdmin { get; set; }

    public bool? MustChangePassword { get; set; }
}

public sealed class ResetPasswordResponse
{
    public string Password { get; set; } = string.Empty;
}

public sealed class GroupDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary><c>Ad</c> / <c>Local</c>。</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public DateTime CreatedAt { get; set; }

    public int MemberCount { get; set; }
}

public sealed class GroupDetailDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public bool IsEnabled { get; set; }

    public DateTime CreatedAt { get; set; }

    public IReadOnlyList<UserBriefDto> Members { get; set; } = [];
}

public sealed class UserBriefDto
{
    public int Id { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
}

public sealed class CreateGroupRequest
{
    public string Name { get; set; } = string.Empty;

    /// <summary>留空则用组名。</summary>
    public string? DisplayName { get; set; }
}

public sealed class UpdateGroupRequest
{
    public string? DisplayName { get; set; }

    public bool? IsEnabled { get; set; }
}

public sealed class SetMembersRequest
{
    /// <summary>组内成员的完整集合。语义是<strong>整体覆盖</strong>，请见 Ad Service 的说明。</summary>
    public IReadOnlyList<int> UserIds { get; set; } = [];
}

// ── 授权条目 ──────────────────────────────────────────────────────────

public sealed class AclEntryDto
{
    public int Id { get; set; }

    public int GroupId { get; set; }

    public string GroupName { get; set; } = string.Empty;

    public int ProjectId { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    /// <summary>为 null 表示覆盖整个项目下所有检项。</summary>
    public int? CheckId { get; set; }

    public string? CheckName { get; set; }

    /// <summary><c>None</c> / <c>Read</c> / <c>Write</c> / <c>Manage</c> / <c>RuleManage</c>。</summary>
    public string Level { get; set; } = nameof(AccessLevel.None);

    public DateTime CreatedAt { get; set; }
}

public sealed class CreateAclRequest
{
    public int GroupId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>留空表示对整个项目授权。</summary>
    public int? CheckId { get; set; }

    /// <summary>等级名。大小写不敏感，非法值一律 400（不静默降级为 Read）。</summary>
    public string Level { get; set; } = nameof(AccessLevel.Read);
}

public sealed class UpdateAclRequest
{
    public string Level { get; set; } = nameof(AccessLevel.Read);
}

/// <summary>
/// 某个用户的有效权限展开结果。
///
/// 这是管理页最有用的一个视图：当有人报告「我看不到某某项目」时，
/// 管理员需要知道<strong>系统认为他有什么</strong>，以及这份权限<strong>来自哪个组</strong>。
/// 只列最终等级不足以定位问题，所以每条都带授予来源。
/// </summary>
public sealed class EffectiveAccessDto
{
    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public bool IsSystemAdmin { get; set; }

    public IReadOnlyList<string> Groups { get; set; } = [];

    public IReadOnlyList<EffectiveCheckAccessDto> Checks { get; set; } = [];
}

public sealed class EffectiveCheckAccessDto
{
    public string Project { get; set; } = string.Empty;

    public string Check { get; set; } = string.Empty;

    public string Level { get; set; } = nameof(AccessLevel.None);

    /// <summary>
    /// 这份权限的授予来源，用于排查。取值形如：
    /// <c>组「检测三组」→ 整个项目</c> 或 <c>组「外包」→ 本检项（覆盖项目级）</c>。
    /// </summary>
    public string GrantedBy { get; set; } = string.Empty;
}

/// <summary>
/// 权限配置的自检结果。
///
/// 权限系统最容易发生的事故不是被攻破，而是<strong>把自己锁在外面</strong>：
/// 授权配到一半、超管位被误关、某个项目没有任何人能管理。
/// 这个出参用于在页面上给出可见的告警。
/// </summary>
public sealed class AclHealthDto
{
    public int AdminCount { get; set; }

    public int GroupCount { get; set; }

    public int AclEntryCount { get; set; }

    /// <summary>没有人具备管理及以上权限的项目（含「根本没有任何授权」）。</summary>
    public IReadOnlyList<UnmanagedProjectDto> UnmanagedProjects { get; set; } = [];

    public IReadOnlyList<string> Warnings { get; set; } = [];
}

public sealed class UnmanagedProjectDto
{
    public int ProjectId { get; set; }

    public string ProjectName { get; set; } = string.Empty;

    public int CheckCount { get; set; }

    /// <summary>该项目下有授权的检项数。低于 <see cref="CheckCount"/> 意味着有人被落下。</summary>
    public int GrantedCheckCount { get; set; }
}
