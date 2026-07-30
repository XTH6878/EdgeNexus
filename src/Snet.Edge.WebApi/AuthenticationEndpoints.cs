using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Snet.Edge.Application;

namespace Snet.Edge.WebApi;

public static class AuthenticationEndpoints
{
    public static IEndpointRouteBuilder MapAuthenticationApi(this IEndpointRouteBuilder endpoints)
    {
        var auth = endpoints.MapGroup("/api/auth");

        auth.MapGet("/antiforgery", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new { token = tokens.RequestToken });
        }).AllowAnonymous();

        auth.MapPost("/login", async (
            LoginRequest request,
            IUserAccountStore accountStore,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await accountStore.AuthenticateAsync(request.Username, request.Password, cancellationToken);
            if (!result.Success)
            {
                return Results.Json(
                    new { error = result.Error, result.IsLocked, result.LockedUntil },
                    statusCode: result.IsLocked ? StatusCodes.Status423Locked : StatusCodes.Status401Unauthorized);
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.Name, result.Username!),
                new Claim(ClaimTypes.Role, result.Role!),
                new Claim("must_change_password", result.MustChangePassword ? "true" : "false")
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                claims,
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = false,
                    AllowRefresh = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                });
            return Results.Ok(new
            {
                result.Username,
                result.Role,
                result.MustChangePassword
            });
        }).AllowAnonymous();

        auth.MapGet("/session", (HttpContext context, IAntiforgery antiforgery) =>
        {
            var tokens = antiforgery.GetAndStoreTokens(context);
            return Results.Ok(new
            {
                username = context.User.Identity!.Name,
                role = context.User.FindFirstValue(ClaimTypes.Role),
                mustChangePassword = context.User.FindFirstValue("must_change_password") == "true",
                csrfToken = tokens.RequestToken
            });
        }).RequireAuthorization();

        auth.MapPost("/change-password", async (
            PasswordChangeRequest request,
            IUserAccountStore accountStore,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var result = await accountStore.ChangePasswordAsync(
                context.User.Identity!.Name!,
                request.CurrentPassword,
                request.NewPassword,
                cancellationToken);
            return result.Success ? Results.NoContent() : Results.BadRequest(new { error = result.Error });
        }).RequireAuthorization();

        auth.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        return endpoints;
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record PasswordChangeRequest(string CurrentPassword, string NewPassword);
}
