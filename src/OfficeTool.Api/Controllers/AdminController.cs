using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// 权限管理（用户 / 组 / 授权条目）。统一前缀 <c>/api/admin</c>。
///
/// 控制器只做参数转发，全部权限判断都在 <see cref="AdminService"/> 里——
/// 与本项目其他控制器一致的约定：判在控制器意味着每新增一个端点都要记得补一层，
/// 漏一个就是一个越权漏洞。
///
/// <c>[Authorize(Policy)]</c> 只是一道粗筛，真正的判定读 <c>Users.IsSystemAdmin</c>，
/// 理由见 <c>AdminService.RequireAdminAsync</c> 的注释。
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = AdminPolicy.Name)]
public sealed class AdminController(AdminService admin) : ControllerBase
{
    private readonly AdminService _admin = admin;

    // ── 用户 ──────────────────────────────────────────────────────

    [HttpGet("users")]
    public Task<PagedResult<UserDto>> ListUsers(
        [FromQuery] string? keyword,
        [FromQuery] string? source,
        [FromQuery] bool? enabled,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        _admin.ListUsersAsync(keyword, source, enabled, page, pageSize, ct);

    [HttpGet("users/{id:int}")]
    public Task<UserDto> GetUser(int id, CancellationToken ct) => _admin.GetUserAsync(id, ct);

    /// <summary>新建本地账户。响应里的明文密码<strong>只出现这一次</strong>。</summary>
    [HttpPost("users")]
    public Task<CreateUserResponse> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct) =>
        _admin.CreateUserAsync(request, ct);

    [HttpPatch("users/{id:int}")]
    public Task<UserDto> UpdateUser(int id, [FromBody] UpdateUserRequest request, CancellationToken ct) =>
        _admin.UpdateUserAsync(id, request, ct);

    /// <summary>重置本地账户密码，返回新的明文密码（同样只出现一次）。</summary>
    [HttpPost("users/{id:int}/reset-password")]
    public Task<ResetPasswordResponse> ResetPassword(int id, CancellationToken ct) =>
        _admin.ResetPasswordAsync(id, ct);

    [HttpDelete("users/{id:int}")]
    public async Task<IActionResult> DeleteUser(int id, CancellationToken ct)
    {
        await _admin.DeleteUserAsync(id, ct);
        return NoContent();
    }

    /// <summary>
    /// 展开某人的有效权限及其来源。用于回答「他为什么看不到这个项目」。
    /// </summary>
    [HttpGet("users/{userName}/effective")]
    public Task<EffectiveAccessDto> EffectiveAccess(string userName, CancellationToken ct) =>
        _admin.EffectiveAccessAsync(Uri.UnescapeDataString(userName), ct);

    // ── 组 ────────────────────────────────────────────────────────

    [HttpGet("groups")]
    public Task<IReadOnlyList<GroupDto>> ListGroups(CancellationToken ct) => _admin.ListGroupsAsync(ct);

    [HttpGet("groups/{id:int}")]
    public Task<GroupDetailDto> GetGroup(int id, CancellationToken ct) => _admin.GetGroupAsync(id, ct);

    [HttpPost("groups")]
    public Task<GroupDto> CreateGroup([FromBody] CreateGroupRequest request, CancellationToken ct) =>
        _admin.CreateGroupAsync(request, ct);

    [HttpPatch("groups/{id:int}")]
    public Task<GroupDto> UpdateGroup(int id, [FromBody] UpdateGroupRequest request, CancellationToken ct) =>
        _admin.UpdateGroupAsync(id, request, ct);

    [HttpDelete("groups/{id:int}")]
    public async Task<IActionResult> DeleteGroup(int id, CancellationToken ct)
    {
        await _admin.DeleteGroupAsync(id, ct);
        return NoContent();
    }

    /// <summary>整体替换组成员。仅本地组可用——域组的成员以域同步为准。</summary>
    [HttpPut("groups/{id:int}/members")]
    public Task<GroupDetailDto> SetMembers(int id, [FromBody] SetMembersRequest request, CancellationToken ct) =>
        _admin.SetMembersAsync(id, request, ct);

    [HttpPost("groups/{id:int}/members/{userId:int}")]
    public Task<GroupDetailDto> AddMember(int id, int userId, CancellationToken ct) =>
        _admin.AddMemberAsync(id, userId, ct);

    [HttpDelete("groups/{id:int}/members/{userId:int}")]
    public async Task<IActionResult> RemoveMember(int id, int userId, CancellationToken ct)
    {
        await _admin.RemoveMemberAsync(id, userId, ct);
        return NoContent();
    }

    // ── 授权条目 ──────────────────────────────────────────────────

    [HttpGet("acl")]
    public Task<IReadOnlyList<AclEntryDto>> ListAcl(
        [FromQuery] int? groupId,
        [FromQuery] int? projectId,
        [FromQuery] int? checkId,
        CancellationToken ct = default) =>
        _admin.ListAclAsync(groupId, projectId, checkId, ct);

    [HttpPost("acl")]
    public Task<AclEntryDto> CreateAcl([FromBody] CreateAclRequest request, CancellationToken ct) =>
        _admin.CreateAclAsync(request, ct);

    [HttpPut("acl/{id:int}")]
    public Task<AclEntryDto> UpdateAcl(int id, [FromBody] UpdateAclRequest request, CancellationToken ct) =>
        _admin.UpdateAclAsync(id, request, ct);

    [HttpDelete("acl/{id:int}")]
    public async Task<IActionResult> DeleteAcl(int id, CancellationToken ct)
    {
        await _admin.DeleteAclAsync(id, ct);
        return NoContent();
    }

    /// <summary>权限配置自检：无人管理的项目、超管数量、是否配过授权。</summary>
    [HttpGet("health")]
    public Task<AclHealthDto> Health(CancellationToken ct) => _admin.HealthAsync(ct);
}

/// <summary>管理接口的授权策略。定义在这里是为了让控制器上的 <c>[Authorize]</c> 有单一出处。</summary>
public static class AdminPolicy
{
    public const string Name = "SystemAdmin";

    /// <summary>与之对应的 Claim 角色名。<c>AuthController.SignInAsync</c> 签发同一个值。</summary>
    public const string Role = "SystemAdmin";
}
