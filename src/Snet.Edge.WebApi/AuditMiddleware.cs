using System.Diagnostics;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.WebApi;

public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IAuditLogStore auditLogStore)
    {
        var startedAt = Stopwatch.GetTimestamp();
        await next(context);

        if (HttpMethods.IsGet(context.Request.Method) ||
            HttpMethods.IsHead(context.Request.Method) ||
            HttpMethods.IsOptions(context.Request.Method))
        {
            return;
        }

        auditLogStore.TryWrite(new AuditEvent(
            Guid.NewGuid(),
            context.TraceIdentifier,
            context.Request.Method,
            context.Request.Path.Value ?? "/",
            context.Response.StatusCode,
            context.User.Identity?.Name ?? "anonymous",
            context.Connection.RemoteIpAddress?.ToString(),
            (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
            DateTimeOffset.UtcNow));
    }
}
