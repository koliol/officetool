using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 项目 / 检项结构维护的行为约束。
///
/// 这一组锁的是 2026-11 报出的「新建项目建不成功」：
/// 项目其实建成了（数据库有行、四个目录也建了），但列表与树把它过滤掉了，
/// 于是界面看不到、第一个检项永远建不出来。这类缺陷不会报错，只能靠断言守住。
/// </summary>
public class ProjectCatalogServiceTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), $"officetool-proj-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            var dbPath = Path.Combine(_root, "test.db");
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={dbPath}")
                .Options;

            Db = new AppDbContext(options);
            Db.Database.EnsureCreated();

            var storage = new StorageOptions
            {
                Mode = nameof(StorageMode.Local),
                TemplatesRoot = Path.Combine(_root, "Templates"),
                DataRoot = Path.Combine(_root, "Data"),
                AttachmentsRoot = Path.Combine(_root, "Attachments"),
                RulesRoot = Path.Combine(_root, "Rules"),
                TrashRoot = Path.Combine(_root, "_trash"),
            };

            var auth = new AuthOptions { Enabled = false };   // 关闭鉴权 = 不受限（等价超管）
            Cache = new MemoryCache(new MemoryCacheOptions());
            Access = new AccessControlService(Db, Cache, auth, Logger<AccessControlService>());
            var context = ContextFor("tester");
            var logs = new OperationLogService(Db, context, Access, Logger<OperationLogService>());

            Catalog = new ProjectCatalogService(
                Db,
                new PathLayout(storage),
                new LocalFileStore(),
                logs,
                context,
                Access);
        }

        public AppDbContext Db { get; }
        public IMemoryCache Cache { get; }
        public AccessControlService Access { get; }
        public ProjectCatalogService Catalog { get; }

        public void Dispose()
        {
            Db.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_root, recursive: true); } catch { /* 目录被占用时忽略 */ }
        }
    }

    private static ILogger<T> Logger<T>() => LoggerFactory.Create(_ => { }).CreateLogger<T>();

    private static RequestContext ContextFor(string? userName)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        if (userName is not null)
        {
            accessor.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName)],
                    "Test"));
        }

        return new RequestContext(accessor, new OfficeTool.Core.Options.SecurityOptions());
    }

    [Fact]
    public async Task Newly_created_project_without_checks_must_stay_visible_in_tree()
    {
        using var h = new Harness();

        var created = await h.Catalog.CreateAsync(new CreateProjectRequest("QL9001"));
        Assert.Equal("QL9001", created.Name);
        Assert.Equal(0, created.CheckCount);

        var tree = await h.Catalog.TreeAsync();
        var node = Assert.Single(tree);
        Assert.Equal("QL9001", node.Name);
        Assert.Empty(node.Checks);

        // 列表接口也必须能看到：前端管理页用它填项目下拉
        var list = await h.Catalog.ListAsync();
        var dto = Assert.Single(list);
        Assert.Equal("QL9001", dto.Name);
        Assert.Equal(0, dto.CheckCount);
    }

    [Fact]
    public async Task Project_name_is_case_insensitive_because_the_folder_is()
    {
        using var h = new Harness();

        await h.Catalog.CreateAsync(new CreateProjectRequest("QL9002"));

        // 磁盘目录在 Windows / SMB 上不区分大小写，库里再建一个只差大小写的项目
        // 会让两个项目指向同一个物理目录，数据互相串。
        var ex = await Assert.ThrowsAsync<ConflictException>(
            () => h.Catalog.CreateAsync(new CreateProjectRequest("ql9002")));

        Assert.Contains("已存在", ex.Message);
        Assert.Single(await h.Catalog.ListAsync());
    }

    [Fact]
    public async Task Check_name_is_case_insensitive_and_lookup_ignores_case()
    {
        using var h = new Harness();

        await h.Catalog.CreateAsync(new CreateProjectRequest("QL9003"));
        await h.Catalog.CreateCheckAsync("QL9003", new CreateCheckRequest("SEC"));

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Catalog.CreateCheckAsync("QL9003", new CreateCheckRequest("sec")));

        // 用小写项目名也要能找到同一个项目（目录名大小写不该影响定位）
        var checks = await h.Catalog.ListChecksAsync("ql9003");
        Assert.Equal("SEC", Assert.Single(checks).Name);
    }

    [Fact]
    public async Task Deleting_the_last_check_keeps_the_project_reachable()
    {
        using var h = new Harness();

        await h.Catalog.CreateAsync(new CreateProjectRequest("QL9004"));
        await h.Catalog.CreateCheckAsync("QL9004", new CreateCheckRequest("SEC"));
        await h.Catalog.DeleteCheckAsync("QL9004", "SEC");

        var tree = await h.Catalog.TreeAsync();
        var node = Assert.Single(tree);
        Assert.Equal("QL9004", node.Name);
        Assert.Empty(node.Checks);
    }
}
