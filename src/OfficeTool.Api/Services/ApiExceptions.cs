namespace OfficeTool.Api.Services;

/// <summary>资源不存在 → 404。</summary>
public sealed class NotFoundException(string message) : Exception(message);

/// <summary>与现有状态冲突（重名等） → 409。</summary>
public sealed class ConflictException(string message) : Exception(message);

/// <summary>请求参数不合法 → 400。</summary>
public sealed class BadRequestException(string message) : Exception(message);

/// <summary>
/// 功能已定义契约但尚未实现 → 501。
/// 用于「接口预留」：调用方拿到的是明确的「还没做」，而不是含糊的 500。
/// </summary>
public sealed class FeatureNotAvailableException(string message) : Exception(message);
