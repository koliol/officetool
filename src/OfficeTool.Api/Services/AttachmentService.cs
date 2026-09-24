using OfficeTool.Api.Contracts;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// 附件服务：模板之外的一类独立文件（「新建文档」后要提取内容进文档的原始材料）。
///
/// ── 为什么附件**不进数据库** ───────────────────────────────────────────
/// 模板/文档进库是为了要稳定 Id、记录「由哪个模板生成」等索引信息。
/// 附件只需要「列出 / 上传 / 删除」，这些全部可以由文件系统直接回答，因此：
///   1. 与既有「文件系统为准、数据库为索引」的原则一致；
///   2. 避免给已经在跑的 SQLite 加表 —— EF 的 EnsureCreated 不会给**已存在**的库
///      补建表，线上库升级会直接报「no such table」。少一张表就少一次升级风险。
/// 将来若要持久记录「某附件已提取到某文档」，再单独加表并配套迁移。
/// </summary>
public sealed class AttachmentService(
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

    /// <summary>列出某项目/检项下的全部附件（按修改时间倒序）。</summary>
    public async Task<IReadOnlyList<AttachmentDto>> ListAsync(
        string projectName, string checkName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).RequireRead(check.Id, $"{project.Name}/{check.Name}");

        var directory = _layout.AttachmentDirectory(project.Name, check.Name);

        if (!_store.DirectoryExists(directory))
        {
            return [];
        }

        return _store.ListFiles(directory)
            .Where(f => IsAllowedExtension(f.Extension))
            .OrderByDescending(f => f.ModifiedAt)
            .Select(f => ToDto(f, project.Name, check.Name))
            .ToList();
    }

    /// <summary>上传附件。与模板上传同样的落盘策略：先写内容再登记，避免半截文件被当成有效附件。</summary>
    public async Task<AttachmentDto> UploadAsync(
        string projectName,
        string checkName,
        string fileName,
        Stream content,
        long declaredSize,
        CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "上传附件", $"{project.Name}/{check.Name}");

        if (declaredSize > _upload.MaxSizeBytes)
        {
            throw new BadRequestException($"附件超过大小上限 {_upload.MaxSizeMB}MB。");
        }

        var safeName = NameValidator.ValidateFileName(fileName);

        // 只校验不取值：落库/展示用的扩展名一律以磁盘上的真实文件名为准（保留原始大小写）
        NameValidator.EnsureAllowedExtension(Path.GetExtension(safeName), _upload.AttachmentExtensions);

        var directory = _layout.AttachmentDirectory(project.Name, check.Name);
        var fullPath = PathGuard.CombineUnderRoot(directory, safeName);

        if (_store.FileExists(fullPath))
        {
            throw new ConflictException($"同目录下已存在同名附件：{safeName}，请先重命名或删除。");
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
                throw new BadRequestException($"附件超过大小上限 {_upload.MaxSizeMB}MB。");
            }

            await _logs.WriteAsync(
                "上传附件", _layout.AttachmentRelativePath(project.Name, check.Name, safeName), success: true, ct: ct);

            return ToDto(meta, project.Name, check.Name);
        }
        catch (Exception ex) when (ex is not ConflictException && ex is not BadRequestException)
        {
            await _logs.WriteFailureAsync("上传附件", $"{projectName}/{checkName}/{fileName}", ex, ct);
            throw;
        }
    }

    /// <summary>删除附件。</summary>
    public async Task DeleteAsync(
        string projectName, string checkName, string fileName, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(projectName, checkName, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Manage, "删除附件", $"{project.Name}/{check.Name}");

        var safeName = NameValidator.ValidateFileName(fileName);

        var fullPath = PathGuard.CombineUnderRoot(
            _layout.AttachmentDirectory(project.Name, check.Name), safeName);

        if (!_store.FileExists(fullPath))
        {
            throw new NotFoundException($"附件不存在：{safeName}");
        }

        var relative = _layout.AttachmentRelativePath(project.Name, check.Name, safeName);
        await _trash.MoveToTrashAsync(
            TrashKind.Attachment, project.Name, check.Name, safeName, fullPath, relative, ct);

        await _logs.WriteAsync("删除附件", relative, success: true, ct: ct);
    }

    /// <summary>
    /// 按规则提取附件内容并回填到目标文档。
    ///
    /// ⚠️ **当前为预留实现**：请求会走完整的校验（项目/检项、附件是否存在、
    /// 规则是否存在、目标文档是否存在），校验全部通过后抛出 501，
    /// 明确告诉调用方「契约已就绪、执行尚未实现」，而不是含糊的 500。
    /// 这样前端可以在开发阶段就把整条链路接通并验证参数传递。
    ///
    /// 「规则」指**当前文件夹下已上传的规则文件**（<c>ExtractionRules\{项目}\{检项}\</c>），
    /// 不再是配置里的常量列表。实现提取时的接入点就是这个规则文件的内容。
    /// </summary>
    public async Task ExtractAsync(ExtractRequest request, CancellationToken ct = default)
    {
        var (project, check) = await _catalog.ResolveAsync(request.Project, request.Check, ct);
        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Write, "提取附件", $"{project.Name}/{check.Name}");

        var attachmentName = NameValidator.ValidateFileName(request.AttachmentFileName);
        var attachmentPath = PathGuard.CombineUnderRoot(
            _layout.AttachmentDirectory(project.Name, check.Name), attachmentName);

        if (!_store.FileExists(attachmentPath))
        {
            throw new NotFoundException($"附件不存在：{attachmentName}");
        }

        if (string.IsNullOrWhiteSpace(request.RuleFileName))
        {
            throw new BadRequestException(
                "缺少提取规则：请先在「提取规则列表」里为该文件夹上传规则，再选择一条。");
        }

        var ruleName = NameValidator.ValidateFileName(request.RuleFileName);
        var rulePath = PathGuard.CombineUnderRoot(
            _layout.RuleDirectory(project.Name, check.Name), ruleName);

        if (!_store.FileExists(rulePath))
        {
            throw new NotFoundException(
                $"该文件夹下没有这条提取规则：{ruleName}。" +
                "可用规则见 GET /api/extraction-rules?project=&check=。");
        }

        if (string.IsNullOrWhiteSpace(request.TargetDocumentFileName))
        {
            throw new BadRequestException(
                "缺少目标文档：提取结果需要写入一个已生成的文档，请传入 targetDocumentFileName。");
        }

        var targetName = NameValidator.ValidateFileName(request.TargetDocumentFileName);
        var targetPath = PathGuard.CombineUnderRoot(
            _layout.DataDirectory(project.Name, check.Name), targetName);

        if (!_store.FileExists(targetPath))
        {
            throw new NotFoundException($"目标文档不存在：{targetName}");
        }

        var pending = new FeatureNotAvailableException(
            $"提取功能尚未实现（接口契约已预留）。本次请求已通过全部校验：" +
            $"附件={attachmentName}、规则={ruleName}、目标文档={targetName}。" +
            "待提取脚本编写完成并按该规则文件执行后即可启用。");

        await _logs.WriteFailureAsync(
            "提取附件", $"{project.Name}/{check.Name}/{attachmentName} → {targetName}", pending, ct);

        throw pending;
    }

    private bool IsAllowedExtension(string extension) =>
        _upload.AttachmentExtensions.Any(
            e => string.Equals(NormalizeExtension(e), NormalizeExtension(extension), StringComparison.OrdinalIgnoreCase));

    private static string NormalizeExtension(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;

    private AttachmentDto ToDto(FileMeta meta, string project, string check) => new(
        meta.FileName,
        meta.Extension,
        meta.Size,
        meta.ModifiedAt,
        project,
        check,
        _layout.AttachmentRelativePath(project, check, meta.FileName),
        ClientPathBuilder.Build(_storage.ClientAttachmentsRoot, _storage.AttachmentsRoot,
            _layout.AttachmentRelativePath(project, check, meta.FileName)));
}
