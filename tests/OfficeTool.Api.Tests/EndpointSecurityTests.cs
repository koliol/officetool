using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace OfficeTool.Api.Tests;

/// <summary>
/// 端点安全声明的静态守卫。
///
/// 为什么需要这类测试：
///   本项目用 <c>FallbackPolicy = RequireAuthenticatedUser</c> 做「默认拒绝」——
///   忘记加 <c>[Authorize]</c> 的新端点会默认受保护，这是刻意的。
///   但反过来，<c>[AllowAnonymous]</c> 是**显式打开缺口**，一旦误加或漏加，
///   后果都不是编译错误，而是运行期静默失效：
///
///     · 漏加在 /api/health 上 → Docker HEALTHCHECK 与反向代理探活全部 401，
///       容器永远 unhealthy、compose 的 service_healthy 卡死、反代不转发流量。
///       这个故障已经真实发生过一次。
///     · 误加在业务端点上 → 该端点对全网裸奔，没有任何报错。
///
///   两种都不会被「跑一遍测试」发现，只能靠断言把白名单钉死。
///   新增匿名端点时必须来改 <see cref="ExpectedAnonymousActions"/>，
///   这个"麻烦"正是本测试的价值。
/// </summary>
public class EndpointSecurityTests
{
    /// <summary>
    /// 允许匿名的端点白名单。键为 <c>控制器名.方法名</c>。
    ///
    /// <para><c>AuthController.Me</c>：前端启动第一个请求就打它来判断「我是谁 / 要不要跳登录页」，
    /// 未登录时返回 401 是正常流程的一部分。</para>
    ///
    /// <para><c>AuthController.Login</c>：登录页本身不可能要求先登录。</para>
    ///
    /// <para><c>SystemController.Health</c>：基础设施探针。出参刻意只有存活状态、
    /// 服务器本地时间与存储可用性，不含路径/项目名/配置。</para>
    /// </summary>
    private static readonly string[] ExpectedAnonymousActions =
    [
        "AuthController.Me",
        "AuthController.Login",
        "SystemController.Health",
    ];

    private static readonly Type[] HttpMethodAttributes =
    [
        typeof(HttpGetAttribute),
        typeof(HttpPostAttribute),
        typeof(HttpPutAttribute),
        typeof(HttpPatchAttribute),
        typeof(HttpDeleteAttribute),
    ];

    private static bool IsAction(MethodInfo method) =>
        HttpMethodAttributes.Any(a => method.GetCustomAttribute(a) is not null);

    /// <summary>枚举全部控制器 action。用 HTTP 方法特性识别，避免把公开的辅助方法算进来。</summary>
    private static List<(string Name, MethodInfo Method)> AllActions()
    {
        var assembly = typeof(OfficeTool.Api.Controllers.SystemController).Assembly;

        return
        [
            .. assembly.GetTypes()
                .Where(t => t is { IsAbstract: false, IsClass: true }
                            && t.Name.EndsWith("Controller", StringComparison.Ordinal))
                .SelectMany(t => t
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                    .Where(IsAction)
                    .Select(m => ($"{t.Name}.{m.Name}", m))),
        ];
    }

    /// <summary>
    /// 先确认反射真的扫到了东西。
    ///
    /// 没有这一条，一旦反射写错（扫到 0 个方法），下面两个测试都会「通过」——
    /// 这正是"永远返回成功的测试"的经典陷阱。
    /// </summary>
    [Fact]
    public void Reflection_finds_all_controllers_and_actions()
    {
        var actions = AllActions();
        var controllers = actions.Select(a => a.Name.Split('.')[0]).Distinct().ToList();

        Assert.True(actions.Count >= 40, $"只扫到 {actions.Count} 个 action，反射可能失效了。");
        Assert.Contains("AuthController", controllers);
        Assert.Contains("AdminController", controllers);
        Assert.Contains("SystemController", controllers);
        Assert.Contains("TemplatesController", controllers);
    }

    /// <summary>
    /// /api/health 必须匿名可达。
    ///
    /// 单独成一条断言而不是只靠白名单比对，是为了让失败信息直接指出病因。
    /// </summary>
    [Fact]
    public void Health_endpoint_must_be_anonymous()
    {
        var health = AllActions().SingleOrDefault(a => a.Name == "SystemController.Health");

        Assert.False(health.Method is null, "找不到 SystemController.Health —— 控制器可能被改名或删除。");

        var anonymous = health.Method!.GetCustomAttribute<AllowAnonymousAttribute>() is not null;
        Assert.True(
            anonymous,
            "SystemController.Health 必须带 [AllowAnonymous]。否则 Docker HEALTHCHECK 与反向代理探活会拿到 401，"
            + "容器永远 unhealthy、compose 的 depends_on: service_healthy 卡死、反代不转发流量。");
    }

    /// <summary>
    /// 匿名白名单必须与预期完全一致。
    ///
    /// 多一个说明有人打开了新的缺口；少一个说明某个必需匿名端点被误加了 [Authorize]。
    /// 两个方向都要失败，所以用集合相等而不是包含关系。
    /// </summary>
    [Fact]
    public void Anonymous_allowlist_matches_expectation()
    {
        var actual = AllActions()
            .Where(a => a.Method.GetCustomAttribute<AllowAnonymousAttribute>() is not null)
            .Select(a => a.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        var expected = ExpectedAnonymousActions.OrderBy(x => x, StringComparer.Ordinal).ToList();

        var unexpected = actual.Except(expected).ToList();
        var missing = expected.Except(actual).ToList();

        Assert.True(
            unexpected.Count == 0,
            "以下端点被标为匿名，但不在白名单里 —— 若确属有意，请更新 ExpectedAnonymousActions 并写明原因：\n  "
            + string.Join("\n  ", unexpected));

        Assert.True(
            missing.Count == 0,
            "以下端点应当匿名可达，但当前不是 —— 这会让对应功能彻底不可用：\n  "
            + string.Join("\n  ", missing));
    }

    /// <summary>
    /// SSO 入口必须显式绑定 Negotiate 方案。
    ///
    /// 它刻意**不带** [AllowAnonymous]：需要由 Negotiate 发起 401 挑战，
    /// 浏览器在内网区域会自动完成握手。若有人"顺手"给它加 AllowAnonymous，
    /// 挑战就不会发生，SSO 静默退化成"永远拿不到身份"。
    /// </summary>
    [Fact]
    public void Sso_endpoint_must_target_negotiate_scheme()
    {
        var sso = AllActions().SingleOrDefault(a => a.Name == "AuthController.Sso");

        Assert.False(sso.Method is null, "找不到 AuthController.Sso。");

        Assert.Null(sso.Method!.GetCustomAttribute<AllowAnonymousAttribute>());

        var authorize = sso.Method.GetCustomAttribute<AuthorizeAttribute>();
        Assert.True(
            authorize is not null && !string.IsNullOrWhiteSpace(authorize.AuthenticationSchemes),
            "AuthController.Sso 必须用 [Authorize(AuthenticationSchemes = ...)] 指定 Negotiate 方案，"
            + "否则浏览器不会收到 401 挑战，SSO 会静默失效。");
    }
}
