using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Options;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Core.Tests;

public class PathLayoutTests
{
    private static PathLayout CreateLayout(string root) => new(new StorageOptions
    {
        Mode = "Local",
        TemplatesRoot = Path.Combine(root, "Templates"),
        DataRoot = Path.Combine(root, "Data"),
    });

    [Fact]
    public void 模板目录应为Templates下项目检项两级()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        var dir = layout.TemplateDirectory("QLS2409", "SEC");

        Assert.Equal(Path.Combine(temp.Path, "Templates", "QLS2409", "SEC"), dir);
    }

    [Fact]
    public void 数据目录应为Data下项目检项两级()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        Assert.Equal(
            Path.Combine(temp.Path, "Data", "QLS2409", "SEC"),
            layout.DataDirectory("QLS2409", "SEC"));
    }

    [Fact]
    public void 只给项目时应返回项目级目录()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        Assert.Equal(Path.Combine(temp.Path, "Templates", "QLS2409"), layout.TemplateDirectory("QLS2409"));
        Assert.Equal(Path.Combine(temp.Path, "Data", "QLS2409"), layout.DataDirectory("QLS2409"));
    }

    [Fact]
    public void 相对路径应使用反斜杠且不含根()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        Assert.Equal(
            @"QLS2409\SEC\检验记录模板.docx",
            layout.TemplateRelativePath("QLS2409", "SEC", "检验记录模板.docx"));

        Assert.Equal(
            @"QLS2409\SEC\20260912.docx",
            layout.DataRelativePath("QLS2409", "SEC", "20260912.docx"));
    }

    [Theory]
    [InlineData("../evil", "SEC")]
    [InlineData("QLS2409", "../../evil")]
    [InlineData("QLS/2409", "SEC")]
    public void 非法项目或检项编码应被拒绝(string project, string check)
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        Assert.ThrowsAny<ArgumentException>(() => layout.TemplateDirectory(project, check));
        Assert.ThrowsAny<ArgumentException>(() => layout.DataDirectory(project, check));
    }

    [Fact]
    public void 空项目应被拒绝()
    {
        using var temp = new TempDirectory();
        var layout = CreateLayout(temp.Path);

        Assert.ThrowsAny<ArgumentException>(() => layout.TemplateDirectory(""));
        Assert.ThrowsAny<ArgumentException>(() => layout.DataDirectory(null!));
    }

    [Fact]
    public void 根路径尾部带分隔符应被规范化()
    {
        using var temp = new TempDirectory();
        var root = temp.Path + Path.DirectorySeparatorChar;
        var layout = CreateLayout(root);

        Assert.Equal(Path.Combine(temp.Path, "Templates"), layout.TemplatesRoot);
        Assert.Equal(
            Path.Combine(temp.Path, "Data", "QLS2409", "SEC"),
            layout.DataDirectory("QLS2409", "SEC"));
    }

    [Fact]
    public void 回收站目录应为类别加日期()
    {
        using var temp = new TempDirectory();
        var layout = new PathLayout(new StorageOptions
        {
            Mode = "Local",
            TemplatesRoot = Path.Combine(temp.Path, "Templates"),
            DataRoot = Path.Combine(temp.Path, "Data"),
        }.WithDefaults());
        var when = new DateTime(2026, 9, 13);

        var dir = layout.TrashKindDirectory("Document", when);

        Assert.Equal(Path.Combine(temp.Path, "_trash", "Document", "20260913"), dir);
    }

    [Fact]
    public void 回收站类别名拒绝路径穿越()
    {
        using var temp = new TempDirectory();
        var layout = new PathLayout(new StorageOptions
        {
            Mode = "Local",
            TemplatesRoot = Path.Combine(temp.Path, "Templates"),
            DataRoot = Path.Combine(temp.Path, "Data"),
        }.WithDefaults());

        Assert.Throws<ArgumentException>(() => { _ = layout.TrashKindDirectory("../etc", DateTime.Now); });
        Assert.Throws<ArgumentException>(() => { _ = layout.TrashKindDirectory("a/b", DateTime.Now); });
    }

    [Fact]
    public void 回收站文件名应含guid与原相对路径()
    {
        var name = PathLayout.BuildTrashFileName("QLS2409\\SEC\\a.docx", "a.docx");

        Assert.EndsWith("_QLS2409_SEC_a.docx", name);
        Assert.NotEqual("a.docx", name);
    }
}

/// <summary>容器内路径 → 客户端可访问路径（群晖部署的核心细节）。</summary>
public class ClientPathBuilderTests
{
    [Fact]
    public void UNC客户端根应拼出标准UNC路径()
    {
        var result = ClientPathBuilder.Build(
            @"\\NAS\OfficeDocs\Data", "/data/Data", @"QLS2409\SEC\20260912.docx");

        Assert.Equal(@"\\NAS\OfficeDocs\Data\QLS2409\SEC\20260912.docx", result);
    }

    [Fact]
    public void UNC路径不应出现混合分隔符()
    {
        var result = ClientPathBuilder.Build(
            @"\\NAS\OfficeDocs\Templates", "/data/Templates", @"QLS2409\SEC\模板.docx");

        Assert.DoesNotContain("/", result);
        Assert.StartsWith(@"\\NAS\", result);
    }

    [Fact]
    public void 客户端根尾部带分隔符应被规范化()
    {
        var result = ClientPathBuilder.Build(
            @"\\NAS\OfficeDocs\Data\", "/data/Data", @"QLS2409\a.docx");

        Assert.Equal(@"\\NAS\OfficeDocs\Data\QLS2409\a.docx", result);
    }

    [Fact]
    public void 相对路径用正斜杠时也应转为反斜杠()
    {
        var result = ClientPathBuilder.Build(
            @"\\NAS\OfficeDocs\Data", "/data/Data", "QLS2409/SEC/a.docx");

        Assert.Equal(@"\\NAS\OfficeDocs\Data\QLS2409\SEC\a.docx", result);
    }

    [Fact]
    public void 未配置客户端根时应回退服务端路径()
    {
        var result = ClientPathBuilder.Build(null, "/data/Data", @"QLS2409\SEC\a.docx");

        Assert.Equal(Path.Combine("/data/Data", "QLS2409", "SEC", "a.docx"), result);
    }

    [Fact]
    public void 客户端根为空串时同样回退()
    {
        var result = ClientPathBuilder.Build("   ", "/data/Data", @"QLS2409\a.docx");

        Assert.Equal(Path.Combine("/data/Data", "QLS2409", "a.docx"), result);
    }
}

/// <summary>存储配置的解析与默认值。</summary>
public class StorageOptionsTests
{
    [Fact]
    public void 未配置客户端根时回退服务端根()
    {
        var options = new StorageOptions
        {
            TemplatesRoot = "/data/Templates",
            DataRoot = "/data/Data",
        }.WithDefaults();

        Assert.Equal("/data/Templates", options.ClientTemplatesRoot);
        Assert.Equal("/data/Data", options.ClientDataRoot);
        Assert.False(options.IsUnc);
    }

    [Fact]
    public void 已配置客户端根时不被覆盖()
    {
        var options = new StorageOptions
        {
            TemplatesRoot = "/data/Templates",
            DataRoot = "/data/Data",
            ClientTemplatesRoot = @"\\NAS\OfficeDocs\Templates",
            ClientDataRoot = @"\\NAS\OfficeDocs\Data",
        }.WithDefaults();

        Assert.Equal(@"\\NAS\OfficeDocs\Templates", options.ClientTemplatesRoot);
        Assert.Equal(@"\\NAS\OfficeDocs\Data", options.ClientDataRoot);
    }

    [Theory]
    [InlineData("Unc", true)]
    [InlineData("unc", true)]
    [InlineData("Local", false)]
    [InlineData("local", false)]
    public void 模式解析大小写不敏感(string mode, bool expectUnc)
    {
        var options = new StorageOptions
        {
            Mode = mode,
            TemplatesRoot = "/data/Templates",
            DataRoot = "/data/Data",
        }.WithDefaults();

        Assert.Equal(expectUnc, options.IsUnc);
    }

    [Fact]
    public void 非法模式应报错()
    {
        var options = new StorageOptions
        {
            Mode = "Samba",
            TemplatesRoot = "/data/Templates",
            DataRoot = "/data/Data",
        };

        var ex = Assert.Throws<InvalidOperationException>(() => options.WithDefaults());
        Assert.Contains("Mode", ex.Message);
    }

    [Fact]
    public void 缺少根路径应报错()
    {
        var options = new StorageOptions { Mode = "Local", TemplatesRoot = "", DataRoot = "/data/Data" };

        Assert.Throws<InvalidOperationException>(() => options.WithDefaults());
    }

    [Fact]
    public void 上传白名单绑定后不应重复()
    {
        // 复现过真实缺陷：预置默认值 + 配置绑定 = 追加，白名单会翻倍
        var options = new UploadOptions
        {
            AllowedExtensions = [".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm"],
        }.WithDefaults();

        Assert.Equal(6, options.AllowedExtensions.Length);
        Assert.Equal(6, options.AllowedExtensions.Distinct().Count());
    }

    [Fact]
    public void 未配置上传白名单时用默认值()
    {
        var options = new UploadOptions().WithDefaults();

        Assert.Equal(6, options.AllowedExtensions.Length);
        Assert.Equal(100, options.MaxSizeMB);
    }

    [Fact]
    public void 回收站根默认与模板库同级()
    {
        var options = new StorageOptions
        {
            Mode = "Local",
            TemplatesRoot = "/data/Templates",
            DataRoot = "/data/Data",
        }.WithDefaults();

        Assert.Equal("/data/_trash", options.TrashRoot);
    }
}
