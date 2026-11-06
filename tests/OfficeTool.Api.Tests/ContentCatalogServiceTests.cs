using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 模板 / 文档目录的写路径。这是全仓最密集的一段（900+ 行），此前一条测试都没有。
///
/// 这里锁的不是"能不能跑通"，而是几类**失败后留下烂摊子**的行为：
/// 上传中断留下的半截文件、写库失败留下的孤儿文件、重命名后库与磁盘各说各话。
/// 这些都不会抛异常，只会在几天后变成"列表里有一条点开报错的记录"。
/// </summary>
public sealed class ContentCatalogServiceTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public Harness(int maxSizeMb = 1)
        {
            _root = Path.Combine(Path.GetTempPath(), $"officetool-content-{Guid.NewGuid():N}");
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
            }.WithDefaults();

            Upload = new UploadOptions { MaxSizeMB = maxSizeMb }.WithDefaults();

            Layout = new PathLayout(Storage);
            Store = new LocalFileStore();
            Naming = new DocumentNamingService();

            var auth = new AuthOptions { Enabled = false };   // 关闭鉴权 = 不受限
            var access = new AccessControlService(
                Db, new MemoryCache(new MemoryCacheOptions()), auth, Logger<AccessControlService>());
            var context = ContextFor();
            var logs = new OperationLogService(Db, context, access, Logger<OperationLogService>());

            var catalog = new ProjectCatalogService(
                Db, Layout, Store, logs, context, access);
            var trash = new TrashService(
                Db, Layout, Store, logs, context, access, Logger<TrashService>());

            Content = new ContentCatalogService(
                Db, catalog, Layout, Store, Naming, Storage, Upload, logs, trash, context, access,
                Logger<ContentCatalogService>());
        }

        public AppDbContext Db { get; }

        public StorageOptions Storage { get; }

        public UploadOptions Upload { get; }

        public PathLayout Layout { get; }

        public LocalFileStore Store { get; }

        public DocumentNamingService Naming { get; }

        public ContentCatalogService Content { get; }

        public async Task SeedAsync(string project = "QL1", string check = "SEC")
        {
            var p = new Project { Name = project };
            Db.Projects.Add(p);
            await Db.SaveChangesAsync();

            Db.Checks.Add(new CheckItem { ProjectId = p.Id, Name = check });
            await Db.SaveChangesAsync();
        }

        public string TemplateDir(string project = "QL1", string check = "SEC") =>
            Layout.TemplateDirectory(project, check);

        public string DataDir(string project = "QL1", string check = "SEC") =>
            Layout.DataDirectory(project, check);

        public Task<TemplateDto> UploadAsync(string fileName, string content = "hello", string project = "QL1",
            string check = "SEC") =>
            Content.UploadTemplateAsync(
                project, check, fileName, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)),
                declaredSize: System.Text.Encoding.UTF8.GetByteCount(content));

        public string[] FilesIn(string dir) =>
            Directory.Exists(dir)
                ? Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>().Order().ToArray()
                : [];

        public void Dispose()
        {
            Naming.Dispose();
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
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tester")],
                "Test"));
        return new RequestContext(accessor);
    }

    // ── 上传 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 上传模板应落盘并写入索引()
    {
        using var h = new Harness();
        await h.SeedAsync();

        var dto = await h.UploadAsync("模板.docx", "内容");

        Assert.Equal("模板.docx", dto.FileName);
        Assert.Equal(".docx", dto.Extension);
        Assert.Equal(["模板.docx"], h.FilesIn(h.TemplateDir()));

        var row = await h.Db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal("QL1/SEC/模板.docx".Replace('/', '\\'), row.RelativePath);
        Assert.Equal(6, row.Size);   // "内容" 两个汉字 × 3 字节
    }

    [Fact]
    public async Task 上传中断不得留下半截文件()
    {
        using var h = new Harness();
        await h.SeedAsync();

        await Assert.ThrowsAsync<IOException>(() => h.Content.UploadTemplateAsync(
            "QL1", "SEC", "断开.docx", new InterruptingStream(), declaredSize: 1024));

        // 三条一起才能说明"干净"：目录里没有文件、没有 .part 残留、库里没有记录
        Assert.Empty(h.FilesIn(h.TemplateDir()));
        Assert.Equal(0, await h.Db.Templates.CountAsync());
    }

    [Fact]
    public async Task 超过大小上限应拒绝且不留文件()
    {
        using var h = new Harness(maxSizeMb: 1);
        await h.SeedAsync();

        var big = new MemoryStream(new byte[(1 * 1024 * 1024) + 10]);

        await Assert.ThrowsAsync<PayloadTooLargeException>(() => h.Content.UploadTemplateAsync(
            "QL1", "SEC", "大文件.docx", big, declaredSize: big.Length));

        Assert.Empty(h.FilesIn(h.TemplateDir()));
        Assert.Equal(0, await h.Db.Templates.CountAsync());
    }

    [Fact]
    public async Task 重名上传应冲突且忽略大小写()
    {
        using var h = new Harness();
        await h.SeedAsync();

        await h.UploadAsync("Report.docx");

        // ext4 上 Report.docx 与 report.docx 是两份文件，Windows 经 SMB 看到的是同一个 ——
        // 允许并存等于让两份内容不同的文件互相顶替
        await Assert.ThrowsAsync<ConflictException>(() => h.UploadAsync("report.docx"));

        Assert.Single(h.FilesIn(h.TemplateDir()));
    }

    // ── 新建文档 ────────────────────────────────────────────────────

    [Fact]
    public async Task 新建文档按日期命名_同名递增字母()
    {
        using var h = new Harness();
        await h.SeedAsync();
        await h.UploadAsync("模板.docx");

        var first = await h.Content.CreateDocumentAsync(
            new CreateDocumentRequest("QL1", "SEC", "模板.docx"));
        var second = await h.Content.CreateDocumentAsync(
            new CreateDocumentRequest("QL1", "SEC", "模板.docx"));

        var baseName = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

        Assert.Equal($"{baseName}.docx", first.FileName);
        Assert.Equal($"{baseName}A.docx", second.FileName);
        // 谱系要记着「由哪个模板派生」，否则文档出问题无法回溯来源
        Assert.Equal("模板.docx", first.SourceTemplatePath.Split('\\')[^1]);
        Assert.Equal(2, await h.Db.Documents.CountAsync());
    }

    // ── 重命名 ──────────────────────────────────────────────────────

    [Fact]
    public async Task 重命名文档应移动文件并更新索引()
    {
        using var h = new Harness();
        await h.SeedAsync();
        await h.UploadAsync("模板.docx");

        var doc = await h.Content.CreateDocumentAsync(
            new CreateDocumentRequest("QL1", "SEC", "模板.docx"));

        var renamed = await h.Content.RenameDocumentAsync(
            new RenameRequest("QL1", "SEC", doc.FileName, "最终版.docx"));

        Assert.Equal("最终版.docx", renamed.FileName);
        Assert.Contains("最终版.docx", h.FilesIn(h.DataDir()));
        Assert.DoesNotContain(doc.FileName, h.FilesIn(h.DataDir()));   // 旧名不得残留

        var row = await h.Db.Documents.AsNoTracking().SingleAsync();
        Assert.Equal("最终版.docx", row.FileName);
        Assert.EndsWith("最终版.docx", row.RelativePath);
    }

    [Fact]
    public async Task 重命名文档不得绕过扩展名白名单()
    {
        using var h = new Harness();
        await h.SeedAsync();
        await h.UploadAsync("模板.docx");

        var doc = await h.Content.CreateDocumentAsync(
            new CreateDocumentRequest("QL1", "SEC", "模板.docx"));

        // 模板侧一直拦，文档侧原先漏了：改个名字就能在共享盘上放一个可执行文件
        await Assert.ThrowsAsync<ExtensionNotAllowedException>(() => h.Content.RenameDocumentAsync(
            new RenameRequest("QL1", "SEC", doc.FileName, "木马.exe")));

        var row = await h.Db.Documents.AsNoTracking().SingleAsync();
        Assert.Equal(doc.FileName, row.FileName);
    }

    // ── 删除 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 删除模板应移入回收站并清掉索引()
    {
        using var h = new Harness();
        await h.SeedAsync();
        await h.UploadAsync("待删.docx");

        await h.Content.DeleteTemplateAsync("QL1", "SEC", "待删.docx");

        Assert.Empty(h.FilesIn(h.TemplateDir()));
        Assert.Equal(0, await h.Db.Templates.CountAsync());

        var trashItem = await h.Db.TrashItems.AsNoTracking().SingleAsync();
        Assert.Equal(nameof(TrashKind.Template), trashItem.Kind);
        Assert.Equal("待删.docx", trashItem.FileName);
        Assert.True(File.Exists(PathGuard.FromRelative(h.Layout.TrashRoot, trashItem.TrashRelativePath)));
    }

    // ── 复制 ────────────────────────────────────────────────────────

    [Fact]
    public async Task 跨目录复制模板应沿用原名_再复制才加副本后缀()
    {
        using var h = new Harness();
        await h.SeedAsync();
        await h.SeedAsync("QL2", "IQC");
        await h.UploadAsync("规范.docx");

        var first = await h.Content.CopyTemplateAsync(
            new CopyTemplateRequest("QL1", "SEC", "规范.docx", null, "QL2", "IQC"));

        var second = await h.Content.CopyTemplateAsync(
            new CopyTemplateRequest("QL1", "SEC", "规范.docx", null, "QL2", "IQC"));

        Assert.Equal("规范.docx", first.FileName);
        Assert.Equal("规范_副本.docx", second.FileName);
        Assert.Contains("规范_副本.docx", h.FilesIn(h.TemplateDir("QL2", "IQC")));
        Assert.Equal(3, await h.Db.Templates.CountAsync());   // 源 1 + 目标 2
    }

    // ── 元数据同步 ──────────────────────────────────────────────────

    [Fact]
    public async Task 同步应跳过零字节文件与Office锁文件()
    {
        using var h = new Harness();
        await h.SeedAsync();

        var dir = h.TemplateDir();
        Directory.CreateDirectory(dir);

        await File.WriteAllTextAsync(Path.Combine(dir, "正常.docx"), "real");
        await File.WriteAllBytesAsync(Path.Combine(dir, "半截.docx"), []);
        await File.WriteAllTextAsync(Path.Combine(dir, "~$正常.docx"), "lock");

        var result = await h.Content.SyncMetadataAsync(force: true);

        Assert.Equal(1, result.TemplatesAdded);
        Assert.Contains(result.Messages, m => m.Contains("0 字节"));
        Assert.Contains(result.Messages, m => m.Contains("临时锁文件"));

        var row = await h.Db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal("正常.docx", row.FileName);
    }

    // ── 列表（模板 / 文档共用一套查询） ──────────────────────────────

    [Fact]
    public async Task 列表应按项目筛选_并正确分页()
    {
        using var h = new Harness();
        await h.SeedAsync("QL1", "SEC");
        await h.SeedAsync("QL2", "IQC");

        await h.UploadAsync("a.docx", project: "QL1");
        await h.UploadAsync("b.docx", project: "QL1");
        await h.UploadAsync("c.docx", project: "QL1");
        await h.UploadAsync("d.docx", project: "QL2", check: "IQC");

        var all = await h.Content.ListTemplatesAsync(new FileQuery());
        Assert.Equal(4, all.Total);

        var ql1 = await h.Content.ListTemplatesAsync(new FileQuery { Project = "QL1" });
        Assert.Equal(3, ql1.Total);
        Assert.All(ql1.Items, i => Assert.Equal("QL1", i.Project));

        // 分页的 total 必须是过滤后的总数，不能是当前页条数
        var page2 = await h.Content.ListTemplatesAsync(new FileQuery
        {
            Project = "QL1", Page = 2, PageSize = 2, SortBy = "name", SortDir = "asc",
        });

        Assert.Equal(3, page2.Total);
        Assert.Single(page2.Items);
        Assert.Equal("c.docx", page2.Items[0].FileName);
    }

    [Fact]
    public async Task 模板列表的时间筛选与排序应基于修改时间()
    {
        using var h = new Harness();
        await h.SeedAsync();

        await h.UploadAsync("旧.docx");
        await h.UploadAsync("新.docx");

        // 直接改库把两者的时间拉开，避免依赖真实时钟精度
        var old = await h.Db.Templates.SingleAsync(t => t.FileName == "旧.docx");
        old.CreatedAt = new DateTime(2026, 1, 1, 9, 0, 0);
        old.ModifiedAt = new DateTime(2026, 1, 1, 9, 0, 0);
        var fresh = await h.Db.Templates.SingleAsync(t => t.FileName == "新.docx");
        fresh.CreatedAt = new DateTime(2026, 6, 1, 9, 0, 0);
        fresh.ModifiedAt = new DateTime(2026, 6, 1, 9, 0, 0);
        await h.Db.SaveChangesAsync();

        // 按修改时间倒序：新.docx 在前
        var sorted = await h.Content.ListTemplatesAsync(new FileQuery { SortBy = "modified" });
        Assert.Equal("新.docx", sorted.Items[0].FileName);

        // 区间筛选也走修改时间：用创建时间会得到另一组结果
        var inJan = await h.Content.ListTemplatesAsync(new FileQuery
        {
            From = new DateTime(2026, 1, 1),
            To = new DateTime(2026, 1, 31),
        });

        Assert.Equal("旧.docx", Assert.Single(inJan.Items).FileName);
    }

    /// <summary>模拟「读到一半连接被重置」的上传流。</summary>
    private sealed class InterruptingStream : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delivered)
            {
                throw new IOException("连接被对端重置。");
            }

            _delivered = true;
            buffer[offset] = 0x41;
            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
