using Serilog;
using Snet.Edge.Watchdog;

var dataDirectory = Path.GetFullPath(Environment.GetEnvironmentVariable("SNET_WATCHDOG_DATA") ?? "watchdog-data");
Directory.CreateDirectory(dataDirectory);

Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Application", "Snet.Edge.Watchdog")
    .WriteTo.Async(sink => sink.Console())
    .WriteTo.Async(sink => sink.File(
        new Serilog.Formatting.Json.JsonFormatter(),
        Path.Combine(dataDirectory, "logs", "watchdog-.json"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30))
    .CreateLogger();

try
{
    await Host.CreateDefaultBuilder(args)
        .UseSerilog()
        .ConfigureServices((context, services) =>
        {
            services.Configure<WatchdogOptions>(context.Configuration.GetSection("Watchdog"));
            services.AddHttpClient("gateway-health", client =>
            {
                client.Timeout = Timeout.InfiniteTimeSpan;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Snet.Edge.Watchdog/1.1");
            });
            services.AddHostedService<GatewayWatchdogWorker>();
        })
        .RunConsoleAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "Watchdog terminated unexpectedly");
}
finally
{
    await Log.CloseAndFlushAsync();
}
