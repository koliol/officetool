using System.Text.Json;
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
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// ELN 主链路的服务级测试：建记录 → 传 PDF → 解析 → 填值 → 提交 → 提交后不可改。
///
/// 用全内存/temp 目录的真实依赖（真 SQLite、真文件、真 PdfPig），
/// 不打桩 —— 这条链路的坑几乎都在"真 IO + 真解析"里，打了桩就测不到。
/// </summary>
public sealed class ElnFlowTests
{
    private sealed class Harness : IDisposable
    {
        private readonly string _root;

        public Harness(bool authEnabled = false)
        {
            _root = Path.Combine(Path.GetTempPath(), $"officetool-eln-{Guid.NewGuid():N}");
            Directory.CreateDirectory(_root);

            Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={Path.Combine(_root, "eln.db")}")
                .Options);
            Db.Database.EnsureCreated();

            Storage = new StorageOptions
            {
                Mode = nameof(StorageMode.Local),
                TemplatesRoot = Path.Combine(_root, "Templates"),
                DataRoot = Path.Combine(_root, "Data"),
                AttachmentsRoot = Path.Combine(_root, "Attachments"),
                RulesRoot = Path.Combine(_root, "Rules"),
                RecordsRoot = Path.Combine(_root, "Records"),
                TrashRoot = Path.Combine(_root, "_trash"),
            }.WithDefaults();

            Upload = new UploadOptions { MaxSizeMB = 5 }.WithDefaults();

            Layout = new PathLayout(Storage);
            Store = new LocalFileStore();
            Accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
            Accessor.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "tester")],
                    "Test"));

            var auth = new AuthOptions { Enabled = authEnabled };
            Access = new AccessControlService(
                Db, new MemoryCache(new MemoryCacheOptions()), auth, Log<AccessControlService>());

            Context = new RequestContext(Accessor);
            var logs = new OperationLogService(Db, Context, Access, Log<OperationLogService>());
            Catalog = new ProjectCatalogService(Db, Layout, Store, logs, Context, Access);

            Records = new FormRecordService(
                Db, Catalog, Layout, Store, Upload, Context, Access, logs, Accessor);
            Extraction = new ExtractionService(Db, Layout, Store, Records, Context, logs);
        }

        public AppDbContext Db { get; }

        public StorageOptions Storage { get; }

        public UploadOptions Upload { get; }

        public PathLayout Layout { get; }

        public LocalFileStore Store { get; }

        public HttpContextAccessor Accessor { get; }

        public AccessControlService Access { get; }

        public RequestContext Context { get; }

        public ProjectCatalogService Catalog { get; }

        public FormRecordService Records { get; }

        public ExtractionService Extraction { get; }

        public async Task<(ProjectTemplate Template, Project Project, CheckItem Check)> SeedAsync(
            string projectName = "QLS2409",
            string checkName = "SEC")
        {
            await BuiltInContentSeeder.SeedAsync(Db, new Microsoft.Extensions.Logging.Abstractions.NullLogger<int>());

            var project = new Project { Name = projectName };
            Db.Projects.Add(project);
            await Db.SaveChangesAsync();

            var check = new CheckItem { ProjectId = project.Id, Name = checkName };
            Db.Checks.Add(check);
            await Db.SaveChangesAsync();

            var template = await Db.FormTemplates.SingleAsync();

            // 管理员把规则授权到这个检项上，否则普通用户看不到
            var rule = await Db.ExtractionRuleDefinitions.SingleAsync();
            Db.ExtractionRuleScopes.Add(new ExtractionRuleScope
            {
                RuleId = rule.Id, ProjectId = project.Id, CheckId = check.Id, CreatedAt = DateTime.Now,
            });
            await Db.SaveChangesAsync();

            return (new ProjectTemplate(template.Id, rule.Id), project, check);
        }

        public Task<FormRecordDto> CreateRecordAsync(ProjectTemplate template) =>
            Records.CreateAsync(new CreateFormRecordRequest(template.TemplateId, "QLS2409", "SEC", null));

        /// <summary>切换"当前请求是谁"。</summary>
        public void AsUser(string userName)
        {
            Accessor.HttpContext!.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, userName)],
                    "Test"));
        }

        /// <summary>建一个只能在指定检项上操作的普通用户，并返回其登录名。</summary>
        public async Task<string> AddScopedUserAsync(int projectId, int checkId, AccessLevel level, string name = "li")
        {
            var user = new User { UserName = name, DisplayName = name, Source = UserSource.Local };
            Db.Users.Add(user);

            var group = new Group { Name = $"g-{name}", DisplayName = name, Source = UserSource.Local };
            Db.Groups.Add(group);
            await Db.SaveChangesAsync();

            Db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });
            Db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = projectId, CheckId = checkId, Level = level,
            });
            await Db.SaveChangesAsync();

            return name;
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
                // 句柄未释放时忽略
            }
        }
    }

    public sealed record ProjectTemplate(int TemplateId, int RuleId);

    private static ILogger<T> Log<T>() =>
        LoggerFactory.Create(_ => { }).CreateLogger<T>();

    private static byte[] BuildReportPdf()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);

        // 规则里带了英文别名（Sample Name / Batch No），
        // 这样用标准字体也能端到端验证整条链路
        page.AddText("Sample Name: Ceftriaxone", 11, new PdfPoint(50, 760), font);
        page.AddText("Batch No: 20260901", 11, new PdfPoint(50, 740), font);
        page.AddText("1   12   3", 11, new PdfPoint(50, 700), font);
        page.AddText("2    9   1", 11, new PdfPoint(50, 684), font);

        return builder.Build();
    }

    [Fact]
    public async Task 建记录到解析到提交的完整链路()
    {
        using var h = new Harness();
        var seed = await h.SeedAsync();

        var record = await h.CreateRecordAsync(seed.Template);

        Assert.Equal(RecordStatus.Draft, Enum.Parse<RecordStatus>(record.Status));
        Assert.StartsWith("QLS2409-SEC-", record.RecordNo);

        // 1) 上传原始报告
        var file = await h.Records.AddFileAsync(
            record.Id,
            "report.pdf",
            new MemoryStream(BuildReportPdf()),
            declaredSize: 4096,
            contentType: "application/pdf");

        Assert.Equal(".pdf", file.Extension);

        // 2) 解析
        var result = await h.Extraction.RunAsync(record.Id, new ExtractRecordRequest(seed.Template.RuleId, null));

        Assert.Equal("Succeeded", result.Status);
        Assert.True(result.MatchedFieldCount >= 3, $"只抽到 {result.MatchedFieldCount} 个字段：{result.Error}");

        var byField = result.Fields.ToDictionary(f => f.Field, f => f);
        Assert.Equal("Ceftriaxone", byField["productName"].Value.GetString());
        Assert.Equal("20260901", byField["batchNo"].Value.GetString());
        Assert.Equal(2, byField["items"].Value.GetArrayLength());

        // 3) 把解析结果填进记录（模拟前端"确认后填入"）
        var data = JsonDocument.Parse($$"""
        {
          "productName": {{byField["productName"].Value.GetRawText()}},
          "batchNo": {{byField["batchNo"].Value.GetRawText()}},
          "testDate": "2026-11-06",
          "method": "光阻法",
          "items": {{byField["items"].Value.GetRawText()}},
          "result": "符合规定"
        }
        """).RootElement.Clone();

        var saved = await h.Records.SaveDraftAsync(record.Id, data);
        Assert.Equal(RecordStatus.Draft, Enum.Parse<RecordStatus>(saved.Status));

        // 4) 提交
        var submitted = await h.Records.SubmitAsync(record.Id);
        Assert.Equal(RecordStatus.Submitted, Enum.Parse<RecordStatus>(submitted.Status));
        Assert.NotNull(submitted.SubmittedAt);
        Assert.Equal("tester", submitted.SubmittedBy);

        // 5) 提交后不允许再改
        await Assert.ThrowsAsync<ConflictException>(() => h.Records.SaveDraftAsync(record.Id, data));

        // 6) 审计有记录
        var runs = await h.Extraction.ListRunsAsync(record.Id);
        Assert.Single(runs);
        Assert.Equal("insoluble_particles", runs[0].RuleCode);
    }

    [Fact]
    public async Task 规则必须被授权到范围后普通用户才能看到()
    {
        // 开鉴权的实例：权限从 ACL 表来，而不是"关闭鉴权=超管"
        using var h = new Harness(authEnabled: true);
        var seed = await h.SeedAsync();

        // 一个只在 SEC 上有写权限的普通用户
        var user = await h.AddScopedUserAsync(seed.Project.Id, seed.Check.Id, AccessLevel.Write);

        // 管理员还没把规则授权到任何范围
        h.Db.ExtractionRuleScopes.RemoveRange(h.Db.ExtractionRuleScopes);
        await h.Db.SaveChangesAsync();

        h.AsUser(user);
        var record = await h.CreateRecordAsync(seed.Template);

        // 没范围 → 看不到规则
        Assert.Empty(await h.Extraction.ListRulesForRecordAsync(record.Id));

        // 授权到整个项目 → 能看到
        var rule = await h.Db.ExtractionRuleDefinitions.SingleAsync();
        h.Db.ExtractionRuleScopes.Add(new ExtractionRuleScope
        {
            RuleId = rule.Id, ProjectId = seed.Project.Id, CheckId = null, CreatedAt = DateTime.Now,
        });
        await h.Db.SaveChangesAsync();

        Assert.Single(await h.Extraction.ListRulesForRecordAsync(record.Id));

        // 改成只授权到另一个检项 → 又看不到了
        var other = new CheckItem { ProjectId = seed.Project.Id, Name = "IQC" };
        h.Db.Checks.Add(other);
        await h.Db.SaveChangesAsync();

        h.Db.ExtractionRuleScopes.RemoveRange(h.Db.ExtractionRuleScopes);
        h.Db.ExtractionRuleScopes.Add(new ExtractionRuleScope
        {
            RuleId = rule.Id, ProjectId = seed.Project.Id, CheckId = other.Id, CreatedAt = DateTime.Now,
        });
        await h.Db.SaveChangesAsync();

        Assert.Empty(await h.Extraction.ListRulesForRecordAsync(record.Id));

        // 停用规则同样不可见（范围恢复后）
        h.Db.ExtractionRuleScopes.RemoveRange(h.Db.ExtractionRuleScopes);
        h.Db.ExtractionRuleScopes.Add(new ExtractionRuleScope
        {
            RuleId = rule.Id, ProjectId = seed.Project.Id, CheckId = null, CreatedAt = DateTime.Now,
        });
        rule.IsEnabled = false;
        await h.Db.SaveChangesAsync();

        Assert.Empty(await h.Extraction.ListRulesForRecordAsync(record.Id));
    }

    [Fact]
    public async Task 上传非白名单格式应被拒绝()
    {
        using var h = new Harness();
        var seed = await h.SeedAsync();
        var record = await h.CreateRecordAsync(seed.Template);

        await Assert.ThrowsAsync<ExtensionNotAllowedException>(() => h.Records.AddFileAsync(
            record.Id, "trojan.exe", new MemoryStream([1, 2, 3]), 3, "application/octet-stream"));
    }

    [Fact]
    public async Task 作废必须填原因且需要管理权()
    {
        using var h = new Harness();
        var seed = await h.SeedAsync();
        var record = await h.CreateRecordAsync(seed.Template);

        await Assert.ThrowsAsync<BadRequestException>(() => h.Records.VoidAsync(record.Id, "  "));

        var voided = await h.Records.VoidAsync(record.Id, "样品污染，重做");
        Assert.Equal(RecordStatus.Voided, Enum.Parse<RecordStatus>(voided.Status));
        Assert.Equal("样品污染，重做", voided.VoidReason);

        // 已作废的记录不能再改
        await Assert.ThrowsAsync<ConflictException>(() =>
            h.Records.SaveDraftAsync(record.Id, JsonDocument.Parse("{}").RootElement.Clone()));
    }
}
