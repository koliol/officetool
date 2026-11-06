using System.Text.Json;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 表单定义校验。
///
/// 这一层是「模板结构」的唯一闸门：一份结构非法却进了库的模板，
/// 会在**所有**引用它的记录上同时爆掉，而且用户看到的只是"页面空白"。
/// </summary>
public class FormSchemaTests
{
    [Fact]
    public void 内置不溶性微粒模板应通过校验()
    {
        var result = FormSchema.Validate(BuiltInContent.InsolubleParticlesTemplateSchema);

        Assert.True(result.IsValid, string.Join("；", result.Errors));
        Assert.NotNull(result.Schema);
        Assert.Equal(3, result.Schema!.Sections.Count);
        Assert.Equal(11, result.Schema.Sections.Sum(s => s.Fields.Count));
    }

    [Fact]
    public void 缺少sections应报错()
    {
        var result = FormSchema.Validate("""{ "title": "x" }""");

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("sections"));
    }

    [Fact]
    public void 字段key重复应报错_且跨分区也算重复()
    {
        const string schema = """
        {
          "sections": [
            { "key": "a", "title": "A", "fields": [ { "key": "x", "label": "X", "type": "text" } ] },
            { "key": "b", "title": "B", "fields": [ { "key": "x", "label": "X2", "type": "text" } ] }
          ]
        }
        """;

        var result = FormSchema.Validate(schema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("重复"));
    }

    [Fact]
    public void 下拉字段没有options应报错()
    {
        const string schema = """
        { "sections": [ { "key": "a", "title": "A",
          "fields": [ { "key": "s", "label": "S", "type": "select" } ] } ] }
        """;

        var result = FormSchema.Validate(schema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("options"));
    }

    [Fact]
    public void 表格字段没有列应报错()
    {
        const string schema = """
        { "sections": [ { "key": "a", "title": "A",
          "fields": [ { "key": "t", "label": "T", "type": "table" } ] } ] }
        """;

        var result = FormSchema.Validate(schema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("columns"));
    }

    [Fact]
    public void 未知字段类型应报错并列出可选值()
    {
        const string schema = """
        { "sections": [ { "key": "a", "title": "A",
          "fields": [ { "key": "f", "label": "F", "type": "signature" } ] } ] }
        """;

        var result = FormSchema.Validate(schema);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("signature"));
    }

    [Fact]
    public void 非法key格式应报错()
    {
        const string schema = """
        { "sections": [ { "key": "a", "title": "A",
          "fields": [ { "key": "1bad", "label": "F", "type": "text" } ] } ] }
        """;

        Assert.False(FormSchema.Validate(schema).IsValid);
    }

    [Fact]
    public void 合法的最小模板应通过()
    {
        const string schema = """
        { "sections": [ { "key": "s1", "title": "基本信息",
          "fields": [ { "key": "name", "label": "名称", "type": "text", "required": true } ] } ] }
        """;

        var result = FormSchema.Validate(schema);

        Assert.True(result.IsValid, string.Join("；", result.Errors));
        Assert.True(result.Schema!.FieldIndex().ContainsKey("name"));
    }

    /// <summary>
    /// 规则里的目标字段必须都能在模板里找到 —— 两边是两份独立的配置，
    /// 改一边忘一边，用户看到的就是"解析成功但什么都没填进去"。
    /// </summary>
    [Fact]
    public void 内置规则的每个目标字段都存在于内置模板中()
    {
        var template = FormSchema.Validate(BuiltInContent.InsolubleParticlesTemplateSchema).Schema!;
        var rule = ExtractionRule.Parse(BuiltInContent.InsolubleParticlesRule);

        var fields = template.FieldIndex();

        foreach (var target in rule.Targets)
        {
            Assert.True(
                fields.ContainsKey(target.Field),
                $"规则目标字段 {target.Field} 在内置模板里不存在");
        }

        Assert.Equal(BuiltInContent.InsolubleParticlesTemplateCode, rule.TemplateCode);
    }
}
