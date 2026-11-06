namespace OfficeTool.Api.Contracts;

/// <summary>
/// 查找条件（设计文档 §5.5）：项目、检项、文件名关键字、扩展名、时间范围、排序、分页。
/// </summary>
public sealed record FileQuery
{
    public string? Project { get; init; }

    public string? Check { get; init; }

    public string? Keyword { get; init; }

    public string? Extension { get; init; }

    public DateTime? From { get; init; }

    public DateTime? To { get; init; }

    /// <summary>name | created | size</summary>
    public string? SortBy { get; init; }

    /// <summary>asc | desc</summary>
    public string? SortDir { get; init; }

    public int Page { get; init; } = 1;

    public int PageSize { get; init; } = 50;

    public FileQuery Normalized() => this with
    {
        Page = Page < 1 ? 1 : Page,
        PageSize = PageSize is < 1 or > 500 ? 50 : PageSize,
        SortBy = string.IsNullOrWhiteSpace(SortBy) ? "created" : SortBy.Trim().ToLowerInvariant(),
        SortDir = string.Equals(SortDir, "asc", StringComparison.OrdinalIgnoreCase) ? "asc" : "desc",

        // 日期区间右端点：前端传的是"结束日期"，值形如 2026-11-06T00:00:00，
        // 直接当上界会漏掉当天从 00:00:01 起的所有记录（用户会以为"今天没有文件"）。
        // 没有具体时刻时补到当天最后一刻。
        To = To is { } to && to.TimeOfDay == TimeSpan.Zero
            ? to.Date.AddDays(1).AddTicks(-1)
            : To,
    };
}

/// <summary>元数据同步结果（设计文档 §6.2）。</summary>
public sealed record SyncResult(
    int TemplatesAdded,
    int TemplatesRemoved,
    int DocumentsAdded,
    int DocumentsRemoved,
    IReadOnlyList<string> Messages);
