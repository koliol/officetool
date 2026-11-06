using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// 内置内容的播种：表单模板 + 提取规则。
///
/// 幂等且可升级：
/// - 库里没有 → 插入；
/// - 有且是内置的、版本比代码里的旧 → 用新版覆盖（升级就能拿到新规则）；
/// - 用户自己改过（IsBuiltIn=false，比如"复制一份再改"）→ 一律不碰。
///
/// 之所以不用 EF 的 HasData：种子内容里有大段 JSON，用迁移快照维护会变成
/// "每次改一个正则就要写一个迁移"，而这类内容本来就不该和表结构绑在一起。
/// </summary>
public static class BuiltInContentSeeder
{
    public static async Task SeedAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var now = DateTime.Now;
        var changed = false;

        // ── 模板 ────────────────────────────────────────────────────
        var template = await db.FormTemplates
            .FirstOrDefaultAsync(t => t.Code == BuiltInContent.InsolubleParticlesTemplateCode, ct);

        if (template is null)
        {
            db.FormTemplates.Add(new FormTemplate
            {
                Code = BuiltInContent.InsolubleParticlesTemplateCode,
                Name = BuiltInContent.InsolubleParticlesTemplateName,
                Category = "理化检验",
                Description = "按中国药典 0903 不溶性微粒检查法设计的记录表，支持从仪器导出的 PDF 自动带入数据。",
                Version = BuiltInContent.InsolubleParticlesVersion,
                SchemaJson = BuiltInContent.InsolubleParticlesTemplateSchema,
                IsEnabled = true,
                IsBuiltIn = true,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = "system",
            });

            changed = true;
            logger.LogInformation("已内置表单模板：{Name}", BuiltInContent.InsolubleParticlesTemplateName);
        }
        else if (template.IsBuiltIn && template.Version < BuiltInContent.InsolubleParticlesVersion)
        {
            template.SchemaJson = BuiltInContent.InsolubleParticlesTemplateSchema;
            template.Version = BuiltInContent.InsolubleParticlesVersion;
            template.UpdatedAt = now;
            template.UpdatedBy = "system";

            changed = true;
            logger.LogInformation(
                "内置表单模板已升级到 v{Version}：{Name}",
                BuiltInContent.InsolubleParticlesVersion,
                template.Name);
        }

        // ── 提取规则 ────────────────────────────────────────────────
        var rule = await db.ExtractionRuleDefinitions
            .FirstOrDefaultAsync(r => r.Code == BuiltInContent.InsolubleParticlesRuleCode, ct);

        if (rule is null)
        {
            db.ExtractionRuleDefinitions.Add(new ExtractionRuleDefinition
            {
                Code = BuiltInContent.InsolubleParticlesRuleCode,
                Name = BuiltInContent.InsolubleParticlesRuleName,
                Category = "理化检验",
                Description = "从仪器导出的不溶性微粒报告 PDF 中抽取样品信息、逐样品计数与结论。",
                Version = BuiltInContent.InsolubleParticlesVersion,
                RuleJson = BuiltInContent.InsolubleParticlesRule,
                IsEnabled = true,
                IsBuiltIn = true,
                CreatedAt = now,
                UpdatedAt = now,
                UpdatedBy = "system",
            });

            changed = true;
            logger.LogInformation("已内置提取规则：{Name}（需管理员设置生效范围后用户才可用）", BuiltInContent.InsolubleParticlesRuleName);
        }
        else if (rule.IsBuiltIn && rule.Version < BuiltInContent.InsolubleParticlesVersion)
        {
            rule.RuleJson = BuiltInContent.InsolubleParticlesRule;
            rule.Version = BuiltInContent.InsolubleParticlesVersion;
            rule.UpdatedAt = now;
            rule.UpdatedBy = "system";

            changed = true;
            logger.LogInformation(
                "内置提取规则已升级到 v{Version}：{Name}",
                BuiltInContent.InsolubleParticlesVersion,
                rule.Name);
        }

        if (changed)
        {
            await db.SaveChangesAsync(ct);
        }
    }
}
