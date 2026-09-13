using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OfficeTool.Api.Data;

namespace OfficeTool.Api.Data.Migrations;

/// <summary>
/// 初始结构。与 <c>AppDbContext.OnModelCreating</c> 及历史 <c>EnsureCreated()</c> 对齐。
/// 旧库升级时由 <c>DatabaseInitializer</c> Baseline 标记为已应用，不会重跑建表。
/// </summary>
[DbContext(typeof(AppDbContext))]
[Migration("0001_InitialCreate")]
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "Projects",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedByIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Projects", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Checks",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                Name = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedByIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Checks", x => x.Id);
                table.ForeignKey(
                    name: "FK_Checks_Projects_ProjectId",
                    column: x => x.ProjectId,
                    principalTable: "Projects",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "Templates",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                CheckId = table.Column<int>(type: "INTEGER", nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                ModifiedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                UploadedByIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Templates", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "Documents",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                CheckId = table.Column<int>(type: "INTEGER", nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                SourceTemplatePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                CreatedByIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Documents", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "RuleNotes",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                RelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_RuleNotes", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "OperationLogs",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Action = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                TargetPath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Ip = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                UserAgent = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                Result = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                Message = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OperationLogs", x => x.Id);
            });

        migrationBuilder.CreateIndex(name: "IX_Checks_ProjectId_Name", table: "Checks", columns: new[] { "ProjectId", "Name" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Documents_ProjectId_CheckId_FileName", table: "Documents", columns: new[] { "ProjectId", "CheckId", "FileName" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Documents_CreatedAt", table: "Documents", column: "CreatedAt");
        migrationBuilder.CreateIndex(name: "IX_OperationLogs_CreatedAt", table: "OperationLogs", column: "CreatedAt");
        migrationBuilder.CreateIndex(name: "IX_Projects_Name", table: "Projects", column: "Name", unique: true);
        migrationBuilder.CreateIndex(name: "IX_RuleNotes_RelativePath", table: "RuleNotes", column: "RelativePath", unique: true);
        migrationBuilder.CreateIndex(name: "IX_Templates_ProjectId_CheckId_FileName", table: "Templates", columns: new[] { "ProjectId", "CheckId", "FileName" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_Templates_ModifiedAt", table: "Templates", column: "ModifiedAt");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "Checks");
        migrationBuilder.DropTable(name: "Documents");
        migrationBuilder.DropTable(name: "OperationLogs");
        migrationBuilder.DropTable(name: "Projects");
        migrationBuilder.DropTable(name: "RuleNotes");
        migrationBuilder.DropTable(name: "Templates");
    }
}
