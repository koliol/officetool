using System.Collections.Concurrent;
using OfficeTool.Core.Exceptions;
using OfficeTool.Core.Services;
using OfficeTool.Core.Storage;
using Xunit;

namespace OfficeTool.Core.Tests;

public class DocumentNamingServiceTests
{
    private static readonly DateTime SampleDate = new(2026, 9, 12);

    private static byte[] SampleContent => "content"u8.ToArray();

    [Fact]
    public void 当天首次生成_无后缀()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        var result = service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent));

        Assert.Equal("20260912.docx", result.FileName);
        Assert.Equal(SampleContent.Length, result.Size);
        Assert.True(File.Exists(result.FullPath));
    }

    [Fact]
    public void 第二次生成_A_第三次_B()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        var names = Enumerable.Range(0, 3)
            .Select(_ => service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)).FileName)
            .ToList();

        Assert.Equal(["20260912.docx", "20260912A.docx", "20260912B.docx"], names);
    }

    [Fact]
    public void 扩展名完全跟随模板()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        // 同名不同扩展名互不冲突——扩展名是文件名的一部分
        Assert.Equal("20260912.xlsx",
            service.CreateWithUniqueName(store, temp.Path, ".xlsx", SampleDate, s => s.Write(SampleContent)).FileName);
        Assert.Equal("20260912.xlsm",
            service.CreateWithUniqueName(store, temp.Path, ".xlsm", SampleDate, s => s.Write(SampleContent)).FileName);

        // 同扩展名再来一次才带后缀
        Assert.Equal("20260912A.xlsx",
            service.CreateWithUniqueName(store, temp.Path, ".xlsx", SampleDate, s => s.Write(SampleContent)).FileName);

        // 大小写跟随模板（设计文档 §4.4「扩展名完全跟随模板」）
        Assert.Equal("20260912.DOCM",
            service.CreateWithUniqueName(store, temp.Path, ".DOCM", SampleDate, s => s.Write(SampleContent)).FileName);
    }

    [Fact]
    public void 后缀字母固定大写()
    {
        Assert.Equal("20260912.docx", DocumentNamingService.BuildCandidate("20260912", 0, ".docx"));
        Assert.Equal("20260912A.docx", DocumentNamingService.BuildCandidate("20260912", 1, ".docx"));
        Assert.Equal("20260912Z.docx", DocumentNamingService.BuildCandidate("20260912", 26, ".docx"));
    }

    [Fact]
    public void 基准名应为yyyyMMdd()
    {
        Assert.Equal("20260912", DocumentNamingService.BuildBaseName(SampleDate));
        Assert.Equal("20260101", DocumentNamingService.BuildBaseName(new DateTime(2026, 1, 1)));
    }

    [Fact]
    public void 超过Z应报当日编号已满()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        // 预置全部 27 个候选名（无后缀 + A..Z）
        for (var i = 0; i <= DocumentNamingService.MaxSuffixIndex; i++)
        {
            File.WriteAllText(Path.Combine(temp.Path, DocumentNamingService.BuildCandidate("20260912", i, ".docx")), "x");
        }

        var ex = Assert.Throws<DayNumberExhaustedException>(
            () => service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)));

        Assert.Contains("当日编号已满", ex.Message);
        Assert.Equal("20260912", ex.BaseName);
    }

    [Fact]
    public void 已存在同名的无关文件应自动跳到下一个后缀()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        File.WriteAllText(Path.Combine(temp.Path, "20260912.docx"), "占位");

        var result = service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent));

        Assert.Equal("20260912A.docx", result.FileName);
        Assert.Equal("占位", File.ReadAllText(Path.Combine(temp.Path, "20260912.docx")));
    }

    [Fact]
    public void 单实例并发_达上限仍全部唯一()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        var capacity = DocumentNamingService.MaxDocumentsPerDirectoryPerDay;
        var names = new ConcurrentBag<string>();

        Parallel.For(0, capacity, _ =>
            names.Add(service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)).FileName));

        Assert.Equal(capacity, names.Count);
        Assert.Equal(capacity, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(capacity, Directory.GetFiles(temp.Path).Length);

        // 第 28 个应明确报「当日编号已满」，而不是悄悄覆盖
        Assert.Throws<DayNumberExhaustedException>(
            () => service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)));
    }

    [Fact]
    public void 多实例并发_依靠原子创建兜底不重名()
    {
        using var temp = new TempDirectory();
        using var serviceA = new DocumentNamingService();
        using var serviceB = new DocumentNamingService();
        var store = new LocalFileStore();

        // 两个独立实例模拟多进程/多实例部署：进程内锁彼此不可见，只有 FileMode.CreateNew 能兜底
        var names = new ConcurrentBag<string>();

        Parallel.Invoke(
            () => Parallel.For(0, 14, _ => names.Add(
                serviceA.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)).FileName)),
            () => Parallel.For(0, 13, _ => names.Add(
                serviceB.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent)).FileName)));

        Assert.Equal(27, names.Count);
        Assert.Equal(27, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(27, Directory.GetFiles(temp.Path).Length);
    }

    [Fact]
    public void 写入内容失败应清理占位文件_且不误报重名()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        // 模拟「占位成功但写入失败」（如磁盘写满、SMB 断链）
        Assert.Throws<InvalidOperationException>(() =>
            service.CreateWithUniqueName(
                store, temp.Path, ".docx", SampleDate,
                _ => throw new InvalidOperationException("模拟写入失败")));

        // 不应留下空占位文件
        Assert.Empty(Directory.GetFiles(temp.Path));

        // 后续正常调用应拿到 20260912.docx，而不是被残留文件顶到 A
        var result = service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(SampleContent));
        Assert.Equal("20260912.docx", result.FileName);
    }

    [Fact]
    public void 目标目录不存在应自动创建()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        var nested = Path.Combine(temp.Path, "QLS2409", "SEC");
        Assert.False(Directory.Exists(nested));

        var result = service.CreateWithUniqueName(store, nested, ".docx", SampleDate, s => s.Write(SampleContent));

        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(result.FullPath));
    }

    [Fact]
    public void 写入内容应完整落盘()
    {
        using var temp = new TempDirectory();
        using var service = new DocumentNamingService();
        var store = new LocalFileStore();

        var payload = new byte[4096];
        Random.Shared.NextBytes(payload);

        var result = service.CreateWithUniqueName(store, temp.Path, ".docx", SampleDate, s => s.Write(payload));

        Assert.Equal(payload.Length, result.Size);
        Assert.Equal(payload, File.ReadAllBytes(result.FullPath));
    }
}
