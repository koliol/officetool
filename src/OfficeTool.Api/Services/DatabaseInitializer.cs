using Microsoft.EntityFrameworkCore;
using OfficeTool.Api.Data;

namespace OfficeTool.Api.Services;

/// <summary>
/// 数据库初始化：EF Migrations + 旧库 Baseline。
///
/// 背景：历史版本用 <c>EnsureCreated()</c> 建库，库内<strong>没有</strong>
/// <c>__EFMigrationsHistory</c>。EF 的 <c>Migrate()</c> 对这种库会尝试重跑
/// 建表迁移导致冲突。因此：
/// <list type="number">
///   <item>空库（无 Projects 表）→ 直接 <c>Migrate()</c> 走完整迁移链</item>
///   <item>已有业务表且无迁移历史 → 仅把 <c>0001_InitialCreate</c> 记入历史（Baseline），再 <c>Migrate()</c> 应用增量</item>
///   <item>已有迁移历史 → 直接 <c>Migrate()</c></item>
/// </list>
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>与迁移程序集里的 ProductVersion 保持一致即可，仅作记录。</summary>
    private const string ProductVersion = "8.0.31";

    public static async Task InitializeAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var hasHistory = await HasTableAsync(db, "__EFMigrationsHistory", ct);
        var hasProjects = await HasTableAsync(db, "Projects", ct);

        if (hasProjects && !hasHistory)
        {
            var allMigrations = db.Database.GetMigrations().ToList();
            if (allMigrations.Count == 0)
            {
                throw new InvalidOperationException("未找到任何 EF 迁移，无法对旧库做 Baseline。");
            }

            var initial = allMigrations[0];
            await EnsureHistoryTableAsync(db, ct);
            await InsertHistoryAsync(db, initial, ct);
            logger.LogInformation(
                "检测到 EnsureCreated 旧库，已 Baseline 标记迁移 {Migration}；后续增量迁移将自动应用。", initial);
        }

        await db.Database.MigrateAsync(ct);

        // SQLite WAL（设计文档 §6）。Migrate 之后执行，避免与建表事务交错。
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
    }

    private static async Task<bool> HasTableAsync(AppDbContext db, string tableName, CancellationToken ct)
    {
        // SQLite：sqlite_master 查询；用 SqlQueryRaw 以便绑定参数避免注入。
        var count = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*) AS "Value"
            FROM sqlite_master
            WHERE type = 'table' AND name = {0}
            """,
            tableName).SingleAsync(ct);
        return count > 0;
    }

    private static async Task EnsureHistoryTableAsync(AppDbContext db, CancellationToken ct)
    {
        await db.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL
            );
            """,
            ct);
    }

    private static async Task InsertHistoryAsync(AppDbContext db, string migrationId, CancellationToken ct)
    {
        // 幂等：已存在则跳过
        var exists = await db.Database.SqlQueryRaw<int>(
            """
            SELECT COUNT(*) AS "Value"
            FROM "__EFMigrationsHistory"
            WHERE "MigrationId" = {0}
            """,
            migrationId).SingleAsync(ct);

        if (exists > 0)
        {
            return;
        }

        await db.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
            VALUES ({0}, {1});
            """,
            migrationId,
            ProductVersion);
    }
}
