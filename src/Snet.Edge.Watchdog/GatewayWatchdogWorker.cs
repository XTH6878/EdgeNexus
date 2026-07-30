using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Snet.Edge.Watchdog;

public sealed record WatchdogOptions
{
    public string HealthEndpoint { get; init; } = "http://127.0.0.1:5088/health";
    public int CheckIntervalSeconds { get; init; } = 5;
    public int RequestTimeoutSeconds { get; init; } = 3;
    public int StartupGraceSeconds { get; init; } = 30;
    public int ConsecutiveFailureThreshold { get; init; } = 3;
    public int RestartCooldownSeconds { get; init; } = 60;
    public int MaxRestartsPerHour { get; init; } = 5;
    public int RestartCommandTimeoutSeconds { get; init; } = 30;
    public string RestartExecutable { get; init; } = string.Empty;
    public string[] RestartArguments { get; init; } = [];
}

public sealed class GatewayWatchdogWorker(
    IHttpClientFactory httpClientFactory,
    IOptions<WatchdogOptions> options,
    ILogger<GatewayWatchdogWorker> logger) : BackgroundService
{
    private readonly WatchdogOptions _options = Validate(options.Value);
    private readonly Queue<DateTimeOffset> _restartHistory = new();
    private int _consecutiveFailures;
    private DateTimeOffset _nextRestartAllowedAt = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Watchdog started for {HealthEndpoint}; threshold {Threshold}, maximum {MaximumRestarts} restarts/hour",
            _options.HealthEndpoint,
            _options.ConsecutiveFailureThreshold,
            _options.MaxRestartsPerHour);

        await Task.Delay(TimeSpan.FromSeconds(_options.StartupGraceSeconds), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_options.CheckIntervalSeconds));
        do
        {
            await ProbeAndRecoverAsync(stoppingToken);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task ProbeAndRecoverAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RequestTimeoutSeconds));
            using var response = await httpClientFactory.CreateClient("gateway-health")
                .GetAsync(_options.HealthEndpoint, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                if (_consecutiveFailures > 0)
                {
                    logger.LogInformation("Gateway health recovered after {FailureCount} failures", _consecutiveFailures);
                }

                _consecutiveFailures = 0;
                return;
            }

            await RegisterFailureAsync($"HTTP {(int)response.StatusCode}", cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await RegisterFailureAsync("health request timeout", cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            logger.LogWarning(exception, "Gateway health request failed");
            await RegisterFailureAsync(exception.Message, cancellationToken);
        }
    }

    private async Task RegisterFailureAsync(string reason, CancellationToken cancellationToken)
    {
        _consecutiveFailures++;
        logger.LogWarning(
            "Gateway unhealthy ({FailureCount}/{Threshold}): {Reason}",
            _consecutiveFailures,
            _options.ConsecutiveFailureThreshold,
            reason);
        if (_consecutiveFailures < _options.ConsecutiveFailureThreshold)
        {
            return;
        }

        await RestartGatewayAsync(reason, cancellationToken);
        _consecutiveFailures = 0;
    }

    private async Task RestartGatewayAsync(string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        while (_restartHistory.TryPeek(out var previous) && now - previous >= TimeSpan.FromHours(1))
        {
            _restartHistory.Dequeue();
        }

        if (now < _nextRestartAllowedAt)
        {
            logger.LogWarning("Restart suppressed until {NextRestartAllowedAt}", _nextRestartAllowedAt);
            return;
        }

        if (_restartHistory.Count >= _options.MaxRestartsPerHour)
        {
            logger.LogCritical(
                "Restart circuit breaker opened after {RestartCount} attempts/hour; manual intervention required",
                _restartHistory.Count);
            _nextRestartAllowedAt = _restartHistory.Peek().AddHours(1);
            return;
        }

        if (string.IsNullOrWhiteSpace(_options.RestartExecutable))
        {
            logger.LogCritical("Gateway needs restart because {Reason}, but RestartExecutable is not configured", reason);
            _nextRestartAllowedAt = now.AddSeconds(_options.RestartCooldownSeconds);
            return;
        }

        _restartHistory.Enqueue(now);
        _nextRestartAllowedAt = now.AddSeconds(_options.RestartCooldownSeconds);
        var startInfo = new ProcessStartInfo
        {
            FileName = _options.RestartExecutable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in _options.RestartArguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Restart process did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.RestartCommandTimeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode == 0)
            {
                logger.LogWarning("Gateway restart command completed because {Reason}", reason);
                return;
            }

            logger.LogError("Gateway restart command exited with code {ExitCode}", process.ExitCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogError("Gateway restart command timed out");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Gateway restart command failed");
        }
    }

    private static WatchdogOptions Validate(WatchdogOptions options)
    {
        if (!Uri.TryCreate(options.HealthEndpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme is not ("http" or "https") ||
            options.CheckIntervalSeconds < 1 ||
            options.RequestTimeoutSeconds < 1 ||
            options.ConsecutiveFailureThreshold < 2 ||
            options.RestartCooldownSeconds < 10 ||
            options.MaxRestartsPerHour < 1 ||
            options.RestartCommandTimeoutSeconds < 1)
        {
            throw new InvalidOperationException("Watchdog configuration is invalid.");
        }

        return options;
    }
}
