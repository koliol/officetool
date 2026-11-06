using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
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
    IAccessControlService access,
    ILogger<OperationLogService> logger)
{
    private readonly AppDbContext _db = db;
    private readonly RequestContext _context = context;
    private readonly IAccessControlService _access = access;
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
                // 鉴权上线后，这一列是唯一能把日志对到「人」的字段。
                UserName = Truncate(_context.UserName, 128),
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

    /// <summary>
    /// 分页读取审计日志。
    ///
    /// 日志里带全站项目的路径名、客户端 IP 与 UA，属于管理面数据，因此**只允许系统管理员**。
    /// 判定放在服务层并在每次请求回查数据库（不信任 Cookie 里的角色声明）。
    /// </summary>
    public async Task<PagedResult<OperationLogDto>> ListAsync(
        int page, int pageSize, CancellationToken ct = default)
    {
        var current = await _access.ResolveAccessAsync(_context, ct);
        if (!current.IsSystemAdmin)
        {
            throw new AccessDeniedException("操作日志包含全站路径信息，仅系统管理员可查看。");
        }

        var total = await _db.OperationLogs.CountAsync(ct);
        var items = await _db.OperationLogs.AsNoTracking()
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new OperationLogDto(
                l.Id, l.Action, l.TargetPath, l.Ip, l.UserAgent,
                l.Result, l.Message, l.UserName, l.CreatedAt))
            .ToListAsync(ct);

        return new PagedResult<OperationLogDto>(items, total, page, pageSize);
    }

    private static string? Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max];
}
