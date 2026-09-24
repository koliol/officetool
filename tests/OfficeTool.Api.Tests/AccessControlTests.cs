using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using OfficeTool.Api.Contracts;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 权限模型：递进等级的语义、项目级展开、检项级例外覆盖，
/// 以及「无外键资源按名称过滤」能否真的翻译成 SQL。
/// </summary>
public class AccessControlTests
{
    private static (AppDbContext Db, string Path) CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officetool-acl-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;
        return (new AppDbContext(options), path);
    }

    private static void Cleanup(AppDbContext db, string path)
    {
        db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static ILogger<T> Logger<T>() => LoggerFactory.Create(b => { }).CreateLogger<T>();

    private static AccessControlService CreateService(AppDbContext db) => new(
        db,
        new MemoryCache(new MemoryCacheOptions()),
        new AuthOptions { Enabled = true },
        Logger<AccessControlService>());

    /// <summary>播种：一个本地组、一个用户、一个项目（含两个检项）。返回各自的 id。</summary>
    private static async Task<(int UserId, int GroupId, int ProjectId, int C1, int C2)> SeedAsync(AppDbContext db)
    {
        var group = new Group { Name = "grp", DisplayName = "测试组", Source = UserSource.Local };
        var user = new User { UserName = "tester", DisplayName = "测试用户", Source = UserSource.Local };
        db.Groups.Add(group);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        db.UserGroups.Add(new UserGroup { UserId = user.Id, GroupId = group.Id });

        var project = new Project { Name = "P1" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var c1 = new CheckItem { ProjectId = project.Id, Name = "C1" };
        var c2 = new CheckItem { ProjectId = project.Id, Name = "C2" };
        db.Checks.AddRange(c1, c2);
        await db.SaveChangesAsync();

        return (user.Id, group.Id, project.Id, c1.Id, c2.Id);
    }

    [Fact]
    public async Task 项目级授权应展开到该项目下所有检项()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (_, groupId, projectId, c1, c2) = await SeedAsync(db);

            db.AclEntries.Add(new AclEntry
            {
                GroupId = groupId,
                ProjectId = projectId,
                CheckId = null,
                Level = AccessLevel.Read,
            });
            await db.SaveChangesAsync();

            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            Assert.True(access.IsVisible(c1));
            Assert.True(access.IsVisible(c2));
            Assert.True(access.Has(c1, AccessLevel.Read));
            Assert.True(access.Has(c2, AccessLevel.Read));

            // 递进：只读不该顺带拿到编辑/管理
            Assert.False(access.Has(c1, AccessLevel.Write));
            Assert.False(access.Has(c1, AccessLevel.Manage));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 同一检项被多条授权命中应取最高等级()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (_, groupId, projectId, c1, _) = await SeedAsync(db);

            db.AclEntries.AddRange(
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = null, Level = AccessLevel.Read },
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = AccessLevel.Manage });
            await db.SaveChangesAsync();

            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            Assert.Equal(AccessLevel.Manage, access.LevelOf(c1));
            Assert.True(access.Has(c1, AccessLevel.Write));
            Assert.True(access.Has(c1, AccessLevel.Manage));
            Assert.False(access.Has(c1, AccessLevel.RuleManage));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 检项级显式授权应能降级覆盖项目级授权()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (_, groupId, projectId, c1, c2) = await SeedAsync(db);

            // 整个项目管理，但 C1 只给只读
            db.AclEntries.AddRange(
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = null, Level = AccessLevel.Manage },
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = AccessLevel.Read });
            await db.SaveChangesAsync();

            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            Assert.Equal(AccessLevel.Read, access.LevelOf(c1));
            Assert.False(access.Has(c1, AccessLevel.Manage));

            // 未被例外覆盖的检项仍继承项目级授权
            Assert.Equal(AccessLevel.Manage, access.LevelOf(c2));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 检项级显式None应把该检项排除在项目授权之外()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (_, groupId, projectId, c1, c2) = await SeedAsync(db);

            db.AclEntries.AddRange(
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = null, Level = AccessLevel.Manage },
                new AclEntry { GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = AccessLevel.None });
            await db.SaveChangesAsync();

            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            // 黑名单例外：这条必须成立，否则「整个项目授权 + 个别检项屏蔽」配不出来
            Assert.False(access.IsVisible(c1));
            Assert.False(access.AllowedCheckIds.Contains(c1));
            Assert.True(access.IsVisible(c2));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 超管应绕过全部ACL且无需过滤()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (userId, _, _, c1, _) = await SeedAsync(db);

            var admin = await db.Users.FirstAsync(u => u.Id == userId);
            admin.IsSystemAdmin = true;
            await db.SaveChangesAsync();

            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            // 关键：超管的 AllowedCheckIds 是空集，调用方必须靠 NeedsCheckFilter 判断要不要加 Where。
            // 这个断言是在守那条规则 —— 直接拿空集做 Contains 会把所有行过滤掉。
            Assert.True(access.IsSystemAdmin);
            Assert.False(access.NeedsCheckFilter);
            Assert.False(access.NeedsScopeFilter);
            Assert.True(access.Has(c1, AccessLevel.RuleManage));
            Assert.Equal(AccessLevel.RuleManage, access.LevelOf(c1));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 无授权用户应不可见任何检项()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());
            var (_, _, _, c1, _) = await SeedAsync(db);

            // 用户存在但没有一条 ACL
            var access = await CreateService(db).ResolveByUserNameAsync("tester");

            Assert.False(access.IsVisible(c1));
            Assert.Empty(access.AllowedCheckIds);
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    /// <summary>
    /// 回收站、规则、附件没有 ProjectId/CheckId 外键，只能按名称过滤。
    /// 这里锁住的是「拼接出来的键能被 EF 翻译成 SQL」——
    /// 一旦退化成内存过滤，分页的 total 就会失真。
    /// </summary>
    [Fact]
    public async Task 按名称过滤应能翻译成SQL而非退化到内存()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, Logger<AppDbContext>());

            db.TrashItems.AddRange(
                new TrashItem
                {
                    Kind = nameof(TrashKind.Template), Project = "P1", Check = "C1", FileName = "a.docx",
                    OriginalRelativePath = "Templates/P1/C1/a.docx", TrashRelativePath = "_trash/x/a.docx",
                },
                new TrashItem
                {
                    Kind = nameof(TrashKind.Template), Project = "P1", Check = "C2", FileName = "b.docx",
                    OriginalRelativePath = "Templates/P1/C2/b.docx", TrashRelativePath = "_trash/x/b.docx",
                });
            await db.SaveChangesAsync();

            var allowed = new List<string> { UserAccess.ScopeKey("P1", "C1") };

            // 若 EF 翻译失败，这里会抛 InvalidOperationException 而不是静默返回错数据
            var rows = await db.TrashItems
                .AsNoTracking()
                .Where(t => allowed.Contains(t.Project + "\u001f" + t.Check))
                .ToListAsync();

            Assert.Single(rows);
            Assert.Equal("C1", rows[0].Check);
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public void 令牌哈希应稳定且区分大小写()
    {
        const string token = "otk_abcDEF123";

        var first = ApiTokenService.Hash(token);
        var second = ApiTokenService.Hash(token);

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
        Assert.NotEqual(ApiTokenService.Hash(token), ApiTokenService.Hash(token.ToUpperInvariant()));
    }

    [Fact]
    public void 密码哈希应可验证且不同盐值产生不同结果()
    {
        var a = PasswordHasher.Hash("Passw0rd!");
        var b = PasswordHasher.Hash("Passw0rd!");

        Assert.NotEqual(a, b);
        Assert.True(PasswordHasher.Verify(a, "Passw0rd!"));
        Assert.False(PasswordHasher.Verify(a, "wrong"));
        Assert.False(PasswordHasher.Verify(null, "Passw0rd!"));
        Assert.False(PasswordHasher.Verify("garbage.format.here", "Passw0rd!"));
    }
}
