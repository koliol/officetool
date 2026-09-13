using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// 附件（二期功能，「提取」契约预留）。
///
/// 接口清单：
///   GET    /api/attachments?project=&amp;check=        列出附件
///   POST   /api/attachments/upload                   上传附件（multipart）
///   DELETE /api/attachments?project=&amp;check=&amp;fileName=  删除附件
///   POST   /api/attachments/extract                  执行提取（当前 501，契约已定）
///
/// 提取规则本身的增删改查在 <see cref="RulesController"/>（规则是文件，按文件夹存放）。
/// </summary>
[ApiController]
[Route("api")]
public sealed class AttachmentsController(AttachmentService attachments) : ControllerBase
{
    private readonly AttachmentService _attachments = attachments;

    /// <summary>
    /// 提取功能是否已实现。
    ///
    /// 目前固定 <c>false</c>：规则可配置、可查询，但真正的提取脚本尚未编写。
    /// 前端读 <c>/api/system/config</c> 里的 <c>extractionAvailable</c> 来决定
    /// 「提取」按钮是否可点 —— 刻意不给出「点了没反应」的按钮。
    ///
    /// **实现提取脚本后，只需把这里改成 true**，其余链路（前端提示、按钮状态）自动生效。
    /// </summary>
    public const bool ExtractionAvailable = false;

    [HttpGet("attachments")]
    public Task<IReadOnlyList<AttachmentDto>> List(
        [FromQuery] string project, [FromQuery] string check, CancellationToken ct = default) =>
        _attachments.ListAsync(project, check, ct);

    [HttpPost("attachments/upload")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<ActionResult<AttachmentDto>> Upload(
        [FromForm] string project,
        [FromForm] string check,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new BadRequestException("上传文件为空。");
        }

        // 与模板上传一致：先落临时文件，避免把不可重绕的请求流直接传进业务逻辑
        var tempPath = Path.Combine(Path.GetTempPath(), $"officetool-attachment-{Guid.NewGuid():N}");
        try
        {
            await using (var temp = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(temp, ct);
            }

            await using var source = System.IO.File.OpenRead(tempPath);
            var created = await _attachments.UploadAsync(
                project, check, file.FileName, source, file.Length, ct);

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

    [HttpDelete("attachments")]
    public async Task<IActionResult> Delete(
        [FromQuery] string project,
        [FromQuery] string check,
        [FromQuery] string fileName,
        CancellationToken ct)
    {
        await _attachments.DeleteAsync(project, check, fileName, ct);
        return NoContent();
    }

    /// <summary>
    /// 按规则把附件内容提取并回填到目标文档。
    /// 当前返回 <b>501</b>（feature_not_available）：请求校验全部通过后才会抛，
    /// 因此前端可以在脚本未就绪时就把整条参数链路验证通。
    /// </summary>
    [HttpPost("attachments/extract")]
    public async Task<IActionResult> Extract([FromBody] ExtractRequest request, CancellationToken ct)
    {
        await _attachments.ExtractAsync(request, ct);
        return NoContent(); // 提取实现后改为返回提取结果
    }
}
