using OfficeTool.Desktop.Core;

namespace OfficeTool.Desktop.Core.Tests;

/// <summary>协议地址解析的测试，重点是「畸形输入必须返回失败而不是抛异常」。</summary>
public class ProtocolUriParserTests
{
    /// <summary>网页实际会发过来的标准地址（URL 编码后的 UNC 路径）。</summary>
    private const string StandardUri =
        "officetool://open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx";

    private const string ExpectedUncPath =
        @"\\mynas\OfficeDocs\Data\QLS2409\SEC\20260912.docx";

    [Fact]
    public void 标准协议地址_解析出解码后的UNC路径()
    {
        var result = ProtocolUriParser.Parse(StandardUri);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ExpectedUncPath, result.Path);
        Assert.Null(result.Error);
    }

    [Theory]
    // 用户/手写时的各种等价形式，都应当能解析出同一条路径。
    [InlineData("officetool://open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx")]
    [InlineData("officetool:open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx")]
    [InlineData("officetool:///open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx")]
    [InlineData("officetool://open/?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx")]
    [InlineData("OFFICETOOL://OPEN?PATH=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx")]
    public void 兼容手写形式_解析结果一致(string rawUri)
    {
        var result = ProtocolUriParser.Parse(rawUri);

        Assert.True(result.Success, result.Error);
        Assert.Equal(ExpectedUncPath, result.Path);
    }

    [Fact]
    public void 未编码的反斜杠也能解析()
    {
        var result = ProtocolUriParser.Parse(@"officetool://open?path=\\mynas\OfficeDocs\Data\a.docx");

        Assert.True(result.Success, result.Error);
        Assert.Equal(@"\\mynas\OfficeDocs\Data\a.docx", result.Path);
    }

    [Fact]
    public void 百分号编码的空格会被解码()
    {
        var result = ProtocolUriParser.Parse("officetool://open?path=%5C%5Cmynas%5COffice%20Docs%5C%E6%96%87%E4%BB%B6.docx");

        Assert.True(result.Success, result.Error);
        Assert.Equal(@"\\mynas\Office Docs\文件.docx", result.Path);
    }

    [Fact]
    public void 多余参数被忽略()
    {
        var result = ProtocolUriParser.Parse(
            "officetool://open?path=%5C%5Cmynas%5COfficeDocs%5Ca.docx&token=abc&from=web");

        Assert.True(result.Success, result.Error);
        Assert.Equal(@"\\mynas\OfficeDocs\a.docx", result.Path);
    }

    [Fact]
    public void path参数名大小写不敏感()
    {
        var result = ProtocolUriParser.Parse("officetool://open?Path=%5C%5Cmynas%5Cb.docx");

        Assert.True(result.Success, result.Error);
        Assert.Equal(@"\\mynas\b.docx", result.Path);
    }

    [Fact]
    public void 片段部分被忽略()
    {
        var result = ProtocolUriParser.Parse("officetool://open?path=%5C%5Cmynas%5Cc.docx#fragment");

        Assert.True(result.Success, result.Error);
        Assert.Equal(@"\\mynas\c.docx", result.Path);
    }

    [Fact]
    public void 非officetool协议_解析失败()
    {
        var result = ProtocolUriParser.Parse("http://open?path=%5C%5Cmynas%5Ca.docx");

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.Contains("协议不匹配", result.Error);
    }

    [Fact]
    public void 缺少path参数_解析失败()
    {
        var result = ProtocolUriParser.Parse("officetool://open");

        Assert.False(result.Success);
        Assert.Contains("缺少 path 参数", result.Error);
    }

    [Fact]
    public void path参数无等号_解析失败()
    {
        var result = ProtocolUriParser.Parse("officetool://open?path");

        Assert.False(result.Success);
        Assert.Contains("为空", result.Error);
    }

    [Theory]
    [InlineData("officetool://open?path=")]
    [InlineData("officetool://open?path=%20")]
    [InlineData("officetool://open?path=%20%20")]
    public void path为空或纯空白_解析失败(string rawUri)
    {
        var result = ProtocolUriParser.Parse(rawUri);

        Assert.False(result.Success);
        Assert.Contains("为空", result.Error);
    }

    [Theory]
    [InlineData("officetool://open?path=%5")]
    [InlineData("officetool://open?path=%ZZ")]
    [InlineData("officetool://open?path=abc%")]
    [InlineData("officetool://open?path=%5C%5Cmynas%5C%x1.docx")]
    [InlineData("officetool://open?path=%G1")]
    public void 非法百分号转义_解析失败(string rawUri)
    {
        var result = ProtocolParseResultProbe(rawUri);

        Assert.False(result.Success);
        Assert.Contains("百分号转义", result.Error);
    }

    [Theory]
    [InlineData("officetool://open?path=%0A")]
    [InlineData("officetool://open?path=%00")]
    [InlineData("officetool://open?path=a%09b")]
    public void 控制字符_解析失败(string rawUri)
    {
        var result = ProtocolUriParser.Parse(rawUri);

        Assert.False(result.Success);
        Assert.Contains("控制字符", result.Error);
    }

    [Fact]
    public void 不支持的动作_解析失败()
    {
        var result = ProtocolUriParser.Parse("officetool://delete?path=%5C%5Cmynas%5Ca.docx");

        Assert.False(result.Success);
        Assert.Contains("不支持的动作", result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("这不是一个地址")]
    [InlineData("://open?path=x")]
    [InlineData("officetool")]
    public void 空输入或非地址_解析失败且不抛异常(string? rawUri)
    {
        var result = ProtocolUriParser.Parse(rawUri);

        Assert.False(result.Success);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("officetool://open?path=%1")]
    [InlineData("officetool://open?path=%25%")]
    [InlineData("officetool://?path=")]
    [InlineData("officetool://open?path=%5C%5C%5C")]
    [InlineData("officetool:")]
    [InlineData("officetool://")]
    [InlineData("officetool://open?&&&")]
    public void 畸形输入_永不抛异常(string rawUri)
    {
        // 只要不抛异常即算通过（返回成功或失败都可以）。
        var exception = Record.Exception(() => ProtocolUriParser.Parse(rawUri));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData("officetool://open?path=a.docx", true)]
    [InlineData("OFFICETOOL://open?path=a.docx", true)]
    [InlineData(" officetool://open?path=a.docx", true)]
    [InlineData("http://open?path=a.docx", false)]
    [InlineData("--install", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsProtocolUri_正确识别协议地址(string? value, bool expected)
    {
        Assert.Equal(expected, ProtocolUriParser.IsProtocolUri(value));
    }

    [Fact]
    public void 失败结果不携带路径()
    {
        var result = ProtocolParseResultProbe("officetool://open?path=%");

        Assert.False(result.Success);
        Assert.Null(result.Path);
        Assert.NotNull(result.Error);
        Assert.Contains("失败", result.ToString());
    }

    [Fact]
    public void 成功结果不携带错误()
    {
        var result = ProtocolUriParser.Parse(StandardUri);

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.Contains("成功", result.ToString());
    }

    /// <summary>为了断言失败文案时不重复写 Parse 调用。</summary>
    private static ProtocolParseResult ProtocolParseResultProbe(string rawUri) => ProtocolUriParser.Parse(rawUri);
}
