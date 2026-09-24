using System.Security.Claims;
using Microsoft.AspNetCore.Http;
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
/// 权限管理的行为约束。
///
/// 这一组测试锁的是「错了不会崩溃、只会静默失效」的那些规则：
/// 缓存没失效（改了不生效）、把自己锁死（界面内无法自救）、
/// 往域组里手工加人（当时生效、第二天被同步冲掉）。
/// 它们都不会抛异常，只能靠断言守住。
/// </summary>
public class AdminServiceTests
{
    private static (AppDbContext Db, string Path) CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officetool-admin-{Guid.NewGuid():N}.db");
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

    private static RequestContext ContextFor(string? userName)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };

        if (userName is not null)
        {
            accessor.HttpContext.User = new ClaimsPrincipal(
                new ClaimsIdentity([new Claim(ClaimTypes.Name, userName)], "Test"));
        }

        return new RequestContext(accessor);
    }

    private sealed class Harness : IDisposable
    {
        public Harness(string? asUser, bool authEnabled = true)
        {
            var (db, path) = CreateDb();
            Db = db;
            Path = path;

            Db.Database.EnsureCreated();

            Options = new AuthOptions { Enabled = authEnabled };
            Cache = new MemoryCache(new MemoryCacheOptions());
            var logs = new OperationLogService(Db, ContextFor(asUser), Logger<OperationLogService>());
            Access = new AccessControlService(Db, Cache, Options, Logger<AccessControlService>());
            Admin = new AdminService(Db, Options, ContextFor(asUser), Access, logs, Logger<AdminService>());
        }

        public AppDbContext Db { get; }
        public string Path { get; }
        public AuthOptions Options { get; }
        public IMemoryCache Cache { get; }
        public AccessControlService Access { get; }
        public AdminService Admin { get; }

        public void Dispose() => Cleanup(Db, Path);
    }

    /// <summary>播种一名超管、一个本地组、一名普通成员、一个含两个检项的项目。</summary>
    private static async Task<(int Admin, int Member, int Group, int Project, int C1, int C2)>
        SeedAsync(AppDbContext db)
    {
        var admin = new User
        {
            UserName = "root", DisplayName = "管理员", Source = UserSource.Local, IsSystemAdmin = true,
        };
        var member = new User { UserName = "alice", DisplayName = "Alice", Source = UserSource.Local };
        var group = new Group { Name = "detectors", DisplayName = "检测组", Source = UserSource.Local };

        db.Users.AddRange(admin, member);
        db.Groups.Add(group);
        await db.SaveChangesAsync();

        db.UserGroups.AddRange(
            new UserGroup { UserId = member.Id, GroupId = group.Id });
        await db.SaveChangesAsync();

        var project = new Project { Name = "P1" };
        db.Projects.Add(project);
        await db.SaveChangesAsync();

        var c1 = new CheckItem { ProjectId = project.Id, Name = "C1" };
        var c2 = new CheckItem { ProjectId = project.Id, Name = "C2" };
        db.Checks.AddRange(c1, c2);
        await db.SaveChangesAsync();

        return (admin.Id, member.Id, group.Id, project.Id, c1.Id, c2.Id);
    }

    // ── 入口校验 ──────────────────────────────────────────────────

    [Fact]
    public async Task 非系统管理员调用管理接口应被拒绝()
    {
        using var h = new Harness("alice");
        await SeedAsync(h.Db);

        await Assert.ThrowsAsync<AccessDeniedException>(
            () => h.Admin.ListGroupsAsync());
    }

    [Fact]
    public async Task 鉴权未启用时管理接口应拒绝而非放行()
    {
        using var h = new Harness("root", authEnabled: false);
        await SeedAsync(h.Db);

        // 一旦放行，管理员会在「以为自己改了权限」的认知下操作，
        // 而实际上全站裸奔 —— 必须明确告知而不是静默成功。
        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.ListGroupsAsync());
    }

    [Fact]
    public async Task 未认证身份应被拒绝()
    {
        using var h = new Harness(null);
        await SeedAsync(h.Db);

        await Assert.ThrowsAsync<AccessDeniedException>(() => h.Admin.HealthAsync());
    }

    // ── 本地账户 ──────────────────────────────────────────────────

    [Fact]
    public async Task 创建本地账户应返回可校验的明文密码()
    {
        using var h = new Harness("root");
        await SeedAsync(h.Db);

        var created = await h.Admin.CreateUserAsync(new CreateUserRequest
        {
            UserName = "bob",
            DisplayName = "Bob",
        });

        Assert.False(string.IsNullOrWhiteSpace(created.Password));
        Assert.Equal("bob", created.User.UserName);
        Assert.Equal(nameof(UserSource.Local), created.User.Source);

        var stored = await h.Db.Users.AsNoTracking().FirstAsync(x => x.UserName == "bob");
        Assert.True(PasswordHasher.Verify(stored.PasswordHash, created.Password));
        Assert.True(stored.MustChangePassword);
    }

    [Fact]
    public async Task 重复登录名应冲突()
    {
        using var h = new Harness("root");
        await SeedAsync(h.Db);

        await h.Admin.CreateUserAsync(new CreateUserRequest { UserName = "bob" });

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Admin.CreateUserAsync(new CreateUserRequest { UserName = "bob" }));
    }

    [Fact]
    public async Task 删除域账户应被拒绝()
    {
        using var h = new Harness("root");
        await SeedAsync(h.Db);

        var adUser = new User { UserName = "域人", Source = UserSource.Ad };
        h.Db.Users.Add(adUser);
        await h.Db.SaveChangesAsync();

        // 域用户删掉后下次 SSO 会重新建档，等于什么都没做；
        // 真正想阻止应当用「禁用」。
        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.DeleteUserAsync(adUser.Id));
    }

    // ── 防把自己锁死 ──────────────────────────────────────────────

    [Fact]
    public async Task 唯一的超管不能禁用自己()
    {
        using var h = new Harness("root");
        var (admin, _, _, _, _, _) = await SeedAsync(h.Db);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Admin.UpdateUserAsync(admin, new UpdateUserRequest { IsEnabled = false }));
    }

    [Fact]
    public async Task 唯一的超管不能取消自己的超管身份()
    {
        using var h = new Harness("root");
        var (admin, _, _, _, _, _) = await SeedAsync(h.Db);

        await Assert.ThrowsAsync<ConflictException>(
            () => h.Admin.UpdateUserAsync(admin, new UpdateUserRequest { IsSystemAdmin = false }));
    }

    [Fact]
    public async Task 唯一的超管不能删除自己()
    {
        using var h = new Harness("root");
        var (admin, _, _, _, _, _) = await SeedAsync(h.Db);

        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.DeleteUserAsync(admin));
    }

    [Fact]
    public async Task 存在第二名超管时可以移除自己()
    {
        using var h = new Harness("root");
        var (admin, _, _, _, _, _) = await SeedAsync(h.Db);

        var second = new User
        {
            UserName = "root2", Source = UserSource.Local, IsSystemAdmin = true, DisplayName = "备用管理员",
        };
        h.Db.Users.Add(second);
        await h.Db.SaveChangesAsync();

        // 有了备份就该允许，否则这套禁令会变成「永远无法撤销的超管」
        await h.Admin.UpdateUserAsync(admin, new UpdateUserRequest { IsSystemAdmin = false });

        var updated = await h.Db.Users.AsNoTracking().FirstAsync(x => x.Id == admin);
        Assert.False(updated.IsSystemAdmin);
    }

    // ── 缓存失效 ──────────────────────────────────────────────────

    [Fact]
    public async Task 修改授权等级后当事人权限应立即生效()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, _, _) = await SeedAsync(h.Db);

        var entry = await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = nameof(AccessLevel.Read),
        });

        // 先解析一次并落到缓存里
        Assert.True((await h.Access.ResolveByUserNameAsync("alice")).Has(
            await CheckIdAsync(h.Db, "C1"), AccessLevel.Read));

        await h.Admin.UpdateAclAsync(entry.Id, new UpdateAclRequest { Level = nameof(AccessLevel.Manage) });

        // 缓存没失效的话这里仍是 Read —— 管理员会认为「改了没生效」
        var after = await h.Access.ResolveByUserNameAsync("alice");
        Assert.True(after.Has(await CheckIdAsync(h.Db, "C1"), AccessLevel.Manage));
    }

    [Fact]
    public async Task 禁用组后其成员应立刻失去授权()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, _, _) = await SeedAsync(h.Db);

        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = nameof(AccessLevel.Manage),
        });

        var c1 = await CheckIdAsync(h.Db, "C1");
        Assert.True((await h.Access.ResolveByUserNameAsync("alice")).IsVisible(c1));

        await h.Admin.UpdateGroupAsync(groupId, new UpdateGroupRequest { IsEnabled = false });

        // 「停用组」必须真的生效，否则这个开关就是个摆设，比不做更危险
        Assert.False((await h.Access.ResolveByUserNameAsync("alice")).IsVisible(c1));
    }

    [Fact]
    public async Task 移出组后当事人的权限应立刻消失()
    {
        using var h = new Harness("root");
        var (_, memberId, groupId, projectId, _, _) = await SeedAsync(h.Db);

        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = nameof(AccessLevel.Write),
        });

        var c1 = await CheckIdAsync(h.Db, "C1");
        Assert.True((await h.Access.ResolveByUserNameAsync("alice")).Has(c1, AccessLevel.Write));

        await h.Admin.RemoveMemberAsync(groupId, memberId);

        Assert.False((await h.Access.ResolveByUserNameAsync("alice")).IsVisible(c1));
    }

    // ── 域组的边界 ────────────────────────────────────────────────

    [Fact]
    public async Task 向域组手工加成员应被拒绝()
    {
        using var h = new Harness("root");
        var (_, memberId, _, _, _, _) = await SeedAsync(h.Db);

        var adGroup = new Group { Name = "S-1-5-21-1000", Source = UserSource.Ad, DisplayName = "域检测组" };
        h.Db.Groups.Add(adGroup);
        await h.Db.SaveChangesAsync();

        // 域组成员以 LDAP 为准，手工加的人会在其下次 SSO 登录时被整体覆盖
        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.AddMemberAsync(adGroup.Id, memberId));
        await Assert.ThrowsAsync<ConflictException>(
            () => h.Admin.SetMembersAsync(adGroup.Id, new SetMembersRequest { UserIds = [memberId] }));
    }

    [Fact]
    public async Task 删除域组应被拒绝()
    {
        using var h = new Harness("root");
        await SeedAsync(h.Db);

        var adGroup = new Group { Name = "域组", Source = UserSource.Ad };
        h.Db.Groups.Add(adGroup);
        await h.Db.SaveChangesAsync();

        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.DeleteGroupAsync(adGroup.Id));
    }

    // ── 授权条目 ──────────────────────────────────────────────────

    [Fact]
    public async Task 同一组合在目标上重复授权应冲突()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, c1, _) = await SeedAsync(h.Db);

        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = nameof(AccessLevel.Read),
        });

        // 刻意不做静默覆盖：重复创建几乎一定是重复点击，覆盖会让已有配置被意外抬高/降低
        await Assert.ThrowsAsync<ConflictException>(() => h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = nameof(AccessLevel.Manage),
        }));
    }

    [Fact]
    public async Task 检项必须属于指定项目()
    {
        using var h = new Harness("root");
        var (_, _, groupId, _, c1, _) = await SeedAsync(h.Db);

        var other = new Project { Name = "P2" };
        h.Db.Projects.Add(other);
        await h.Db.SaveChangesAsync();

        // 不校验的话会产生一条永远匹配不上的授权，管理页上看着正常但毫无作用
        await Assert.ThrowsAsync<NotFoundException>(() => h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = other.Id, CheckId = c1, Level = nameof(AccessLevel.Read),
        }));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("SuperAdmin")]
    // 关键：Enum.TryParse 会把纯数字解析成一个「不存在」的枚举值且不报错，
    // 存进 SQLite 后就是一条行为不可预测的授权。
    [InlineData("999")]
    public async Task 非法权限等级应被拒绝(string level)
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, _, _) = await SeedAsync(h.Db);

        await Assert.ThrowsAsync<BadRequestException>(() => h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = level,
        }));
    }

    [Fact]
    public async Task 合法权限等级应大小写不敏感地接受()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, _, _) = await SeedAsync(h.Db);

        var entry = await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = "manage",
        });

        Assert.Equal(nameof(AccessLevel.Manage), entry.Level);
    }

    // ── 展开与自检 ────────────────────────────────────────────────

    [Fact]
    public async Task 有效权限展开应标注授予来源()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, c1, _) = await SeedAsync(h.Db);

        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = nameof(AccessLevel.Read),
        });
        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = c1, Level = nameof(AccessLevel.Manage),
        });

        var dto = await h.Admin.EffectiveAccessAsync("alice");

        var c1Dto = Assert.Single(dto.Checks.Where(x => x.Check == "C1"));
        var c2Dto = Assert.Single(dto.Checks.Where(x => x.Check == "C2"));

        // 检项级覆盖项目级
        Assert.Equal(nameof(AccessLevel.Manage), c1Dto.Level);
        Assert.Contains("本检项", c1Dto.GrantedBy);

        Assert.Equal(nameof(AccessLevel.Read), c2Dto.Level);
        Assert.Contains("整个项目", c2Dto.GrantedBy);
    }

    [Fact]
    public async Task 自检应报告无人管理的项目()
    {
        using var h = new Harness("root");
        var (_, _, groupId, projectId, _, _) = await SeedAsync(h.Db);

        var before = await h.Admin.HealthAsync();
        Assert.Contains(before.UnmanagedProjects, x => x.ProjectName == "P1");
        Assert.Contains(before.Warnings, w => w.Contains("系统管理员"));

        await h.Admin.CreateAclAsync(new CreateAclRequest
        {
            GroupId = groupId, ProjectId = projectId, CheckId = null, Level = nameof(AccessLevel.Read),
        });

        // 只读不算「有人管理」：删除、回收站、元数据同步都无从下手
        var stillBad = await h.Admin.HealthAsync();
        Assert.Contains(stillBad.UnmanagedProjects, x => x.ProjectName == "P1");

        var entry = await h.Db.AclEntries.AsNoTracking().FirstAsync();
        await h.Admin.UpdateAclAsync(entry.Id, new UpdateAclRequest { Level = nameof(AccessLevel.Manage) });

        var after = await h.Admin.HealthAsync();
        Assert.DoesNotContain(after.UnmanagedProjects, x => x.ProjectName == "P1");
    }

    private static Task<int> CheckIdAsync(AppDbContext db, string name) =>
        db.Checks.AsNoTracking().Where(c => c.Name == name).Select(c => c.Id).FirstAsync();
}
