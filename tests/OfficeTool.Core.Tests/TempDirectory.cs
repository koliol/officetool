namespace OfficeTool.Core.Tests;

/// <summary>测试用临时目录，释放时递归清理。</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "officetool-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    public string Combine(params string[] segments) =>
        System.IO.Path.Combine([Path, .. segments]);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
            // 测试清理失败不应影响结果
        }
    }
}
