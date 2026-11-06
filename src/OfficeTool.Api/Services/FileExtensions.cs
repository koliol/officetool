using OfficeTool.Core.Services;

namespace OfficeTool.Api.Services;

/// <summary>
/// 扩展名白名单判定。
///
/// 附件与规则两份列表原本各写了一份「规范化 + 忽略大小写比较」，
/// 内容逐字相同。收在这里的理由不是省几行，而是**只留一处判断口径**：
/// 下次若要支持「复合扩展名（.tar.gz）」或补一个前导点的容错，
/// 改一个地方即可，不会出现「附件拦住了、规则没拦住」。
/// </summary>
internal static class FileExtensions
{
    /// <summary>扩展名是否在白名单内（忽略大小写，允许两边带或不带前导点）。</summary>
    public static bool IsAllowed(string extension, IEnumerable<string> allowed) =>
        allowed.Any(e => string.Equals(Normalize(e), Normalize(extension), StringComparison.OrdinalIgnoreCase));

    private static string Normalize(string extension) =>
        extension.StartsWith('.') ? extension : "." + extension;
}
