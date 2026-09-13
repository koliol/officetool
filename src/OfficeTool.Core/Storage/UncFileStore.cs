using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Options;

namespace OfficeTool.Core.Storage;

/// <summary>
/// UNC 远程共享实现（设计文档 §7.2）：操作前先确保 SMB 凭据模拟已建立，再复用本地文件原语。
/// 一旦凭据连接成功，UNC 路径对 System.IO 而言就是普通路径。
/// </summary>
public sealed class UncFileStore(StorageOptions options, LocalFileStore inner) : IFileStore
{
    private readonly StorageOptions _options = options;
    private readonly LocalFileStore _inner = inner;

    public bool IsAvailable =>
        OperatingSystem.IsWindows()
        && _options.IsUnc
        && !string.IsNullOrWhiteSpace(_options.TemplatesRoot)
        && _options.TemplatesRoot.StartsWith(@"\\", StringComparison.Ordinal);

    private void Ensure(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformUnsupportedException(
                "当前主机不是 Windows，UNC 远程共享不可用。" +
                "群晖/Linux 部署请将 Storage:Mode 设为 Local 并把共享文件夹 bind mount 进容器。");
        }

        UncConnection.EnsureConnected(path, _options.UserName, _options.DecryptedPassword, _options.Domain);
    }

    public bool DirectoryExists(string path)
    {
        Ensure(path);
        return _inner.DirectoryExists(path);
    }

    public void CreateDirectory(string path)
    {
        Ensure(path);
        _inner.CreateDirectory(path);
    }

    public bool FileExists(string path)
    {
        Ensure(path);
        return _inner.FileExists(path);
    }

    public IReadOnlyList<FileMeta> ListFiles(string directory)
    {
        Ensure(directory);
        return _inner.ListFiles(directory);
    }

    public IReadOnlyList<string> ListDirectories(string directory)
    {
        Ensure(directory);
        return _inner.ListDirectories(directory);
    }

    public Stream OpenRead(string path)
    {
        Ensure(path);
        return _inner.OpenRead(path);
    }

    public Stream CreateNew(string path)
    {
        Ensure(path);
        return _inner.CreateNew(path);
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        Ensure(sourcePath);
        Ensure(destinationPath);
        _inner.CopyFile(sourcePath, destinationPath, overwrite);
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        Ensure(sourcePath);
        Ensure(destinationPath);
        _inner.MoveFile(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path)
    {
        Ensure(path);
        _inner.DeleteFile(path);
    }

    public void DeleteDirectory(string path)
    {
        Ensure(path);
        _inner.DeleteDirectory(path);
    }

    public FileMeta GetMeta(string path)
    {
        Ensure(path);
        return _inner.GetMeta(path);
    }
}
