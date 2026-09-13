namespace OfficeTool.Desktop.Core;

/// <summary>
/// 解析形如 <c>officetool://open?path=%5C%5Cmynas%5COfficeDocs%5CData%5CQLS2409%5CSEC%5C20260912.docx</c>
/// 的自定义协议地址。
/// </summary>
/// <remarks>
/// 关键点：在这个地址里 <c>open</c> 是 <b>Host</b>，<c>path=...</c> 是 <b>Query</b>，
/// 真正的文件路径在查询串里，<b>不是</b> <c>Uri.AbsolutePath</c>。
/// 因此这里手工切分字符串，而不用 <see cref="Uri"/> 的属性，顺便兼容用户手写的几种写法：
/// <list type="bullet">
///   <item><c>officetool://open?path=...</c>（网页使用的标准形式）</item>
///   <item><c>officetool:open?path=...</c>（省略 //）</item>
///   <item><c>officetool:///open?path=...</c>（多写了斜杠）</item>
///   <item><c>officetool://open/?path=...</c>（带结尾斜杠）</item>
/// </list>
/// </remarks>
public static class ProtocolUriParser
{
    /// <summary>协议名（小写）。注册表项与浏览器地址栏里使用的就是它。</summary>
    public const string Scheme = "officetool";

    /// <summary>承载文件路径的查询参数名。</summary>
    public const string PathParameterName = "path";

    /// <summary>唯一支持的动作，位于 Host 位置。</summary>
    private const string OpenAction = "open";

    /// <summary>
    /// 判断一个命令行参数是否是本插件的协议地址（不关心内容是否合法）。
    /// 用于把「协议唤起」和「--install」这类开关区分开。
    /// </summary>
    public static bool IsProtocolUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.TrimStart();
        var colon = text.IndexOf(':');
        return colon > 0 && text.AsSpan(0, colon).Equals(Scheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 解析协议地址。任何畸形输入都返回失败结果，绝不抛异常。
    /// </summary>
    public static ProtocolParseResult Parse(string? rawUri)
    {
        if (string.IsNullOrWhiteSpace(rawUri))
        {
            return ProtocolParseResult.Fail("没有收到任何协议地址。");
        }

        var text = rawUri.Trim();

        // 1) 校验协议名。
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return ProtocolParseResult.Fail($"“{text}”不是合法的协议地址（缺少「协议名:」）。");
        }

        if (!text.AsSpan(0, colon).Equals(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolParseResult.Fail($"协议不匹配：应为 {Scheme}://，实际为 {text[..colon]}://。");
        }

        // 2) 去掉 “//” 以及手写时多写的斜杠。“open” 处在 Host 位置，它不是文件路径。
        var remainder = text[(colon + 1)..];
        if (remainder.StartsWith("//", StringComparison.Ordinal))
        {
            remainder = remainder[2..];
        }

        remainder = remainder.TrimStart('/');

        // 3) 丢弃 # 片段，再按第一个 ? 拆出「动作」与「查询串」。
        var hashIndex = remainder.IndexOf('#');
        if (hashIndex >= 0)
        {
            remainder = remainder[..hashIndex];
        }

        var queryIndex = remainder.IndexOf('?');
        var action = queryIndex >= 0 ? remainder[..queryIndex] : remainder;
        var query = queryIndex >= 0 ? remainder[(queryIndex + 1)..] : string.Empty;

        // 4) Host 段只接受 open（允许写成 "open/" 这种带结尾斜杠的形式）；
        //    允许为空，方便手工调试时省略动作。
        var actionName = action.Trim().Trim('/');
        if (actionName.Length > 0 && !actionName.Equals(OpenAction, StringComparison.OrdinalIgnoreCase))
        {
            return ProtocolParseResult.Fail(
                $"不支持的动作「{actionName}」：只支持 {Scheme}://{OpenAction}?{PathParameterName}=...。");
        }

        // 5) 在查询串里找 path 参数（大小写不敏感，取第一个）。
        string? rawPath = null;
        foreach (var segment in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equalsIndex = segment.IndexOf('=');
            var name = equalsIndex >= 0 ? segment[..equalsIndex] : segment;
            if (!name.Equals(PathParameterName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            rawPath = equalsIndex >= 0 ? segment[(equalsIndex + 1)..] : string.Empty;
            break;
        }

        if (rawPath is null)
        {
            return ProtocolParseResult.Fail($"协议地址里缺少 {PathParameterName} 参数。");
        }

        // 6) 百分号解码。Uri.UnescapeDataString 遇到非法转义既不抛异常也不报错，
        //    只会原样保留，所以必须先自己校验一遍，否则畸形地址会被静默地当成合法路径。
        if (!TryUnescape(rawPath, out var decoded, out var unescapeError))
        {
            return ProtocolParseResult.Fail(unescapeError!);
        }

        // 校验顺序很重要：先查控制字符，再查空白。
        // 否则 %0A（换行）会先被判成「为空」，用户看到的提示就指错了方向。
        if (ContainsControlCharacter(decoded))
        {
            return ProtocolParseResult.Fail($"{PathParameterName} 参数包含非法控制字符。");
        }

        if (string.IsNullOrWhiteSpace(decoded))
        {
            return ProtocolParseResult.Fail($"{PathParameterName} 参数为空。");
        }

        return ProtocolParseResult.Ok(decoded);
    }

    /// <summary>
    /// 校验并执行百分号解码：<c>%</c> 后面必须紧跟两位十六进制数字。
    /// </summary>
    private static bool TryUnescape(string value, out string decoded, out string? error)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '%')
            {
                continue;
            }

            if (i + 2 >= value.Length || !IsHexDigit(value[i + 1]) || !IsHexDigit(value[i + 2]))
            {
                decoded = string.Empty;
                error = "非法的百分号转义：% 后面必须跟两位十六进制数字。";
                return false;
            }

            i += 2;
        }

        // 说明：这里刻意不把 '+' 当作空格，因为 '+' 在 Windows 文件名里是合法字符，
        // 而网页侧对空格会编码成 %20。
        decoded = Uri.UnescapeDataString(value);
        error = null;
        return true;
    }

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static bool ContainsControlCharacter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsControl(c))
            {
                return true;
            }
        }

        return false;
    }
}
