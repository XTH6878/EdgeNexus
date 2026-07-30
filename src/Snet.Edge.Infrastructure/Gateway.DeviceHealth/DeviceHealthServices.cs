using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure.GatewayDeviceHealth;

public sealed class DeviceErrorManager : IDeviceErrorManager
{
    public int Resolve(DeviceRuntimeStatus status, int communicationFailureCode) =>
        status.State == DeviceConnectionState.Online ||
        status.State == DeviceConnectionState.Degraded && status.LastSuccessAt is not null
            ? DeviceErrorCodes.Normal
            : communicationFailureCode;
}

public sealed class SystemTagBuilder : ISystemTagBuilder
{
    public TagData BuildDeviceErrorTag(string deviceId, string tagPrefix, int errorCode)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            throw new ArgumentException("设备ID不能为空。", nameof(deviceId));
        }

        if (string.IsNullOrWhiteSpace(tagPrefix))
        {
            throw new ArgumentException("设备通讯状态Tag前缀不能为空。", nameof(tagPrefix));
        }

        return new TagData($"{tagPrefix.Trim().TrimEnd('_')}_{deviceId.Trim()}", errorCode, DataQuality.Good);
    }
}

public sealed class DeviceHealthStateStore : IDeviceHealthStateStore
{
    private readonly ConcurrentDictionary<Guid, DeviceHealthStatus> _states = new();

    public IReadOnlyCollection<DeviceHealthStatus> GetAll() => _states.Values.ToArray();

    public DeviceHealthStatus? Get(Guid deviceId) => _states.GetValueOrDefault(deviceId);

    public bool SetIfChanged(DeviceHealthStatus status)
    {
        while (true)
        {
            if (!_states.TryGetValue(status.DeviceId, out var previous))
            {
                if (_states.TryAdd(status.DeviceId, status))
                {
                    return true;
                }

                continue;
            }

            if (previous.DeviceExternalId == status.DeviceExternalId &&
                previous.DeviceName == status.DeviceName &&
                previous.ErrorCode == status.ErrorCode &&
                previous.ConnectionState == status.ConnectionState &&
                previous.Error == status.Error)
            {
                return false;
            }

            if (_states.TryUpdate(status.DeviceId, status, previous))
            {
                return true;
            }
        }
    }

    public void Remove(Guid deviceId) => _states.TryRemove(deviceId, out _);
}

public sealed class DeviceHealthMonitor(
    IDeviceStatusEventSource statusEvents,
    IRuntimeStatusStore runtimeStatuses,
    IDeviceHealthStateStore healthStates,
    IDeviceErrorManager errorManager,
    ISystemTagBuilder systemTagBuilder,
    IRuntimeTagSampleStore tagSampleStore,
    IGatewayConfigurationStore configurationStore,
    ITemplateResolver templateResolver,
    IMqttMessagePublisher publisher,
    ILogger<DeviceHealthMonitor> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, DeviceContext> _devices = new();
    private readonly ConcurrentDictionary<Guid, int> _publishedContextFingerprints = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RefreshConfigurationAsync(stoppingToken);
        await Task.WhenAll(
            ConsumeStatusChangesAsync(stoppingToken),
            ReconcileAsync(stoppingToken));
    }

    private async Task ConsumeStatusChangesAsync(CancellationToken cancellationToken)
    {
        await foreach (var status in statusEvents.ReadAllAsync(cancellationToken))
        {
            await ProcessStatusAsync(status, cancellationToken);
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            await RefreshConfigurationAsync(cancellationToken);
            foreach (var status in runtimeStatuses.GetAll())
            {
                await ProcessStatusAsync(status, cancellationToken);
            }
        }
    }

    private async Task RefreshConfigurationAsync(CancellationToken cancellationToken)
    {
        var snapshot = await configurationStore.GetSnapshotAsync(cancellationToken);
        var groups = snapshot.Groups.Where(group => group.Enabled).ToDictionary(group => group.Id);
        var tagsByDevice = snapshot.Tags
            .Where(tag => tag.Enabled)
            .ToLookup(tag => tag.DeviceId);
        var activeDeviceIds = new HashSet<Guid>();
        var runtimeByDevice = runtimeStatuses.GetAll().ToDictionary(status => status.DeviceId);
        var initializedHealthIds = healthStates.GetAll().Select(state => state.DeviceId).ToHashSet();

        foreach (var device in snapshot.Devices.Where(device => device.Enabled))
        {
            if (!groups.TryGetValue(device.GroupId, out var group))
            {
                continue;
            }

            activeDeviceIds.Add(device.Id);
            var activeTagIds = tagsByDevice[device.Id].Select(tag => tag.Id).ToHashSet();
            var template = await templateResolver.ResolveAsync(device.MqttTemplateId, cancellationToken);
            _devices[device.Id] = new DeviceContext(
                device,
                group,
                activeTagIds,
                snapshot.Mqtt.Enabled,
                template.DeviceErrorTagPrefix,
                template.CommunicationFailureCode,
                CreateContextFingerprint(
                    device,
                    group,
                    activeTagIds,
                    snapshot.Mqtt.Enabled,
                    template));
            if (!initializedHealthIds.Contains(device.Id))
            {
                var initial = runtimeByDevice.GetValueOrDefault(device.Id) ?? new DeviceRuntimeStatus(
                    device.Id,
                    DeviceConnectionState.Offline,
                    DateTimeOffset.UtcNow,
                    null,
                    "设备状态等待初始化。")
                {
                    FaultKind = DeviceFaultKind.Offline
                };
                await ProcessStatusAsync(initial, cancellationToken);
            }
        }

        foreach (var deviceId in _devices.Keys)
        {
            if (!activeDeviceIds.Contains(deviceId))
            {
                _devices.TryRemove(deviceId, out _);
                _publishedContextFingerprints.TryRemove(deviceId, out _);
                healthStates.Remove(deviceId);
            }
        }
    }

    private async Task ProcessStatusAsync(DeviceRuntimeStatus status, CancellationToken cancellationToken)
    {
        if (!_devices.TryGetValue(status.DeviceId, out var context))
        {
            return;
        }

        var deviceExternalId = ResolveDeviceId(context.Device);
        var errorCode = errorManager.Resolve(status, context.CommunicationFailureCode);
        var health = new DeviceHealthStatus(
            context.Device.Id,
            deviceExternalId,
            context.Device.Name,
            errorCode,
            status.State,
            status.ChangedAt,
            status.Error);
        var previousHealth = healthStates.Get(context.Device.Id);
        healthStates.SetIfChanged(health);
        var statusValueChanged = previousHealth is null ||
                                 previousHealth.DeviceExternalId != deviceExternalId ||
                                 previousHealth.DeviceName != context.Device.Name ||
                                 previousHealth.ErrorCode != errorCode;
        if (!context.MqttEnabled)
        {
            _publishedContextFingerprints.TryRemove(context.Device.Id, out _);
            return;
        }

        var contextChanged = !_publishedContextFingerprints.TryGetValue(context.Device.Id, out var publishedFingerprint) ||
                             publishedFingerprint != context.Fingerprint;
        if (!statusValueChanged && !contextChanged)
        {
            return;
        }

        try
        {
            var latestSamples = tagSampleStore.GetByDevice(context.Device.Id)
                .Where(sample => context.ActiveTagIds.Contains(sample.TagId))
                .OrderBy(sample => sample.TagName, StringComparer.Ordinal)
                .ToArray();
            var tags = new List<TagData>(latestSamples.Length + 1)
            {
                systemTagBuilder.BuildDeviceErrorTag(deviceExternalId, context.DeviceErrorTagPrefix, errorCode)
            };
            tags.AddRange(latestSamples.Select(sample => new TagData(sample.TagName, sample.Value, sample.Quality)));
            var timestamp = latestSamples.Length == 0
                ? status.ChangedAt
                : latestSamples.Max(sample => sample.Timestamp > status.ChangedAt ? sample.Timestamp : status.ChangedAt);
            var data = new DeviceData(
                deviceExternalId,
                context.Device.Name,
                context.Device.Host,
                context.Device.Protocol,
                timestamp,
                tags);
            await publisher.PublishAsync(new MqttPublishRequest(
                context.Group.Id,
                context.Group.Name,
                context.Group.Line,
                context.Group.Topic,
                context.Device.MqttTemplateId,
                data)
            {
                SourceDeviceId = context.Device.Id,
                SourceTagIds = latestSamples.Select(sample => sample.TagId).ToArray()
            }, cancellationToken);
            _publishedContextFingerprints[context.Device.Id] = context.Fingerprint;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "设备 {DeviceName} 状态错误码Tag进入MQTT队列失败，状态监控继续运行",
                context.Device.Name);
        }
    }

    private static string ResolveDeviceId(DeviceDefinition device) =>
        string.IsNullOrWhiteSpace(device.MqttDeviceId) ? device.Name : device.MqttDeviceId.Trim();

    private static int CreateContextFingerprint(
        DeviceDefinition device,
        MqttGroup group,
        IReadOnlySet<Guid> activeTagIds,
        bool mqttEnabled,
        MqttTemplate template)
    {
        var hash = new HashCode();
        hash.Add(device);
        hash.Add(group);
        hash.Add(mqttEnabled);
        hash.Add(template.Id);
        hash.Add(template.DeviceErrorTagPrefix, StringComparer.Ordinal);
        hash.Add(template.CommunicationFailureCode);
        hash.Add(template.UpdateTime);
        foreach (var tagId in activeTagIds.Order())
        {
            hash.Add(tagId);
        }

        return hash.ToHashCode();
    }

    private sealed record DeviceContext(
        DeviceDefinition Device,
        MqttGroup Group,
        IReadOnlySet<Guid> ActiveTagIds,
        bool MqttEnabled,
        string DeviceErrorTagPrefix,
        int CommunicationFailureCode,
        int Fingerprint);
}
