using System.Text.Json;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 提取规则引擎。用**真实版式的文本**当夹具，而不是 PDF ——
/// 引擎只吃文本，所以规则的行为可以在这里被完整、快速地覆盖。
/// </summary>
public class ExtractionEngineTests
{
    /// <summary>一份典型的不溶性微粒仪器报告（文本层，按行）。</summary>
    private const string SampleReport = """
        不溶性微粒检查报告
        样品名称：注射用头孢曲松钠
        批号：20260901
        规格：1.0g
        检验日期：2026-10-12
        仪器编号：PSS-2000
        检验方法：光阻法

        测定结果（粒/容器）
        样品编号    ≥10μm   ≥25μm   结论
        1           12       3       符合
        2            9       1       符合
        3           15       4       符合

        结论：符合规定
        检验人：张三
        """;

    private static ExtractionRuleSet Rule => ExtractionRule.Parse(BuiltInContent.InsolubleParticlesRule);

    [Fact]
    public void 应抽出基本信息与逐样品表格()
    {
        var outcome = ExtractionEngine.Run(Rule, SampleReport);

        var index = outcome.Fields.ToDictionary(f => f.FieldKey, f => f);

        Assert.Equal("注射用头孢曲松钠", Text(index["productName"]));
        Assert.Equal("20260901", Text(index["batchNo"]));
        Assert.Equal("1.0g", Text(index["specification"]));
        Assert.Equal("2026-10-12", Text(index["testDate"]));
        Assert.Equal("PSS-2000", Text(index["instrumentNo"]));
        Assert.Equal("光阻法", Text(index["method"]));

        var rows = index["items"].Value;
        Assert.Equal(JsonValueKind.Array, rows.ValueKind);
        Assert.Equal(3, rows.GetArrayLength());
        Assert.Equal("1", rows[0].GetProperty("sampleNo").GetString());
        Assert.Equal("12", rows[0].GetProperty("ge10").GetString());
        Assert.Equal("3", rows[0].GetProperty("ge25").GetString());
        Assert.Equal("符合", rows[0].GetProperty("conclusion").GetString());

        Assert.Equal("符合规定", Text(index["result"]));
    }

    [Fact]
    public void 表格应被停用词截断_不把结论行当成数据行()
    {
        // 最后一行「结论：符合规定」如果被当成表格行，会多出一行垃圾数据
        var outcome = ExtractionEngine.Run(Rule, SampleReport);
        var items = outcome.Fields.First(f => f.FieldKey == "items").Value;

        Assert.Equal(3, items.GetArrayLength());
    }

    [Fact]
    public void 值在下一行时也应抽出()
    {
        const string text = """
            样品名称
            注射用水
            批号：
            20261231
            """;

        var outcome = ExtractionEngine.Run(Rule, text);
        var index = outcome.Fields.ToDictionary(f => f.FieldKey, f => f);

        Assert.Equal("注射用水", Text(index["productName"]));
        Assert.Equal("20261231", Text(index["batchNo"]));
    }

    [Fact]
    public void 全角数字与全角冒号应被归一化()
    {
        const string text = """
            样品名称：氯化钠注射液
            批号：２０２６００１
            """;

        var outcome = ExtractionEngine.Run(Rule, text);
        var index = outcome.Fields.ToDictionary(f => f.FieldKey, f => f);

        Assert.Equal("2026001", Text(index["batchNo"]));
    }

    [Fact]
    public void 表格没有结论列时也能抽出行()
    {
        const string text = """
            样品编号  ≥10μm  ≥25μm
            1  8   2
            2  11  3
            """;

        var outcome = ExtractionEngine.Run(Rule, text);
        var items = outcome.Fields.First(f => f.FieldKey == "items").Value;

        Assert.Equal(2, items.GetArrayLength());
        Assert.Equal("11", items[1].GetProperty("ge10").GetString());
        // 没有结论列时该属性为空串（模板里是 select，空值不算"填了"）
        Assert.Equal(string.Empty, items[0].GetProperty("conclusion").GetString());
    }

    [Fact]
    public void 结论的英文与简写应映射到标准取值()
    {
        const string text = "结论：合格";
        var outcome = ExtractionEngine.Run(Rule, text);
        var index = outcome.Fields.ToDictionary(f => f.FieldKey, f => f);

        Assert.Equal("符合规定", Text(index["result"]));
    }

    [Fact]
    public void 完全无关的文本应返回空结果且不抛异常()
    {
        var outcome = ExtractionEngine.Run(Rule, "这是一份与检验无关的会议纪要，没有任何样品信息。");

        Assert.False(outcome.HasResult);
        Assert.NotEmpty(outcome.Warnings);
    }

    [Fact]
    public void 空文本应给出可读的原因()
    {
        var outcome = ExtractionEngine.Run(Rule, "   ");

        Assert.False(outcome.HasResult);
        Assert.Contains(outcome.Warnings, w => w.Contains("扫描件") || w.Contains("文本"));
    }

    [Fact]
    public void 非法规则应抛出规则异常而不是别的()
    {
        Assert.Throws<InvalidRuleException>(() => ExtractionRule.Parse("{}"));
        Assert.Throws<InvalidRuleException>(() => ExtractionRule.Parse("""{ "targets": [ { "field": "a", "extractors": [] } ] }"""));
        Assert.Throws<InvalidRuleException>(() =>
            ExtractionRule.Parse("""{ "targets": [ { "field": "a", "extractors": [ { "type": "regex", "patterns": ["(["] } ] } ] }"""));
    }

    [Fact]
    public void 表格抽取器不能用在非表格字段上()
    {
        const string rule = """
        { "targets": [ { "field": "a", "extractors": [
          { "type": "tableRows", "rowPattern": "^(?<x>\\d+)$", "columns": { "c": "x" } } ] } ] }
        """;

        Assert.Throws<InvalidRuleException>(() => ExtractionRule.Parse(rule));
    }

    private static string Text(ExtractedField field) =>
        field.Value.ValueKind == JsonValueKind.String ? field.Value.GetString() ?? string.Empty : field.Value.GetRawText();
}
