using OfficeTool.Core.Models;

namespace OfficeTool.Api.Contracts;

/// <summary>当前登录者。前端据此决定渲染哪些功能。</summary>
public sealed class AuthMeDto
{
    public bool AuthEnabled { get; set; }

    public bool Authenticated { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    /// <summary>域账户还是本地账户。</summary>
    public string Source { get; set; } = string.Empty;

    public bool IsSystemAdmin { get; set; }

    /// <summary>引导出来的本地管理员首次登录被要求改密。</summary>
    public bool MustChangePassword { get; set; }
}

/// <summary>某个 项目/检项 上的有效权限，供前端按权限渲染按钮。</summary>
public sealed class EffectiveRightsDto
{
    public bool Visible { get; set; }

    /// <summary>
    /// 有效等级名：<c>None</c> / <c>Read</c> / <c>Write</c> / <c>Manage</c> / <c>RuleManage</c>。
    /// 递进语义，前端直接比较等级即可；下面四个 bool 是按同一等级展开的便捷值。
    /// </summary>
    public string Level { get; set; } = nameof(AccessLevel.None);

    public bool CanRead { get; set; }

    public bool CanWrite { get; set; }

    public bool CanManage { get; set; }

    public bool CanManageRules { get; set; }
}

public sealed class LoginRequest
{
    public string UserName { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;
}public sealed class ChangePasswordRequest
{
    public string OldPassword { get; set; } = string.Empty;

    public string NewPassword { get; set; } = string.Empty;
}

public sealed class CreateTokenRequest
{
    /// <summary>备注名，用于管理页区分多条令牌，如「检测室 03 号机」。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>过期时间。留空表示长期有效。</summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// 创建令牌的响应。<see cref="Token"/> 是<strong>唯一一次</strong>能拿到明文的机会。
/// </summary>
public sealed class CreateTokenResponse
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>明文令牌。前端必须提示用户立刻复制——关闭弹窗后无法再次查看。</summary>
    public string Token { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }
}

public sealed class ApiTokenDto
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    /// <summary>最近使用时间。因写入有节流，它只表示「近期还在用」，不是精确时刻。</summary>
    public DateTime? LastUsedAt { get; set; }

    public DateTime? ExpiresAt { get; set; }

    public bool IsRevoked { get; set; }
}

/// <summary>
/// 当前用户在<strong>全部可见检项</strong>上的有效等级。
///
/// 存在的理由：前端要在「项目树 + 文件列表 + 规则列表」三处按权限决定按钮的显示。
/// 若用 <c>/api/auth/rights</c> 逐个 项目/检项 询问，切一次范围就是一次往返，
/// 树上有几十个节点时不可接受。这里一次性返回，前端在内存里查表。
///
/// 前端据此渲染<strong>只是体验优化</strong>——拦截始终在服务端。
/// </summary>
public sealed class AccessMapDto
{
    public bool AuthEnabled { get; set; }

    public bool IsSystemAdmin { get; set; }

    /// <summary>可见检项（等级高于 <c>None</c>）。超管返回全部检项，等级为 <c>RuleManage</c>。</summary>
    public IReadOnlyList<CheckAccessDto> Checks { get; set; } = [];
}

public sealed class CheckAccessDto
{
    public int CheckId { get; set; }

    public int ProjectId { get; set; }

    /// <summary>项目名。规则 / 附件 / 回收站按名称过滤，前端需要它做匹配。</summary>
    public string Project { get; set; } = string.Empty;

    public string Check { get; set; } = string.Empty;

    /// <summary><c>None</c> / <c>Read</c> / <c>Write</c> / <c>Manage</c> / <c>RuleManage</c>。</summary>
    public string Level { get; set; } = nameof(AccessLevel.None);
}
