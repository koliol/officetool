using OfficeTool.Core.Services;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// PDF 文本抽取。
///
/// 这里用 PdfPig 自己**生成**一份 PDF 再读回来，不引入二进制夹具：
/// 夹具一旦进仓库就再没人敢动，而生成式夹具的版式是写在测试里、可读可改的。
/// 用 ASCII 是因为标准 14 号字体没有中文字形（中文抽取由引擎层的文本夹具覆盖）。
/// </summary>
public class PdfTextReaderTests
{
    [Fact]
    public void 应按视觉行重建文本_表格行不会被拼在一起()
    {
        var pdf = BuildPdf(
            ("Sample Name: Ceftriaxone", 760),
            ("Batch No: 20260901", 740),
            ("1   12   3", 700),
            ("2    9   1", 684));

        var text = PdfTextReader.ReadText(pdf);
        var lines = TextNormalizer.ToLines(text);

        Assert.Contains(lines, l => l.Contains("Sample Name") && l.Contains("Ceftriaxone"));
        Assert.Contains(lines, l => l.Contains("Batch No") && l.Contains("20260901"));

        // 关键：两行数据必须是两行，不能被拼成 "1 12 3 2 9 1"
        var row1 = Assert.Single(lines, l => l.StartsWith("1 ") && l.Contains("12"));
        Assert.Contains("3", row1);
        Assert.DoesNotContain("2    9", row1);
    }

    [Fact]
    public void 没有文本层的空白页应返回空字符串()
    {
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4).AddText(" ", 12, new PdfPoint(50, 700),
            builder.AddStandard14Font(Standard14Font.Helvetica));

        var text = PdfTextReader.ReadText(builder.Build());

        Assert.True(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void 损坏的PDF应抛出而不是返回空()
    {
        // 上层（ExtractionService）据此把状态置为 Failed 并给出可读原因，
        // 如果这里吞掉异常返回空串，用户会看到"解析成功但没数据"这种更糟的提示
        Assert.ThrowsAny<Exception>(() => PdfTextReader.ReadText("这不是 PDF"u8.ToArray()));
    }

    private static byte[] BuildPdf(params (string Text, double Y)[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        foreach (var (text, y) in lines)
        {
            page.AddText(text, 11, new PdfPoint(50, y), font);
        }

        return builder.Build();
    }
}
