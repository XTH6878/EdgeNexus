using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.WebApi;

public sealed class StatusHub(
    IRuntimeStatusStore statusStore,
    IDeviceHealthStateStore healthStateStore,
    IRuntimeTagSampleStore tagSampleStore,
    IAcquisitionControl acquisitionControl,
    IResourceGuard resourceGuard,
    IGatewayMetrics metrics) : Hub
{
    internal static string DeviceGroupName(Guid deviceId) => $"device:{deviceId:N}";

    public override async Task OnConnectedAsync()
    {
        metrics.SignalRConnected();
        try
        {
            await Clients.Caller.SendAsync(
                "runtimeStatus",
                new
                {
                    isRunning = acquisitionControl.IsRunning,
                    devices = statusStore.GetAll(),
                    deviceHealth = healthStateStore.GetAll(),
                    resource = resourceGuard.Current,
                    isSnapshot = true,
                    timestamp = DateTimeOffset.UtcNow
                },
                Context.ConnectionAborted);
            await base.OnConnectedAsync();
        }
        catch
        {
            metrics.SignalRDisconnected();
            throw;
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        metrics.SignalRDisconnected();
        await base.OnDisconnectedAsync(exception);
    }

    public async Task SubscribeDevice(Guid deviceId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, DeviceGroupName(deviceId), Context.ConnectionAborted);
        await Clients.Caller.SendAsync(
            "tagSamples",
            tagSampleStore.GetByDevice(deviceId),
            Context.ConnectionAborted);
    }

    public Task UnsubscribeDevice(Guid deviceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, DeviceGroupName(deviceId), Context.ConnectionAborted);
}

public sealed class StatusBroadcastWorker(
    IHubContext<StatusHub> hubContext,
    IRuntimeStatusStore statusStore,
    IDeviceHealthStateStore healthStateStore,
    IRuntimeTagSampleStore tagSampleStore,
    IAcquisitionControl acquisitionControl,
    IResourceGuard resourceGuard) : BackgroundService
{
    private readonly Dictionary<Guid, DeviceRuntimeStatus> _lastStatuses = [];
    private readonly Dictionary<Guid, DeviceHealthStatus> _lastHealthStatuses = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(
            BroadcastStatusesAsync(stoppingToken),
            BroadcastTagSamplesAsync(stoppingToken));
    }

    private async Task BroadcastStatusesAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changes = GetChanges(statusStore.GetAll());
                await hubContext.Clients.All.SendAsync(
                    "runtimeStatus",
                    new
                    {
                        isRunning = acquisitionControl.IsRunning,
                        devices = changes,
                        deviceHealth = GetHealthChanges(healthStateStore.GetAll()),
                        resource = resourceGuard.Current,
                        isSnapshot = false,
                        timestamp = DateTimeOffset.UtcNow
                    },
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task BroadcastTagSamplesAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var changes = tagSampleStore.TakeChanges();
                if (changes.Count == 0)
                {
                    continue;
                }

                foreach (var deviceSamples in changes.GroupBy(sample => sample.DeviceId))
                {
                    await hubContext.Clients
                        .Group(StatusHub.DeviceGroupName(deviceSamples.Key))
                        .SendAsync("tagSamples", deviceSamples.ToArray(), stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private IReadOnlyCollection<DeviceRuntimeStatus> GetChanges(IReadOnlyCollection<DeviceRuntimeStatus> current)
    {
        var changes = new List<DeviceRuntimeStatus>();
        foreach (var status in current)
        {
            if (!_lastStatuses.TryGetValue(status.DeviceId, out var previous) || previous != status)
            {
                changes.Add(status);
                _lastStatuses[status.DeviceId] = status;
            }
        }

        return changes;
    }

    private IReadOnlyCollection<DeviceHealthStatus> GetHealthChanges(IReadOnlyCollection<DeviceHealthStatus> current)
    {
        var changes = new List<DeviceHealthStatus>();
        foreach (var status in current)
        {
            if (!_lastHealthStatuses.TryGetValue(status.DeviceId, out var previous) || previous != status)
            {
                changes.Add(status);
                _lastHealthStatuses[status.DeviceId] = status;
            }
        }

        return changes;
    }
}
