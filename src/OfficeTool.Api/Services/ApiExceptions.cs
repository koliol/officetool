namespace OfficeTool.Api.Services;

/// <summary>资源不存在 → 404。</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>与现有状态冲突（重名等） → 409。</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>请求参数不合法 → 400。</summary>
public sealed class BadRequestException(string message) : Exception(message);

/// <summary>
/// 已认证且资源存在，但权限等级不够 → 403。
///
/// 与 <see cref="NotFoundException"/> 的分工见 <c>AccessGuard.RequireRead</c> 的注释：
/// 读权限不足一律伪装成 404，只有「写入类」操作才用 403 明说。
/// </summary>
public sealed class AccessDeniedException(string message) : Exception(message);

/// <summary>
/// 功能已定义契约但尚未实现 → 501。
/// 用于「接口预留」：调用方拿到的是明确的「还没做」，而不是含糊的 500。
/// </summary>
public sealed class FeatureNotAvailableException(string message) : Exception(message);
