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

/// <summary>
/// 字符串前缀校验挡不住符号链接：共享盘对所有人可写时，
/// 在里面建一个指向系统目录的 junction，上传就会写进系统目录。
/// 守卫必须做真实的链接解析（见 PathGuard.EnsureNoLinkEscape）。
/// </summary>
public class ReparsePointGuardTests
{
    [Fact]
    public void 指向根外的链接必须被拒绝()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;   // junction 是 Windows 概念；Linux 上用真符号链接，CI 里单独测
        }

        var root = Path.Combine(Path.GetTempPath(), $"ot-guard-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "Templates", "QLS2409"));

        var outside = Path.Combine(Path.GetTempPath(), $"ot-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);

        string? link = null;
        string? innerLink = null;

        try
        {
            link = Path.Combine(root, "Templates", "QLS2409", "SEC");
            Run("cmd", $"/c mklink /J \"{link}\" \"{outside}\"");

            var rootFull = Path.GetFullPath(root);

            // 目录链上有指向根外的 junction → 拒绝
            Assert.Throws<PathEscapeException>(() =>
                PathGuard.CombineUnderRoot(rootFull, "Templates", "QLS2409", "SEC", "a.docx"));

            // 指回根内部的 junction → 放行（合法的目录整理手段）
            innerLink = Path.Combine(root, "Templates", "QLS2409", "ALT");
            Run("cmd", $"/c mklink /J \"{innerLink}\" \"{Path.Combine(root, "Templates", "QLS2409")}\"");

            var allowed = PathGuard.CombineUnderRoot(rootFull, "Templates", "QLS2409", "ALT", "a.docx");
            Assert.StartsWith(rootFull, allowed, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            // 先摘链接再删目录：Directory.Delete(recursive) 会试图穿过 junction，
            // 对指向受限位置的链接报 AccessDenied，反而把清理搞挂
            try { Directory.Delete(link, recursive: false); } catch (IOException) { }
            try { Directory.Delete(innerLink, recursive: false); } catch (IOException) { }
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
            try { Directory.Delete(outside, recursive: true); } catch (IOException) { }
        }
    }

    private static void Run(string fileName, string arguments)
    {
        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using var process = System.Diagnostics.Process.Start(psi);
        process?.WaitForExit(10_000);

        // 权限不足等情况下 junction 建不出来：断言失败给出可读信息
        Assert.True(process is { ExitCode: 0 }, $"创建 junction 失败：{fileName} {arguments}");
    }
}
