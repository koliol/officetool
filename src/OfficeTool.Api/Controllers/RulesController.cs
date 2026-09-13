using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// 提取规则（与模板/文档/附件一样的「项目/检项」两级目录，规则本体是文件）。
///
/// 接口清单：
///   GET    /api/extraction-rules?project=&amp;check=          列出该文件夹的规则（含备注）
///   POST   /api/extraction-rules/upload                    上传规则文件（multipart）
///   DELETE /api/extraction-rules?project=&amp;check=&amp;fileName=  删除规则
///   POST   /api/extraction-rules/note                      在网页上直接改备注
///   POST   /api/extraction-rules/copy                      复制到其他项目/检项
/// </summary>
[ApiController]
[Route("api")]
public sealed class RulesController(RuleCatalogService rules) : ControllerBase
{
    private readonly RuleCatalogService _rules = rules;

    [HttpGet("extraction-rules")]
    public Task<IReadOnlyList<RuleDto>> List(
        [FromQuery] string project, [FromQuery] string check, CancellationToken ct = default) =>
        _rules.ListAsync(project, check, ct);

    [HttpPost("extraction-rules/upload")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<ActionResult<RuleDto>> Upload(
        [FromForm] string project,
        [FromForm] string check,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new BadRequestException("上传文件为空。");
        }

        // 与模板/附件一致：先落临时文件，避免把不可重绕的请求流直接传进业务逻辑
        var tempPath = Path.Combine(Path.GetTempPath(), $"officetool-rule-{Guid.NewGuid():N}");
        try
        {
            await using (var temp = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(temp, ct);
            }

            await using var source = System.IO.File.OpenRead(tempPath);
            var created = await _rules.UploadAsync(project, check, file.FileName, source, file.Length, ct);

            return Ok(created);
        }
        finally
        {
            if (System.IO.File.Exists(tempPath))
            {
                System.IO.File.Delete(tempPath);
            }
        }
    }

    [HttpDelete("extraction-rules")]
    public async Task<IActionResult> Delete(
        [FromQuery] string project,
        [FromQuery] string check,
        [FromQuery] string fileName,
        CancellationToken ct)
    {
        await _rules.DeleteAsync(project, check, fileName, ct);
        return NoContent();
    }

    /// <summary>网页上直接修改备注。</summary>
    [HttpPost("extraction-rules/note")]
    public Task<RuleDto> UpdateNote([FromBody] UpdateRuleNoteRequest request, CancellationToken ct) =>
        _rules.UpdateNoteAsync(request, ct);

    /// <summary>把选中的规则复制到另一个项目/检项（备注一并带过去）。</summary>
    [HttpPost("extraction-rules/copy")]
    public Task<CopyResultDto> Copy([FromBody] CopyRulesRequest request, CancellationToken ct) =>
        _rules.CopyAsync(request, ct);
}
