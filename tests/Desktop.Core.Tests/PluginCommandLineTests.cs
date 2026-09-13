using OfficeTool.Desktop.Core;

namespace OfficeTool.Desktop.Core.Tests;

/// <summary>命令行分发测试：确保「被浏览器唤起」这条主路径不会误入托盘逻辑。</summary>
public class PluginCommandLineTests
{
    private const string OpenUri =
        "officetool://open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx";

    [Fact]
    public void 无参数_进入托盘模式()
    {
        var commandLine = PluginCommandLine.Parse(Array.Empty<string>());

        Assert.Equal(PluginMode.Tray, commandLine.Mode);
        Assert.Null(commandLine.ProtocolResult);
        Assert.Null(commandLine.RawUri);
    }

    [Fact]
    public void null参数_进入托盘模式()
    {
        var commandLine = PluginCommandLine.Parse(null);

        Assert.Equal(PluginMode.Tray, commandLine.Mode);
    }

    [Theory]
    [InlineData("--install")]
    [InlineData("--INSTALL")]
    [InlineData("--Install")]
    public void 安装开关_进入安装模式(string arg)
    {
        var commandLine = PluginCommandLine.Parse(new[] { arg });

        Assert.Equal(PluginMode.Install, commandLine.Mode);
    }

    [Theory]
    [InlineData("--uninstall")]
    [InlineData("--UNINSTALL")]
    public void 卸载开关_进入卸载模式(string arg)
    {
        var commandLine = PluginCommandLine.Parse(new[] { arg });

        Assert.Equal(PluginMode.Uninstall, commandLine.Mode);
    }

    [Fact]
    public void 协议地址_进入打开模式并解析出路径()
    {
        var commandLine = PluginCommandLine.Parse(new[] { OpenUri });

        Assert.Equal(PluginMode.Open, commandLine.Mode);
        Assert.NotNull(commandLine.ProtocolResult);
        Assert.True(commandLine.ProtocolResult!.Success, commandLine.ProtocolResult.Error);
        Assert.Equal(@"\\mynas\OfficeDocs\Data\QLS2409\SEC\20260912.docx", commandLine.ProtocolResult.Path);
        Assert.Equal(OpenUri, commandLine.RawUri);
    }

    [Fact]
    public void 畸形协议地址_仍是打开模式但带着失败原因()
    {
        var commandLine = PluginCommandLine.Parse(new[] { "officetool://open?path=%" });

        Assert.Equal(PluginMode.Open, commandLine.Mode);
        Assert.NotNull(commandLine.ProtocolResult);
        Assert.False(commandLine.ProtocolResult!.Success);
        Assert.NotNull(commandLine.ProtocolResult.Error);
    }

    [Fact]
    public void 安装开关优先于协议地址()
    {
        var commandLine = PluginCommandLine.Parse(new[] { OpenUri, "--install" });

        Assert.Equal(PluginMode.Install, commandLine.Mode);
    }

    [Fact]
    public void 卸载开关优先于协议地址()
    {
        var commandLine = PluginCommandLine.Parse(new[] { OpenUri, "--uninstall" });

        Assert.Equal(PluginMode.Uninstall, commandLine.Mode);
    }

    [Theory]
    [InlineData("--foo")]
    [InlineData("tray")]
    [InlineData("C:\\some\\path.docx")]
    public void 无关参数_进入托盘模式(string arg)
    {
        var commandLine = PluginCommandLine.Parse(new[] { arg });

        Assert.Equal(PluginMode.Tray, commandLine.Mode);
    }

    [Fact]
    public void 协议地址出现在非首位也能识别()
    {
        var commandLine = PluginCommandLine.Parse(new[] { "something", OpenUri });

        Assert.Equal(PluginMode.Open, commandLine.Mode);
        Assert.Equal(@"\\mynas\OfficeDocs\Data\QLS2409\SEC\20260912.docx", commandLine.ProtocolResult!.Path);
    }
}
