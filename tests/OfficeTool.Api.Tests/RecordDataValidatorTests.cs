using System.Text.Json;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 记录数据的服务端校验。草稿与提交两种严格程度是 ELN 的关键差别：
/// 中途必须能保存半成品，提交则必须齐全。
/// </summary>
public class RecordDataValidatorTests
{
    private static FormDefinition Schema =>
        FormSchema.Validate(BuiltInContent.InsolubleParticlesTemplateSchema).Schema!;

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public void 空数据在草稿状态应放行()
    {
        Assert.Empty(RecordDataValidator.Validate(Schema, Json("{}"), requireComplete: false));
    }

    [Fact]
    public void 空数据在提交时应指出缺哪些必填项()
    {
        var errors = RecordDataValidator.Validate(Schema, Json("{}"), requireComplete: true);

        Assert.Contains(errors, e => e.Contains("样品名称"));
        Assert.Contains(errors, e => e.Contains("批号"));
        Assert.Contains(errors, e => e.Contains("检验日期"));
        Assert.Contains(errors, e => e.Contains("检验方法"));
        Assert.Contains(errors, e => e.Contains("逐样品测定结果"));
        Assert.Contains(errors, e => e.Contains("总体结论"));
    }

    [Fact]
    public void 下拉值不在选项内应报错()
    {
        var errors = RecordDataValidator.Validate(
            Schema,
            Json("""{ "method": "目测法" }"""),
            requireComplete: false);

        Assert.Contains(errors, e => e.Contains("检验方法") && e.Contains("目测法"));
    }

    [Fact]
    public void 数字列填了非数字应报错()
    {
        var errors = RecordDataValidator.Validate(
            Schema,
            Json("""{ "items": [ { "sampleNo": "1", "ge10": "十二", "ge25": "3" } ] }"""),
            requireComplete: false);

        Assert.Contains(errors, e => e.Contains("≥10μm") && e.Contains("数字"));
    }

    [Fact]
    public void 数字以字符串形式提交应被接受()
    {
        // 表单原生 input 拿到的就是字符串，前端不做转换也必须能存
        var errors = RecordDataValidator.Validate(
            Schema,
            Json("""{ "items": [ { "sampleNo": "1", "ge10": "12", "ge25": "3.5" } ] }"""),
            requireComplete: false);

        Assert.Empty(errors);
    }

    [Fact]
    public void 提交时表格行数不足应报错()
    {
        var errors = RecordDataValidator.Validate(
            Schema,
            Json("""{ "items": [] }"""),
            requireComplete: true);

        Assert.Contains(errors, e => e.Contains("逐样品测定结果"));
    }

    [Fact]
    public void 日期格式非法应报错()
    {
        var errors = RecordDataValidator.Validate(
            Schema,
            Json("""{ "testDate": "二〇二六年" }"""),
            requireComplete: false);

        Assert.Contains(errors, e => e.Contains("检验日期"));
    }

    [Fact]
    public void 填写完整的记录应通过提交校验()
    {
        const string data = """
        {
          "productName": "注射用头孢曲松钠",
          "batchNo": "20260901",
          "testDate": "2026-11-06",
          "method": "光阻法",
          "items": [ { "sampleNo": "1", "ge10": "12", "ge25": "3", "conclusion": "符合" } ],
          "result": "符合规定"
        }
        """;

        Assert.Empty(RecordDataValidator.Validate(Schema, Json(data), requireComplete: true));
    }
}
