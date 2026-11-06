using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Filters;
using OfficeTool.Core.Options;

namespace OfficeTool.Api.Middleware;

/// <summary>
/// 按配置统一请求体上限。
///
/// 之前三个上传端点各写死 <c>[RequestSizeLimit(200 * 1024 * 1024)]</c>，
/// 而 Kestrel 的全局上限是按 <c>Upload:MaxSizeMB</c>（默认 100MB）+ 余量算出的 116MB。
/// 结果是：101MB 的文件先通过端点限制、再被 Kestrel 挡掉，
/// 用户看到的是一个和"文件太大"毫无关系的 500。
///
/// 现在只有一个来源（<see cref="UploadOptions.MaxSizeBytes"/>），
/// 端点不再各自声明。用 ResourceFilter 而不是动作过滤器：
/// resource 阶段在模型绑定**之前**，只有那时改 MaxRequestBodySize 才生效。
/// </summary>
public sealed class UploadSizeFilter(UploadOptions upload) : IAsyncResourceFilter
{
    /// <summary>
    /// 表单分界符、字段头与多段封装的额外开销。留 16MB 与 Kestrel 全局值保持一致，
    /// 免得出现"端点放行、服务器拒绝"的两套阈值。
    /// </summary>
    private const long MultipartOverheadBytes = 16L * 1024 * 1024;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var feature = context.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();

        // 只读表示请求体已经开始读取（例如流式上传），此时再改会抛异常
        if (feature is { IsReadOnly: false })
        {
            feature.MaxRequestBodySize = upload.MaxSizeBytes + MultipartOverheadBytes;
        }

        await next();
    }
}
