using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace OfficeTool.Api.Data.Migrations
{
    /// <summary>
    /// ELN 主体结构：表单模板、记录、记录附件、提取规则、规则生效范围、解析审计。
    ///
    /// 手写而非 <c>dotnet ef migrations add</c> 生成：本仓库的迁移 ID 是
    /// <c>0001_…</c> 这类序号式（历史原因），EF 的脚手架在解析这类 ID 时会抛
    /// <c>startIndex cannot be larger than length of string</c>，无法生成新迁移。
    /// 表结构以模型（AppDbContext）为准，这份迁移只负责把库带到同一形态。
    ///
    /// 注意：ModelSnapshot 未同步更新（同上原因）。若将来恢复脚手架生成，
    /// 请先手工对齐快照，否则会生成一份"重复建表"的迁移。
    /// </summary>
    [DbContext(typeof(OfficeTool.Api.Data.AppDbContext))]
    [Migration("0005_AddElnCore")]
    public partial class AddElnCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // ── 表单模板 ────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "FormTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    SchemaJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormTemplates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FormTemplates_Code",
                table: "FormTemplates",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormTemplates_IsEnabled_Category",
                table: "FormTemplates",
                columns: new[] { "IsEnabled", "Category" });

            // ── 提取规则定义 ────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ExtractionRuleDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Code = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    Category = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleJson = table.Column<string>(type: "TEXT", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionRuleDefinitions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionRuleDefinitions_Code",
                table: "ExtractionRuleDefinitions",
                column: "Code",
                unique: true);

            // ── 记录 ────────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "FormRecords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TemplateName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    TemplateVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    TemplateSchemaJson = table.Column<string>(type: "TEXT", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckId = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordNo = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    DataJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SubmittedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SubmittedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    VoidedAt = table.Column<DateTime>(type: "TEXT", nullable: true),
                    VoidReason = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FormRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FormRecords_Checks_CheckId",
                        column: x => x.CheckId,
                        principalTable: "Checks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FormRecords_FormTemplates_TemplateId",
                        column: x => x.TemplateId,
                        principalTable: "FormTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FormRecords_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FormRecords_CheckId_Status_CreatedAt",
                table: "FormRecords",
                columns: new[] { "CheckId", "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_FormRecords_RecordNo",
                table: "FormRecords",
                column: "RecordNo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FormRecords_ProjectId",
                table: "FormRecords",
                column: "ProjectId");

            migrationBuilder.CreateIndex(
                name: "IX_FormRecords_TemplateId",
                table: "FormRecords",
                column: "TemplateId");

            // ── 记录附件 ────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "RecordFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    Kind = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    RelativePath = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: false),
                    Extension = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Size = table.Column<long>(type: "INTEGER", nullable: false),
                    ContentType = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false),
                    UploadedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UploadedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecordFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecordFiles_FormRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "FormRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_RecordFiles_RecordId",
                table: "RecordFiles",
                column: "RecordId");

            // ── 规则生效范围 ────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ExtractionRuleScopes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    ProjectId = table.Column<int>(type: "INTEGER", nullable: false),
                    CheckId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionRuleScopes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractionRuleScopes_Checks_CheckId",
                        column: x => x.CheckId,
                        principalTable: "Checks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtractionRuleScopes_ExtractionRuleDefinitions_RuleId",
                        column: x => x.RuleId,
                        principalTable: "ExtractionRuleDefinitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExtractionRuleScopes_Projects_ProjectId",
                        column: x => x.ProjectId,
                        principalTable: "Projects",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionRuleScopes_CheckId",
                table: "ExtractionRuleScopes",
                column: "CheckId");

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionRuleScopes_RuleId_ProjectId_CheckId",
                table: "ExtractionRuleScopes",
                columns: new[] { "RuleId", "ProjectId", "CheckId" },
                unique: true);

            // 与 AclEntries 同一个坑：三列唯一索引对 CheckId IS NULL 不生效，
            // 项目级范围必须靠这条过滤唯一索引兜住
            migrationBuilder.CreateIndex(
                name: "IX_ExtractionRuleScopes_Rule_Project_ProjectWide",
                table: "ExtractionRuleScopes",
                columns: new[] { "RuleId", "ProjectId" },
                unique: true,
                filter: "\"CheckId\" IS NULL");

            // ── 解析审计 ────────────────────────────────────────────────
            migrationBuilder.CreateTable(
                name: "ExtractionRuns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RecordId = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleDefinitionId = table.Column<int>(type: "INTEGER", nullable: false),
                    RuleCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RuleVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    RecordFileId = table.Column<int>(type: "INTEGER", nullable: true),
                    SourceFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    MatchedFieldCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawText = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExtractionRuns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExtractionRuns_FormRecords_RecordId",
                        column: x => x.RecordId,
                        principalTable: "FormRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ExtractionRuns_RecordId",
                table: "ExtractionRuns",
                column: "RecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "ExtractionRuns");
            migrationBuilder.DropTable(name: "RecordFiles");
            migrationBuilder.DropTable(name: "ExtractionRuleScopes");
            migrationBuilder.DropTable(name: "FormRecords");
            migrationBuilder.DropTable(name: "ExtractionRuleDefinitions");
            migrationBuilder.DropTable(name: "FormTemplates");
        }
    }
}
