using Serilog;
using System.IO.Compression;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.ResponseCompression;
using Snet.Edge.Infrastructure;
using Snet.Edge.WebApi;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory,
    WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot")
});

var configuredDataDirectory = builder.Configuration["Gateway:DataDirectory"] ?? "data";
var dataDirectory = Path.GetFullPath(Path.IsPathRooted(configuredDataDirectory)
    ? configuredDataDirectory
    : Path.Combine(builder.Environment.ContentRootPath, configuredDataDirectory));
Directory.CreateDirectory(dataDirectory);
var databasePath = Path.Combine(dataDirectory, "gateway.db");
var connectionString = $"Data Source={databasePath};Cache=Shared;Pooling=True";
var authenticationOptions = builder.Configuration.GetSection("Authentication").Get<AuthenticationBootstrapOptions>()
    ?? new AuthenticationBootstrapOptions();
var acquisitionStabilityOptions = builder.Configuration.GetSection("AcquisitionStability").Get<AcquisitionStabilityOptions>()
    ?? new AcquisitionStabilityOptions();
var requireHttps = builder.Configuration.GetValue("Authentication:RequireHttps", false);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "EdgeNexus.Gateway")
    .WriteTo.Async(sink => sink.Console())
    .WriteTo.Async(sink => sink.File(
        new Serilog.Formatting.Json.JsonFormatter(),
        Path.Combine(dataDirectory, "logs", "gateway-.json"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30))
    .CreateLogger();

builder.Host.UseSerilog();
builder.Host.UseWindowsService(options => options.ServiceName = "EdgeNexus Gateway");
builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});
builder.Services.Configure<BrotliCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(options => options.Level = CompressionLevel.Fastest);
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "SnetEdge.Auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", policy => policy.RequireRole("Admin"));
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = "SnetEdge.Csrf";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = requireHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
});
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = false;
    options.MaximumReceiveMessageSize = 64 * 1024;
});
builder.Services.AddEdgeInfrastructure(
    connectionString,
    dataDirectory,
    authenticationOptions: authenticationOptions,
    acquisitionStabilityOptions: acquisitionStabilityOptions);
builder.Services.AddHostedService<StatusBroadcastWorker>();

var app = builder.Build();

app.UseExceptionHandler();
if (requireHttps)
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
if (builder.Configuration.GetValue("Swagger:Enabled", builder.Environment.IsDevelopment()))
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseResponseCompression();
app.UseMiddleware<WebNetworkBindingMiddleware>();
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-eval'; style-src 'self' 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; connect-src 'self' ws: wss:; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    context.Response.Headers.Append("Permissions-Policy", "camera=(), microphone=(), geolocation=(), payment=()");
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/vendor")
            ? "public,max-age=31536000,immutable"
            : "no-cache,no-store,must-revalidate";
    }
});
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<AntiforgeryProtectionMiddleware>();
app.UseMiddleware<AuditMiddleware>();
app.MapGet("/", () => Results.File(
    Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"),
    "text/html; charset=utf-8"));
app.MapGatewayMonitoring();
app.MapAuthenticationApi();
app.MapGatewayApi();
app.MapHub<StatusHub>("/hubs/status").RequireAuthorization();

await app.RunAsync();
