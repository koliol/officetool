using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfficeTool.Api.Data.Migrations
{
    /// <summary>
    /// 清理从未写入过的 IP 列。
    ///
    /// Projects/Checks 的 CreatedByIp、Templates 的 UploadedByIp、Documents 的 CreatedByIp、
    /// TrashItems 的 DeletedByIp —— 建表时"顺手"加上，但代码里从来只有 null：
    /// 真正的审计落在 OperationLogs（有 IP、有 UA、有操作人），这些列留着只会让人
    /// 以为它们有意义。SQLite ≥3.35 的 DROP COLUMN 由 EF 自动转成表重建。
    /// </summary>
    [DbContext(typeof(OfficeTool.Api.Data.AppDbContext))]
    [Migration("0006_DropUnusedIpColumns")]
    public partial class DropUnusedIpColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 注意：EF 的 SQLite 迁移生成器不支持 DropColumnOperation（会抛 NotSupportedException）。
            // 随包分发的 SQLite（≥3.35）原生支持 ALTER TABLE ... DROP COLUMN，所以直接下发原生 SQL。
            migrationBuilder.Sql("ALTER TABLE \"Projects\" DROP COLUMN \"CreatedByIp\";");
            migrationBuilder.Sql("ALTER TABLE \"Checks\" DROP COLUMN \"CreatedByIp\";");
            migrationBuilder.Sql("ALTER TABLE \"Templates\" DROP COLUMN \"UploadedByIp\";");
            migrationBuilder.Sql("ALTER TABLE \"Documents\" DROP COLUMN \"CreatedByIp\";");
            migrationBuilder.Sql("ALTER TABLE \"TrashItems\" DROP COLUMN \"DeletedByIp\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"Projects\" ADD COLUMN \"CreatedByIp\" TEXT;");
            migrationBuilder.Sql("ALTER TABLE \"Checks\" ADD COLUMN \"CreatedByIp\" TEXT;");
            migrationBuilder.Sql("ALTER TABLE \"Templates\" ADD COLUMN \"UploadedByIp\" TEXT;");
            migrationBuilder.Sql("ALTER TABLE \"Documents\" ADD COLUMN \"CreatedByIp\" TEXT;");
            migrationBuilder.Sql("ALTER TABLE \"TrashItems\" ADD COLUMN \"DeletedByIp\" TEXT;");
        }
    }
}
