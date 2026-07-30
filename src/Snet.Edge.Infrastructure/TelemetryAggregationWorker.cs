using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class TelemetryAggregationWorker(
    ITelemetryBuffer telemetryBuffer,
    IGatewayConfigurationStore configurationStore,
    IDeviceHealthStateStore healthStateStore,
    ITemplateResolver templateResolver,
    ISystemTagBuilder systemTagBuilder,
    IMqttMessagePublisher publisher,
    ILogger<TelemetryAggregationWorker> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<Guid, TagSample>> _latestByGroup = new();
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastEvaluated = new();
    private readonly ConcurrentDictionary<Guid, PublishedTagState> _publishedChanges = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.WhenAll(ConsumeAsync(stoppingToken), PublishLoopAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        await foreach (var sample in telemetryBuffer.ReadAllAsync(cancellationToken))
        {
            _latestByGroup
                .GetOrAdd(sample.GroupId, static _ => new ConcurrentDictionary<Guid, TagSample>())[sample.TagId] = sample;
        }
    }

    private async Task PublishLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var snapshot = await configurationStore.GetSnapshotAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var tagsById = snapshot.Tags.ToDictionary(item => item.Id);
            var devicesById = snapshot.Devices.ToDictionary(item => item.Id);
            var activeGroups = snapshot.Groups.Where(item => item.Enabled).ToDictionary(item => item.Id);
            foreach (var cachedGroupId in _latestByGroup.Keys)
            {
                if (!activeGroups.ContainsKey(cachedGroupId))
                {
                    _latestByGroup.TryRemove(cachedGroupId, out _);
                    _lastEvaluated.TryRemove(cachedGroupId, out _);
                }
            }

            foreach (var group in activeGroups.Values)
            {
                if (!_latestByGroup.TryGetValue(group.Id, out var groupSamples) || groupSamples.IsEmpty)
                {
                    continue;
                }

                var lastEvaluated = _lastEvaluated.GetValueOrDefault(group.Id, DateTimeOffset.MinValue);
                var uploadCycle = Math.Clamp(
                    group.UploadCycleSeconds,
                    GroupScheduleLimits.MinimumUploadCycleSeconds,
                    GroupScheduleLimits.MaximumUploadCycleSeconds);
                if (now - lastEvaluated < TimeSpan.FromSeconds(uploadCycle))
                {
                    continue;
                }

                _lastEvaluated[group.Id] = now;
                var pendingByDevice = new Dictionary<Guid, PendingDeviceData>();
                foreach (var pair in groupSamples.ToArray())
                {
                    var sample = pair.Value;
                    if (!tagsById.TryGetValue(sample.TagId, out var tag) ||
                        !tag.Enabled ||
                        tag.DeviceId != sample.DeviceId)
                    {
                        groupSamples.TryRemove(pair.Key, out _);
                        _publishedChanges.TryRemove(sample.TagId, out _);
                        continue;
                    }

                    if (tag.PublishOnChange &&
                        _publishedChanges.TryGetValue(sample.TagId, out var previous) &&
                        !HasMeaningfulChange(previous, sample, tag.Deadband))
                    {
                        continue;
                    }

                    if (!devicesById.TryGetValue(sample.DeviceId, out var device) ||
                        !device.Enabled ||
                        device.GroupId != group.Id)
                    {
                        groupSamples.TryRemove(pair.Key, out _);
                        continue;
                    }

                    if (!pendingByDevice.TryGetValue(device.Id, out var pending))
                    {
                        pending = new PendingDeviceData(device, [], []);
                        pendingByDevice[device.Id] = pending;
                    }

                    pending.Tags.Add(new TagData(sample.TagName, sample.Value, sample.Quality));
                    pending.SourceTagIds.Add(sample.TagId);
                    if (sample.Timestamp > pending.Timestamp)
                    {
                        pending.Timestamp = sample.Timestamp;
                    }

                    if (tag.PublishOnChange)
                    {
                        pending.ChangedSamples.Add(sample);
                    }
                }

                foreach (var pending in pendingByDevice.Values)
                {
                    if (pending.Tags.Count == 0)
                    {
                        continue;
                    }

                    try
                    {
                        var mqttDeviceId = string.IsNullOrWhiteSpace(pending.Device.MqttDeviceId)
                            ? pending.Device.Name
                            : pending.Device.MqttDeviceId.Trim();
                        var template = await templateResolver.ResolveAsync(
                            pending.Device.MqttTemplateId,
                            cancellationToken);
                        var health = healthStateStore.Get(pending.Device.Id);
                        var errorCode = health?.ErrorCode ?? template.CommunicationFailureCode;
                        pending.Tags.Insert(
                            0,
                            systemTagBuilder.BuildDeviceErrorTag(
                                mqttDeviceId,
                                template.DeviceErrorTagPrefix,
                                errorCode));
                        var data = new DeviceData(
                            mqttDeviceId,
                            pending.Device.Name,
                            pending.Device.Host,
                            pending.Device.Protocol,
                            pending.Timestamp == default ? now : pending.Timestamp,
                            pending.Tags);
                        await publisher.PublishAsync(new MqttPublishRequest(
                            group.Id,
                            group.Name,
                            group.Line,
                            group.Topic,
                            pending.Device.MqttTemplateId,
                            data)
                        {
                            SourceDeviceId = pending.Device.Id,
                            SourceTagIds = pending.SourceTagIds.ToArray()
                        }, cancellationToken);
                        foreach (var sample in pending.ChangedSamples)
                        {
                            _publishedChanges[sample.TagId] = new PublishedTagState(sample.Value, sample.Quality);
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        logger.LogError(
                            exception,
                            "分组 {GroupName} 设备 {DeviceName} MQTT报文生成或进入离线队列失败，采集任务继续运行",
                            group.Name,
                            pending.Device.Name);
                    }
                }
            }
        }
    }

    private static bool HasMeaningfulChange(PublishedTagState previous, TagSample current, decimal deadband)
    {
        if (previous.Quality != current.Quality)
        {
            return true;
        }

        if (Equals(previous.Value, current.Value))
        {
            return false;
        }

        if (deadband <= 0 || previous.Value is null || current.Value is null)
        {
            return true;
        }

        try
        {
            var previousNumber = Convert.ToDecimal(previous.Value, System.Globalization.CultureInfo.InvariantCulture);
            var currentNumber = Convert.ToDecimal(current.Value, System.Globalization.CultureInfo.InvariantCulture);
            return Math.Abs(currentNumber - previousNumber) > deadband;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return true;
        }
    }

    private sealed record PublishedTagState(object? Value, DataQuality Quality);

    private sealed class PendingDeviceData(
        DeviceDefinition device,
        List<TagData> tags,
        List<TagSample> changedSamples)
    {
        public DeviceDefinition Device { get; } = device;
        public List<TagData> Tags { get; } = tags;
        public List<TagSample> ChangedSamples { get; } = changedSamples;
        public HashSet<Guid> SourceTagIds { get; } = [];
        public DateTimeOffset Timestamp { get; set; }
    }
}
