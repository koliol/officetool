using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 回收站的删除 → 恢复 → 彻底删除回路。
///
/// 此前这一层没有测试：AccessControlTests 里那段只是把 TrashService 的 LINQ 抄了一遍，
/// 从来没调用过服务本身。而恢复恰恰是最容易「半途失败」的地方——
/// 文件已移回、库记录没建，结果既恢复不了也删不掉。
/// </summary>
public sealed class TrashServiceTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public Harness()
        {
            _root = Path.Combine(Path.GetTempPath(), $"officetool-trash-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_root, "test.db")}")
                .Options);
            Db.Database.EnsureCreated();

            Storage = new StorageOptions
            {
                Mode = nameof(StorageMode.Local),
                TemplatesRoot = Path.Combine(_root, "Templates"),
                DataRoot = Path.Combine(_root, "Data"),
                AttachmentsRoot = Path.Combine(_root, "Attachments"),
                RulesRoot = Path.Combine(_root, "Rules"),
                TrashRoot = Path.Combine(_root, "_trash"),
            };

            Layout = new PathLayout(Storage);
            Store = new LocalFileStore();

            var auth = new AuthOptions { Enabled = false };
            var access = new AccessControlService(
                Db, new MemoryCache(new MemoryCacheOptions()), auth, Logger<AccessControlService>());
            var context = ContextFor();
            var logs = new OperationLogService(Db, context, access, Logger<OperationLogService>());

            Trash = new TrashService(Db, Layout, Store, logs, context, access, Logger<TrashService>());
        }

        public AppDbContext Db { get; }

        public StorageOptions Storage { get; }

        public PathLayout Layout { get; }

        public LocalFileStore Store { get; }

        public TrashService Trash { get; }

        /// <summary>在回收站里放一份文件，并登记成条目（模拟「已删除」的状态）。</summary>
        public async Task<TrashItem> SeedTrashedTemplateAsync(string project = "P1", string check = "C1")
        {
            const string fileName = "t.docx";
            const string trashRelative = @"Template\20261106\x.docx";

            var trashPath = PathGuard.FromRelative(Layout.TrashRoot, trashRelative);
            Directory.CreateDirectory(Path.GetDirectoryName(trashPath)!);
            await File.WriteAllTextAsync(trashPath, "content");

            var item = new TrashItem
            {
                Kind = nameof(TrashKind.Template),
                Project = project,
                Check = check,
                FileName = fileName,
                OriginalRelativePath = $@"{project}\{check}\{fileName}",
                TrashRelativePath = trashRelative,
                Size = 7,
                DeletedAt = DateTime.Now,
            };

            Db.TrashItems.Add(item);
            await Db.SaveChangesAsync();
            return item;
        }

        public void Dispose()
        {
            Db.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(_root, recursive: true);
            }
            catch (IOException)
            {
                // 目录被占用时忽略
            }
        }
    }

    private static ILogger<T> Logger<T>() => LoggerFactory.Create(_ => { }).CreateLogger<T>();

    private static RequestContext ContextFor()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        accessor.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "root")],
                "Test"));
        return new RequestContext(accessor, new OfficeTool.Core.Options.SecurityOptions());
    }

    [Fact]
    public async Task 恢复模板应把文件移回原位并重建索引()
    {
        using var h = new Harness();

        var project = new Project { Name = "P1" };
        h.Db.Projects.Add(project);
        await h.Db.SaveChangesAsync();   // 先拿到自增 Id，再挂检项

        h.Db.Checks.Add(new CheckItem { ProjectId = project.Id, Name = "C1" });
        await h.Db.SaveChangesAsync();

        var item = await h.SeedTrashedTemplateAsync();

        await h.Trash.RestoreAsync(item.Id);

        var restored = Path.Combine(h.Layout.TemplateDirectory("P1", "C1"), "t.docx");
        Assert.True(File.Exists(restored));

        var row = await h.Db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal("t.docx", row.FileName);
        Assert.Equal(0, await h.Db.TrashItems.CountAsync());
    }

    [Fact]
    public async Task 原项目已删除时恢复应失败且文件留在回收站()
    {
        using var h = new Harness();

        // 刻意不建项目：这正是「删了项目，回收站里还留着它的模板」的真实状态
        var item = await h.SeedTrashedTemplateAsync();
        var trashPath = PathGuard.FromRelative(h.Layout.TrashRoot, item.TrashRelativePath);

        await Assert.ThrowsAsync<NotFoundException>(() => h.Trash.RestoreAsync(item.Id));

        // 三条断言合起来才说明「原子」：
        // 文件还在回收站、条目还在库、业务目录里没有半成品
        Assert.True(File.Exists(trashPath));
        Assert.Equal(1, await h.Db.TrashItems.CountAsync());
        Assert.False(Directory.Exists(h.Layout.TemplateDirectory("P1", "C1"))
                     && Directory.GetFiles(h.Layout.TemplateDirectory("P1", "C1")).Length > 0);
    }

    [Fact]
    public async Task 原位置已有同名文件时不得覆盖也不得动回收站()
    {
        using var h = new Harness();

        var project = new Project { Name = "P1" };
        h.Db.Projects.Add(project);
        await h.Db.SaveChangesAsync();

        h.Db.Checks.Add(new CheckItem { ProjectId = project.Id, Name = "C1" });
        await h.Db.SaveChangesAsync();

        var item = await h.SeedTrashedTemplateAsync();

        var occupied = Path.Combine(h.Layout.TemplateDirectory("P1", "C1"), "t.docx");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!);
        await File.WriteAllTextAsync(occupied, "someone else");

        await Assert.ThrowsAsync<ConflictException>(() => h.Trash.RestoreAsync(item.Id));

        Assert.Equal("someone else", await File.ReadAllTextAsync(occupied));
        Assert.True(File.Exists(PathGuard.FromRelative(h.Layout.TrashRoot, item.TrashRelativePath)));
        Assert.Equal(1, await h.Db.TrashItems.CountAsync());
    }
}
