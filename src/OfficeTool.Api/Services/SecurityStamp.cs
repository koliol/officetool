using System.Security.Cryptography;
using System.Text;
using OfficeTool.Core.Models;

namespace OfficeTool.Api.Services;

/// <summary>
/// 身份票与服务端用户状态的绑定值（等价于 ASP.NET Identity 的 SecurityStamp）。
///
/// ── 要解决的事故 ──────────────────────────────────────────────────────
/// Cookie 是自包含的：一旦签发就在 <c>CookieHours</c> 内始终有效（原先还开了
/// <c>SlidingExpiration</c>，等于只要在用就永不失效）。管理员停用一个账号、
/// 重置某个人的密码、或把某人撤下超管位之后，对方手里的旧 Cookie 仍然畅通无阻——
/// 「停用」在界面上显示已生效，实际要等 8 小时。
///
/// ── 为什么是「派生值」而不是新增一列 ──────────────────────────────────
/// 需要在数据库里存一个可轮换的随机串，就得配一次 EF 迁移；而派生值同样能表达
/// 「凭据或权限变了」，且**不碰 schema**，老库直接生效。
/// 参与计算的三项正好覆盖三条失效路径：
/// <list type="bullet">
///   <item>密码哈希变化 → 改密 / 重置密码后旧票作废</item>
///   <item>IsEnabled 变化 → 停用 / 重新启用后旧票作废</item>
///   <item>IsSystemAdmin 变化 → 超管位是写在 Cookie 的 Role 声明里的，
///         降级后旧票里的 Role 还在，不换票等于降级无效</item>
/// </list>
/// 改显示名、改密保标记这类不影响权限的字段不参与计算，避免顺手改个备注就让全员掉线。
///
/// 局限：域账户的密码在域控制器上，改域密码不会改变这里（本来也不该由本系统感知）。
/// </summary>
public static class SecurityStamp
{
    /// <summary>Cookie 中承载该值的声明类型。</summary>
    public const string ClaimType = "officetool:stamp";

    /// <summary>按用户当前状态计算票值。同一状态恒得同一值，比较用序号比较。</summary>
    public static string Compute(User user)
    {
        ArgumentNullException.ThrowIfNull(user);

        // 逐段拼接后取 SHA-256：各段之间用不会出现在登录名里的分隔符，
        // 避免 "ab"+"c" 与 "a"+"bc" 拼出同一个串这种低级碰撞。
        var material = string.Join(
            '\u001f',
            user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            user.UserName.ToLowerInvariant(),
            user.PasswordHash ?? "-",
            user.IsEnabled ? "1" : "0",
            user.IsSystemAdmin ? "1" : "0");

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }

    /// <summary>票值是否与库里的当前状态一致。空值一律视为不一致（旧版本签发的票）。</summary>
    public static bool Matches(string? claimValue, User user) =>
        !string.IsNullOrEmpty(claimValue) &&
        string.Equals(claimValue, Compute(user), StringComparison.Ordinal);
}
