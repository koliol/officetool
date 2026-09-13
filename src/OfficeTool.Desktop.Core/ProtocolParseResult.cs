namespace OfficeTool.Desktop.Core;

/// <summary>
/// <c>officetool://</c> 协议地址的解析结果。
/// 解析失败时不抛异常，而是通过 <see cref="Error"/> 返回中文原因，方便调用方直接提示用户。
/// </summary>
public sealed class ProtocolParseResult
{
    private ProtocolParseResult(bool success, string? path, string? error)
    {
        Success = success;
        Path = path;
        Error = error;
    }

    /// <summary>是否解析成功。</summary>
    public bool Success { get; }

    /// <summary>解码后的本地路径或 UNC 路径；解析失败时为 <c>null</c>。</summary>
    public string? Path { get; }

    /// <summary>失败原因（中文）；解析成功时为 <c>null</c>。</summary>
    public string? Error { get; }

    /// <summary>构造一个成功结果。</summary>
    public static ProtocolParseResult Ok(string path) => new(true, path, null);

    /// <summary>构造一个失败结果。</summary>
    public static ProtocolParseResult Fail(string error) => new(false, null, error);

    /// <inheritdoc />
    public override string ToString() => Success ? $"成功：{Path}" : $"失败：{Error}";
}
