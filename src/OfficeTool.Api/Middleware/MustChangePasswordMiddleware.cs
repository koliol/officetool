using OfficeTool.Api.Contracts;
using OfficeTool.Core.Models;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Middleware;

/// <summary>
/// 本次请求已校验过的用户实体。
///
/// Cookie 的 <c>OnValidatePrincipal</c> 每个请求都会回查一次用户（见 Program.cs），
/// 把结果放在 <see cref="HttpContext.Items"/> 里，后面的中间件直接复用，
/// 不再为了一个布尔位再查一次库。
/// </summary>
public static class RequestUserCache
{
    public const string Key = "officetool:request-user";
}

/// <summary>
/// 「首次登录必须改密」的服务端强制点。
///
/// 之前这个约束只写在前端：引导部署的管理员初始密码会打进启动日志，
/// 只要关掉弹窗（或干脆用 curl 直连接口）就能一直用下去 ——
/// 等于系统里长期存在一个写在日志里的固定管理员密码。
///
/// 这里只放行「身份相关」与「改密」两类接口，其余 /api 请求一律 409。
/// 静态资源与 SPA 页面不拦：否则连改密弹窗都渲染不出来。
///
/// 注意：Bearer 令牌请求不进这道门（令牌通道没有走 Cookie 校验，
/// 也就没有本次请求的用户实体）。令牌只能由已登录用户在网页上签发，
/// 而那时改密提示已经生效，所以这个缺口不构成绕过路径。
/// </summary>
public sealed class MustChangePasswordMiddleware(RequestDelegate next, AuthOptions authOptions)
{
    private readonly AuthOptions _auth = authOptions;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_auth.Enabled
            || context.User.Identity?.IsAuthenticated != true
            || context.Items[RequestUserCache.Key] is not User user
            || !user.MustChangePassword
            || !IsBlocked(context.Request.Path))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status409Conflict;
        context.Response.ContentType = "application/json; charset=utf-8";

        await context.Response.WriteAsJsonAsync(new ApiError(
            "must_change_password",
            "首次登录必须先修改密码。请在弹出的窗口中设置新密码后继续。"));
    }

    /// <summary>
    /// 是否拦截。只拦 /api，且放行 /api/auth/*（me / rights/map / change-password / logout）。
    /// </summary>
    internal static bool IsBlocked(PathString path)
    {
        if (!path.StartsWithSegments("/api"))
        {
            return false;
        }

        return !path.StartsWithSegments("/api/auth");
    }
}
