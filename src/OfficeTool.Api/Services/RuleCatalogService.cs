using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// 提取规则目录服务。
///
/// 规则是**文件**，与模板/文档/附件一样按「项目/检项」两级存放：
/// <c>ExtractionRules\{项目}\{检项}\*.json</c>。
/// 建项目/检项时目录一并创建（见 <see cref="ProjectCatalogService"/>）。
///
/// ── 备注为什么在数据库 ────────────────────────────────────────────────
/// 规则本体必须在共享盘上（用户要能直接看到、替换、备份），
/// 但备注是纯应用侧元数据：放进共享盘会多出用户看不懂的散碎文件，
/// 放进数据库则可在网页上直接改、且不干扰共享盘的目录结构。
/// 关联键是「相对 RulesRoot 的路径」，因此复制规则时能把备注一起带过去。
///
/// ⚠️ 规则文件**只存储、不执行**。
/// </summary>
public sealed class RuleCatalogService(
    AppDbContext db,
    ProjectCatalogService catalog,
    PathLayout layout,
    IFileStore store,
    StorageOptions storageOptions,
    UploadOptions uploadOptions,
    OperationLogService logs,
    TrashService trash,
    RequestContext request,
    IAccessControlService access)
{
    private readonly AppDbContext _db = db;
    private readonly ProjectCatalogService _catalog = catalog;
    private readonly PathLayout _layout = layout;
    private readonly IFileStore _store = store;
    private readonly StorageOptions _storage = storageOptions;
    private readonly UploadOptions _upload = uploadOptions;
    private readonly OperationLogService _logs = logs;
    private readonly TrashService _trash = trash;
    private readonly RequestContext _request = request;
    private readonly IAccessControlService _access = access;

    private Task<UserAccess> CurrentAccessAsync(CancellationToken ct) => _access.ResolveAccessAsync(_request, ct);

    // ── 查询 ────────────────────────────────────────────────────────────

    /// <summary>列出某项目/检项下的全部规则文件（含各自备注）。</summary>
    public async Task<IReadOnlyList<RuleDto>> ListAsync(
        string projectName, string checkName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).RequireRead(check.Id, $"{project.Name}/{check.Name}");

        var directory = _layout.RuleDirectory(project.Name, check.Name);

        if (!_store.DirectoryExists(directory))
        {
            return [];
        }

        var notes = await LoadNotesAsync(project.Name, check.Name, ct);

        return _store.ListFiles(directory)
            .Where(f => IsAllowedExtension(f.Extension))
            .OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                var relative = _layout.RuleRelativePath(project.Name, check.Name, f.FileName);
                notes.TryGetValue(relative, out var note);
                return ToDto(f, project.Name, check.Name, note);
            })
            .ToList();
    }

    // ── 上传 / 删除 ─────────────────────────────────────────────────────

    /// <summary>上传规则文件。与模板/附件一致：先落盘、再登记，避免半截文件被当成有效规则。</summary>
    public async Task<RuleDto> UploadAsync(
        string projectName,
        string checkName,
        string fileName,
        Stream content,
        long declaredSize,
        CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.RuleManage, "上传提取规则", $"{project.Name}/{check.Name}");

        if (declaredSize > _upload.MaxSizeBytes)
        {
            throw new BadRequestException($"规则文件超过大小上限 {_upload.MaxSizeMB}MB。");
        }

        var safeName = NameValidator.ValidateFileName(fileName);

        // 只校验不取值：展示用的扩展名以磁盘上的真实文件名为准（保留原始大小写）
        NameValidator.EnsureAllowedExtension(Path.GetExtension(safeName), _upload.RuleExtensions);

        var directory = _layout.RuleDirectory(project.Name, check.Name);
        var fullPath = PathGuard.CombineUnderRoot(directory, safeName);

        if (_store.FileExists(fullPath))
        {
            throw new ConflictException($"同目录下已存在同名规则：{safeName}，请先重命名或删除。");
        }

        try
        {
            _store.CreateDirectory(directory);

            using (var target = _store.CreateNew(fullPath))
            {
                await content.CopyToAsync(target, ct);
            }

            var meta = _store.GetMeta(fullPath);

            if (meta.Size > _upload.MaxSizeBytes)
            {
                _store.DeleteFile(fullPath);
                throw new BadRequestException($"规则文件超过大小上限 {_upload.MaxSizeMB}MB。");
            }

            await _logs.WriteAsync(
                "上传提取规则", _layout.RuleRelativePath(project.Name, check.Name, safeName), success: true, ct: ct);

            return ToDto(meta, project.Name, check.Name, note: null);
        }
        catch (Exception ex) when (ex is not ConflictException && ex is not BadRequestException)
        {
            await _logs.WriteFailureAsync("上传提取规则", $"{projectName}/{checkName}/{fileName}", ex, ct);
            throw;
        }
    }

    /// <summary>删除规则文件，并一并清掉它的备注记录（否则会留下指向不存在文件的孤儿备注）。</summary>
    public async Task DeleteAsync(
        string projectName, string checkName, string fileName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.RuleManage, "删除提取规则", $"{project.Name}/{check.Name}");

        var safeName = NameValidator.ValidateFileName(fileName);

        var fullPath = PathGuard.CombineUnderRoot(_layout.RuleDirectory(project.Name, check.Name), safeName);

        if (!_store.FileExists(fullPath))
        {
            throw new NotFoundException($"规则不存在：{safeName}");
        }

        var relative = _layout.RuleRelativePath(project.Name, check.Name, safeName);
        await _trash.MoveToTrashAsync(
            TrashKind.Rule, project.Name, check.Name, safeName, fullPath, relative, ct);

        var note = await _db.RuleNotes.FirstOrDefaultAsync(n => n.RelativePath == relative, ct);
        if (note is not null)
        {
            _db.RuleNotes.Remove(note);
            await _db.SaveChangesAsync(ct);
        }

        await _logs.WriteAsync("删除提取规则", relative, success: true, ct: ct);
    }

    // ── 备注（网页上直接改） ────────────────────────────────────────────

    /// <summary>设置或清除某条规则的备注。传空串/空白即删除备注记录，不留空行。</summary>
    public async Task<RuleDto> UpdateNoteAsync(UpdateRuleNoteRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.RuleManage, "修改规则备注", $"{project.Name}/{check.Name}");

        var safeName = NameValidator.ValidateFileName(request.FileName);

        var fullPath = PathGuard.CombineUnderRoot(_layout.RuleDirectory(project.Name, check.Name), safeName);

        if (!_store.FileExists(fullPath))
        {
            throw new NotFoundException($"规则不存在：{safeName}");
        }

        var relative = _layout.RuleRelativePath(project.Name, check.Name, safeName);
        var text = (request.Note ?? string.Empty).Trim();

        var entity = await _db.RuleNotes.FirstOrDefaultAsync(n => n.RelativePath == relative, ct);

        if (text.Length == 0)
        {
            if (entity is not null)
            {
                _db.RuleNotes.Remove(entity);
                await _db.SaveChangesAsync(ct);
            }

            await _logs.WriteAsync("修改规则备注", relative, success: true, ct: ct);
            return ToDto(_store.GetMeta(fullPath), project.Name, check.Name, note: null);
        }

        if (entity is null)
        {
            entity = new RuleNote { RelativePath = relative, Note = text, UpdatedAt = DateTime.Now };
            _db.RuleNotes.Add(entity);
        }
        else
        {
            entity.Note = text;
            entity.UpdatedAt = DateTime.Now;
        }

        await _db.SaveChangesAsync(ct);
        await _logs.WriteAsync("修改规则备注", relative, success: true, ct: ct);

        return ToDto(_store.GetMeta(fullPath), project.Name, check.Name, new RuleNoteView(text, entity.UpdatedAt));
    }

    // ── 复制到其他文件夹 ────────────────────────────────────────────────

    /// <summary>
    /// 把若干条规则复制到另一个项目/检项，备注一并带过去。
    ///
    /// 批量操作**允许部分成功**：目标已存在同名的跳过并回报，
    /// 不做静默覆盖（覆盖规则可能悄悄改变别人的口径），也不因一条冲突就整体失败。
    /// </summary>
    public async Task<CopyResultDto> CopyAsync(CopyRulesRequest request, CancellationToken ct = default)
    {
        if (request.FileNames is null || request.FileNames.Count == 0)
        {
            throw new BadRequestException("没有选择要复制的规则。");
        }

        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);
        var (targetProject, targetCheck) = await _catalog.ResolveAsync(
            request.TargetProject, request.TargetCheck, ct);

        if (project.Id == targetProject.Id && check.Id == targetCheck.Id)
        {
            throw new BadRequestException("目标文件夹与源文件夹相同，无需复制。");
        }

        // 规则复制要求两端都是 RuleManage：读源规则口径 + 往目标写规则都算规则管理，
        // 只用 Write 会让「能改文档的人」悄悄改掉别人的提取口径。
        var ruleAccess = await CurrentAccessAsync(ct);
        ruleAccess.Require(check.Id, AccessLevel.RuleManage, "复制提取规则", $"{project.Name}/{check.Name}");
        ruleAccess.Require(targetCheck.Id, AccessLevel.RuleManage, "复制提取规则到", $"{targetProject.Name}/{targetCheck.Name}");

        var sourceDir = _layout.RuleDirectory(project.Name, check.Name);
        var targetDir = _layout.RuleDirectory(targetProject.Name, targetCheck.Name);

        var sourceNotes = await LoadNotesAsync(project.Name, check.Name, ct);

        var copied = new List<string>();
        var skipped = new List<string>();

        _store.CreateDirectory(targetDir);

        foreach (var rawName in request.FileNames)
        {
            var safeName = NameValidator.ValidateFileName(rawName);
            var sourcePath = PathGuard.CombineUnderRoot(sourceDir, safeName);

            if (!_store.FileExists(sourcePath))
            {
                throw new NotFoundException($"规则不存在：{safeName}");
            }

            var targetPath = PathGuard.CombineUnderRoot(targetDir, safeName);
            if (_store.FileExists(targetPath))
            {
                skipped.Add(safeName);
                continue;
            }

            _store.CopyFile(sourcePath, targetPath, overwrite: false);

            // 备注跟着规则一起走，否则复制过去的规则会“丢掉说明”
            var sourceRelative = _layout.RuleRelativePath(project.Name, check.Name, safeName);
            if (sourceNotes.TryGetValue(sourceRelative, out var note) && note.Note.Length > 0)
            {
                var targetRelative = _layout.RuleRelativePath(targetProject.Name, targetCheck.Name, safeName);
                var existing = await _db.RuleNotes.FirstOrDefaultAsync(n => n.RelativePath == targetRelative, ct);

                if (existing is null)
                {
                    _db.RuleNotes.Add(new RuleNote
                    {
                        RelativePath = targetRelative,
                        Note = note.Note,
                        UpdatedAt = DateTime.Now,
                    });
                }
                else
                {
                    existing.Note = note.Note;
                    existing.UpdatedAt = DateTime.Now;
                }
            }

            copied.Add(safeName);
        }

        if (copied.Count > 0 && _db.ChangeTracker.HasChanges())
        {
            await _db.SaveChangesAsync(ct);
        }

        await _logs.WriteAsync(
            "复制提取规则",
            $"{project.Name}/{check.Name} → {targetProject.Name}/{targetCheck.Name}（成功 {copied.Count}，跳过 {skipped.Count}）",
            success: copied.Count > 0,
            ct: ct);

        return new CopyResultDto(copied, skipped, targetProject.Name, targetCheck.Name);
    }

    // ── 辅助 ────────────────────────────────────────────────────────────

    /// <summary>
    /// 取出某文件夹下所有规则的备注。
    /// 用「项目\检项\」前缀匹配：项目/检项编码不允许分隔符，因此前缀不会误伤别的文件夹。
    /// </summary>
    private async Task<Dictionary<string, RuleNoteView>> LoadNotesAsync(
        string projectName, string checkName, CancellationToken ct)
    {
        var prefix = $"{projectName}\\{checkName}\\";

        var rows = await _db.RuleNotes.AsNoTracking()
            .Where(n => n.RelativePath.StartsWith(prefix))
            .ToListAsync(ct);

        var result = new Dictionary<string, RuleNoteView>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            result[row.RelativePath] = new RuleNoteView(row.Note, row.UpdatedAt);
        }

        return result;
    }

    private bool IsAllowedExtension(string extension) =>
        _upload.RuleExtensions.Any(
            e => string.Equals(NormalizeExtension(e), NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;

    private RuleDto ToDto(FileMeta meta, string project, string check, RuleNoteView? note)
    {
        var relative = _layout.RuleRelativePath(project, check, meta.FileName);

        return new RuleDto(
            meta.FileName,
            meta.Extension,
            meta.Size,
            meta.ModifiedAt,
            meta.ModifiedAt,
            project,
            check,
            relative,
            ClientPathBuilder.Build(_storage.ClientRulesRoot, _storage.RulesRoot, relative),
            note?.Note ?? string.Empty,
            note?.UpdatedAt);
    }

    private sealed record RuleNoteView(string Note, DateTime UpdatedAt);
}
