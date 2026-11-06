using Microsoft.EntityFrameworkCore;
using OfficeTool.Core.Models;

namespace OfficeTool.Api.Data;

/// <summary>EF Core 上下文（设计文档 §6.1）。默认 SQLite + WAL。</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Project> Projects => Set<Project>();

    public DbSet<CheckItem> Checks => Set<CheckItem>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<Document> Documents => Set<Document>();

    public DbSet<RuleNote> RuleNotes => Set<RuleNote>();

    public DbSet<OperationLog> OperationLogs => Set<OperationLog>();

    public DbSet<TrashItem> TrashItems => Set<TrashItem>();

    // ── 鉴权（Users / Groups / UserGroups / AclEntries）─────────────────

    public DbSet<User> Users => Set<User>();

    public DbSet<Group> Groups => Set<Group>();

    public DbSet<UserGroup> UserGroups => Set<UserGroup>();

    public DbSet<AclEntry> AclEntries => Set<AclEntry>();

    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    // ── ELN（表单模板 / 记录 / 规则 / 解析审计）─────────────────────────

    public DbSet<FormTemplate> FormTemplates => Set<FormTemplate>();

    public DbSet<FormRecord> FormRecords => Set<FormRecord>();

    public DbSet<RecordFile> RecordFiles => Set<RecordFile>();

    public DbSet<ExtractionRuleDefinition> ExtractionRuleDefinitions => Set<ExtractionRuleDefinition>();

    public DbSet<ExtractionRuleScope> ExtractionRuleScopes => Set<ExtractionRuleScope>();

    public DbSet<ExtractionRun> ExtractionRuns => Set<ExtractionRun>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Project>(entity =>
        {
            entity.ToTable("Projects");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.HasIndex(x => x.Name).IsUnique();
            entity.HasMany(x => x.Checks)
                .WithOne(x => x.Project!)
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CheckItem>(entity =>
        {
            entity.ToTable("Checks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<Template>(entity =>
        {
            entity.ToTable("Templates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Extension).HasMaxLength(16);
            entity.Property(x => x.UploadedByIp).HasMaxLength(64);
            entity.HasIndex(x => new { x.ProjectId, x.CheckId, x.FileName }).IsUnique();

            // 列表默认按修改时间倒序排。0001_InitialCreate 就在库里建了这个索引，
            // 但模型里一直没声明 —— 导致后续任何 migrations add 都会生成 DROP INDEX。
            // 这里补上声明，让模型与既有库结构对齐（见 docs/STATUS.md 第七节）。
            entity.HasIndex(x => x.ModifiedAt);
        });

        modelBuilder.Entity<Document>(entity =>
        {
            entity.ToTable("Documents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.SourceTemplatePath).HasMaxLength(500);
            entity.Property(x => x.Extension).HasMaxLength(16);
            entity.Property(x => x.CreatedByIp).HasMaxLength(64);
            entity.HasIndex(x => new { x.ProjectId, x.CheckId, x.FileName }).IsUnique();

            // 同 Templates.ModifiedAt：0001 建了索引但模型未声明，此处补声明对齐。
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<RuleNote>(entity =>
        {
            entity.ToTable("RuleNotes");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RelativePath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => x.RelativePath).IsUnique();
        });

        modelBuilder.Entity<TrashItem>(entity =>
        {
            entity.ToTable("TrashItems");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Project).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Check).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.OriginalRelativePath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.TrashRelativePath).HasMaxLength(500).IsRequired();
            entity.Property(x => x.DeletedByIp).HasMaxLength(64);
            entity.HasIndex(x => x.DeletedAt);
            entity.HasIndex(x => new { x.Kind, x.Project, x.Check });
        });

        modelBuilder.Entity<OperationLog>(entity =>
        {
            entity.ToTable("OperationLogs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Action).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TargetPath).HasMaxLength(500);
            entity.Property(x => x.Ip).HasMaxLength(64);
            entity.Property(x => x.UserAgent).HasMaxLength(512);
            entity.Property(x => x.Result).HasMaxLength(16);
            entity.Property(x => x.Message).HasMaxLength(2000);
            entity.HasIndex(x => x.CreatedAt);
        });

        // ── 鉴权实体 ──────────────────────────────────────────────────

        modelBuilder.Entity<User>(entity =>
        {
            entity.ToTable("Users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.UserName).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512);
            entity.HasIndex(x => x.UserName).IsUnique();
            entity.HasIndex(x => x.Source);
        });

        modelBuilder.Entity<Group>(entity =>
        {
            entity.ToTable("Groups");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            entity.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<UserGroup>(entity =>
        {
            entity.ToTable("UserGroups");
            entity.HasKey(x => new { x.UserId, x.GroupId });
            entity.HasOne(x => x.User)
                .WithMany(x => x.UserGroups)
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Group)
                .WithMany(x => x.UserGroups)
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.GroupId);
        });

        modelBuilder.Entity<AclEntry>(entity =>
        {
            entity.ToTable("AclEntries");
            entity.HasKey(x => x.Id);
            entity.HasOne(x => x.Group)
                .WithMany()
                .HasForeignKey(x => x.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Check)
                .WithMany()
                .HasForeignKey(x => x.CheckId)
                .OnDelete(DeleteBehavior.Cascade);

            // 同一 (组, 项目, 检项) 只允许一条，避免重复授权导致语义分裂
            entity.HasIndex(x => new { x.GroupId, x.ProjectId, x.CheckId }).IsUnique();

            // 上面那条三列唯一索引对**项目级**授权（CheckId IS NULL）完全无效：
            // SQL 标准里 NULL 互不相等，SQLite 也照此实现，于是同一个组可以对
            // 同一个项目插入任意多条项目级授权。「这个人的权限是谁给的」就说不清了，
            // 取消其中一条也不影响另一条 —— 这类重复在界面上看不出来，只能靠约束兜住。
            // 用过滤索引精确表达「项目级条目在同一组+项目上唯一」。
            entity.HasIndex(x => new { x.GroupId, x.ProjectId })
                .IsUnique()
                .HasFilter("\"CheckId\" IS NULL")
                .HasDatabaseName("IX_AclEntries_Group_Project_ProjectWide");

            // 按检项反查授权是每次请求都要走的路径
            entity.HasIndex(x => x.CheckId);
            entity.HasIndex(x => x.ProjectId);
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.ToTable("ApiTokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.TokenHash).HasMaxLength(64).IsRequired();
            entity.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // 插件每次调用都要按哈希验令牌，这是唯一索引而非普通索引的原因：
            // 既保证查找是 O(log n)，也杜绝两条令牌哈希相同导致身份串味。
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.UserId);
        });

        // ── ELN ────────────────────────────────────────────────────────

        modelBuilder.Entity<FormTemplate>(entity =>
        {
            entity.ToTable("FormTemplates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(64);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.SchemaJson).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(128);

            // 模板编码是用户选择模板时唯一稳定的标识（名称会改、Id 会变）
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.IsEnabled, x.Category });
        });

        modelBuilder.Entity<FormRecord>(entity =>
        {
            entity.ToTable("FormRecords");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.TemplateCode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.TemplateName).HasMaxLength(128).IsRequired();
            entity.Property(x => x.RecordNo).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Title).HasMaxLength(200);
            entity.Property(x => x.DataJson).IsRequired();
            entity.Property(x => x.TemplateSchemaJson).IsRequired();
            entity.Property(x => x.CreatedBy).HasMaxLength(128);
            entity.Property(x => x.UpdatedBy).HasMaxLength(128);
            entity.Property(x => x.SubmittedBy).HasMaxLength(128);
            entity.Property(x => x.VoidReason).HasMaxLength(500);

            // 记录号唯一：并发下靠它兜底（生成逻辑见 FormRecordService）
            entity.HasIndex(x => x.RecordNo).IsUnique();

            // 列表默认按 项目+检项+状态 过滤、按时间倒序
            entity.HasIndex(x => new { x.CheckId, x.Status, x.CreatedAt });

            // 模板不允许级联删除记录：模板"停用"就够了，删模板会把历史记录一起带走
            entity.HasOne(x => x.Template)
                .WithMany()
                .HasForeignKey(x => x.TemplateId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(x => x.Check)
                .WithMany()
                .HasForeignKey(x => x.CheckId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<RecordFile>(entity =>
        {
            entity.ToTable("RecordFiles");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.FileName).HasMaxLength(260).IsRequired();
            entity.Property(x => x.RelativePath).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.Extension).HasMaxLength(16);
            entity.Property(x => x.ContentType).HasMaxLength(128);
            entity.Property(x => x.UploadedBy).HasMaxLength(128);

            entity.HasOne(x => x.Record)
                .WithMany(x => x.Files)
                .HasForeignKey(x => x.RecordId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.RecordId);
        });

        modelBuilder.Entity<ExtractionRuleDefinition>(entity =>
        {
            entity.ToTable("ExtractionRuleDefinitions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Code).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(128).IsRequired();
            entity.Property(x => x.Category).HasMaxLength(64);
            entity.Property(x => x.Description).HasMaxLength(1000);
            entity.Property(x => x.RuleJson).IsRequired();
            entity.Property(x => x.UpdatedBy).HasMaxLength(128);

            entity.HasIndex(x => x.Code).IsUnique();
        });

        modelBuilder.Entity<ExtractionRuleScope>(entity =>
        {
            entity.ToTable("ExtractionRuleScopes");
            entity.HasKey(x => x.Id);

            entity.HasOne(x => x.Rule)
                .WithMany(x => x.Scopes)
                .HasForeignKey(x => x.RuleId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Project)
                .WithMany()
                .HasForeignKey(x => x.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(x => x.Check)
                .WithMany()
                .HasForeignKey(x => x.CheckId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => new { x.RuleId, x.ProjectId, x.CheckId }).IsUnique();

            // 与 AclEntry 同一个坑：SQL 里 NULL 互不相等，三列唯一索引对
            // 项目级范围（CheckId IS NULL）不生效，必须补一条过滤唯一索引。
            entity.HasIndex(x => new { x.RuleId, x.ProjectId })
                .IsUnique()
                .HasFilter("\"CheckId\" IS NULL")
                .HasDatabaseName("IX_ExtractionRuleScopes_Rule_Project_ProjectWide");

            entity.HasIndex(x => x.CheckId);
        });

        modelBuilder.Entity<ExtractionRun>(entity =>
        {
            entity.ToTable("ExtractionRuns");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RuleCode).HasMaxLength(64).IsRequired();
            entity.Property(x => x.SourceFileName).HasMaxLength(260);
            entity.Property(x => x.CreatedBy).HasMaxLength(128);
            entity.Property(x => x.ErrorMessage).HasMaxLength(1000);

            entity.HasOne<FormRecord>()
                .WithMany()
                .HasForeignKey(x => x.RecordId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(x => x.RecordId);
        });
    }
}
