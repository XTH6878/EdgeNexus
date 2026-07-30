using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;
using Snet.Edge.Infrastructure.GatewayDeviceHealth;

namespace Snet.Edge.Tests;

public sealed class TelemetryAggregationWorkerTests
{
    [Fact]
    public async Task FirstAvailableSamplePublishesImmediatelyAfterIdleInsteadOfWaitingAnotherCycle()
    {
        var group = CreateGroup();
        var device = CreateDevice(group.Id, "PLC-IDLE");
        var tag = CreateTag(device.Id, "Temperature", "0");
        var buffer = new TelemetryBuffer();
        var publisher = new CapturingPublisher();
        var healthStates = new TestHealthStateStore();
        healthStates.Set(device, DeviceConnectionState.Online, DeviceErrorCodes.Normal);
        using var worker = CreateWorker(
            buffer,
            new StaticConfigurationStore(new GatewaySnapshot([group], [device], [tag], new MqttOptions())),
            healthStates,
            CreateTemplate(),
            publisher);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.Delay(1_100);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            await buffer.WriteAsync(CreateSample(group, device, tag, 25, DateTimeOffset.UtcNow), CancellationToken.None);

            var published = await publisher.ReadAsync();

            Assert.Equal(tag.Name, published.DeviceData.Tags[1].TagName);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(750));
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    [Fact]
    public async Task DeletedTagIsEvictedAndStatusTagRemainsFirst()
    {
        var group = CreateGroup();
        var device = CreateDevice(group.Id, "PLC002");
        var deletedTag = CreateTag(device.Id, "Deleted", "0");
        var activeTag = CreateTag(device.Id, "Retained", "1");
        var configuration = new MutableConfigurationStore(
            new GatewaySnapshot([group], [device], [deletedTag, activeTag], new MqttOptions()));
        var buffer = new TelemetryBuffer();
        var publisher = new CapturingPublisher();
        var healthStates = new TestHealthStateStore();
        healthStates.Set(device, DeviceConnectionState.Online, DeviceErrorCodes.Normal);
        using var worker = CreateWorker(buffer, configuration, healthStates, CreateTemplate(), publisher);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var timestamp = DateTimeOffset.UtcNow;
            await buffer.WriteAsync(CreateSample(group, device, deletedTag, 2, timestamp), CancellationToken.None);
            await buffer.WriteAsync(CreateSample(group, device, activeTag, 1, timestamp), CancellationToken.None);

            var first = await publisher.ReadAsync();
            Assert.Equal(3, first.DeviceData.Tags.Count);
            Assert.Equal("#DEVICE_ERROR_PLC002", first.DeviceData.Tags[0].TagName);
            Assert.Equal(DeviceErrorCodes.Normal, first.DeviceData.Tags[0].Value);

            configuration.Update(
                new GatewaySnapshot([group], [device], [activeTag], new MqttOptions()));
            await buffer.WriteAsync(
                CreateSample(group, device, activeTag, 3, DateTimeOffset.UtcNow),
                CancellationToken.None);

            var published = await publisher.ReadAsync();
            Assert.Equal(2, published.DeviceData.Tags.Count);
            Assert.Equal("#DEVICE_ERROR_PLC002", published.DeviceData.Tags[0].TagName);
            Assert.Equal(activeTag.Name, published.DeviceData.Tags[1].TagName);
            Assert.DoesNotContain(deletedTag.Id, published.SourceTagIds);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    [Fact]
    public async Task ChangeOnlyTagsRemainBoundToGroupCycleAndRespectDeadband()
    {
        var group = CreateGroup();
        var device = CreateDevice(group.Id, "PLC001");
        var regularTag = CreateTag(device.Id, "Regular", "0");
        var changeTag = CreateTag(device.Id, "OnChange", "1") with
        {
            DataType = TagDataType.Float32,
            PublishOnChange = true,
            Deadband = 1M
        };
        var snapshot = new GatewaySnapshot(
            [group],
            [device],
            [regularTag, changeTag],
            new MqttOptions());
        var buffer = new TelemetryBuffer();
        var publisher = new CapturingPublisher();
        var healthStates = new TestHealthStateStore();
        healthStates.Set(device, DeviceConnectionState.Online, DeviceErrorCodes.Normal);
        using var worker = CreateWorker(
            buffer,
            new StaticConfigurationStore(snapshot),
            healthStates,
            CreateTemplate(),
            publisher);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WriteSamplesAsync(buffer, group, device, regularTag, changeTag, 10M);
            var first = await publisher.ReadAsync();
            Assert.Equal(3, first.DeviceData.Tags.Count);
            Assert.Equal("#DEVICE_ERROR_PLC001", first.DeviceData.Tags[0].TagName);
            Assert.Equal("PLC001", first.DeviceData.DeviceId);

            var secondCycle = System.Diagnostics.Stopwatch.StartNew();
            await WriteSamplesAsync(buffer, group, device, regularTag, changeTag, 10.5M);
            var second = await publisher.ReadAsync();
            Assert.Equal(2, second.DeviceData.Tags.Count);
            Assert.Contains(second.DeviceData.Tags, tag => tag.TagName == regularTag.Name);
            Assert.DoesNotContain(second.DeviceData.Tags, tag => tag.TagName == changeTag.Name);
            Assert.True(secondCycle.Elapsed >= TimeSpan.FromMilliseconds(750));

            var thirdCycle = System.Diagnostics.Stopwatch.StartNew();
            await WriteSamplesAsync(buffer, group, device, regularTag, changeTag, 12M);
            var third = await publisher.ReadAsync();
            Assert.Equal(3, third.DeviceData.Tags.Count);
            Assert.Contains(third.DeviceData.Tags, tag => tag.TagName == changeTag.Name);
            Assert.True(thirdCycle.Elapsed >= TimeSpan.FromMilliseconds(750));
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    [Fact]
    public async Task MissingHealthStateUsesConfiguredFailureCodeAndCustomPrefix()
    {
        var group = CreateGroup();
        var device = CreateDevice(group.Id, "PLC003");
        var tag = CreateTag(device.Id, "Temperature", "0");
        var template = CreateTemplate() with
        {
            DeviceErrorTagPrefix = "#PLC_HEALTH",
            CommunicationFailureCode = -10
        };
        var buffer = new TelemetryBuffer();
        var publisher = new CapturingPublisher();
        using var worker = CreateWorker(
            buffer,
            new StaticConfigurationStore(
                new GatewaySnapshot([group], [device], [tag], new MqttOptions())),
            new TestHealthStateStore(),
            template,
            publisher);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await buffer.WriteAsync(
                CreateSample(group, device, tag, 25, DateTimeOffset.UtcNow),
                CancellationToken.None);

            var published = await publisher.ReadAsync();
            Assert.Equal(2, published.DeviceData.Tags.Count);
            Assert.Equal("#PLC_HEALTH_PLC003", published.DeviceData.Tags[0].TagName);
            Assert.Equal(-10, published.DeviceData.Tags[0].Value);
            Assert.Equal(DataQuality.Good, published.DeviceData.Tags[0].Quality);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    [Fact]
    public async Task DeviceMqttIdentityAndTemplateBindingFlowIntoPublishRequest()
    {
        var group = CreateGroup() with { Topic = "factory/${deviceId}/data" };
        var templateId = Guid.NewGuid();
        var device = CreateDevice(group.Id, "PLC-BIZ-007") with
        {
            Name = "Machine A",
            MqttTemplateId = templateId
        };
        var tag = CreateTag(device.Id, "Temperature", "0");
        var template = CreateTemplate() with
        {
            Id = templateId,
            DeviceErrorTagPrefix = "#LINK_STATE"
        };
        var templateResolver = new CapturingTemplateResolver(template);
        var buffer = new TelemetryBuffer();
        var publisher = new CapturingPublisher();
        var healthStates = new TestHealthStateStore();
        healthStates.Set(device, DeviceConnectionState.Online, DeviceErrorCodes.Normal);
        using var worker = new TelemetryAggregationWorker(
            buffer,
            new StaticConfigurationStore(
                new GatewaySnapshot([group], [device], [tag], new MqttOptions())),
            healthStates,
            templateResolver,
            new SystemTagBuilder(),
            publisher,
            NullLogger<TelemetryAggregationWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await buffer.WriteAsync(
                CreateSample(group, device, tag, 25, DateTimeOffset.UtcNow),
                CancellationToken.None);

            var published = await publisher.ReadAsync();

            Assert.Equal(templateId, templateResolver.RequestedTemplateId);
            Assert.Equal(templateId, published.TemplateId);
            Assert.Equal("PLC-BIZ-007", published.DeviceData.DeviceId);
            Assert.Equal("Machine A", published.DeviceData.DeviceName);
            Assert.Equal("#LINK_STATE_PLC-BIZ-007", published.DeviceData.Tags[0].TagName);
        }
        finally
        {
            await StopWorkerAsync(worker);
        }
    }

    private static TelemetryAggregationWorker CreateWorker(
        ITelemetryBuffer buffer,
        IGatewayConfigurationStore configuration,
        IDeviceHealthStateStore healthStates,
        MqttTemplate template,
        IMqttMessagePublisher publisher) =>
        new(
            buffer,
            configuration,
            healthStates,
            new StaticTemplateResolver(template),
            new SystemTagBuilder(),
            publisher,
            NullLogger<TelemetryAggregationWorker>.Instance);

    private static async ValueTask WriteSamplesAsync(
        ITelemetryBuffer buffer,
        MqttGroup group,
        DeviceDefinition device,
        TagDefinition regularTag,
        TagDefinition changeTag,
        decimal changeValue)
    {
        var timestamp = DateTimeOffset.UtcNow;
        await buffer.WriteAsync(
            CreateSample(group, device, regularTag, 1, timestamp),
            CancellationToken.None);
        await buffer.WriteAsync(
            CreateSample(group, device, changeTag, changeValue, timestamp),
            CancellationToken.None);
    }

    private static TagSample CreateSample(
        MqttGroup group,
        DeviceDefinition device,
        TagDefinition tag,
        object value,
        DateTimeOffset timestamp) =>
        new(
            group.Id,
            group.Topic,
            device.Id,
            device.Name,
            tag.Id,
            tag.Name,
            value,
            value,
            DataQuality.Good,
            timestamp);

    private static MqttGroup CreateGroup() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Test Group",
        Topic = "edge/test",
        UploadCycleSeconds = 1
    };

    private static DeviceDefinition CreateDevice(Guid groupId, string mqttDeviceId) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = groupId,
        Name = mqttDeviceId,
        MqttDeviceId = mqttDeviceId,
        Protocol = ProtocolKind.ModbusTcp,
        Host = "192.168.1.10",
        Port = 502
    };

    private static TagDefinition CreateTag(Guid deviceId, string name, string address) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        Name = name,
        Address = address,
        DataType = TagDataType.Int16
    };

    private static MqttTemplate CreateTemplate() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Default",
        CreateTime = DateTimeOffset.UtcNow,
        UpdateTime = DateTimeOffset.UtcNow
    };

    private static async ValueTask StopWorkerAsync(TelemetryAggregationWorker worker)
    {
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(shutdown.Token);
    }

    private sealed class CapturingPublisher : IMqttMessagePublisher
    {
        private readonly Channel<MqttPublishRequest> _messages =
            Channel.CreateUnbounded<MqttPublishRequest>();

        public ValueTask PublishAsync(
            MqttPublishRequest message,
            CancellationToken cancellationToken) =>
            _messages.Writer.WriteAsync(message, cancellationToken);

        public async ValueTask<MqttPublishRequest> ReadAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            return await _messages.Reader.ReadAsync(timeout.Token);
        }
    }

    private sealed class StaticTemplateResolver(MqttTemplate template) : ITemplateResolver
    {
        public ValueTask<MqttTemplate> ResolveAsync(
            Guid? templateId,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(template);
    }

    private sealed class CapturingTemplateResolver(MqttTemplate template) : ITemplateResolver
    {
        public Guid? RequestedTemplateId { get; private set; }

        public ValueTask<MqttTemplate> ResolveAsync(
            Guid? templateId,
            CancellationToken cancellationToken)
        {
            RequestedTemplateId = templateId;
            return ValueTask.FromResult(template);
        }
    }

    private sealed class TestHealthStateStore : IDeviceHealthStateStore
    {
        private readonly Dictionary<Guid, DeviceHealthStatus> _states = [];

        public IReadOnlyCollection<DeviceHealthStatus> GetAll() => _states.Values;

        public DeviceHealthStatus? Get(Guid deviceId) => _states.GetValueOrDefault(deviceId);

        public bool SetIfChanged(DeviceHealthStatus status)
        {
            _states[status.DeviceId] = status;
            return true;
        }

        public void Remove(Guid deviceId) => _states.Remove(deviceId);

        public void Set(DeviceDefinition device, DeviceConnectionState state, int errorCode) =>
            SetIfChanged(new DeviceHealthStatus(
                device.Id,
                device.MqttDeviceId ?? device.Name,
                device.Name,
                errorCode,
                state,
                DateTimeOffset.UtcNow,
                null));
    }

    private class StaticConfigurationStore(GatewaySnapshot snapshot) : IGatewayConfigurationStore
    {
        protected GatewaySnapshot Snapshot = snapshot;

        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<GatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
        public ValueTask<GatewaySnapshot> GetDraftSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Snapshot);
        public ValueTask<IReadOnlyList<ConfigurationRevision>> GetRevisionsAsync(int take, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ConfigurationRevision>>([]);
        public ValueTask<GatewaySnapshot> GetRevisionSnapshotAsync(long version, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PublishResult> PublishDraftAsync(string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<PublishResult> RollbackAsync(long version, long expectedActiveVersion, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteRevisionAsync(long version, long expectedActiveVersion, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpsertGroupAsync(MqttGroup group, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask UpsertDeviceAsync(DeviceDefinition device, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ReplaceTagsAsync(Guid deviceId, IReadOnlyCollection<TagDefinition> tags, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask SaveMqttOptionsAsync(MqttOptions options, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask ReplaceDraftAsync(GatewaySnapshot replacement, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutableConfigurationStore(GatewaySnapshot snapshot) :
        StaticConfigurationStore(snapshot)
    {
        public void Update(GatewaySnapshot updated) => Snapshot = updated;
    }
}
