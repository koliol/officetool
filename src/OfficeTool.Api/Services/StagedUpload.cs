using OfficeTool.Core.Abstractions;

namespace OfficeTool.Api.Services;

/// <summary>
/// 上传落盘的统一封装：**先写临时文件、完整后才转正**。
///
/// ── 为什么不能直接写目标文件 ──────────────────────────────────────────
/// 浏览器中途断开、反向代理超时、进程被杀，都会让目标文件停在半个字节上。
/// 更糟的是「文件系统为准」的元数据同步（<c>SyncMetadataAsync</c>）会把这种
/// 半截文件当成有效模板补录进库 —— 用户看到一条记录、点开却打不开。
///
/// 临时文件带 <see cref="TempSuffix"/> 后缀，天然被三处「按扩展名过滤」的
/// 列表逻辑忽略（模板列表走数据库、附件/规则列表按白名单扩展名过滤、
/// 同步只扫 <c>*.docx</c> 等固定扩展名），所以写到一半的文件不会出现在任何 UI 上。
///
/// ── 转正为什么用 MoveFile ────────────────────────────────────────────
/// 同目录改名在 NTFS 与 ext4 上都是一次目录项更新，目标文件要么不存在、
/// 要么内容完整，不存在可观察到的中间态。<c>overwrite:false</c> 顺带挡住了
/// 「上传过程中目录里刚被别人建了同名文件」的竞争。
///
/// 失败路径统一删除临时文件；进程被强杀时留下的 <c>.part</c> 是可回收垃圾
/// （不可见、不会被索引），下一次同名上传不会复用它们。
/// </summary>
public static class StagedUpload
{
    /// <summary>临时文件前缀。以点开头，便于在共享盘上一眼认出是垃圾。</summary>
    public const string TempPrefix = ".officetool-upload-";

    /// <summary>临时文件后缀。刻意不是业务扩展名，保证不会被列表与同步误认。</summary>
    public const string TempSuffix = ".part";

    private const int BufferSize = 81920;

    /// <summary>
    /// 把 <paramref name="content"/> 写入 <paramref name="fullPath"/>。
    ///
    /// 调用方负责前置校验（重名、声明大小、扩展名白名单）；本方法只负责
    /// 「要么完整落盘、要么什么都不留」，并在落盘后按真实大小复核上限。
    /// </summary>
    /// <param name="tooLargeMessage">超出上限时抛给用户看的文案（各业务措辞不同）。</param>
    public static async Task<FileMeta> WriteAsync(
        IFileStore store,
        string directory,
        string fullPath,
        Stream content,
        long maxSizeBytes,
        string tooLargeMessage,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ArgumentNullException.ThrowIfNull(content);

        store.CreateDirectory(directory);

        var tempPath = BuildTempPath(directory);

        try
        {
            // 临时期同样用 CreateNew：并发上传同名文件时各自拿到不同的临时名，
            // 真正的冲突判定留到转正那一步。
            using (var target = store.CreateNew(tempPath))
            {
                await content.CopyToAsync(target, BufferSize, ct).ConfigureAwait(false);
            }

            var tempMeta = store.GetMeta(tempPath);

            // 声明大小可被伪造（甚至根本没发 Content-Length），以磁盘上的真实大小为准
            if (tempMeta.Size > maxSizeBytes)
            {
                throw new BadRequestException(tooLargeMessage);
            }

            store.MoveFile(tempPath, fullPath, overwrite: false);

            return store.GetMeta(fullPath);
        }
        catch
        {
            // 包括取消、超限、转正失败（目录里已有同名文件）—— 一律不留临时文件
            TryDelete(store, tempPath);
            throw;
        }
    }

    /// <summary>目录下的一次性临时文件名。</summary>
    public static string BuildTempPath(string directory) =>
        Path.Combine(directory, TempPrefix + Guid.NewGuid().ToString("N") + TempSuffix);

    private static void TryDelete(IFileStore store, string path)
    {
        try
        {
            if (store.FileExists(path))
            {
                store.DeleteFile(path);
            }
        }
        catch (IOException)
        {
            // 清理失败不能掩盖原始异常
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }
    }
}
