using OfficeTool.Api.Services;
using OfficeTool.Core.Models;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 身份票与服务端状态的一致性判定。
///
/// 这组规则决定了「改密 / 停用 / 降级之后，对方手里的旧 Cookie 还有没有效」。
/// 判错的后果是静默的：界面上显示已停用，对方却照常进得来，只能靠断言守住。
/// </summary>
public class SecurityStampTests
{
    private static User MakeUser() => new()
    {
        Id = 7,
        UserName = "alice",
        PasswordHash = "HASH-A",
        IsEnabled = true,
    };

    [Fact]
    public void 状态不变时票值稳定()
    {
        var user = MakeUser();

        Assert.Equal(SecurityStamp.Compute(user), SecurityStamp.Compute(user));
        Assert.True(SecurityStamp.Matches(SecurityStamp.Compute(user), user));
    }

    [Fact]
    public void 改密后旧票失效()
    {
        var user = MakeUser();
        var before = SecurityStamp.Compute(user);

        user.PasswordHash = "HASH-B";

        Assert.NotEqual(before, SecurityStamp.Compute(user));
        Assert.False(SecurityStamp.Matches(before, user));
    }

    [Fact]
    public void 停用后旧票失效()
    {
        var user = MakeUser();
        var before = SecurityStamp.Compute(user);

        user.IsEnabled = false;

        Assert.False(SecurityStamp.Matches(before, user));
    }

    [Fact]
    public void 升降超管位后旧票失效()
    {
        var user = MakeUser();
        var before = SecurityStamp.Compute(user);

        user.IsSystemAdmin = true;

        // 超管位写在 Cookie 的 Role 声明里：不换票的话降级要等票过期才生效
        Assert.False(SecurityStamp.Matches(before, user));
    }

    [Fact]
    public void 改显示名不应让登录态失效()
    {
        var user = MakeUser();
        var before = SecurityStamp.Compute(user);

        user.DisplayName = "张三";
        user.MustChangePassword = true;

        // 顺手改个备注就把所有人踢下线，会让人以为系统不稳
        Assert.True(SecurityStamp.Matches(before, user));
    }

    [Fact]
    public void 旧版本签发的票没有该声明视为不匹配()
    {
        var user = MakeUser();

        Assert.False(SecurityStamp.Matches(null, user));
        Assert.False(SecurityStamp.Matches(string.Empty, user));
    }
}
