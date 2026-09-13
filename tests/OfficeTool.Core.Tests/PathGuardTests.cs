using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Services;
using Xunit;

namespace OfficeTool.Core.Tests;

public class PathGuardTests
{
    [Fact]
    public void 正常拼接位于根之下()
    {
        using var temp = new TempDirectory();
        var result = PathGuard.CombineUnderRoot(temp.Path, "QLS2409", "SEC");

        Assert.StartsWith(Path.GetFullPath(temp.Path), result);
        Assert.EndsWith(Path.Combine("QLS2409", "SEC"), result);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../etc")]
    [InlineData(@"..\windows")]
    [InlineData("a/../../b")]
    public void 拼接段含穿越应被拒绝(string segment)
    {
        using var temp = new TempDirectory();
        Assert.Throws<InvalidNameException>(() => PathGuard.CombineUnderRoot(temp.Path, segment));
    }

    [Fact]
    public void 拼接段含分隔符应被拒绝()
    {
        using var temp = new TempDirectory();
        Assert.Throws<InvalidNameException>(() => PathGuard.CombineUnderRoot(temp.Path, @"QLS\SEC"));
        Assert.Throws<InvalidNameException>(() => PathGuard.CombineUnderRoot(temp.Path, "QLS/SEC"));
    }

    [Fact]
    public void 根之外的绝对路径应被拒绝()
    {
        using var temp = new TempDirectory();
        var outside = Path.Combine(Path.GetTempPath(), "definitely-outside-" + Guid.NewGuid().ToString("N"));

        Assert.Throws<PathEscapeException>(() => PathGuard.EnsureUnderRoot(temp.Path, outside));
    }

    [Fact]
    public void 通过相对路径逃逸应被拒绝()
    {
        using var temp = new TempDirectory();
        var escaping = Path.Combine(temp.Path, "..", "escaped.txt");

        Assert.Throws<PathEscapeException>(() => PathGuard.EnsureUnderRoot(temp.Path, escaping));
    }

    [Fact]
    public void 根自身应被接受()
    {
        using var temp = new TempDirectory();
        var result = PathGuard.EnsureUnderRoot(temp.Path, temp.Path);
        Assert.Equal(Path.GetFullPath(temp.Path), result);
    }

    [Fact]
    public void 相对路径转换为反斜杠形式()
    {
        using var temp = new TempDirectory();
        var full = Path.Combine(temp.Path, "QLS2409", "SEC", "a.docx");

        Assert.Equal(@"QLS2409\SEC\a.docx", PathGuard.ToRelative(temp.Path, full));
    }

    [Fact]
    public void 尾部带分隔符的根应被规范化()
    {
        using var temp = new TempDirectory();
        var withSeparator = temp.Path + Path.DirectorySeparatorChar;

        Assert.Equal(temp.Path, PathGuard.NormalizeRoot(withSeparator));

        // 规范化后仍应正确判定归属
        var full = Path.Combine(temp.Path, "QLS2409");
        Assert.StartsWith(Path.GetFullPath(temp.Path), PathGuard.EnsureUnderRoot(withSeparator, full));
    }
}
