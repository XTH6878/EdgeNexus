using Snet.Edge.Application;

namespace Snet.Edge.WebApi;

public sealed class WebNetworkBindingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, INetworkConfigurationService networkConfiguration)
    {
        if (networkConfiguration.IsWebRequestAllowed(context.Connection.LocalIpAddress))
        {
            await next(context);
            return;
        }

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            error = "当前请求未通过已绑定的Web管理网卡进入。请使用绑定网卡的IPv4地址访问，或在本机通过127.0.0.1恢复配置。"
        });
    }
}
