using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Data;
using OfficeTool.Core.Models;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 授权表的数据库级约束。
///
/// 这一条只能靠数据库兜：三列唯一索引 (GroupId, ProjectId, CheckId) 对
/// <c>CheckId IS NULL</c> 的项目级授权**完全不生效**（SQL 里 NULL 互不相等），
/// 于是同一个组可以对同一个项目插入任意多条项目级授权 —— 界面上看不出来，
/// 取消一条另一条仍然生效，「这个人的权限是谁给的」从此说不清。
///
/// 迁移 0004 补了一条过滤唯一索引来堵住它，这里锁住行为。
/// </summary>
public class AclEntryConstraintTests
{
    [Fact]
    public async Task 同一组对同一项目的项目级授权只能有一条()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officetool-acl-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        try
        {
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var group = new Group { Name = "g1", DisplayName = "检测组", Source = UserSource.Local };
            var project = new Project { Name = "QL1" };
            db.Groups.Add(group);
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = project.Id, CheckId = null, Level = AccessLevel.Read,
            });
            await db.SaveChangesAsync();

            db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = project.Id, CheckId = null, Level = AccessLevel.Manage,
            });

            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task 检项级授权不受项目级约束影响()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officetool-acl2-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={path}")
            .Options;

        try
        {
            await using var db = new AppDbContext(options);
            await db.Database.EnsureCreatedAsync();

            var group = new Group { Name = "g1", DisplayName = "检测组", Source = UserSource.Local };
            var project = new Project { Name = "QL1" };
            db.Groups.Add(group);
            db.Projects.Add(project);
            await db.SaveChangesAsync();

            var c1 = new CheckItem { ProjectId = project.Id, Name = "SEC" };
            var c2 = new CheckItem { ProjectId = project.Id, Name = "IQC" };
            db.Checks.AddRange(c1, c2);
            await db.SaveChangesAsync();

            // 项目级一条 + 两个检项各一条：三者合法共存
            db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = project.Id, CheckId = null, Level = AccessLevel.Read,
            });
            db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = project.Id, CheckId = c1.Id, Level = AccessLevel.Manage,
            });
            db.AclEntries.Add(new AclEntry
            {
                GroupId = group.Id, ProjectId = project.Id, CheckId = c2.Id, Level = AccessLevel.Write,
            });
            await db.SaveChangesAsync();

            Assert.Equal(3, await db.AclEntries.CountAsync());
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
