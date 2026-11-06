using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace OfficeTool.Core.Services;

/// <summary>
/// PDF 文本抽取。
///
/// 为什么自己按坐标重建行，而不是直接用 PdfPig 自带的 ContentOrderTextExtractor：
/// 我们要解析的是**表格**（不溶性微粒的逐样品计数），行的完整性直接决定成败。
/// 自带抽取器面向"阅读顺序的连续文本"，对表格行常常把相邻行的字拼在一起；
/// 而"基线相同的词属于同一行"这条规则对这类报告稳定得多，也能解释清楚。
///
/// 已知边界：扫描件（只有图片、没有文本层）抽不出任何东西 ——
/// 这种情况必须如实告诉用户"需要 OCR"，不能假装解析成功。
/// </summary>
public static class PdfTextReader
{
    /// <summary>最多读这么多页：真实检验报告很少超过 10 页，超了多半是别的文档。</summary>
    public const int MaxPages = 50;

    public static string ReadText(Stream stream)
    {
        using var document = PdfDocument.Open(stream);
        return ReadText(document);
    }

    public static string ReadText(byte[] bytes)
    {
        using var document = PdfDocument.Open(bytes);
        return ReadText(document);
    }

    private static string ReadText(PdfDocument document)
    {
        var builder = new StringBuilder();
        var lastPage = Math.Min(document.NumberOfPages, MaxPages);

        for (var pageNumber = 1; pageNumber <= lastPage; pageNumber++)
        {
            var page = document.GetPage(pageNumber);
            var words = page.GetWords().ToList();

            if (words.Count == 0)
            {
                continue;
            }

            // 行高容差：按页高取比例，再兜一个下限。
            // 太小会把同一行的上标/下标拆成两行，太大会把紧邻两行并成一行。
            var tolerance = Math.Max(1.5, page.Height * 0.004);

            var lines = new List<List<Word>>();

            foreach (var word in words
                         .OrderByDescending(w => w.BoundingBox.Bottom)
                         .ThenBy(w => w.BoundingBox.Left))
            {
                var current = lines.Count > 0 ? lines[^1] : null;

                if (current is null ||
                    Math.Abs(current[0].BoundingBox.Bottom - word.BoundingBox.Bottom) > tolerance)
                {
                    lines.Add([word]);
                }
                else
                {
                    current.Add(word);
                }
            }

            foreach (var line in lines)
            {
                builder.AppendLine(JoinLine(line));
            }

            builder.AppendLine();
        }

        return builder.ToString();
    }

    /// <summary>
    /// 把一行的词按 X 顺序拼起来。
    ///
    /// 空格只在这两处补：词间有明显间隙，或两侧都是西文字符。
    /// 中文之间插空格会让"批　号"这类正常标签变成"批 号"，后续标签匹配全部失效。
    /// </summary>
    private static string JoinLine(List<Word> line)
    {
        var builder = new StringBuilder();
        double? previousRight = null;
        char previousLast = '\0';

        foreach (var word in line.OrderBy(w => w.BoundingBox.Left))
        {
            var text = word.Text;

            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (previousRight is { } right && previousLast != '\0')
            {
                var gap = word.BoundingBox.Left - right;
                var charWidth = word.BoundingBox.Width / Math.Max(1, text.Length);

                // 阈值取字符宽度的 35%：普通词间空格约等于一个空格符（≈30% 字宽）能过，
                // 而字内的细微间距（连字、字距微调）不会被误判成空格。
                var significant = gap > Math.Max(1.2, charWidth * 0.35);

                // 中文之间不补空格：中文排版里"批 号"这种空格会让标签匹配直接失效
                if (significant && !(IsCjk(previousLast) && IsCjk(text[0])))
                {
                    builder.Append(' ');
                }
            }

            builder.Append(text);
            previousRight = word.BoundingBox.Right;
            previousLast = text[^1];
        }

        return builder.ToString().Trim();
    }

    private static bool IsCjk(char ch) =>
        ch is >= '\u4e00' and <= '\u9fff' or >= '\u3400' and <= '\u4dbf';
}
