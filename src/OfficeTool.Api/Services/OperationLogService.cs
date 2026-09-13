using OfficeTool.Api.Data;
using OfficeTool.Core.Models;

namespace OfficeTool.Api.Services;

/// <summary>
/// 操作日志（设计文档 §6.1 OperationLogs、§11、§12）：
/// 记录上传、创建、删除、重命名的目标路径、客户端 IP、UA 与结果。
/// 日志写入失败不应影响主流程。
/// </summary>
public sealed class OperationLogService(
    AppDbContext db,
    RequestContext context,
    ILogger<OperationLogService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly RequestContext _context = context;
    private readonly ILogger<OperationLogService> _logger = logger;

    public async Task WriteAsync(
        string action,
        string targetPath,
        bool success,
        string? message = null,
        CancellationToken ct = default)
    {
        try
        {
            _db.OperationLogs.Add(new OperationLog
            {
                Action = action,
                TargetPath = Truncate(targetPath, 500) ?? string.Empty,
                Ip = Truncate(_context.Ip, 64),
                UserAgent = Truncate(_context.UserAgent, 512),
                Result = success ? "成功" : "失败",
                Message = Truncate(message, 2000),
                CreatedAt = DateTime.Now,
            });

            await _db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // 审计日志失败不能拖垮业务操作，但必须留痕
            _logger.LogError(ex, "写入操作日志失败：{Action} {TargetPath}", action, targetPath);
        }
    }

    /// <summary>记录一次失败操作（在 catch 块中调用）。</summary>
    public Task WriteFailureAsync(
        string action,
        string targetPath,
        Exception exception,
        CancellationToken ct = default) =>
        WriteAsync(action, targetPath, success: false, message: exception.Message, ct: ct);

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
