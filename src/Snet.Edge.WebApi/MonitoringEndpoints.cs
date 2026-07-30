using System.Globalization;
using System.Text;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.WebApi;

public static class MonitoringEndpoints
{
    public static IEndpointRouteBuilder MapGatewayMonitoring(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health/live", () => Results.Ok(new
        {
            status = "alive",
            timestamp = DateTimeOffset.UtcNow
        })).AllowAnonymous();

        endpoints.MapGet("/health/ready", CheckReadinessAsync).AllowAnonymous();
        endpoints.MapGet("/health", CheckReadinessAsync).AllowAnonymous();
        endpoints.MapGet("/metrics", WriteMetricsAsync).AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> CheckReadinessAsync(
        IInitializationState initializationState,
        IGatewayConfigurationStore configurationStore,
        IResourceGuard resourceGuard,
        CancellationToken cancellationToken)
    {
        if (!initializationState.IsReady)
        {
            return Results.Json(new
            {
                status = "not_ready",
                initializationState.Error,
                timestamp = DateTimeOffset.UtcNow
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        if (resourceGuard.Current.Level == ResourceProtectionLevel.Critical)
        {
            return Results.Json(new
            {
                status = "degraded",
                resource = resourceGuard.Current,
                timestamp = DateTimeOffset.UtcNow
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await configurationStore.GetSnapshotAsync(timeout.Token);
            return Results.Ok(new
            {
                status = "ready",
                initializationState.ReadyAt,
                resource = resourceGuard.Current,
                timestamp = DateTimeOffset.UtcNow
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Results.Json(new
            {
                status = "not_ready",
                dependency = "sqlite",
                error = exception.GetType().Name,
                timestamp = DateTimeOffset.UtcNow
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    private static async Task WriteMetricsAsync(
        HttpContext context,
        IGatewayMetrics metrics,
        IRuntimeStatusStore statusStore,
        IResourceGuard resourceGuard,
        IMqttOutboxMonitor outboxMonitor)
    {
        var output = new StringBuilder(2_048);
        var snapshot = metrics.Snapshot;
        AppendCounter(output, "snet_gateway_acquisition_cycles_succeeded_total", snapshot.AcquisitionCyclesSucceeded);
        AppendCounter(output, "snet_gateway_acquisition_cycles_failed_total", snapshot.AcquisitionCyclesFailed);
        AppendCounter(output, "snet_gateway_tag_samples_read_total", snapshot.TagSamplesRead);
        AppendCounter(output, "snet_gateway_acquisition_duration_milliseconds_total", snapshot.AcquisitionDurationMilliseconds);
        AppendCounter(output, "snet_gateway_mqtt_messages_published_total", snapshot.MqttMessagesPublished);
        AppendCounter(output, "snet_gateway_mqtt_publish_failures_total", snapshot.MqttPublishFailures);
        AppendGauge(output, "snet_gateway_signalr_connections", snapshot.SignalRConnections);

        var statuses = statusStore.GetAll();
        AppendGauge(output, "snet_gateway_devices", statuses.Count);
        output.Append("# TYPE snet_gateway_devices_by_state gauge").AppendLine();
        foreach (var state in Enum.GetValues<DeviceConnectionState>())
        {
            var count = statuses.Count(status => status.State == state);
            AppendSample(output, "snet_gateway_devices_by_state", count, ("state", state.ToString().ToLowerInvariant()));
        }

        var resource = resourceGuard.Current;
        AppendGauge(output, "snet_gateway_process_cpu_percent", resource.CpuPercent);
        AppendGauge(output, "snet_gateway_process_working_set_bytes", resource.WorkingSetMb * 1_048_576D);
        AppendGauge(output, "snet_gateway_managed_heap_bytes", resource.ManagedHeapMb * 1_048_576D);
        AppendGauge(output, "snet_gateway_disk_used_percent", resource.DiskUsedPercent);
        AppendGauge(output, "snet_gateway_disk_free_bytes", resource.DiskFreeMb * 1_048_576D);
        AppendGauge(output, "snet_gateway_process_threads", resource.ThreadCount);
        AppendGauge(output, "snet_gateway_resource_protection_level", (int)resource.Level);

        var scrapeError = 0;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var outbox = await outboxMonitor.GetStatisticsAsync(timeout.Token);
            AppendGauge(output, "snet_gateway_mqtt_outbox_pending", outbox.PendingMessages);
            AppendGauge(output, "snet_gateway_mqtt_dead_letters", outbox.DeadLetterMessages);
            AppendGauge(output, "snet_gateway_mqtt_oldest_pending_age_seconds", outbox.OldestPendingAgeSeconds);
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            scrapeError = 1;
        }

        AppendGauge(output, "snet_gateway_metrics_scrape_error", scrapeError);
        context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
        await context.Response.WriteAsync(output.ToString(), context.RequestAborted);
    }

    private static void AppendCounter(StringBuilder output, string name, long value)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" counter");
        AppendSample(output, name, value);
    }

    private static void AppendGauge(StringBuilder output, string name, double value, params (string Name, string Value)[] labels)
    {
        output.Append("# TYPE ").Append(name).AppendLine(" gauge");
        AppendSample(output, name, value, labels);
    }

    private static void AppendSample(StringBuilder output, string name, double value, params (string Name, string Value)[] labels)
    {
        output.Append(name);
        if (labels.Length != 0)
        {
            output.Append('{');
            for (var index = 0; index < labels.Length; index++)
            {
                if (index != 0)
                {
                    output.Append(',');
                }

                output.Append(labels[index].Name).Append("=\"").Append(labels[index].Value).Append('"');
            }

            output.Append('}');
        }

        output.Append(' ').AppendLine(value.ToString("G17", CultureInfo.InvariantCulture));
    }
}
