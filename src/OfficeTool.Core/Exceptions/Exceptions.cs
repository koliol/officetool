namespace OfficeTool.Core.Exceptions;

/// <summary>当日编号 A..Z 已用尽（设计文档 §4.4）。</summary>
public sealed class DayNumberExhaustedException(string baseName, string extension)
    : InvalidOperationException($"当日编号已满：{baseName}*.{extension.TrimStart('.')} 已占用至 Z")
{
    public string BaseName { get; } = baseName;

    public string Extension { get; } = extension;
}

/// <summary>项目 / 检项 / 文件名不合法（设计文档 §5.1、§7.3）。</summary>
public sealed class InvalidNameException(string message) : ArgumentException(message);

/// <summary>路径逃逸出配置的 SMB 根（设计文档 §7.3）。</summary>
public sealed class PathEscapeException(string message) : UnauthorizedAccessException(message);

/// <summary>扩展名不在白名单内。</summary>
public sealed class ExtensionNotAllowedException(string extension)
    : InvalidOperationException($"扩展名不在白名单内：{extension}")
{
    public string Extension { get; } = extension;
}

/// <summary>运行环境不支持该操作（例如在非 Windows 主机上访问 UNC）。</summary>
public sealed class PlatformUnsupportedException(string message) : PlatformNotSupportedException(message);
