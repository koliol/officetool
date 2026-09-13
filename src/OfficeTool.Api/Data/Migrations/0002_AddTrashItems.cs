using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using OfficeTool.Api.Data;

namespace OfficeTool.Api.Data.Migrations;

/// <summary>回收站登记表。文件本体在共享盘 <c>_trash</c>，此表只存索引。</summary>
[DbContext(typeof(AppDbContext))]
[Migration("0002_AddTrashItems")]
public partial class AddTrashItems : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TrashItems",
            columns: table => new
            {
                Id = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                Project = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                Check = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                OriginalRelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                TrashRelativePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                Size = table.Column<long>(type: "INTEGER", nullable: false),
                DeletedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                DeletedByIp = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_TrashItems", x => x.Id);
            });

        migrationBuilder.CreateIndex(name: "IX_TrashItems_DeletedAt", table: "TrashItems", column: "DeletedAt");
        migrationBuilder.CreateIndex(name: "IX_TrashItems_Kind_Project_Check", table: "TrashItems", columns: new[] { "Kind", "Project", "Check" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "TrashItems");
    }
}
