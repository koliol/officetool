namespace OfficeTool.Core.Services;

/// <summary>
/// 随产品发布的内置内容（表单模板 + 提取规则）。
///
/// 放在 Core 常量里而不是数据库初始化脚本里，图的是两件事：
/// 1. 升级时能按版本号判断"内置内容该更新了"，而用户自建的模板不受影响；
/// 2. 单元测试可以直接拿这份 JSON 跑规则引擎，测的就是线上那一份，不另写夹具。
/// </summary>
public static class BuiltInContent
{
    /// <summary>不溶性微粒检查记录模板编码。</summary>
    public const string InsolubleParticlesTemplateCode = "INSOLUBLE_PARTICLES";

    /// <summary>不溶性微粒提取规则编码。</summary>
    public const string InsolubleParticlesRuleCode = "insoluble_particles";

    /// <summary>内置模板/规则的版本。内容变化时 +1，升级时据此覆盖内置副本。</summary>
    public const int InsolubleParticlesVersion = 1;

    public const string InsolubleParticlesTemplateName = "不溶性微粒检查记录";

    public const string InsolubleParticlesRuleName = "不溶性微粒（仪器报告）";

    /// <summary>
    /// 不溶性微粒记录的表单定义。
    ///
    /// 字段划分对应中国药典 0903 的常见记录结构：基本信息 / 测定结果 / 结论。
    /// 表格列固定为「样品编号 / ≥10μm / ≥25μm / 结论」，
    /// 与规则里 tableRows 的命名组一一对应 —— 两边改一处就必须改另一处，
    /// 所以测试里有一条专门断言"规则的目标字段都能在模板里找到"。
    /// </summary>
    public const string InsolubleParticlesTemplateSchema = """
    {
      "title": "不溶性微粒检查记录",
      "sections": [
        {
          "key": "basic",
          "title": "基本信息",
          "fields": [
            { "key": "productName", "label": "样品名称", "type": "text", "required": true, "placeholder": "如 注射用头孢曲松钠" },
            { "key": "batchNo", "label": "批号", "type": "text", "required": true, "placeholder": "如 20260901" },
            { "key": "specification", "label": "规格", "type": "text", "placeholder": "如 1.0g" },
            { "key": "testDate", "label": "检验日期", "type": "date", "required": true },
            { "key": "instrumentNo", "label": "仪器编号", "type": "text" },
            {
              "key": "method",
              "label": "检验方法",
              "type": "select",
              "required": true,
              "options": [ "显微计数法", "光阻法" ]
            }
          ]
        },
        {
          "key": "results",
          "title": "测定结果",
          "fields": [
            {
              "key": "items",
              "label": "逐样品测定结果",
              "type": "table",
              "required": true,
              "minRows": 1,
              "help": "每个容器（或每个样品）一行；单位：粒/容器",
              "columns": [
                { "key": "sampleNo", "label": "样品编号", "type": "text" },
                { "key": "ge10", "label": "≥10μm", "type": "number", "unit": "粒/容器" },
                { "key": "ge25", "label": "≥25μm", "type": "number", "unit": "粒/容器" },
                { "key": "conclusion", "label": "单项结论", "type": "select", "options": [ "符合", "不符合" ] }
              ]
            }
          ]
        },
        {
          "key": "conclusion",
          "title": "结论与签署",
          "fields": [
            {
              "key": "result",
              "label": "总体结论",
              "type": "select",
              "required": true,
              "options": [ "符合规定", "不符合规定" ]
            },
            { "key": "analyst", "label": "检验人", "type": "text" },
            { "key": "reviewer", "label": "复核人", "type": "text" },
            { "key": "remark", "label": "备注", "type": "textarea", "maxLength": 1000 }
          ]
        }
      ]
    }
    """;

    /// <summary>
    /// 不溶性微粒的提取规则。
    ///
    /// 面向的输入是仪器/系统导出的**文本型 PDF**，典型版面：
    /// <code>
    /// 样品名称：注射用头孢曲松钠
    /// 批号：20260901
    /// 检验日期：2026-10-12
    /// 1      12      3      符合
    /// 2       9      1      符合
    /// 结论：符合规定
    /// </code>
    /// 表格行给了两条模式：带结论列的、不带结论列的（不同仪器导出不一样）。
    /// </summary>
    public const string InsolubleParticlesRule = """
    {
      "templateCode": "INSOLUBLE_PARTICLES",
      "version": 1,
      "targets": [
        {
          "field": "productName",
          "label": "样品名称",
          "extractors": [
            { "type": "labelValue", "labels": [ "样品名称", "品名", "检品名称", "Sample Name" ] }
          ]
        },
        {
          "field": "batchNo",
          "label": "批号",
          "extractors": [
            { "type": "labelValue", "labels": [ "批号", "生产批号", "Lot No", "Batch No" ] }
          ]
        },
        {
          "field": "specification",
          "label": "规格",
          "extractors": [
            { "type": "labelValue", "labels": [ "规格", "Specification" ] }
          ]
        },
        {
          "field": "testDate",
          "label": "检验日期",
          "extractors": [
            { "type": "labelValue", "labels": [ "检验日期", "检测日期", "报告日期" ] },
            { "type": "regex", "patterns": [ "检验日期[:\\s]*(?<v>\\d{4}[-/年]\\d{1,2}[-/月]\\d{1,2})" ] }
          ]
        },
        {
          "field": "instrumentNo",
          "label": "仪器编号",
          "extractors": [
            { "type": "labelValue", "labels": [ "仪器编号", "仪器型号", "设备编号" ] }
          ]
        },
        {
          "field": "method",
          "label": "检验方法",
          "extractors": [
            { "type": "regex", "patterns": [ "(?<v>显微计数法|光阻法)" ] },
            { "type": "labelValue", "labels": [ "检验方法", "检测方法" ] }
          ]
        },
        {
          "field": "items",
          "label": "逐样品测定结果",
          "kind": "table",
          "extractors": [
            {
              "type": "tableRows",
              "minRows": 1,
              "rowPattern": "^(?<sampleNo>[A-Za-z\\u4e00-\\u9fa5]{0,6}[-_]?\\d{1,4}[A-Za-z]?)\\s{1,}(?<ge10>\\d{1,7})\\s{1,}(?<ge25>\\d{1,7})(?:\\s{1,}(?<conclusion>符合|不符合|合格|不合格))?$",
              "columns": { "sampleNo": "sampleNo", "ge10": "ge10", "ge25": "ge25", "conclusion": "conclusion" },
              "stopPatterns": [ "^备注", "^检验人", "^复核人", "^结论", "^审核" ]
            },
            {
              "type": "tableRows",
              "minRows": 1,
              "rowPattern": "^(?<sampleNo>\\d{1,3})\\s{1,}(?<ge10>\\d{1,7})\\s{1,}(?<ge25>\\d{1,7})$",
              "columns": { "sampleNo": "sampleNo", "ge10": "ge10", "ge25": "ge25" }
            }
          ]
        },
        {
          "field": "result",
          "label": "总体结论",
          "extractors": [
            {
              "type": "regex",
              "patterns": [ "(?:结论|判定)[:：\\s]*(?<v>符合规定|不符合规定|符合|不合格|合格)" ],
              "map": { "符合": "符合规定", "合格": "符合规定", "不合格": "不符合规定" }
            },
            { "type": "regex", "patterns": [ "(?<v>符合规定|不符合规定)" ] }
          ]
        }
      ]
    }
    """;
}
