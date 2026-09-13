using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using OfficeTool.Api.Data;

namespace OfficeTool.Api.Data.Migrations;

/// <summary>
/// 模型快照。手写维护（本机�?dotnet-ef 时）�?/// 修改 <c>AppDbContext</c> 实体后，须同步更新本文件并新增迁移�?/// </summary>
[DbContext(typeof(AppDbContext))]
partial class AppDbContextModelSnapshot : ModelSnapshot
{
    /// <inheritdoc />
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder
            .HasAnnotation("ProductVersion", "8.0.31");

        modelBuilder.Entity("OfficeTool.Core.Models.CheckItem", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("CreatedByIp")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<int>("ProjectId")
                .HasColumnType("INTEGER");

            b.HasKey("Id");

            b.HasIndex("ProjectId", "Name")
                .IsUnique();

            b.ToTable("Checks");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.Document", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<int>("CheckId")
                .HasColumnType("INTEGER");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("CreatedByIp")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<string>("Extension")
                .IsRequired()
                .HasMaxLength(16)
                .HasColumnType("TEXT");

            b.Property<string>("FileName")
                .IsRequired()
                .HasMaxLength(260)
                .HasColumnType("TEXT");

            b.Property<int>("ProjectId")
                .HasColumnType("INTEGER");

            b.Property<string>("RelativePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.Property<long>("Size")
                .HasColumnType("INTEGER");

            b.Property<string>("SourceTemplatePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("ProjectId", "CheckId", "FileName")
                .IsUnique();

            b.HasIndex("CreatedAt");

            b.ToTable("Documents");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.OperationLog", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Action")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("Ip")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<string>("Message")
                .HasMaxLength(2000)
                .HasColumnType("TEXT");

            b.Property<string>("Result")
                .IsRequired()
                .HasMaxLength(16)
                .HasColumnType("TEXT");

            b.Property<string>("TargetPath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.Property<string>("UserAgent")
                .HasMaxLength(512)
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("CreatedAt");

            b.ToTable("OperationLogs");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.Project", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("CreatedByIp")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<string>("Name")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("Name")
                .IsUnique();

            b.ToTable("Projects");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.RuleNote", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Note")
                .IsRequired()
                .HasMaxLength(1000)
                .HasColumnType("TEXT");

            b.Property<string>("RelativePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.Property<DateTime>("UpdatedAt")
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("RelativePath")
                .IsUnique();

            b.ToTable("RuleNotes");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.Template", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<int>("CheckId")
                .HasColumnType("INTEGER");

            b.Property<DateTime>("CreatedAt")
                .HasColumnType("TEXT");

            b.Property<string>("Extension")
                .IsRequired()
                .HasMaxLength(16)
                .HasColumnType("TEXT");

            b.Property<string>("FileName")
                .IsRequired()
                .HasMaxLength(260)
                .HasColumnType("TEXT");

            b.Property<DateTime>("ModifiedAt")
                .HasColumnType("TEXT");

            b.Property<int>("ProjectId")
                .HasColumnType("INTEGER");

            b.Property<string>("RelativePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.Property<long>("Size")
                .HasColumnType("INTEGER");

            b.Property<string>("UploadedByIp")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("ProjectId", "CheckId", "FileName")
                .IsUnique();

            b.HasIndex("ModifiedAt");

            b.ToTable("Templates");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.TrashItem", b =>
        {
            b.Property<int>("Id")
                .ValueGeneratedOnAdd()
                .HasColumnType("INTEGER");

            b.Property<string>("Check")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<DateTime>("DeletedAt")
                .HasColumnType("TEXT");

            b.Property<string>("DeletedByIp")
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<string>("FileName")
                .IsRequired()
                .HasMaxLength(260)
                .HasColumnType("TEXT");

            b.Property<string>("Kind")
                .IsRequired()
                .HasMaxLength(32)
                .HasColumnType("TEXT");

            b.Property<string>("OriginalRelativePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.Property<string>("Project")
                .IsRequired()
                .HasMaxLength(64)
                .HasColumnType("TEXT");

            b.Property<long>("Size")
                .HasColumnType("INTEGER");

            b.Property<string>("TrashRelativePath")
                .IsRequired()
                .HasMaxLength(500)
                .HasColumnType("TEXT");

            b.HasKey("Id");

            b.HasIndex("DeletedAt");

            b.HasIndex("Kind", "Project", "Check");

            b.ToTable("TrashItems");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.CheckItem", b =>
        {
            b.HasOne("OfficeTool.Core.Models.Project", "Project")
                .WithMany("Checks")
                .HasForeignKey("ProjectId")
                .OnDelete(DeleteBehavior.Restrict)
                .IsRequired();

            b.Navigation("Project");
        });

        modelBuilder.Entity("OfficeTool.Core.Models.Project", b =>
        {
            b.Navigation("Checks");
        });
    }
}
