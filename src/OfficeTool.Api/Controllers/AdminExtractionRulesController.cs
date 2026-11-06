using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// 提取规则的**管理侧**接口（仅系统管理员）。
///
/// 规则本身是产品资产，不由用户上传：管理员能做的是
/// 「看有哪些规则 → 启停 → 划生效范围」。
/// </summary>
[ApiController]
[Route("api/admin/extraction-rules")]
public sealed class AdminExtractionRulesController(ExtractionRuleAdminService rules) : ControllerBase
{
    private readonly ExtractionRuleAdminService _rules = rules;

    [HttpGet]
    public Task<IReadOnlyList<AdminExtractionRuleDto>> List(CancellationToken ct) => _rules.ListAsync(ct);

    [HttpPost]
    public async Task<ActionResult<AdminExtractionRuleDto>> Create(
        [FromBody] CreateRuleRequest request,
        CancellationToken ct)
    {
        var created = await _rules.CreateAsync(request.Code, request.Definition, ct);
        return Ok(created);
    }

    [HttpPut("{id:int}")]
    public Task<AdminExtractionRuleDto> Update(
        int id,
        [FromBody] SaveRuleDefinitionRequest request,
        CancellationToken ct) =>
        _rules.UpdateAsync(id, request, ct);

    [HttpPost("{id:int}/enabled")]
    public Task<AdminExtractionRuleDto> SetEnabled(int id, [FromQuery] bool value, CancellationToken ct) =>
        _rules.SetEnabledAsync(id, value, ct);

    /// <summary>整体替换生效范围（前端每次提交完整集合）。</summary>
    [HttpPut("{id:int}/scopes")]
    public Task<AdminExtractionRuleDto> ReplaceScopes(
        int id,
        [FromBody] SaveRuleScopesRequest request,
        CancellationToken ct) =>
        _rules.ReplaceScopesAsync(id, request, ct);
}
