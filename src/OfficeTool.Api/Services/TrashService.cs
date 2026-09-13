using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// 回收站：删除时把文件移入 <c>_trash/{Kind}/{yyyyMMdd}/</c>，可恢复、可彻底删除。
/// 文件系统为准，本表只作索引；恢复时目标已存在则 409，绝不静默覆盖。
/// </summary>
public sealed class TrashService(
    AppDbContext db,
    PathLayout layout,
    IFileStore store,
    OperationLogService logs)
{
    /// <summary>把业务文件移入回收站并登记。调用方负责删除业务库中的活动记录。</summary>
    public async Task<TrashItemDto> MoveToTrashAsync(
        TrashKind kind,
        string project,
        string check,
        string fileName,
        string sourceFullPath,
        string originalRelativePath,
        CancellationToken ct = default)
    {
        var kindName = kind.ToString();
        var deletedAt = DateTime.Now;
        var trashFileName = PathLayout.BuildTrashFileName(originalRelativePath, fileName);
        var trashDir = layout.TrashKindDirectory(kindName, deletedAt);
        var trashFullPath = PathGuard.CombineUnderRoot(trashDir, trashFileName);

        long size = 0;
        if (store.FileExists(sourceFullPath))
        {
            size = store.GetMeta(sourceFullPath).Size;
            store.MoveFile(sourceFullPath, trashFullPath, overwrite: false);
        }

        var trashRelative = PathGuard.ToRelative(layout.TrashRoot, trashFullPath);

        var entity = new TrashItem
        {
            Kind = kindName,
            Project = project,
            Check = check,
            FileName = fileName,
            OriginalRelativePath = originalRelativePath,
            TrashRelativePath = trashRelative,
            Size = size,
            DeletedAt = deletedAt,
        };

        db.TrashItems.Add(entity);
        await db.SaveChangesAsync(ct);

        await logs.WriteAsync(
            "移入回收站",
            $"{kindName} {originalRelativePath} → {trashRelative}",
            success: true,
            ct: ct);

        return ToDto(entity);
    }

    public async Task<PagedResult<TrashItemDto>> ListAsync(
        string? kind, string? project, int page, int pageSize, CancellationToken ct = default)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize is < 1 or > 200 ? 50 : pageSize;

        var q = db.TrashItems.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(kind))
        {
            q = q.Where(t => t.Kind == kind);
        }

        if (!string.IsNullOrWhiteSpace(project))
        {
            q = q.Where(t => t.Project == project);
        }

        var total = await q.CountAsync(ct);
        var items = await q
            .OrderByDescending(t => t.DeletedAt)
            .ThenByDescending(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<TrashItemDto>(items.Select(ToDto).ToList(), total, page, pageSize);
    }

    public async Task<TrashItemDto> RestoreAsync(int id, CancellationToken ct = default)
    {
        var item = await db.TrashItems.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException($"回收站条目不存在：{id}");

        var trashPath = PathGuard.FromRelative(layout.TrashRoot, item.TrashRelativePath);
        if (!store.FileExists(trashPath))
        {
            throw new NotFoundException($"回收站中的文件已缺失：{item.TrashRelativePath}");
        }

        var (root, _) = ResolveKindRoot(item.Kind);
        var restorePath = PathGuard.FromRelative(root, item.OriginalRelativePath);

        if (store.FileExists(restorePath))
        {
            throw new ConflictException(
                $"原位置已存在同名文件，无法恢复：{item.OriginalRelativePath}。请先处理冲突文件。");
        }

        store.MoveFile(trashPath, restorePath, overwrite: false);

        // 模板/文档进库：删除时已从活动表移除，恢复必须重建索引记录。
        // 附件/规则本体不进库，只需移回文件（规则备注删除时已清，恢复后备注为空）。
        if (item.Kind is nameof(TrashKind.Template) or nameof(TrashKind.Document))
        {
            await RestoreCatalogAsync(item, restorePath, ct);
        }

        db.TrashItems.Remove(item);
        await db.SaveChangesAsync(ct);

        await logs.WriteAsync(
            "从回收站恢复",
            $"{item.Kind} {item.TrashRelativePath} → {item.OriginalRelativePath}",
            success: true,
            ct: ct);

        return ToDto(item);
    }

    public async Task PurgeAsync(int id, CancellationToken ct = default)
    {
        var item = await db.TrashItems.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new NotFoundException($"回收站条目不存在：{id}");

        var trashPath = PathGuard.FromRelative(layout.TrashRoot, item.TrashRelativePath);
        if (store.FileExists(trashPath))
        {
            store.DeleteFile(trashPath);
        }

        db.TrashItems.Remove(item);
        await db.SaveChangesAsync(ct);

        await logs.WriteAsync("彻底删除回收站条目", item.TrashRelativePath, success: true, ct: ct);
    }

    private (string Root, string Label) ResolveKindRoot(string kind) => kind switch
    {
        nameof(TrashKind.Template) => (layout.TemplatesRoot, "模板"),
        nameof(TrashKind.Document) => (layout.DataRoot, "文档"),
        nameof(TrashKind.Attachment) => (layout.AttachmentsRoot, "附件"),
        nameof(TrashKind.Rule) => (layout.RulesRoot, "提取规则"),
        _ => throw new BadRequestException($"未知回收站类别：{kind}"),
    };

    private async Task RestoreCatalogAsync(TrashItem item, string restoredFullPath, CancellationToken ct)
    {
        var project = await db.Projects.FirstOrDefaultAsync(p => p.Name == item.Project, ct)
            ?? throw new NotFoundException($"原项目已不存在，无法恢复库记录：{item.Project}");

        var check = await db.Checks.FirstOrDefaultAsync(
            c => c.ProjectId == project.Id && c.Name == item.Check, ct)
            ?? throw new NotFoundException($"原检项已不存在，无法恢复库记录：{item.Project}/{item.Check}");

        var meta = store.GetMeta(restoredFullPath);
        var extension = Path.GetExtension(item.FileName);

        if (item.Kind == nameof(TrashKind.Template))
        {
            var exists = await db.Templates.AnyAsync(
                t => t.ProjectId == project.Id && t.CheckId == check.Id && t.FileName == item.FileName, ct);
            if (!exists)
            {
                db.Templates.Add(new Template
                {
                    ProjectId = project.Id,
                    CheckId = check.Id,
                    FileName = item.FileName,
                    RelativePath = item.OriginalRelativePath,
                    Extension = extension,
                    Size = meta.Size,
                    ModifiedAt = meta.ModifiedAt,
                    CreatedAt = DateTime.Now,
                });
            }
        }
        else
        {
            var exists = await db.Documents.AnyAsync(
                d => d.ProjectId == project.Id && d.CheckId == check.Id && d.FileName == item.FileName, ct);
            if (!exists)
            {
                db.Documents.Add(new Document
                {
                    ProjectId = project.Id,
                    CheckId = check.Id,
                    FileName = item.FileName,
                    RelativePath = item.OriginalRelativePath,
                    SourceTemplatePath = string.Empty,
                    Extension = extension,
                    Size = meta.Size,
                    CreatedAt = DateTime.Now,
                });
            }
        }
    }

    private static TrashItemDto ToDto(TrashItem t) => new(
        t.Id, t.Kind, t.Project, t.Check, t.FileName,
        t.OriginalRelativePath, t.TrashRelativePath, t.Size, t.DeletedAt);
}
