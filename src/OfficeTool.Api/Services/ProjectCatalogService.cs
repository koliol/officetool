using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>项目与检项管理（设计文档 §5.1、§5.2）。</summary>
public sealed class ProjectCatalogService(
    AppDbContext db,
    PathLayout layout,
    IFileStore store,
    OperationLogService logs)
{
    private readonly AppDbContext _db = db;
    private readonly PathLayout _layout = layout;
    private readonly IFileStore _store = store;
    private readonly OperationLogService _logs = logs;

    public async Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken ct = default)
    {
        var projects = await _db.Projects.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var counts = await _db.Checks.AsNoTracking()
            .GroupBy(c => c.ProjectId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return [.. projects.Select(p => new ProjectDto(p.Id, p.Name, p.CreatedAt, counts.GetValueOrDefault(p.Id)))];
    }

    /// <summary>
    /// 项目树：一次返回全部项目及其检项，避免前端对每个项目再拉一次 checks（N+1）。
    /// 检项总量通常远小于文件数，整树载入可接受；将来项目很多时再改懒加载。
    /// </summary>
    public async Task<IReadOnlyList<ProjectTreeNodeDto>> TreeAsync(CancellationToken ct = default)
    {
        var projects = await _db.Projects.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var checks = await _db.Checks.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        var byProject = checks.ToLookup(c => c.ProjectId);

        return
        [
            .. projects.Select(p => new ProjectTreeNodeDto(
                p.Id,
                p.Name,
                p.CreatedAt,
                [.. byProject[p.Id].Select(c => new CheckDto(c.Id, c.Name, c.ProjectId, c.CreatedAt))])),
        ];
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        var name = NameValidator.ValidateCode(request.Name, "项目编码");

        if (await _db.Projects.AnyAsync(p => p.Name == name, ct))
        {
            throw new ConflictException($"项目已存在：{name}");
        }

        // 同时在模板库、数据、附件、提取规则四处创建项目级目录（§4.2）
        EnsureDirectoriesFor(name);

        var project = new Project
        {
            Name = name,
            CreatedAt = DateTime.Now,
        };

        _db.Projects.Add(project);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("新建项目", name, success: true, ct: ct);
        return new ProjectDto(project.Id, project.Name, project.CreatedAt, 0);
    }

    public async Task DeleteAsync(string name, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == name, ct)
            ?? throw new NotFoundException($"项目不存在：{name}");

        var checks = await _db.Checks.Where(c => c.ProjectId == project.Id).ToListAsync(ct);

        // 目录非空时禁止删除（§5.1）
        var directories = DirectoriesFor(name);

        foreach (var dir in directories)
        {
            if (!_store.DirectoryExists(dir))
            {
                continue;
            }

            if (_store.ListFiles(dir).Count > 0 || _store.ListDirectories(dir).Count > 0)
            {
                throw new ConflictException($"项目目录非空，禁止删除：{dir}");
            }
        }

        // 先删空目录再删库：目录删除失败时数据库保持原样，不会出现「记录没了目录还在」
        foreach (var dir in directories.Where(_store.DirectoryExists))
        {
            _store.DeleteDirectory(dir);
        }

        if (checks.Count > 0)
        {
            _db.Checks.RemoveRange(checks);
        }

        _db.Projects.Remove(project);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除项目", name, success: true, ct: ct);
    }

    public async Task<IReadOnlyList<CheckDto>> ListChecksAsync(string projectName, CancellationToken ct = default)
    {
        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        return [.. (await _db.Checks.AsNoTracking()
                .Where(c => c.ProjectId == project.Id)
                .OrderBy(c => c.Name)
                .ToListAsync(ct))
            .Select(c => new CheckDto(c.Id, c.Name, c.ProjectId, c.CreatedAt))];
    }

    public async Task<CheckDto> CreateCheckAsync(string projectName, CreateCheckRequest request, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        var name = NameValidator.ValidateCode(request.Name, "检项编码");

        if (await _db.Checks.AnyAsync(c => c.ProjectId == project.Id && c.Name == name, ct))
        {
            throw new ConflictException($"检项已存在：{projectName}/{name}");
        }

        EnsureDirectoriesFor(projectName, name);

        var check = new CheckItem
        {
            ProjectId = project.Id,
            Name = name,
            CreatedAt = DateTime.Now,
        };

        _db.Checks.Add(check);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("新建检项", $"{projectName}/{name}", success: true, ct: ct);
        return new CheckDto(check.Id, check.Name, check.ProjectId, check.CreatedAt);
    }

    public async Task DeleteCheckAsync(string projectName, string checkName, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        var check = await _db.Checks.FirstOrDefaultAsync(c => c.ProjectId == project.Id && c.Name == checkName, ct)
            ?? throw new NotFoundException($"检项不存在：{projectName}/{checkName}");

        var directories = DirectoriesFor(projectName, checkName);

        foreach (var dir in directories)
        {
            if (!_store.DirectoryExists(dir))
            {
                continue;
            }

            if (_store.ListFiles(dir).Count > 0)
            {
                throw new ConflictException($"检项目录非空，禁止删除：{dir}");
            }
        }

        // 先删空目录再删库（同上）
        foreach (var dir in directories.Where(_store.DirectoryExists))
        {
            _store.DeleteDirectory(dir);
        }

        _db.Checks.Remove(check);
        await _db.SaveChangesAsync(ct);

        await _logs.WriteAsync("删除检项", $"{projectName}/{checkName}", success: true, ct: ct);
    }

    /// <summary>
    /// 一个项目/检项对应的**四个平行目录**：模板库、数据、附件、提取规则。
    ///
    /// 集中成一处，是因为创建时的「一并创建」和删除时的「非空判定」必须
    /// 覆盖同一批目录 —— 分散写就很容易在某次新增目录类型时漏掉一个分支
    /// （上一轮加附件目录时就出现过这个隐患）。
    /// </summary>
    private string[] DirectoriesFor(string project, string? check = null) =>
    [
        _layout.TemplateDirectory(project, check),
        _layout.DataDirectory(project, check),
        _layout.AttachmentDirectory(project, check),
        _layout.RuleDirectory(project, check),
    ];

    /// <summary>创建项目/检项时把这四个目录一次建好。</summary>
    private void EnsureDirectoriesFor(string project, string? check = null)
    {
        foreach (var directory in DirectoriesFor(project, check))
        {
            _store.CreateDirectory(directory);
        }
    }

    /// <summary>解析项目与检项，供其他服务复用。</summary>
    public async Task<(Project Project, CheckItem Check)> ResolveAsync(
        string projectName, string checkName, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        var check = await _db.Checks.FirstOrDefaultAsync(c => c.ProjectId == project.Id && c.Name == checkName, ct)
            ?? throw new NotFoundException($"检项不存在：{projectName}/{checkName}");

        return (project, check);
    }
}
