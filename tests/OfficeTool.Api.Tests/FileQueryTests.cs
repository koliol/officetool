using OfficeTool.Api.Contracts;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 查询条件归一化。
///
/// 这类"顺手补一刀"的逻辑最容易在重构里被删掉，删掉也不会报错，
/// 只会让用户觉得"按日期筛出来的结果少了一天"。
/// </summary>
public class FileQueryTests
{
    [Fact]
    public void 日期区间右端点无时刻时应补到当天最后一刻()
    {
        // 前端日期选择器传的是纯日期（00:00:00）。若直接当上界，
        // 当天 00:00:01 之后创建的记录全被漏掉 —— 用户会以为"今天没有文件"。
        var to = new FileQuery { To = new DateTime(2026, 11, 6) }.Normalized().To!.Value;

        Assert.Equal(new DateTime(2026, 11, 6), to.Date);
        Assert.True(to > new DateTime(2026, 11, 6, 23, 59, 59), $"右端点应覆盖当天：{to:O}");
        Assert.True(to < new DateTime(2026, 11, 7), $"右端点不应越到次日：{to:O}");
    }

    [Fact]
    public void 日期区间右端点带时刻时应原样保留()
    {
        var to = new DateTime(2026, 11, 6, 12, 30, 0);

        Assert.Equal(to, new FileQuery { To = to }.Normalized().To);
    }

    [Fact]
    public void 分页与排序参数应被夹到合法范围()
    {
        var normalized = new FileQuery { Page = 0, PageSize = 9999, SortBy = " Name ", SortDir = "ASC" }
            .Normalized();

        Assert.Equal(1, normalized.Page);
        Assert.Equal(50, normalized.PageSize);
        Assert.Equal("name", normalized.SortBy);
        Assert.Equal("asc", normalized.SortDir);
    }
}
