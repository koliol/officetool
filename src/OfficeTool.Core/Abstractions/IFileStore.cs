namespace OfficeTool.Core.Abstractions;

/// <summary>文件元数据快照。</summary>
public sealed record FileMeta(
    string FullPath,
    string FileName,
    string Extension,
    long Size,
    DateTime ModifiedAt);

/// <summary>
/// 文件存储抽象。设计上刻意与具体后端解耦：
/// 开发环境用 <c>LocalFileStore</c>（本地目录，可在任意 OS 验证逻辑），
/// 生产环境用 <c>UncFileStore</c>（Windows + SMB 凭据模拟，设计文档 §7.2）。
/// </summary>
public interface IFileStore
{
    /// <summary>该存储是否可在当前主机上真实工作。</summary>
    bool IsAvailable { get; }

    bool DirectoryExists(string path);

    void CreateDirectory(string path);

    bool FileExists(string path);

    /// <summary>列出目录下的文件（不递归）。目录不存在时返回空集合。</summary>
    IReadOnlyList<FileMeta> ListFiles(string directory);

    /// <summary>列出目录下的子目录名（不递归）。目录不存在时返回空集合。</summary>
    IReadOnlyList<string> ListDirectories(string directory);

    /// <summary>以只读方式打开文件。</summary>
    Stream OpenRead(string path);

    /// <summary>
    /// 原子创建文件（等价于 FileMode.CreateNew）：文件已存在时抛 IOException。
    /// 这是并发命名去重的基础（设计文档 §7.4 第 4 步）。
    /// </summary>
    Stream CreateNew(string path);

    void CopyFile(string sourcePath, string destinationPath, bool overwrite);

    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    void DeleteFile(string path);

    /// <summary>删除目录本身（非递归）。目录非空时抛 IOException。</summary>
    void DeleteDirectory(string path);

    FileMeta GetMeta(string path);
}
