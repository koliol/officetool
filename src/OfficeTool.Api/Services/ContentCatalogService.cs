using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;

namespace OfficeTool.Api.Services;

/// <summary>
/// 模板与文档的目录服务（设计文档 §5.3、§5.4、§5.5、§6.2）。
/// 文件系统为准、数据库为索引。
/// </summary>
public sealed class ContentCatalogService(
    AppDbContext db,
    ProjectCatalogService catalog,
    PathLayout layout,
    IFileStore store,
    DocumentNamingService naming,
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
    private readonly DocumentNamingService _naming = naming;
    private readonly StorageOptions _storage = storageOptions;
    private readonly UploadOptions _upload = uploadOptions;
    private readonly OperationLogService _logs = logs;
    private readonly TrashService _trash = trash;
    private readonly RequestContext _request = request;
    private readonly IAccessControlService _access = access;

    /// <summary>
    /// 当前请求的权限视图。有 10 分钟缓存（见 <c>Auth:AclCacheMinutes</c>），
    /// 所以可以在每个方法里放心调用，不必担心重复查库。
    /// </summary>
    private Task<UserAccess> CurrentAccessAsync(CancellationToken ct) => _access.ResolveAccessAsync(_request, ct);

    // ── 模板 ────────────────────────────────────────────────────────────

    public async Task<PagedResult<TemplateDto>> ListTemplatesAsync(FileQuery query, CancellationToken ct = default)
    {
        var q = query.Normalized();
        var access = await CurrentAccessAsync(ct);

        var filtered =
            from t in _db.Templates.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on t.ProjectId equals p.Id
            join c in _db.Checks.AsNoTracking() on t.CheckId equals c.Id
            select new { t, Project = p.Name, Check = c.Name };

        // 可见性过滤必须下推到 SQL：AllowedCheckIds 是预先展开好的 id 集合，
        // 翻译成 IN 子句。若把它拉到内存里过滤，分页的 total 会算错。
        // NeedsCheckFilter 为 false（超管 / 鉴权关闭）时完全不加条件，避免全表扫描被误过滤。
        if (access.NeedsCheckFilter)
        {
            var allowed = access.AllowedCheckIds.ToList();
            filtered = filtered.Where(x => allowed.Contains(x.t.CheckId));
        }

        if (!string.IsNullOrWhiteSpace(q.Project))
        {
            var project = q.Project.ToLower();
            filtered = filtered.Where(x => x.Project.ToLower() == project);
        }

        if (!string.IsNullOrWhiteSpace(q.Check))
        {
            var check = q.Check.ToLower();
            filtered = filtered.Where(x => x.Check.ToLower() == check);
        }

        if (!string.IsNullOrWhiteSpace(q.Extension))
        {
            var wanted = (q.Extension.StartsWith('.') ? q.Extension : "." + q.Extension).ToLower();
            filtered = filtered.Where(x => x.t.Extension.ToLower() == wanted);
        }

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var kw = q.Keyword.ToLower();
            filtered = filtered.Where(x => x.t.FileName.ToLower().Contains(kw));
        }

        if (q.From is { } from)
        {
            filtered = filtered.Where(x => x.t.CreatedAt >= from);
        }

        if (q.To is { } to)
        {
            filtered = filtered.Where(x => x.t.CreatedAt <= to);
        }

        var total = await filtered.CountAsync(ct);

        var ordered = q.SortBy switch
        {
            "name" when q.SortDir == "asc" => filtered.OrderBy(x => x.t.FileName),
            "name" => filtered.OrderByDescending(x => x.t.FileName),
            "size" when q.SortDir == "asc" => filtered.OrderBy(x => x.t.Size),
            "size" => filtered.OrderByDescending(x => x.t.Size),
            "created" when q.SortDir == "asc" => filtered.OrderBy(x => x.t.CreatedAt),
            _ => filtered.OrderByDescending(x => x.t.CreatedAt),
        };

        var rows = await ordered
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => ToTemplateDto(x.t, x.Project, x.Check)).ToList();
        return new PagedResult<TemplateDto>(items, total, q.Page, q.PageSize);
    }

    public async Task<TemplateDto> UploadTemplateAsync(
        string projectName,
        string checkName,
        string fileName,
        Stream content,
        long declaredSize,
        CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "上传模板", $"{project.Name}/{check.Name}");

        if (declaredSize > _upload.MaxSizeBytes)
        {
            throw new BadRequestException($"文件超过大小上限 {_upload.MaxSizeMB}MB。");
        }

        var safeName = NameValidator.ValidateFileName(fileName);
        var extension = NameValidator.EnsureAllowedExtension(
            Path.GetExtension(safeName), _upload.AllowedExtensions);

        var directory = _layout.TemplateDirectory(project.Name, check.Name);
        var fullPath = PathGuard.CombineUnderRoot(directory, safeName);

        if (_store.FileExists(fullPath))
        {
            throw new ConflictException($"同目录下已存在同名模板：{safeName}，请先重命名或删除。");
        }

        try
        {
            _store.CreateDirectory(directory);

            // 先写占位再灌内容，避免半截文件被当成有效模板
            using (var target = _store.CreateNew(fullPath))
            {
                await content.CopyToAsync(target, ct);
            }

            var meta = _store.GetMeta(fullPath);

            if (meta.Size > _upload.MaxSizeBytes)
            {
                _store.DeleteFile(fullPath);
                throw new BadRequestException($"文件超过大小上限 {_upload.MaxSizeMB}MB。");
            }

            var entity = new Template
            {
                ProjectId = project.Id,
                CheckId = check.Id,
                FileName = safeName,
                RelativePath = _layout.TemplateRelativePath(project.Name, check.Name, safeName),
                Extension = extension,
                Size = meta.Size,
                ModifiedAt = meta.ModifiedAt,
                CreatedAt = DateTime.Now,
            };

            _db.Templates.Add(entity);
            await _db.SaveChangesAsync(ct);

            await _logs.WriteAsync("上传模板", entity.RelativePath, success: true, ct: ct);
            return ToTemplateDto(entity, project.Name, check.Name);
        }
        catch (Exception ex) when (ex is not ConflictException && ex is not BadRequestException)
        {
            await _logs.WriteFailureAsync("上传模板", $"{projectName}/{checkName}/{fileName}", ex, ct);
            throw;
        }
    }

    /// <summary>
    /// 复制模板，支持指定目标项目/检项。
    ///
    /// 目标只接受**编码**（TargetProject/TargetCheck），路径一律由服务端按配置拼装 ——
    /// 前端无法构造出越界路径。两者留空时是同目录复制，沿用旧行为。
    ///
    /// 两种命名策略刻意不同：
    /// <list type="bullet">
    ///   <item>同目录 —— 必然重名，所以<strong>总是</strong>追加「_副本」</item>
    ///   <item>跨目录 —— 「把这份模板放到另一个文件夹」保留原名更符合直觉，
    ///         因此<strong>沿用原名</strong>，目标已有同名才追加「_副本」</item>
    /// </list>
    /// 显式指定了新文件名又撞名 → 直接 409，不做静默改名（用户表达的是明确意图）。
    /// </summary>
    public async Task<TemplateDto> CopyTemplateAsync(CopyTemplateRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);

        Project targetProject;
        CheckItem targetCheck;

        if (string.IsNullOrWhiteSpace(request.TargetProject))
        {
            targetProject = project;
            targetCheck = check;
        }
        else
        {
            // 只给了 TargetProject 没给 TargetCheck 时，沿用源的检项编码：
            // 「复制到另一个项目的同名检项下」是常见操作，不该为此报错。
            (targetProject, targetCheck) = await _catalog.ResolveAsync(
                request.TargetProject, request.TargetCheck ?? request.Check, ct);
        }

        var crossFolder = targetProject.Id != project.Id || targetCheck.Id != check.Id;

        // 读「源」要 Read，写「目标」要 Write —— 跨目录复制时这是两个不同的检项，
        // 必须分别判。只判源是最典型的越权：用户可以借此把模板塞进自己只有只读权限的目录。
        var access = await CurrentAccessAsync(ct);
        access.RequireRead(check.Id, $"{project.Name}/{check.Name}/{request.SourceFileName}");
        access.Require(targetCheck.Id, AccessLevel.Write, "复制模板到", $"{targetProject.Name}/{targetCheck.Name}");

        var sourceName = NameValidator.ValidateFileName(request.SourceFileName);
        var sourceDirectory = _layout.TemplateDirectory(project.Name, check.Name);
        var sourcePath = PathGuard.CombineUnderRoot(sourceDirectory, sourceName);

        if (!_store.FileExists(sourcePath))
        {
            throw new NotFoundException($"模板文件不存在：{sourceName}");
        }

        var targetDirectory = _layout.TemplateDirectory(targetProject.Name, targetCheck.Name);

        if (crossFolder)
        {
            // 目标目录可能尚未创建（项目/检项刚建、还没传过模板）
            _store.CreateDirectory(targetDirectory);
        }

        string targetName;

        if (string.IsNullOrWhiteSpace(request.NewFileName))
        {
            targetName = crossFolder
                ? BuildCrossFolderName(sourceName, targetDirectory)
                : BuildCopyName(sourceName, targetDirectory);
        }
        else
        {
            targetName = NameValidator.ValidateFileName(request.NewFileName);
            NameValidator.EnsureAllowedExtension(Path.GetExtension(targetName), _upload.AllowedExtensions);

            if (_store.FileExists(PathGuard.CombineUnderRoot(targetDirectory, targetName)))
            {
                throw new ConflictException($"目标文件夹已存在同名模板：{targetName}");
            }
        }

        var targetPath = PathGuard.CombineUnderRoot(targetDirectory, targetName);

        _store.CopyFile(sourcePath, targetPath, overwrite: false);
        var meta = _store.GetMeta(targetPath);

        var entity = new Template
        {
            ProjectId = targetProject.Id,
            CheckId = targetCheck.Id,
            FileName = targetName,
            RelativePath = _layout.TemplateRelativePath(targetProject.Name, targetCheck.Name, targetName),
            Extension = Path.GetExtension(targetName),
            Size = meta.Size,
            ModifiedAt = meta.ModifiedAt,
            CreatedAt = DateTime.Now,
        };

        _db.Templates.Add(entity);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("复制模板", entity.RelativePath, success: true, ct: ct);
        return ToTemplateDto(entity, targetProject.Name, targetCheck.Name);
    }

    public async Task<TemplateDto> RenameTemplateAsync(RenameRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);

        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "重命名模板", $"{project.Name}/{check.Name}");

        var oldName = NameValidator.ValidateFileName(request.FileName);
        var newName = NameValidator.ValidateFileName(request.NewFileName);

        var entity = await _db.Templates.FirstOrDefaultAsync(
            t => t.ProjectId == project.Id && t.CheckId == check.Id && t.FileName == oldName, ct)
            ?? throw new NotFoundException($"模板不存在：{oldName}");

        var directory = _layout.TemplateDirectory(project.Name, check.Name);
        var oldPath = PathGuard.CombineUnderRoot(directory, oldName);
        var newPath = PathGuard.CombineUnderRoot(directory, newName);

        if (_store.FileExists(newPath))
        {
            throw new ConflictException($"目标文件名已存在：{newName}");
        }

        var extension = NameValidator.EnsureAllowedExtension(
            Path.GetExtension(newName), _upload.AllowedExtensions);

        _store.MoveFile(oldPath, newPath, overwrite: false);

        var meta = _store.GetMeta(newPath);
        entity.FileName = newName;
        entity.RelativePath = _layout.TemplateRelativePath(project.Name, check.Name, newName);
        entity.Extension = extension;
        entity.Size = meta.Size;
        entity.ModifiedAt = meta.ModifiedAt;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("重命名模板", entity.RelativePath, success: true, ct: ct);
        return ToTemplateDto(entity, project.Name, check.Name);
    }

    public async Task DeleteTemplateAsync(string projectName, string checkName, string fileName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Manage, "删除模板", $"{project.Name}/{check.Name}");

        var safeName = NameValidator.ValidateFileName(fileName);

        var entity = await _db.Templates.FirstOrDefaultAsync(
            t => t.ProjectId == project.Id && t.CheckId == check.Id && t.FileName == safeName, ct)
            ?? throw new NotFoundException($"模板不存在：{safeName}");

        var fullPath = PathGuard.CombineUnderRoot(_layout.TemplateDirectory(projectName, checkName), safeName);

        await _trash.MoveToTrashAsync(
            TrashKind.Template,
            project.Name,
            check.Name,
            safeName,
            fullPath,
            entity.RelativePath,
            ct);

        _db.Templates.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除模板", entity.RelativePath, success: true, ct: ct);
    }

    // ── 文档 ────────────────────────────────────────────────────────────

    public async Task<PagedResult<DocumentDto>> ListDocumentsAsync(FileQuery query, CancellationToken ct = default)
    {
        var q = query.Normalized();
        var access = await CurrentAccessAsync(ct);

        var filtered =
            from d in _db.Documents.AsNoTracking()
            join p in _db.Projects.AsNoTracking() on d.ProjectId equals p.Id
            join c in _db.Checks.AsNoTracking() on d.CheckId equals c.Id
            select new { d, Project = p.Name, Check = c.Name };

        // 同 ListTemplatesAsync：可见性过滤下推到 SQL，保证分页 total 正确。
        if (access.NeedsCheckFilter)
        {
            var allowed = access.AllowedCheckIds.ToList();
            filtered = filtered.Where(x => allowed.Contains(x.d.CheckId));
        }

        if (!string.IsNullOrWhiteSpace(q.Project))
        {
            var project = q.Project.ToLower();
            filtered = filtered.Where(x => x.Project.ToLower() == project);
        }

        if (!string.IsNullOrWhiteSpace(q.Check))
        {
            var check = q.Check.ToLower();
            filtered = filtered.Where(x => x.Check.ToLower() == check);
        }

        if (!string.IsNullOrWhiteSpace(q.Extension))
        {
            var wanted = (q.Extension.StartsWith('.') ? q.Extension : "." + q.Extension).ToLower();
            filtered = filtered.Where(x => x.d.Extension.ToLower() == wanted);
        }

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var kw = q.Keyword.ToLower();
            filtered = filtered.Where(x => x.d.FileName.ToLower().Contains(kw));
        }

        if (q.From is { } from)
        {
            filtered = filtered.Where(x => x.d.CreatedAt >= from);
        }

        if (q.To is { } to)
        {
            filtered = filtered.Where(x => x.d.CreatedAt <= to);
        }

        var total = await filtered.CountAsync(ct);

        var ordered = q.SortBy switch
        {
            "name" when q.SortDir == "asc" => filtered.OrderBy(x => x.d.FileName),
            "name" => filtered.OrderByDescending(x => x.d.FileName),
            "size" when q.SortDir == "asc" => filtered.OrderBy(x => x.d.Size),
            "size" => filtered.OrderByDescending(x => x.d.Size),
            "created" when q.SortDir == "asc" => filtered.OrderBy(x => x.d.CreatedAt),
            _ => filtered.OrderByDescending(x => x.d.CreatedAt),
        };

        var rows = await ordered
            .Skip((q.Page - 1) * q.PageSize)
            .Take(q.PageSize)
            .ToListAsync(ct);

        var items = rows.Select(x => ToDocumentDto(x.d, x.Project, x.Check)).ToList();
        return new PagedResult<DocumentDto>(items, total, q.Page, q.PageSize);
    }

    public async Task<DocumentDto> CreateDocumentAsync(CreateDocumentRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "新建文档", $"{project.Name}/{check.Name}");

        var templateName = NameValidator.ValidateFileName(request.TemplateFileName);

        var template = await _db.Templates.FirstOrDefaultAsync(
            t => t.ProjectId == project.Id && t.CheckId == check.Id && t.FileName == templateName, ct)
            ?? throw new NotFoundException($"模板不存在：{templateName}");

        var templatePath = PathGuard.CombineUnderRoot(
            _layout.TemplateDirectory(project.Name, check.Name), templateName);

        if (!_store.FileExists(templatePath))
        {
            throw new NotFoundException($"模板文件在存储中缺失：{template.RelativePath}");
        }

        var dataDirectory = _layout.DataDirectory(project.Name, check.Name);

        try
        {
            using var source = _store.OpenRead(templatePath);

            // 命名 + 并发去重全权交给 DocumentNamingService（§4.4、§7.4）
            var allocated = _naming.CreateWithUniqueName(
                _store, dataDirectory, template.Extension, DateTime.Now, target => source.CopyTo(target));

            var entity = new Document
            {
                ProjectId = project.Id,
                CheckId = check.Id,
                FileName = allocated.FileName,
                RelativePath = _layout.DataRelativePath(project.Name, check.Name, allocated.FileName),
                SourceTemplatePath = template.RelativePath,
                Extension = template.Extension,
                Size = allocated.Size,
                CreatedAt = DateTime.Now,
            };

            _db.Documents.Add(entity);
            await _db.SaveChangesAsync(ct);

            await _logs.WriteAsync("新建文档", entity.RelativePath, success: true, ct: ct);
            return ToDocumentDto(entity, project.Name, check.Name);
        }
        catch (DayNumberExhaustedException ex)
        {
            await _logs.WriteFailureAsync("新建文档", $"{project.Name}/{check.Name}", ex, ct);
            throw new ConflictException(ex.Message);
        }
        catch (Exception ex)
        {
            await _logs.WriteFailureAsync("新建文档", $"{project.Name}/{check.Name}", ex, ct);
            throw;
        }
    }

    public async Task<DocumentDto> RenameDocumentAsync(RenameRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);

        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "重命名文档", $"{project.Name}/{check.Name}");

        var oldName = NameValidator.ValidateFileName(request.FileName);
        var newName = NameValidator.ValidateFileName(request.NewFileName);

        var entity = await _db.Documents.FirstOrDefaultAsync(
            d => d.ProjectId == project.Id && d.CheckId == check.Id && d.FileName == oldName, ct)
            ?? throw new NotFoundException($"文档不存在：{oldName}");

        var directory = _layout.DataDirectory(project.Name, check.Name);
        var oldPath = PathGuard.CombineUnderRoot(directory, oldName);
        var newPath = PathGuard.CombineUnderRoot(directory, newName);

        if (_store.FileExists(newPath))
        {
            throw new ConflictException($"目标文件名已存在：{newName}");
        }

        _store.MoveFile(oldPath, newPath, overwrite: false);

        var meta = _store.GetMeta(newPath);
        entity.FileName = newName;
        entity.RelativePath = _layout.DataRelativePath(project.Name, check.Name, newName);
        entity.Extension = Path.GetExtension(newName);
        entity.Size = meta.Size;

        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("重命名文档", entity.RelativePath, success: true, ct: ct);
        return ToDocumentDto(entity, project.Name, check.Name);
    }

    public async Task DeleteDocumentAsync(string projectName, string checkName, string fileName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Manage, "删除文档", $"{project.Name}/{check.Name}");

        var safeName = NameValidator.ValidateFileName(fileName);

        var entity = await _db.Documents.FirstOrDefaultAsync(
            d => d.ProjectId == project.Id && d.CheckId == check.Id && d.FileName == safeName, ct)
            ?? throw new NotFoundException($"文档不存在：{safeName}");

        var fullPath = PathGuard.CombineUnderRoot(_layout.DataDirectory(projectName, checkName), safeName);

        await _trash.MoveToTrashAsync(
            TrashKind.Document,
            project.Name,
            check.Name,
            safeName,
            fullPath,
            entity.RelativePath,
            ct);

        _db.Documents.Remove(entity);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除文档", entity.RelativePath, success: true, ct: ct);
    }

    // ── 复制到其他文件夹 ────────────────────────────────────────────────

    /// <summary>
    /// 把文档复制到另一个项目/检项。
    ///
    /// 目标只接受**编码**（targetProject/targetCheck），路径一律由服务端按配置拼装 ——
    /// 前端无法构造出越界路径。
    ///
    /// 命名策略：留空新文件名时**沿用源文件名**；目标已有同名才加「_副本」序号。
    /// 这里刻意与「复制模板」不同：模板是在同一目录内复制、必然重名，所以总是改名；
    /// 而跨目录复制，「把这份文档放到另一个文件夹」保留原名才符合直觉。
    /// 显式指定了新文件名又撞名时直接 409，不做静默改名（用户表达的是明确意图）。
    /// </summary>
    public async Task<CopyResultDto> CopyDocumentAsync(
        CopyDocumentRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);
        var (targetProject, targetCheck) = await _catalog.ResolveAsync(
            request.TargetProject, request.TargetCheck, ct);

        if (project.Id == targetProject.Id && check.Id == targetCheck.Id)
        {
            throw new BadRequestException("目标文件夹与源文件夹相同，无需复制。");
        }

        // 源要 Read、目标要 Write：跨目录复制天然跨越两个检项，
        // 只判其一就等于放行「读到不该读的」或「写进不该写的」。
        var docAccess = await CurrentAccessAsync(ct);
        docAccess.RequireRead(check.Id, $"{project.Name}/{check.Name}/{request.FileName}");
        docAccess.Require(targetCheck.Id, AccessLevel.Write, "复制文档到", $"{targetProject.Name}/{targetCheck.Name}");

        var sourceName = NameValidator.ValidateFileName(request.FileName);

        var source = await _db.Documents.FirstOrDefaultAsync(
            d => d.ProjectId == project.Id && d.CheckId == check.Id && d.FileName == sourceName, ct)
            ?? throw new NotFoundException($"文档不存在：{sourceName}");

        var sourcePath = PathGuard.CombineUnderRoot(
            _layout.DataDirectory(project.Name, check.Name), sourceName);

        if (!_store.FileExists(sourcePath))
        {
            throw new NotFoundException($"文档文件在存储中缺失：{source.RelativePath}");
        }

        var targetDirectory = _layout.DataDirectory(targetProject.Name, targetCheck.Name);
        _store.CreateDirectory(targetDirectory);

        string targetName;
        if (string.IsNullOrWhiteSpace(request.NewFileName))
        {
            targetName = BuildCrossFolderName(sourceName, targetDirectory);
        }
        else
        {
            targetName = NameValidator.ValidateFileName(request.NewFileName);
            NameValidator.EnsureAllowedExtension(Path.GetExtension(targetName), _upload.AllowedExtensions);

            if (_store.FileExists(PathGuard.CombineUnderRoot(targetDirectory, targetName)))
            {
                throw new ConflictException($"目标文件夹已存在同名文档：{targetName}");
            }
        }

        var targetPath = PathGuard.CombineUnderRoot(targetDirectory, targetName);

        try
        {
            _store.CopyFile(sourcePath, targetPath, overwrite: false);
            var meta = _store.GetMeta(targetPath);

            var entity = new Document
            {
                ProjectId = targetProject.Id,
                CheckId = targetCheck.Id,
                FileName = targetName,
                RelativePath = _layout.DataRelativePath(targetProject.Name, targetCheck.Name, targetName),
                // 复制件沿用源文档的来源模板：「由哪个模板派生」这条谱系仍然成立；
                // 「由哪份文档复制而来」记在操作日志里，不污染 SourceTemplatePath 的语义。
                SourceTemplatePath = source.SourceTemplatePath,
                Extension = Path.GetExtension(targetName),
                Size = meta.Size,
                CreatedAt = DateTime.Now,
            };

            _db.Documents.Add(entity);
            await _db.SaveChangesAsync(ct);

            await _logs.WriteAsync(
                "复制文档", $"{source.RelativePath} → {entity.RelativePath}", success: true, ct: ct);

            return new CopyResultDto([targetName], [], targetProject.Name, targetCheck.Name);
        }
        catch (Exception ex) when (ex is not ConflictException && ex is not BadRequestException)
        {
            await _logs.WriteFailureAsync(
                "复制文档", $"{project.Name}/{check.Name}/{sourceName} → {targetProject.Name}/{targetCheck.Name}", ex, ct);
            throw;
        }
    }

    /// <summary>跨文件夹复制的目标名：优先沿用原名，重名才加「_副本」序号。</summary>
    private string BuildCrossFolderName(string sourceName, string targetDirectory)
    {
        if (!_store.FileExists(Path.Combine(targetDirectory, sourceName)))
        {
            return sourceName;
        }

        var baseName = Path.GetFileNameWithoutExtension(sourceName);
        var extension = Path.GetExtension(sourceName);

        var candidate = $"{baseName}_副本{extension}";
        var index = 2;

        while (_store.FileExists(Path.Combine(targetDirectory, candidate)))
        {
            candidate = $"{baseName}_副本{index}{extension}";
            index++;

            if (index > 999)
            {
                throw new ConflictException($"无法为 {sourceName} 生成副本名，候选名已耗尽。");
            }
        }

        return candidate;
    }

    // ── 元数据同步（§6.2 文件系统为准） ─────────────────────────────────

    public async Task<SyncResult> SyncMetadataAsync(CancellationToken ct = default)
    {
        // 限定系统管理员。同步要扫描整个存储根，返回的 messages 里会带上
        // 所有项目的路径名 —— 给普通放行等于开放一次全量清单枚举。
        var syncAccess = await CurrentAccessAsync(ct);
        if (!syncAccess.IsSystemAdmin)
        {
            throw new AccessDeniedException("元数据同步是系统级维护操作，仅系统管理员可执行。");
        }

        var messages = new List<string>();

        var templatesAdded = await SyncOneAsync(
            _layout.TemplatesRoot,
            "模板",
            diskEntries: ScanRoot(_layout.TemplatesRoot, "*.docx", "*.xlsx", "*.pptx", "*.docm", "*.xlsm", "*.pptm"),
            dbEntries: await _db.Templates.ToListAsync(ct),
            dbRelative: t => t.RelativePath,
            addFromDisk: (relative, meta) =>
            {
                var ids = ResolveIdsFromRelativePath(relative);
                if (ids is null)
                {
                    messages.Add($"跳过（无法归属项目/检项）：{relative}");
                    return null;
                }

                return new Template
                {
                    ProjectId = ids.Value.ProjectId,
                    CheckId = ids.Value.CheckId,
                    FileName = meta.FileName,
                    RelativePath = relative,
                    Extension = meta.Extension,
                    Size = meta.Size,
                    ModifiedAt = meta.ModifiedAt,
                    CreatedAt = DateTime.Now,
                };
            },
            ct: ct);

        var documentsAdded = await SyncOneAsync(
            _layout.DataRoot,
            "文档",
            diskEntries: ScanRoot(_layout.DataRoot, "*.docx", "*.xlsx", "*.pptx", "*.docm", "*.xlsm", "*.pptm"),
            dbEntries: await _db.Documents.ToListAsync(ct),
            dbRelative: d => d.RelativePath,
            addFromDisk: (relative, meta) =>
            {
                var ids = ResolveIdsFromRelativePath(relative);
                if (ids is null)
                {
                    messages.Add($"跳过（无法归属项目/检项）：{relative}");
                    return null;
                }

                return new Document
                {
                    ProjectId = ids.Value.ProjectId,
                    CheckId = ids.Value.CheckId,
                    FileName = meta.FileName,
                    RelativePath = relative,
                    SourceTemplatePath = string.Empty,
                    Extension = meta.Extension,
                    Size = meta.Size,
                    CreatedAt = DateTime.Now,
                };
            },
            ct: ct);

        await _logs.WriteAsync("同步元数据", $"{_layout.TemplatesRoot} / {_layout.DataRoot}", success: true, ct: ct);

        return new SyncResult(
            templatesAdded.Added, templatesAdded.Removed,
            documentsAdded.Added, documentsAdded.Removed,
            messages);
    }

    private async Task<(int Added, int Removed)> SyncOneAsync<TEntity>(
        string root,
        string label,
        IReadOnlyList<(string Relative, FileMeta Meta)> diskEntries,
        List<TEntity> dbEntries,
        Func<TEntity, string> dbRelative,
        Func<string, FileMeta, TEntity?> addFromDisk,
        CancellationToken ct)
        where TEntity : class
    {
        var diskSet = new HashSet<string>(diskEntries.Select(e => e.Relative), StringComparer.OrdinalIgnoreCase);
        var dbSet = new HashSet<string>(dbEntries.Select(dbRelative), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var (relative, meta) in diskEntries)
        {
            if (dbSet.Contains(relative))
            {
                continue;
            }

            var entity = addFromDisk(relative, meta);
            if (entity is not null)
            {
                _db.Add(entity);
                added++;
            }
        }

        var removed = 0;
        foreach (var entity in dbEntries.Where(e => !diskSet.Contains(dbRelative(e))))
        {
            _db.Remove(entity);
            removed++;
        }

        if (added > 0 || removed > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        _ = label;
        return (added, removed);
    }

    private IReadOnlyList<(string Relative, FileMeta Meta)> ScanRoot(string root, params string[] patterns)
    {
        if (!_store.DirectoryExists(root))
        {
            return [];
        }

        var results = new List<(string, FileMeta)>();

        foreach (var pattern in patterns)
        {
            // 逐层下钻：项目 / 检项 两级
            foreach (var project in _store.ListDirectories(root))
            {
                var projectDir = Path.Combine(root, project);

                foreach (var check in _store.ListDirectories(projectDir))
                {
                    var checkDir = Path.Combine(projectDir, check);

                    foreach (var file in _store.ListFiles(checkDir).Where(f => MatchesPattern(f.FileName, pattern)))
                    {
                        var relative = PathGuard.ToRelative(root, file.FullPath);
                        results.Add((relative, file));
                    }
                }
            }
        }

        return results
            .GroupBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static bool MatchesPattern(string fileName, string pattern) =>
        System.IO.Path.GetFileName(fileName)
            .EndsWith(pattern.TrimStart('*'), StringComparison.OrdinalIgnoreCase);

    private (int ProjectId, int CheckId)? ResolveIdsFromRelativePath(string relative)
    {
        var parts = relative.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        var project = _db.Projects.AsNoTracking().FirstOrDefault(p => p.Name == parts[0]);
        if (project is null)
        {
            return null;
        }

        var check = _db.Checks.AsNoTracking().FirstOrDefault(c => c.ProjectId == project.Id && c.Name == parts[1]);
        return check is null ? null : (project.Id, check.Id);
    }

    // ── 映射与辅助 ─────────────────────────────────────────────────────

    private TemplateDto ToTemplateDto(Template t, string project, string check) => new(
        t.Id, t.FileName, t.Extension, t.Size, t.ModifiedAt, t.CreatedAt, project, check,
        t.RelativePath,
        ClientPathBuilder.Build(_storage.ClientTemplatesRoot, _storage.TemplatesRoot, t.RelativePath));

    private DocumentDto ToDocumentDto(Document d, string project, string check) => new(
        d.Id, d.FileName, d.Extension, d.Size, d.CreatedAt, project, check,
        d.SourceTemplatePath, d.RelativePath,
        ClientPathBuilder.Build(_storage.ClientDataRoot, _storage.DataRoot, d.RelativePath));

    private string BuildCopyName(string sourceName, string directory)
    {
        var baseName = Path.GetFileNameWithoutExtension(sourceName);
        var extension = Path.GetExtension(sourceName);

        var candidate = $"{baseName}_副本{extension}";
        var index = 2;

        while (_store.FileExists(Path.Combine(directory, candidate)))
        {
            candidate = $"{baseName}_副本{index}{extension}";
            index++;

            if (index > 999)
            {
                throw new ConflictException($"无法为 {sourceName} 生成副本名，候选名已耗尽。");
            }
        }

        return candidate;
    }
}
