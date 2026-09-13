using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Core.Tests;

public class NameValidatorTests
{
    [Theory]
    [InlineData("QLS2409")]
    [InlineData("SEC")]
    [InlineData("qls_2409")]
    [InlineData("A-B_C-1")]
    public void 合法编码应通过(string code)
    {
        Assert.True(NameValidator.TryValidateCode(code, out var error), error);
    }

    [Theory]
    [InlineData("", "空")]
    [InlineData("   ", "空")]
    [InlineData("QLS 2409", "空格")]
    [InlineData("QLS/2409", "斜杠")]
    [InlineData("QLS\\2409", "反斜杠")]
    [InlineData("..", "..")]
    [InlineData(".", ".")]
    [InlineData("项目", "中文")]
    [InlineData("QLS2409.", "点号")]
    [InlineData("a:b", "冒号")]
    public void 非法编码应被拒绝(string code, string reason)
    {
        Assert.False(NameValidator.TryValidateCode(code, out var error), $"应拒绝（{reason}）：{code}");
        Assert.NotEmpty(error);
    }

    [Fact]
    public void 系统保留名应被拒绝()
    {
        foreach (var reserved in new[] { "CON", "PRN", "AUX", "NUL", "COM1", "LPT9", "con" })
        {
            Assert.False(NameValidator.TryValidateCode(reserved, out _), $"应拒绝保留名：{reserved}");
        }
    }

    [Fact]
    public void 编码超长应被拒绝()
    {
        var tooLong = new string('A', NameValidator.MaxCodeLength + 1);
        Assert.False(NameValidator.TryValidateCode(tooLong, out _));
        Assert.True(NameValidator.TryValidateCode(new string('A', NameValidator.MaxCodeLength), out _));
    }

    [Theory]
    [InlineData("检验记录模板.docx")]
    [InlineData("20260912.xlsx")]
    public void 合法文件名应通过(string name)
    {
        Assert.True(NameValidator.TryValidateFileName(name, out var error), error);
    }

    [Theory]
    [InlineData("../evil.docx")]
    [InlineData(@"..\evil.docx")]
    [InlineData("a/b.docx")]
    [InlineData("a\\b.docx")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void 非法文件名应被拒绝(string name)
    {
        Assert.False(NameValidator.TryValidateFileName(name, out _), $"应拒绝：{name}");
    }

    [Fact]
    public void 扩展名白名单大小写不敏感_且非白名单被拒()
    {
        var allowed = new[] { ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm" };

        // 大小写不敏感地匹配白名单，但返回值保留原始大小写（扩展名跟随模板）
        Assert.Equal(".docx", NameValidator.EnsureAllowedExtension(".docx", allowed));
        Assert.Equal(".DOCX", NameValidator.EnsureAllowedExtension(".DOCX", allowed));
        Assert.Equal(".XLSX", NameValidator.EnsureAllowedExtension("XLSX", allowed));

        Assert.Throws<ExtensionNotAllowedException>(
            () => NameValidator.EnsureAllowedExtension(".exe", allowed));
        Assert.Throws<ExtensionNotAllowedException>(
            () => NameValidator.EnsureAllowedExtension(".doc", allowed));
        Assert.Throws<ExtensionNotAllowedException>(
            () => NameValidator.EnsureAllowedExtension("", allowed));
    }
}
