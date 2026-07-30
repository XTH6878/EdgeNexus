using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;
using Snet.Edge.Infrastructure.GatewayDeviceHealth;

namespace Snet.Edge.Tests;

public sealed class AcquisitionIsolationTests
{
    [Fact]
    public async Task LongScanCycleUsesHeartbeatToKeepConnectionHealthy()
    {
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "慢速扫描组",
            Topic = "test/heartbeat",
            ScanCycleMs = 2_000
        };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "长连接设备",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = 502
        };
        var tag = CreateTag(device.Id, "保活点位", "1");
        var store = new SnapshotStore(new GatewaySnapshot(
            [group],
            [device],
            [tag],
            new MqttOptions()));
        var telemetry = new CapturingTelemetryBuffer();
        var statusStore = new RuntimeStatusStore();
        var driverFactory = new IdleSensitiveDriverFactory(tag);
        using var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            driverFactory,
            telemetry,
            statusStore,
            new AcquisitionControl(),
            new ResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions
            {
                FailureThreshold = 1,
                SuccessThreshold = 2,
                HeartbeatIntervalSeconds = 1,
                ReconnectDelaysSeconds = [1],
                ReconnectJitterRatio = 0
            },
            NullLogger<AcquisitionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (driverFactory.FullReadCount < 2 ||
                   statusStore.GetAll().SingleOrDefault()?.State != DeviceConnectionState.Online)
            {
                await Task.Delay(25, timeout.Token);
            }

            Assert.True(driverFactory.HeartbeatCount > 0);
            Assert.Equal(1, driverFactory.ConnectCount);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task DeviceTimeoutInvalidatesPreviouslyGoodPointQuality()
    {
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "超时测试组",
            Topic = "test/timeout",
            ScanCycleMs = 100
        };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "超时设备",
            Protocol = ProtocolKind.SiemensS7,
            Host = "127.0.0.1",
            Port = 102
        };
        var tag = CreateTag(device.Id, "缓存点位", "1");
        var store = new SnapshotStore(new GatewaySnapshot(
            [group],
            [device],
            [tag],
            new MqttOptions()));
        var telemetry = new QualityTrackingTelemetryBuffer(tag.Id);
        var statusStore = new RuntimeStatusStore();
        using var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            new TimeoutAfterFirstReadDriverFactory(tag),
            telemetry,
            statusStore,
            new AcquisitionControl(),
            new ResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions
            {
                FailureThreshold = 1,
                SuccessThreshold = 1,
                HeartbeatIntervalSeconds = 10,
                ReconnectDelaysSeconds = [1],
                ReconnectJitterRatio = 0
            },
            NullLogger<AcquisitionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var failedSample = await telemetry.WaitForTimeoutAsync(timeout.Token);

            Assert.Equal(DataQuality.Timeout, failedSample.Quality);
            Assert.Null(failedSample.Value);
            Assert.Null(failedSample.RawValue);
            Assert.Contains("超时", failedSample.Error, StringComparison.Ordinal);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task SingleRequestSessionFallsBackToReconnectPerScan()
    {
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "短连接兼容组",
            Topic = "test/reconnect-per-scan",
            ScanCycleMs = 2_000
        };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "单请求S7服务端",
            Protocol = ProtocolKind.SiemensS7,
            Host = "127.0.0.1",
            Port = 102
        };
        var tag = CreateTag(device.Id, "功率", "1");
        var store = new SnapshotStore(new GatewaySnapshot(
            [group],
            [device],
            [tag],
            new MqttOptions()));
        var statusStore = new RuntimeStatusStore();
        var driverFactory = new SingleRequestDriverFactory(tag);
        using var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            driverFactory,
            new CapturingTelemetryBuffer(),
            statusStore,
            new AcquisitionControl(),
            new ResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions
            {
                FailureThreshold = 3,
                SuccessThreshold = 2,
                HeartbeatIntervalSeconds = 1,
                ReconnectDelaysSeconds = [1],
                ReconnectJitterRatio = 0
            },
            NullLogger<AcquisitionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            while (driverFactory.FullReadCount < 2 ||
                   statusStore.GetAll().SingleOrDefault()?.State != DeviceConnectionState.Online)
            {
                await Task.Delay(25, timeout.Token);
            }

            Assert.True(driverFactory.ConnectCount >= 3);
            Assert.Equal(2, driverFactory.FullReadCount);
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task InvalidPointDoesNotReconnectDeviceOrBlockHealthyPoint()
    {
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "测试组",
            Topic = "test/isolation",
            ScanCycleMs = 100
        };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "Modbus设备",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = 502
        };
        var invalidTag = CreateTag(device.Id, "坏点位", "40000");
        var healthyTag = CreateTag(device.Id, "正常点位", "1");
        var store = new SnapshotStore(new GatewaySnapshot(
            [group],
            [device],
            [invalidTag, healthyTag],
            new MqttOptions()));
        var telemetry = new CapturingTelemetryBuffer();
        var statusStore = new RuntimeStatusStore();
        var driverFactory = new IsolatingDriverFactory(invalidTag.Id, healthyTag.Id);
        using var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            driverFactory,
            telemetry,
            statusStore,
            new AcquisitionControl(),
            new ResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions(),
            NullLogger<AcquisitionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!telemetry.HasSamples(invalidTag.Id, healthyTag.Id) ||
                   statusStore.GetAll().SingleOrDefault()?.State != DeviceConnectionState.Degraded)
            {
                await Task.Delay(25, timeout.Token);
            }

            var samples = telemetry.Latest;
            Assert.Equal(DataQuality.ConfigurationError, samples[invalidTag.Id].Quality);
            Assert.Null(samples[invalidTag.Id].Value);
            Assert.Equal(DataQuality.Good, samples[healthyTag.Id].Quality);
            Assert.Equal(658M, samples[healthyTag.Id].Value);
            Assert.Equal(1, driverFactory.ConnectCount);
            var runtimeStatus = statusStore.GetAll().Single();
            Assert.Equal(DeviceConnectionState.Degraded, runtimeStatus.State);
            Assert.NotNull(runtimeStatus.LastSuccessAt);
            Assert.Equal(
                DeviceErrorCodes.Normal,
                new DeviceErrorManager().Resolve(runtimeStatus, -9001));
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
            worker.Dispose();
        }
    }

    private static TagDefinition CreateTag(Guid deviceId, string name, string address) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        Name = name,
        Address = address,
        DataType = TagDataType.Int16,
        ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters
    };

    private sealed class IsolatingDriverFactory(Guid invalidTagId, Guid healthyTagId) : IDeviceDriverFactory
    {
        private int _connectCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);

        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null) =>
            new IsolatingDriver(invalidTagId, healthyTagId, () => Interlocked.Increment(ref _connectCount));
    }

    private sealed class IsolatingDriver(Guid invalidTagId, Guid healthyTagId, Action connected) : IDeviceDriver
    {
        public ProtocolKind Protocol => ProtocolKind.ModbusTcp;

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            connected();
            return ValueTask.CompletedTask;
        }

        public ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(tag.Id == healthyTagId ? 658 : null);

        public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyDictionary<Guid, DeviceTagReadResult>>(
                new Dictionary<Guid, DeviceTagReadResult>
                {
                    [invalidTagId] = DeviceTagReadResult.ConfigurationFailure("读取的数据越界"),
                    [healthyTagId] = DeviceTagReadResult.Success(658)
                });

        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class IdleSensitiveDriverFactory(TagDefinition tag) : IDeviceDriverFactory
    {
        private int _connectCount;
        private int _fullReadCount;
        private int _heartbeatCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int FullReadCount => Volatile.Read(ref _fullReadCount);
        public int HeartbeatCount => Volatile.Read(ref _heartbeatCount);

        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null) =>
            new IdleSensitiveDriver(
                tag,
                () => Interlocked.Increment(ref _connectCount),
                () => Interlocked.Increment(ref _fullReadCount),
                () => Interlocked.Increment(ref _heartbeatCount));
    }

    private sealed class IdleSensitiveDriver(
        TagDefinition tag,
        Action connected,
        Action fullRead,
        Action heartbeat) : IDeviceDriver
    {
        private long _lastActivity;
        private bool _hasRead;

        public ProtocolKind Protocol => ProtocolKind.ModbusTcp;

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            connected();
            _lastActivity = System.Diagnostics.Stopwatch.GetTimestamp();
            return ValueTask.CompletedTask;
        }

        public ValueTask<object?> ReadAsync(TagDefinition definition, CancellationToken cancellationToken)
        {
            heartbeat();
            _lastActivity = System.Diagnostics.Stopwatch.GetTimestamp();
            return ValueTask.FromResult<object?>(12);
        }

        public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_hasRead && System.Diagnostics.Stopwatch.GetElapsedTime(_lastActivity, now) > TimeSpan.FromMilliseconds(1_700))
            {
                throw new TimeoutException("连接因长时间空闲失效。");
            }

            _hasRead = true;
            _lastActivity = now;
            fullRead();
            return ValueTask.FromResult<IReadOnlyDictionary<Guid, DeviceTagReadResult>>(
                new Dictionary<Guid, DeviceTagReadResult>
                {
                    [tag.Id] = DeviceTagReadResult.Success(12)
                });
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TimeoutAfterFirstReadDriverFactory(TagDefinition tag) : IDeviceDriverFactory
    {
        private int _readCount;

        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null) =>
            new TimeoutAfterFirstReadDriver(
                tag,
                () => Interlocked.Increment(ref _readCount));
    }

    private sealed class TimeoutAfterFirstReadDriver(TagDefinition tag, Func<int> nextRead) : IDeviceDriver
    {
        public ProtocolKind Protocol => ProtocolKind.SiemensS7;

        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask<object?> ReadAsync(TagDefinition definition, CancellationToken cancellationToken) =>
            ValueTask.FromResult<object?>(42);

        public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken)
        {
            if (nextRead() > 1)
            {
                throw new TimeoutException("模拟PLC接收超时。");
            }

            return ValueTask.FromResult<IReadOnlyDictionary<Guid, DeviceTagReadResult>>(
                new Dictionary<Guid, DeviceTagReadResult>
                {
                    [tag.Id] = DeviceTagReadResult.Success(42)
                });
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleRequestDriverFactory(TagDefinition tag) : IDeviceDriverFactory
    {
        private int _connectCount;
        private int _fullReadCount;

        public int ConnectCount => Volatile.Read(ref _connectCount);
        public int FullReadCount => Volatile.Read(ref _fullReadCount);

        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null) =>
            new SingleRequestDriver(
                tag,
                () => Interlocked.Increment(ref _connectCount),
                () => Interlocked.Increment(ref _fullReadCount));
    }

    private sealed class SingleRequestDriver(TagDefinition tag, Action connected, Action fullRead) : IDeviceDriver
    {
        private int _requestsInSession;

        public ProtocolKind Protocol => ProtocolKind.SiemensS7;

        public ValueTask ConnectAsync(CancellationToken cancellationToken)
        {
            _requestsInSession = 0;
            connected();
            return ValueTask.CompletedTask;
        }

        public ValueTask<object?> ReadAsync(TagDefinition definition, CancellationToken cancellationToken)
        {
            EnsureFirstRequest();
            return ValueTask.FromResult<object?>(15.5M);
        }

        public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken)
        {
            EnsureFirstRequest();
            fullRead();
            return ValueTask.FromResult<IReadOnlyDictionary<Guid, DeviceTagReadResult>>(
                new Dictionary<Guid, DeviceTagReadResult>
                {
                    [tag.Id] = DeviceTagReadResult.Success(15.5M)
                });
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private void EnsureFirstRequest()
        {
            if (++_requestsInSession > 1)
            {
                throw new TimeoutException("当前服务端每条连接只接受一次读取请求。");
            }
        }
    }

    private sealed class QualityTrackingTelemetryBuffer(Guid tagId) : ITelemetryBuffer
    {
        private readonly TaskCompletionSource<TagSample> _timeoutSample =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask WriteAsync(TagSample sample, CancellationToken cancellationToken)
        {
            if (sample.TagId == tagId && sample.Quality == DataQuality.Timeout)
            {
                _timeoutSample.TrySetResult(sample);
            }

            return ValueTask.CompletedTask;
        }

        public Task<TagSample> WaitForTimeoutAsync(CancellationToken cancellationToken) =>
            _timeoutSample.Task.WaitAsync(cancellationToken);

        public async IAsyncEnumerable<TagSample> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CapturingTelemetryBuffer : ITelemetryBuffer
    {
        private readonly ConcurrentDictionary<Guid, TagSample> _latest = new();

        public IReadOnlyDictionary<Guid, TagSample> Latest => _latest;

        public bool HasSamples(params Guid[] tagIds) => tagIds.All(_latest.ContainsKey);

        public ValueTask WriteAsync(TagSample sample, CancellationToken cancellationToken)
        {
            _latest[sample.TagId] = sample;
            return ValueTask.CompletedTask;
        }

        public async IAsyncEnumerable<TagSample> ReadAllAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
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
}
