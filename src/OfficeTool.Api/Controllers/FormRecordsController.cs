using Microsoft.AspNetCore.Mvc;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Services;

namespace OfficeTool.Api.Controllers;

/// <summary>
/// ELN 记录：填、存、提交、作废、挂原始文件、解析。
///
/// 路由刻意按「记录 → 子资源」组织（/files、/rules、/extract、/runs），
/// 因为所有操作的作用域都是"一条记录"，扁平化会让接口变成一堆 id 参数。
/// </summary>
[ApiController]
[Route("api/form-records")]
public sealed class FormRecordsController(
    FormRecordService records,
    ExtractionService extraction) : ControllerBase
{
    private readonly FormRecordService _records = records;
    private readonly ExtractionService _extraction = extraction;

    [HttpGet]
    public Task<PagedResult<FormRecordListItemDto>> List(
        [FromQuery] string? project,
        [FromQuery] string? check,
        [FromQuery] string? status,
        [FromQuery] string? keyword,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default) =>
        _records.ListAsync(project, check, status, keyword, page, pageSize, ct);

    [HttpGet("{id:int}")]
    public Task<FormRecordDto> Get(int id, CancellationToken ct) => _records.GetAsync(id, ct);

    [HttpPost]
    public async Task<ActionResult<FormRecordDto>> Create(
        [FromBody] CreateFormRecordRequest request,
        CancellationToken ct)
    {
        var created = await _records.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = created.Id }, created);
    }

    /// <summary>保存草稿。整份覆盖写 —— 表单提交天然是整体语义。</summary>
    [HttpPut("{id:int}/data")]
    public Task<FormRecordDto> SaveDraft(int id, [FromBody] SaveRecordDataRequest request, CancellationToken ct) =>
        _records.SaveDraftAsync(id, request.Data, ct);

    [HttpPost("{id:int}/submit")]
    public Task<FormRecordDto> Submit(int id, CancellationToken ct) => _records.SubmitAsync(id, ct);

    [HttpPost("{id:int}/void")]
    public Task<FormRecordDto> Void(int id, [FromBody] VoidRecordRequest request, CancellationToken ct) =>
        _records.VoidAsync(id, request.Reason, ct);

    // ── 原始文件 ────────────────────────────────────────────────────

    [HttpPost("{id:int}/files")]
    public async Task<ActionResult<RecordFileDto>> Upload(
        int id,
        IFormFile file,
        CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();

        var dto = await _records.AddFileAsync(id, file.FileName, stream, file.Length, file.ContentType, ct);
        return Ok(dto);
    }

    [HttpDelete("{id:int}/files/{fileId:int}")]
    public async Task<IActionResult> DeleteFile(int id, int fileId, CancellationToken ct)
    {
        await _records.DeleteFileAsync(id, fileId, ct);
        return NoContent();
    }

    // ── 解析 ────────────────────────────────────────────────────────

    /// <summary>这条记录在它所属范围上可以用哪些规则（管理员没划范围的规则不会出现）。</summary>
    [HttpGet("{id:int}/rules")]
    public Task<IReadOnlyList<ExtractionRuleListItemDto>> Rules(int id, CancellationToken ct) =>
        _extraction.ListRulesForRecordAsync(id, ct);

    /// <summary>执行解析，返回候选值。**不写记录数据**，由用户确认后再 PUT /data。</summary>
    [HttpPost("{id:int}/extract")]
    public Task<ExtractRecordResultDto> Extract(int id, [FromBody] ExtractRecordRequest request, CancellationToken ct) =>
        _extraction.RunAsync(id, request, ct);

    [HttpGet("{id:int}/runs")]
    public Task<IReadOnlyList<ExtractionRunDto>> Runs(int id, CancellationToken ct) =>
        _extraction.ListRunsAsync(id, ct);
}
