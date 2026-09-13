using OfficeTool.Desktop.Core;

namespace OfficeTool.Desktop.Core.Tests;

/// <summary>注册表取值的纯函数测试，顺带锁死注册表项的字段名与路径。</summary>
public class RegistryCommandBuilderTests
{
    [Fact]
    public void 协议命令_路径含空格时整体加引号且保留百分之一占位符()
    {
        var command = RegistryCommandBuilder.BuildProtocolOpenCommand(@"C:\Program Files\OfficeTool\OfficeToolPlugin.exe");

        Assert.Equal("\"C:\\Program Files\\OfficeTool\\OfficeToolPlugin.exe\" \"%1\"", command);
    }

    [Fact]
    public void 协议命令_路径不含空格时同样加引号()
    {
        var command = RegistryCommandBuilder.BuildProtocolOpenCommand(@"C:\OfficeTool\OfficeToolPlugin.exe");

        Assert.Equal("\"C:\\OfficeTool\\OfficeToolPlugin.exe\" \"%1\"", command);
    }

    [Fact]
    public void 协议命令_以带引号的百分之一结尾()
    {
        var command = RegistryCommandBuilder.BuildProtocolOpenCommand(@"C:\a b\OfficeToolPlugin.exe");

        Assert.EndsWith("\"%1\"", command);
        Assert.Contains(RegistryCommandBuilder.ArgumentPlaceholder, command);
    }

    [Fact]
    public void 协议命令_能从中还原出exe路径()
    {
        var exe = @"C:\Program Files\OfficeTool\OfficeToolPlugin.exe";
        var command = RegistryCommandBuilder.BuildProtocolOpenCommand(exe);

        Assert.StartsWith(RegistryCommandBuilder.QuoteExecutablePath(exe), command);
    }

    [Fact]
    public void 自启命令_只有带引号的exe路径()
    {
        var command = RegistryCommandBuilder.BuildAutoStartCommand(@"C:\Program Files\OfficeTool\OfficeToolPlugin.exe");

        Assert.Equal("\"C:\\Program Files\\OfficeTool\\OfficeToolPlugin.exe\"", command);
        Assert.DoesNotContain("%1", command);
    }

    [Fact]
    public void 图标取值_带引号且以逗号零结尾()
    {
        var value = RegistryCommandBuilder.BuildDefaultIconValue(@"C:\OfficeTool\OfficeToolPlugin.exe");

        Assert.Equal("\"C:\\OfficeTool\\OfficeToolPlugin.exe\",0", value);
    }

    [Fact]
    public void 图标取值_路径含逗号也不会被误解()
    {
        var value = RegistryCommandBuilder.BuildDefaultIconValue(@"C:\a,b\OfficeToolPlugin.exe");

        Assert.Equal("\"C:\\a,b\\OfficeToolPlugin.exe\",0", value);
        Assert.StartsWith("\"", value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void 空exe路径_抛参数异常(string? exePath)
    {
        Assert.Throws<ArgumentException>(() => RegistryCommandBuilder.QuoteExecutablePath(exePath!));
        Assert.Throws<ArgumentException>(() => RegistryCommandBuilder.BuildProtocolOpenCommand(exePath!));
        Assert.Throws<ArgumentException>(() => RegistryCommandBuilder.BuildAutoStartCommand(exePath!));
        Assert.Throws<ArgumentException>(() => RegistryCommandBuilder.BuildDefaultIconValue(exePath!));
    }

    [Fact]
    public void 含双引号的exe路径_抛参数异常()
    {
        Assert.Throws<ArgumentException>(() => RegistryCommandBuilder.QuoteExecutablePath("C:\\a\"b\\x.exe"));
    }

    [Fact]
    public void 注册表路径_全部位于HKCU的相对路径下()
    {
        Assert.Equal(@"Software\Classes\officetool", RegistryCommandBuilder.ProtocolKeyPath);
        Assert.Equal(@"Software\Classes\officetool\shell\open\command", RegistryCommandBuilder.ProtocolCommandKeyPath);
        Assert.Equal(@"Software\Classes\officetool\DefaultIcon", RegistryCommandBuilder.ProtocolIconKeyPath);
        Assert.Equal(@"Software\Microsoft\Windows\CurrentVersion\Run", RegistryCommandBuilder.AutoStartKeyPath);

        // 绝不能出现 HKLM / HKEY_CLASSES_ROOT 字样：那样会要求管理员权限。
        Assert.DoesNotContain("HKEY_LOCAL_MACHINE", RegistryCommandBuilder.ProtocolKeyPath);
        Assert.DoesNotContain("HKEY_CLASSES_ROOT", RegistryCommandBuilder.ProtocolKeyPath);
    }

    [Fact]
    public void 协议名与友好名称符合约定()
    {
        Assert.Equal("officetool", RegistryCommandBuilder.UriScheme);
        Assert.Equal("%1", RegistryCommandBuilder.ArgumentPlaceholder);
        Assert.Equal("URL:Office 文档管理插件", RegistryCommandBuilder.ProtocolFriendlyName);
        Assert.StartsWith("URL:", RegistryCommandBuilder.ProtocolFriendlyName);
        Assert.Equal("OfficeToolPlugin", RegistryCommandBuilder.AutoStartValueName);
    }
}
