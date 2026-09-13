using System.Collections.Concurrent;
using System.Globalization;
using OfficeTool.Core.Abstractions;
using OfficeTool.Core.Exceptions;

namespace OfficeTool.Core.Services;

/// <summary>一次成功分配的结果。</summary>
public sealed record AllocatedDocument(string FileName, string FullPath, long Size);

/// <summary>
/// 文档副本命名与并发去重（设计文档 §4.4、§7.4）。
///
/// 规则：当天第一次 <c>yyyyMMdd.ext</c>，之后 <c>yyyyMMddA.ext</c> 递增至 <c>yyyyMMddZ.ext</c>，
/// 字母固定大写，扩展名完全跟随模板；Z 仍冲突则报「当日编号已满」。
///
/// 并发策略分两层：
/// 1. 进程内：按目标目录的 <see cref="SemaphoreSlim"/> 串行化，避免同实例自相竞争；
/// 2. 跨实例：<see cref="IFileStore.CreateNew"/> 的原子创建（FileMode.CreateNew）兜底，
///    即使未来多实例部署也不会重名。
/// </summary>
public sealed class DocumentNamingService : IDisposable
{
    /// <summary>后缀序号上限：0 表示无后缀，1..26 表示 A..Z。</summary>
    public const int MaxSuffixIndex = 26;

    /// <summary>单个目录每天可生成的文档上限：yyyyMMdd + A..Z，共 27 个。</summary>
    public const int MaxDocumentsPerDirectoryPerDay = MaxSuffixIndex + 1;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _directoryLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _disposed;

    /// <summary>构造候选文件名。suffixIndex 0 → 无后缀；1 → A；26 → Z。</summary>
    public static string BuildCandidate(string baseName, int suffixIndex, string extension)
    {
        if (suffixIndex < 0 || suffixIndex > MaxSuffixIndex)
        {
            throw new ArgumentOutOfRangeException(
                nameof(suffixIndex), suffixIndex, $"后缀序号必须在 0..{MaxSuffixIndex} 之间。");
        }

        var suffix = suffixIndex == 0
            ? string.Empty
            : ((char)('A' + suffixIndex - 1)).ToString();

        var normalizedExtension = extension.StartsWith('.') ? extension : "." + extension;
        return baseName + suffix + normalizedExtension;
    }

    public static string BuildBaseName(DateTime date) =>
        date.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    /// <summary>
    /// 在 <paramref name="directory"/> 下按日期规则分配唯一文件名并写入内容。
    /// </summary>
    /// <param name="writeContent">写入回调，接收已原子创建的可写流。</param>
    public AllocatedDocument CreateWithUniqueName(
        IFileStore store,
        string directory,
        string extension,
        DateTime date,
        Action<Stream> writeContent)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(writeContent);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var normalizedExtension = extension.StartsWith('.') ? extension : "." + extension;
        var lockKey = Path.GetFullPath(directory);
        var gate = _directoryLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        gate.Wait();
        try
        {
            store.CreateDirectory(directory);

            var baseName = BuildBaseName(date);

            for (var suffixIndex = 0; suffixIndex <= MaxSuffixIndex; suffixIndex++)
            {
                var fileName = BuildCandidate(baseName, suffixIndex, normalizedExtension);
                var fullPath = Path.Combine(directory, fileName);

                // 第一步：原子占位。只有「创建」这一步的失败才代表名字被占用。
                Stream stream;
                try
                {
                    stream = store.CreateNew(fullPath);
                }
                catch (IOException) when (store.FileExists(fullPath))
                {
                    // 名字已被占用（本进程前一轮，或另一个进程/实例），继续尝试下一个后缀。
                    continue;
                }

                // 第二步：写入内容。此步失败必须清理占位文件，
                // 否则会留下一个空文件，且调用方会误以为失败原因是重名。
                try
                {
                    using (stream)
                    {
                        writeContent(stream);
                    }
                }
                catch
                {
                    TryDeletePlaceholder(store, fullPath);
                    throw;
                }

                var size = store.GetMeta(fullPath).Size;
                return new AllocatedDocument(fileName, fullPath, size);
            }

            throw new DayNumberExhaustedException(baseName, normalizedExtension);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void TryDeletePlaceholder(IFileStore store, string fullPath)
    {
        try
        {
            if (store.FileExists(fullPath))
            {
                store.DeleteFile(fullPath);
            }
        }
        catch (IOException)
        {
            // 清理失败不掩盖原始异常
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }

    /// <summary>当前被跟踪的目录锁数量，便于诊断。</summary>
    public int TrackedDirectoryCount => _directoryLocks.Count;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var gate in _directoryLocks.Values)
        {
            gate.Dispose();
        }

        _directoryLocks.Clear();
    }
}
