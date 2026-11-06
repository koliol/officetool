using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfficeTool.Api.Data.Migrations
{
    /// <summary>
    /// 给「项目级授权」补一条数据库级唯一约束。
    ///
    /// 背景：原有唯一索引是 (GroupId, ProjectId, CheckId)，而项目级授权的
    /// CheckId 为 NULL。SQL 里 NULL 互不相等，SQLite 也照此实现 ——
    /// 同一个组可以对同一个项目插入任意多条项目级授权，既不报错也看不出来，
    /// 结果是「这个人的权限是谁给的」说不清、取消一条另一条仍然生效。
    ///
    /// 用过滤唯一索引精确表达：只在 CheckId IS NULL 的行上，要求 (GroupId, ProjectId) 唯一。
    /// 检项级授权仍由原索引负责，两者互不干扰。
    /// </summary>
    [DbContext(typeof(OfficeTool.Api.Data.AppDbContext))]
    [Migration("0004_AddProjectWideAclUniqueIndex")]
    public partial class AddProjectWideAclUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 历史库里可能已经有重复的项目级条目：先保留每个 (组, 项目) 的第一条
            // （最早创建的那条），否则建唯一索引会直接失败，升级中断。
            migrationBuilder.Sql(
                """
                DELETE FROM "AclEntries"
                WHERE "CheckId" IS NULL
                  AND "Id" NOT IN (
                      SELECT MIN("Id") FROM "AclEntries"
                      WHERE "CheckId" IS NULL
                      GROUP BY "GroupId", "ProjectId"
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_AclEntries_Group_Project_ProjectWide",
                table: "AclEntries",
                columns: new[] { "GroupId", "ProjectId" },
                unique: true,
                filter: "\"CheckId\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AclEntries_Group_Project_ProjectWide",
                table: "AclEntries");
        }
    }
}
