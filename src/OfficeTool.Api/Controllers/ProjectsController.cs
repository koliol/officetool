using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>项目管理与检项管理（设计文档 §5.1、§5.2、§7.5）。</summary>
[ApiController]
[Route("api/projects")]
public sealed class ProjectsController(ProjectCatalogService catalog) : ControllerBase
{
    private readonly ProjectCatalogService _catalog = catalog;

    [HttpGet]
    public Task<IReadOnlyList<ProjectDto>> List(CancellationToken ct) => _catalog.ListAsync(ct);

    /// <summary>一次返回项目 + 检项树，前端初始化用，避免 N+1。</summary>
    [HttpGet("tree")]
    public Task<IReadOnlyList<ProjectTreeNodeDto>> Tree(CancellationToken ct) => _catalog.TreeAsync(ct);

    [HttpPost]
    public async Task<ActionResult<ProjectDto>> Create([FromBody] CreateProjectRequest request, CancellationToken ct)
    {
        var created = await _catalog.CreateAsync(request, ct);
        return CreatedAtAction(nameof(List), new { }, created);
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete(string name, CancellationToken ct)
    {
        await _catalog.DeleteAsync(name, ct);
        return NoContent();
    }

    [HttpGet("{project}/checks")]
    public Task<IReadOnlyList<CheckDto>> ListChecks(string project, CancellationToken ct) =>
        _catalog.ListChecksAsync(project, ct);

    [HttpPost("{project}/checks")]
    public async Task<ActionResult<CheckDto>> CreateCheck(
        string project, [FromBody] CreateCheckRequest request, CancellationToken ct)
    {
        var created = await _catalog.CreateCheckAsync(project, request, ct);
        return CreatedAtAction(nameof(ListChecks), new { project }, created);
    }

    [HttpDelete("{project}/checks/{check}")]
    public async Task<IActionResult> DeleteCheck(string project, string check, CancellationToken ct)
    {
        await _catalog.DeleteCheckAsync(project, check, ct);
        return NoContent();
    }
}
