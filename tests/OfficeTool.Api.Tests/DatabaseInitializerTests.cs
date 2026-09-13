using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OfficeTool.Api.Data;
using OfficeTool.Api.Services;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 数据库升级路径：空库 Migrate、旧 EnsureCreated 库 Baseline、幂等。
/// 使用临时 SQLite 文件，互不干扰。
/// </summary>
public class DatabaseInitializerTests
{
    private static (AppDbContext Db, string Path) CreateDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"officetool-test-{Guid.NewGuid():N}.db");
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

    private static ILogger CreateLogger() =>
        LoggerFactory.Create(b => { }).CreateLogger("test");

    [Fact]
    public async Task 空库应走完整迁移并建出全部业务表()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, CreateLogger());

            Assert.True(await HasTableAsync(db, "Projects"));
            Assert.True(await HasTableAsync(db, "Checks"));
            Assert.True(await HasTableAsync(db, "Templates"));
            Assert.True(await HasTableAsync(db, "Documents"));
            Assert.True(await HasTableAsync(db, "RuleNotes"));
            Assert.True(await HasTableAsync(db, "OperationLogs"));
            Assert.True(await HasTableAsync(db, "TrashItems"));
            Assert.True(await HasTableAsync(db, "__EFMigrationsHistory"));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 旧EnsureCreated库应Baseline后补上TrashItems()
    {
        var (db, path) = CreateDb();
        try
        {
            // 模拟历史 EnsureCreated：业务表齐全、无迁移历史
            await db.Database.ExecuteSqlRawAsync(
                """
                CREATE TABLE "Projects" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Projects" PRIMARY KEY AUTOINCREMENT,
                    "Name" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedByIp" TEXT NULL
                );
                CREATE TABLE "Checks" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Checks" PRIMARY KEY AUTOINCREMENT,
                    "ProjectId" INTEGER NOT NULL,
                    "Name" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedByIp" TEXT NULL
                );
                CREATE TABLE "Templates" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Templates" PRIMARY KEY AUTOINCREMENT,
                    "ProjectId" INTEGER NOT NULL,
                    "CheckId" INTEGER NOT NULL,
                    "FileName" TEXT NOT NULL,
                    "RelativePath" TEXT NOT NULL,
                    "Extension" TEXT NOT NULL,
                    "Size" INTEGER NOT NULL,
                    "ModifiedAt" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UploadedByIp" TEXT NULL
                );
                CREATE TABLE "Documents" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_Documents" PRIMARY KEY AUTOINCREMENT,
                    "ProjectId" INTEGER NOT NULL,
                    "CheckId" INTEGER NOT NULL,
                    "FileName" TEXT NOT NULL,
                    "RelativePath" TEXT NOT NULL,
                    "SourceTemplatePath" TEXT NOT NULL,
                    "Extension" TEXT NOT NULL,
                    "Size" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "CreatedByIp" TEXT NULL
                );
                CREATE TABLE "RuleNotes" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_RuleNotes" PRIMARY KEY AUTOINCREMENT,
                    "RelativePath" TEXT NOT NULL,
                    "Note" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE TABLE "OperationLogs" (
                    "Id" INTEGER NOT NULL CONSTRAINT "PK_OperationLogs" PRIMARY KEY AUTOINCREMENT,
                    "Action" TEXT NOT NULL,
                    "TargetPath" TEXT NOT NULL,
                    "Ip" TEXT NULL,
                    "UserAgent" TEXT NULL,
                    "Result" TEXT NOT NULL,
                    "Message" TEXT NULL,
                    "CreatedAt" TEXT NOT NULL
                );
                INSERT INTO "Projects" ("Name", "CreatedAt") VALUES ('LEGACY', '2026-01-01');
                """);

            await DatabaseInitializer.InitializeAsync(db, CreateLogger());

            Assert.True(await HasTableAsync(db, "TrashItems"));
            Assert.True(await HasTableAsync(db, "RuleNotes"));
            Assert.True(await HasTableAsync(db, "__EFMigrationsHistory"));

            var name = await db.Projects.Select(p => p.Name).FirstAsync();
            Assert.Equal("LEGACY", name);

            var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
            Assert.Contains("0001_InitialCreate", applied);
            Assert.Contains("0002_AddTrashItems", applied);
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    [Fact]
    public async Task 重复初始化应幂等()
    {
        var (db, path) = CreateDb();
        try
        {
            await DatabaseInitializer.InitializeAsync(db, CreateLogger());
            await DatabaseInitializer.InitializeAsync(db, CreateLogger());
            Assert.True(await HasTableAsync(db, "TrashItems"));
        }
        finally
        {
            Cleanup(db, path);
        }
    }

    private static async Task<bool> HasTableAsync(AppDbContext db, string name)
    {
        var count = await db.Database.SqlQueryRaw<int>(
            """SELECT COUNT(*) AS "Value" FROM sqlite_master WHERE type='table' AND name={0}""",
            name).SingleAsync();
        return count > 0;
    }
}
