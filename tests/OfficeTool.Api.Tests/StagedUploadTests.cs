using OfficeTool.Api.Services;
using OfficeTool.Core.Storage;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 上传落盘：要么完整落盘、要么什么都不留。
///
/// 三处上传（模板 / 附件 / 规则）共用这套逻辑。这里锁的是「失败时不留下半截文件」——
/// 一旦留下，元数据同步会把它补录成有效记录，用户看到一条点开就报错的假数据。
/// </summary>
public sealed class StagedUploadTests : IDisposable
{
    private readonly string _root;
    private readonly LocalFileStore _store = new();

    public StagedUploadTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"officetool-upload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 目录被占用时忽略，临时目录由系统回收
        }
    }

    private string[] LeftoverTempFiles() =>
        Directory.Exists(_root)
            ? Directory.GetFiles(_root, $"*{StagedUpload.TempSuffix}")
            : [];

    [Fact]
    public async Task 上传完整时转正为目标文件且不留临时文件()
    {
        var target = Path.Combine(_root, "模板.docx");
        using var content = new MemoryStream("hello"u8.ToArray());

        var meta = await StagedUpload.WriteAsync(
            _store, _root, target, content, maxSizeBytes: 1024, tooLargeMessage: "太大");

        Assert.Equal(5, meta.Size);
        Assert.True(_store.FileExists(target));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task 超出上限时不留下任何文件()
    {
        var target = Path.Combine(_root, "大文件.docx");
        using var content = new MemoryStream(new byte[2048]);

        await Assert.ThrowsAsync<BadRequestException>(() => StagedUpload.WriteAsync(
            _store, _root, target, content, maxSizeBytes: 1024, tooLargeMessage: "太大"));

        Assert.False(_store.FileExists(target));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task 传输中途断开时不留下任何文件()
    {
        var target = Path.Combine(_root, "半截.docx");
        using var content = new InterruptingStream("前几个字节"u8.ToArray());

        await Assert.ThrowsAsync<IOException>(() => StagedUpload.WriteAsync(
            _store, _root, target, content, maxSizeBytes: 1024, tooLargeMessage: "太大"));

        // 关键断言：目标路径上没有任何东西 —— 同步不会把这次失败补录成一条记录
        Assert.False(_store.FileExists(target));
        Assert.Empty(LeftoverTempFiles());
    }

    [Fact]
    public async Task 目标已存在时保留原文件并清掉临时文件()
    {
        var target = Path.Combine(_root, "已存在.docx");
        await File.WriteAllTextAsync(target, "original");
        using var content = new MemoryStream(new byte[16]);

        await Assert.ThrowsAnyAsync<IOException>(() => StagedUpload.WriteAsync(
            _store, _root, target, content, maxSizeBytes: 1024, tooLargeMessage: "太大"));

        Assert.Equal("original", await File.ReadAllTextAsync(target));
        Assert.Empty(LeftoverTempFiles());
    }

    /// <summary>模拟「读到一半连接被重置」的上传流。</summary>
    private sealed class InterruptingStream(byte[] firstChunk) : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delivered)
            {
                throw new IOException("连接被对端重置。");
            }

            _delivered = true;
            var length = Math.Min(count, firstChunk.Length);
            firstChunk.AsSpan(0, length).CopyTo(buffer.AsSpan(offset, length));
            return length;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
