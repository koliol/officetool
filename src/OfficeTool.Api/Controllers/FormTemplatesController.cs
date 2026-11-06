using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// ELN 表单模板（在线定义的记录格式）。
///
/// 与旧的 TemplatesController 的区别：那一个是"共享盘上的 .docx 文件"，
/// 只负责上传/改名/删除；这里管的是**结构**，没有文件。
/// 旧的保留给"历史文件"只读页面用。
/// </summary>
[ApiController]
[Route("api/form-templates")]
public sealed class FormTemplatesController(FormTemplateService templates) : ControllerBase
{
    private readonly FormTemplateService _templates = templates;

    [HttpGet]
    public Task<IReadOnlyList<FormTemplateListItemDto>> List(
        [FromQuery] string? keyword,
        [FromQuery] string? category,
        [FromQuery] bool includeDisabled = false,
        CancellationToken ct = default) =>
        _templates.ListAsync(keyword, category, includeDisabled, ct);

    [HttpGet("{id:int}")]
    public Task<FormTemplateDto> Get(int id, CancellationToken ct) => _templates.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<FormTemplateDto>> Create(
        [FromBody] SaveFormTemplateRequest request,
        CancellationToken ct)
    {
        var created = await _templates.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    public Task<FormTemplateDto> Update(int id, [FromBody] SaveFormTemplateRequest request, CancellationToken ct) =>
        _templates.UpdateAsync(id, request, ct);

    /// <summary>启用/停用。停用不影响已建的记录，只是不再出现在"新建记录"的模板选择里。</summary>
    [HttpPost("{id:int}/enabled")]
    public Task<FormTemplateDto> SetEnabled(int id, [FromQuery] bool value, CancellationToken ct) =>
        _templates.SetEnabledAsync(id, value, ct);

    [HttpPost("{id:int}/copy")]
    public Task<FormTemplateDto> Copy(int id, [FromBody] CopyFormTemplateRequest request, CancellationToken ct) =>
        _templates.CopyAsync(id, request, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        await _templates.DeleteAsync(id, ct);
        return NoContent();
    }
}
