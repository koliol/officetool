using Microsoft.AspNetCore.DataProtection;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Services;

/// <summary>
/// 请求上下文（客户端 IP / UserAgent），用于操作日志字段（§6.1）。
///
/// IP 的取值规则（曾有审计日志被任意请求头污染的事故，规则收紧）：
/// 只有当请求直连地址在 <c>Security:TrustedProxies</c> 里时，
/// 才采信 <c>X-Forwarded-For</c>；否则一律用直连地址。
/// 内网直连部署不用配任何东西，行为与从前不同的一点是：伪造 XFF 不再生效。
/// </summary>
public sealed class RequestContext(IHttpContextAccessor accessor, SecurityOptions security)
{
    private readonly IHttpContextAccessor _accessor = accessor;
    private readonly SecurityOptions _security = security;

    public string? Ip
    {
        get
        {
            var context = _accessor.HttpContext;
            if (context is null)
            {
                return null;
            }

            var remote = context.Connection.RemoteIpAddress?.ToString();

            if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwarded) &&
                IsTrustedProxy(remote))
            {
                var first = forwarded.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(first))
                {
                    return first.Trim();
                }
            }

            return remote;
        }
    }

    private bool IsTrustedProxy(string? remoteIp)
    {
        if (string.IsNullOrWhiteSpace(remoteIp) || _security.TrustedProxies.Length == 0)
        {
            return false;
        }

        return _security.TrustedProxies.Contains(remoteIp, StringComparer.OrdinalIgnoreCase);
    }

    public string? UserAgent => _accessor.HttpContext?.Request.Headers.UserAgent.ToString();

    /// <summary>
    /// 当前请求的身份。<see cref="IAccessControlService"/> 用它解析权限。
    /// Cookie 通道（浏览器）与 Bearer 通道（脚本）产生的 principal 结构一致。
    /// </summary>
    public System.Security.Claims.ClaimsPrincipal? User => _accessor.HttpContext?.User;

    /// <summary>
    /// 当前操作人登录名。鉴权关闭时为 null。
    /// Linux 上 Negotiate 给的是 <c>user@DOMAIN</c>，这里统一成不带域前缀的 sAMAccountName。
    /// </summary>
    public string? UserName
    {
        get
        {
            var raw = _accessor.HttpContext?.User?.Identity?.Name;
            return string.IsNullOrWhiteSpace(raw) ? null : AccessControlService.NormalizeAccountName(raw);
        }
    }
}

/// <summary>
/// 凭据加解密（设计文档 §7.2）：密码以密文入库/入配置，运行时解密后仅驻留内存。
/// </summary>
public sealed class CredentialProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("OfficeTool.SmbCredential.v1");

    public string Protect(string plainText) => _protector.Protect(plainText);

    public string Unprotect(string cipherText) => _protector.Unprotect(cipherText);

    /// <summary>尝试解密；配置里是明文或空值时给出明确降级而不是崩溃。</summary>
    public bool TryUnprotect(string? cipherText, out string? plainText, out string? error)
    {
        plainText = null;
        error = null;

        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return false;
        }

        try
        {
            plainText = _protector.Unprotect(cipherText);
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            error = "Smb:EncryptedPassword 解密失败，可能密钥环不匹配或值未加密。";
            _ = ex;
            return false;
        }
    }

    /// <summary>启动期解析存储密码：优先按密文解，失败则视为明文原样使用并记录告警。</summary>
    public static string? ResolvePassword(StorageOptions options, CredentialProtector protector, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(options.EncryptedPassword))
        {
            return null;
        }

        if (protector.TryUnprotect(options.EncryptedPassword, out var plain, out var error))
        {
            return plain;
        }

        logger.LogWarning(
            "{Error} 已回退为按明文处理。生产环境请改用 /api/system/protect 生成密文。",
            error);

        return options.EncryptedPassword;
    }
}
