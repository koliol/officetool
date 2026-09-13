using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>回收站：列出、恢复、彻底删除。</summary>
[ApiController]
[Route("api/trash")]
public sealed class TrashController(TrashService trash) : ControllerBase
{
    private readonly TrashService _trash = trash;

    [HttpGet]
    public Task<PagedResult<TrashItemDto>> List(
        [FromQuery] string? kind,
        [FromQuery] string? project,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default) =>
        _trash.ListAsync(kind, project, page, pageSize, ct);

    [HttpPost("{id:int}/restore")]
    public Task<TrashItemDto> Restore(int id, CancellationToken ct) =>
        _trash.RestoreAsync(id, ct);

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Purge(int id, CancellationToken ct)
    {
        await _trash.PurgeAsync(id, ct);
        return NoContent();
    }
}
