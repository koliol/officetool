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
    OperationLogService logs,
    RequestContext request,
    IAccessControlService access)
{
    private readonly AppDbContext _db = db;
    private readonly PathLayout _layout = layout;
    private readonly IFileStore _store = store;
    private readonly OperationLogService _logs = logs;
    private readonly RequestContext _request = request;
    private readonly IAccessControlService _access = access;

    private Task<UserAccess> CurrentAccessAsync(CancellationToken ct) => _access.ResolveAccessAsync(_request, ct);

    /// <summary>
    /// 结构维护（建/删项目与检项）的权限门。
    ///
    /// 这里刻意不用「对某个检项有 Manage」：一个项目在创建时还没有任何检项，
    /// 无从授权，按检项判会出现「谁都建不了第一个项目」的死锁。
    /// 所以：
    /// <list type="bullet">
    ///   <item>新建项目（没有授权对象）→ 只能系统管理员</item>
    ///   <item>删除项目 / 增删检项 → 对该项目下<strong>所有</strong>可见检项都有 Manage</item>
    /// </list>
    /// 日常的增删改文件走各自的 Read/Write/Manage 门，不受此限制。
    /// </summary>
    private async Task RequireProjectAdminAsync(Project project, string action, CancellationToken ct)
    {
        var access = await CurrentAccessAsync(ct);

        if (access.IsSystemAdmin)
        {
            return;
        }

        var ids = await _db.Checks.AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .Select(c => c.Id)
            .ToListAsync(ct);

        // 项目下还没有检项时无从授权，只允许系统管理员操作
        if (ids.Count == 0 || !ids.All(id => access.Has(id, AccessLevel.Manage)))
        {
            throw new AccessDeniedException($"无权{action}：{project.Name}（需对该项目下所有检项具备管理权限）");
        }
    }

    public async Task<IReadOnlyList<ProjectDto>> ListAsync(CancellationToken ct = default)
    {
        var access = await CurrentAccessAsync(ct);

        var projects = await _db.Projects.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var checks = await _db.Checks.AsNoTracking()
            .Select(c => new { c.Id, c.ProjectId })
            .ToListAsync(ct);

        // 可见性裁剪有两层含义：
        //   1. 项目里没有任何可见检项时，这个项目不该出现在列表里
        //   2. 检项计数也要按可见的算 —— 显示「8 个检项」点进去一个都看不到，比不显示更糟
        var visibleChecks = access.NeedsCheckFilter
            ? checks.Where(x => access.IsVisible(x.Id)).ToList()
            : checks;

        var counts = visibleChecks
            .GroupBy(x => x.ProjectId)
            .ToDictionary(g => g.Key, g => g.Count());

        return [.. projects
            .Where(p => counts.ContainsKey(p.Id))
            .Select(p => new ProjectDto(p.Id, p.Name, p.CreatedAt, counts[p.Id]))];
    }

    /// <summary>
    /// 项目树：一次返回全部项目及其检项，避免前端对每个项目再拉一次 checks（N+1）。
    /// 检项总量通常远小于文件数，整树载入可接受；将来项目很多时再改懒加载。
    /// </summary>
    public async Task<IReadOnlyList<ProjectTreeNodeDto>> TreeAsync(CancellationToken ct = default)
    {
        var access = await CurrentAccessAsync(ct);

        var projects = await _db.Projects.AsNoTracking()
            .OrderBy(p => p.Name)
            .ToListAsync(ct);

        var checks = await _db.Checks.AsNoTracking()
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

        // 无可见检项的项目直接不出节点：留一个点不开的空节点，
        // 除了让人猜到「这里有个我进不去的项目」之外没有价值。
        if (access.NeedsCheckFilter)
        {
            checks = [.. checks.Where(c => access.IsVisible(c.Id))];
        }

        var byProject = checks.ToLookup(c => c.ProjectId);

        return
        [
            .. projects
                .Where(p => byProject[p.Id].Any())
                .Select(p => new ProjectTreeNodeDto(
                    p.Id,
                    p.Name,
                    p.CreatedAt,
                    [.. byProject[p.Id].Select(c => new CheckDto(c.Id, c.Name, c.ProjectId, c.CreatedAt))])),
        ];
    }

    public async Task<ProjectDto> CreateAsync(CreateProjectRequest request, CancellationToken ct = default)
    {
        // 新建项目时它还不存在，没有任何授权对象可以判断，只能用系统管理员兜底。
        var createAccess = await CurrentAccessAsync(ct);
        if (!createAccess.IsSystemAdmin)
        {
            throw new AccessDeniedException("新建项目是系统级维护操作，仅系统管理员可执行。");
        }

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

        await RequireProjectAdminAsync(project, "删除项目", ct);

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
        var access = await CurrentAccessAsync(ct);

        var project = await _db.Projects.AsNoTracking().FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        var checks = await _db.Checks.AsNoTracking()
            .Where(c => c.ProjectId == project.Id)
            .Select(c => new CheckDto(c.Id, c.Name, c.ProjectId, c.CreatedAt))
            .ToListAsync(ct);

        // 项目本身可能对用户可见（它含有其他可见检项），但单个检项仍可能不在授权范围内
        if (access.NeedsCheckFilter)
        {
            checks = [.. checks.Where(c => access.IsVisible(c.Id))];
        }

        return [.. checks.OrderBy(c => c.Name)];
    }

    public async Task<CheckDto> CreateCheckAsync(string projectName, CreateCheckRequest request, CancellationToken ct = default)
    {
        var project = await _db.Projects.FirstOrDefaultAsync(p => p.Name == projectName, ct)
            ?? throw new NotFoundException($"项目不存在：{projectName}");

        var name = NameValidator.ValidateCode(request.Name, "检项编码");

        await RequireProjectAdminAsync(project, "新建检项", ct);

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

        (await CurrentAccessAsync(ct)).Require(check.Id, AccessLevel.Manage, "删除检项", $"{projectName}/{checkName}");

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
