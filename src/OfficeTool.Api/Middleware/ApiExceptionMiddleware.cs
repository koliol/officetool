using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;
using OfficeTool.Core.Exceptions;

namespace OfficeTool.Api.Middleware;

/// <summary>
/// 统一异常 → HTTP 状态码 + 结构化错误体。
/// 把领域异常与存储异常映射为明确的语义，避免一律 500。
/// </summary>
public sealed class ApiExceptionMiddleware(
    RequestDelegate next,
    ILogger<ApiExceptionMiddleware> logger,
    IHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHostEnvironment _environment = environment;

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex) when (!context.Response.HasStarted)
        {
            var (status, code, message) = Map(ex);

            if (status >= 500)
            {
                logger.LogError(ex, "请求处理失败：{Method} {Path}", context.Request.Method, context.Request.Path);
            }
            else
            {
                logger.LogWarning("请求被拒绝：{Method} {Path} → {Code} {Message}",
                    context.Request.Method, context.Request.Path, code, message);
            }

            context.Response.Clear();
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json; charset=utf-8";

            // 仅开发环境回传内部异常细节，避免生产环境泄露路径、SQL、堆栈等信息（§11）
            var detail = _environment.IsDevelopment() ? ex.Message : null;

            var payload = new ApiError(code, message, detail);
            await context.Response.WriteAsync(JsonSerializer.Serialize(payload, JsonOptions));
        }
    }

    private static (int Status, string Code, string Message) Map(Exception ex) => ex switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, "not_found", ex.Message),
        ConflictException => (StatusCodes.Status409Conflict, "conflict", ex.Message),
        BadRequestException => (StatusCodes.Status400BadRequest, "bad_request", ex.Message),
        AccessDeniedException => (StatusCodes.Status403Forbidden, "access_denied", ex.Message),
        FeatureNotAvailableException => (StatusCodes.Status501NotImplemented, "feature_not_available", ex.Message),

        DayNumberExhaustedException => (StatusCodes.Status409Conflict, "day_number_exhausted", ex.Message),
        ExtensionNotAllowedException => (StatusCodes.Status400BadRequest, "extension_not_allowed", ex.Message),
        InvalidNameException => (StatusCodes.Status400BadRequest, "invalid_name", ex.Message),
        PathEscapeException => (StatusCodes.Status403Forbidden, "path_escape", ex.Message),
        PlatformUnsupportedException => (StatusCodes.Status503ServiceUnavailable, "platform_unsupported", ex.Message),

        DbUpdateConcurrencyException => (StatusCodes.Status409Conflict, "concurrency_conflict", "数据已被其他操作修改，请重试。"),
        IOException => (StatusCodes.Status502BadGateway, "storage_io_error", $"存储访问失败：{ex.Message}"),
        UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "forbidden", ex.Message),
        ArgumentException => (StatusCodes.Status400BadRequest, "invalid_argument", ex.Message),

        _ => (StatusCodes.Status500InternalServerError, "internal_error", "服务器内部错误。"),
    };
}
