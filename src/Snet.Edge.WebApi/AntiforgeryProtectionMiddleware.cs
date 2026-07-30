using Microsoft.AspNetCore.Antiforgery;

namespace Snet.Edge.WebApi;

public sealed class AntiforgeryProtectionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (RequiresValidation(context.Request))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = "请求安全令牌无效或已过期，请刷新页面后重试。" });
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") &&
        !HttpMethods.IsGet(request.Method) &&
        !HttpMethods.IsHead(request.Method) &&
        !HttpMethods.IsOptions(request.Method);
}
