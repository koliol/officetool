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
    }
}
