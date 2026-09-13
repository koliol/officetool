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
    }
}
