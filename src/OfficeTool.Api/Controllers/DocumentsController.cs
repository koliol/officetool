using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>文档副本管理（设计文档 §5.4、§7.5）。</summary>
[ApiController]
[Route("api/documents")]
public sealed class DocumentsController(ContentCatalogService content) : ControllerBase
{
    private readonly ContentCatalogService _content = content;

    [HttpGet]
    public Task<PagedResult<DocumentDto>> List(
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
        _content.ListDocumentsAsync(
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

    [HttpPost("create")]
    public Task<DocumentDto> Create([FromBody] CreateDocumentRequest request, CancellationToken ct) =>
        _content.CreateDocumentAsync(request, ct);

    [HttpPost("rename")]
    public Task<DocumentDto> Rename([FromBody] RenameRequest request, CancellationToken ct) =>
        _content.RenameDocumentAsync(request, ct);

    /// <summary>把文档复制到另一个项目/检项（目标由用户在网页上选择）。</summary>
    [HttpPost("copy")]
    public Task<CopyResultDto> Copy([FromBody] CopyDocumentRequest request, CancellationToken ct) =>
        _content.CopyDocumentAsync(request, ct);

    [HttpDelete]
    public async Task<IActionResult> Delete(
        [FromQuery] string project, [FromQuery] string check, [FromQuery] string fileName, CancellationToken ct)
    {
        await _content.DeleteDocumentAsync(project, check, fileName, ct);
        return NoContent();
    }
}
