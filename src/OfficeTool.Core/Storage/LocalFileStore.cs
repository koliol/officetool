using OfficeTool.Core.Abstractions;

namespace OfficeTool.Core.Storage;

/// <summary>
/// 本地目录实现。开发/测试环境使用，也可用于 SMB 共享被映射为本地盘符的场景。
/// </summary>
public sealed class LocalFileStore : IFileStore
{
    public bool IsAvailable => true;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public IReadOnlyList<FileMeta> ListFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateFiles(directory)
            .Select(ToMeta)
            .OrderBy(m => m.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<string> ListDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.EnumerateDirectories(directory)
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Stream OpenRead(string path) =>
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

    public Stream CreateNew(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        // FileMode.CreateNew：文件已存在则抛 IOException，且创建动作由文件系统保证原子性。
        return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    public void CopyFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Copy(sourcePath, destinationPath, overwrite);
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        var dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.Move(sourcePath, destinationPath, overwrite);
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path) => Directory.Delete(path, recursive: false);

    public FileMeta GetMeta(string path) => ToMeta(path);

    private static FileMeta ToMeta(string path)
    {
        var info = new FileInfo(path);
        var exists = info.Exists;
        return new FileMeta(
            info.FullName,
            info.Name,
            info.Extension,
            exists ? info.Length : 0,
            exists ? info.LastWriteTime : default);
    }
}
