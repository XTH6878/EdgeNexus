using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;
using Xunit.Abstractions;

namespace Snet.Edge.Tests;

public sealed class CapacitySmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task OneThousandDeviceLoopsStartAndCollectWithoutBlocking()
    {
        const int deviceCount = 1_000;
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "capacity",
            Topic = "test/capacity",
            ScanCycleMs = 1_000
        };
        var devices = new DeviceDefinition[deviceCount];
        var tags = new TagDefinition[deviceCount];
        for (var index = 0; index < deviceCount; index++)
        {
            var deviceId = Guid.NewGuid();
            devices[index] = new DeviceDefinition
            {
                Id = deviceId,
                GroupId = group.Id,
                Name = $"device-{index}",
                Protocol = ProtocolKind.ModbusTcp,
                Host = "127.0.0.1",
                Port = 502
            };
            tags[index] = new TagDefinition
            {
                Id = Guid.NewGuid(),
                DeviceId = deviceId,
                Name = "value",
                Address = "0",
                DataType = TagDataType.Int16
            };
        }

        var store = new SnapshotStore(new GatewaySnapshot([group], devices, tags, new MqttOptions()));
        var statusStore = new RuntimeStatusStore();
        using var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            new FakeDriverFactory(),
            new DiscardTelemetryBuffer(),
            statusStore,
            new AcquisitionControl(),
            new ResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions(),
            NullLogger<AcquisitionWorker>.Instance);
        var initialWorkingSet = Environment.WorkingSet;
        var stopwatch = Stopwatch.StartNew();

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (metrics.Snapshot.AcquisitionCyclesSucceeded < deviceCount ||
                   statusStore.GetAll().Count != deviceCount ||
                   statusStore.GetAll().Any(status => status.State != DeviceConnectionState.Online))
            {
                await Task.Delay(25, timeout.Token);
            }

            stopwatch.Stop();
            Assert.Equal(deviceCount, statusStore.GetAll().Count);
            Assert.All(statusStore.GetAll(), status => Assert.Equal(DeviceConnectionState.Online, status.State));
            Assert.True(metrics.Snapshot.TagSamplesRead >= deviceCount);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15));

            var workingSetDeltaMb = Math.Max(0, Environment.WorkingSet - initialWorkingSet) / 1024D / 1024D;
            output.WriteLine($"Started {deviceCount} device loops in {stopwatch.Elapsed.TotalMilliseconds:F0} ms; working-set delta {workingSetDeltaMb:F1} MB.");
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stopTimeout.Token);
            worker.Dispose();
        }
    }

    private sealed class SnapshotStore(GatewaySnapshot snapshot) : IGatewayConfigurationStore
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<GatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
        public ValueTask<GatewaySnapshot> GetDraftSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
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

    private sealed class FakeDriverFactory : IDeviceDriverFactory
    {
        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null) => new FakeDriver(tags ?? []);
    }

    private sealed class FakeDriver(IReadOnlyList<TagDefinition> tags) : IDeviceDriver
    {
        private readonly IReadOnlyDictionary<Guid, DeviceTagReadResult> _values = tags.ToDictionary(
            tag => tag.Id,
            _ => DeviceTagReadResult.Success(1));

        public ProtocolKind Protocol => ProtocolKind.ModbusTcp;
        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken) => ValueTask.FromResult<object?>(1);
        public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_values);
        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class DiscardTelemetryBuffer : ITelemetryBuffer
    {
        public ValueTask WriteAsync(TagSample sample, CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public async IAsyncEnumerable<TagSample> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
