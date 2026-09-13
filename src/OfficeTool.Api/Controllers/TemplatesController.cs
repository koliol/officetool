using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>模板管理（设计文档 §5.3、§7.5）。</summary>
[ApiController]
[Route("api/templates")]
public sealed class TemplatesController(ContentCatalogService content) : ControllerBase
{
    private readonly ContentCatalogService _content = content;

    [HttpGet]
    public Task<PagedResult<TemplateDto>> List(
        [FromQuery] string? project,
        [FromQuery] string? check,
        [FromQuery] string? keyword,
        [FromQuery] string? extension,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? sortBy,
        [FromQuery] string? sortDir,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        _content.ListTemplatesAsync(
            new FileQuery
            {
                Project = project,
                Check = check,
                Keyword = keyword,
                Extension = extension,
                From = from,
                To = to,
                SortBy = sortBy,
                SortDir = sortDir,
                Page = page,
                PageSize = pageSize,
            },
            ct);

    [HttpPost("upload")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    public async Task<ActionResult<TemplateDto>> Upload(
        [FromForm] string project,
        [FromForm] string check,
        [FromForm] IFormFile file,
        CancellationToken ct)
    {
        if (file is null || file.Length == 0)
        {
            throw new BadRequestException("上传文件为空。");
        }

        // 临时落盘再交给服务层，避免把不可重绕的请求流直接传进业务逻辑
        var tempPath = Path.Combine(Path.GetTempPath(), $"officetool-upload-{Guid.NewGuid():N}");
        try
        {
            await using (var temp = System.IO.File.Create(tempPath))
            {
                await file.CopyToAsync(temp, ct);
            }

            await using var source = System.IO.File.OpenRead(tempPath);
            var created = await _content.UploadTemplateAsync(project, check, file.FileName, source, file.Length, ct);
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

    [HttpPost("copy")]
    public Task<TemplateDto> Copy([FromBody] CopyTemplateRequest request, CancellationToken ct) =>
        _content.CopyTemplateAsync(request, ct);

    [HttpPost("rename")]
    public Task<TemplateDto> Rename([FromBody] RenameRequest request, CancellationToken ct) =>
        _content.RenameTemplateAsync(request, ct);

    [HttpDelete]
    public async Task<IActionResult> Delete(
        [FromQuery] string project, [FromQuery] string check, [FromQuery] string fileName, CancellationToken ct)
    {
        await _content.DeleteTemplateAsync(project, check, fileName, ct);
        return NoContent();
    }
}
