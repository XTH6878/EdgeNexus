using System.Collections.Concurrent;
using System.Threading.Channels;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class TelemetryBuffer : ITelemetryBuffer, IRuntimeTagSampleStore
{
    private readonly ConcurrentDictionary<Guid, TagSample> _latest = new();
    private readonly ConcurrentDictionary<Guid, TagSample> _pending = new();
    private readonly Channel<TagSample> _channel = Channel.CreateBounded<TagSample>(new BoundedChannelOptions(100_000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public async ValueTask WriteAsync(TagSample sample, CancellationToken cancellationToken)
    {
        await _channel.Writer.WriteAsync(sample, cancellationToken);
        _latest[sample.TagId] = sample;
        _pending[sample.TagId] = sample;
    }

    public IAsyncEnumerable<TagSample> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);

    public IReadOnlyCollection<TagSample> GetByDevice(Guid deviceId) =>
        _latest.Values.Where(sample => sample.DeviceId == deviceId).ToArray();

    public IReadOnlyCollection<TagSample> TakeChanges()
    {
        var changes = new List<TagSample>(_pending.Count);
        foreach (var pair in _pending.ToArray())
        {
            if (((ICollection<KeyValuePair<Guid, TagSample>>)_pending).Remove(pair))
            {
                changes.Add(pair.Value);
            }
        }

        return changes;
    }
}

public sealed class RuntimeStatusStore : IRuntimeStatusStore, IDeviceStatusEventSource
{
    private readonly ConcurrentDictionary<Guid, DeviceRuntimeStatus> _statuses = new();
    private readonly Channel<DeviceRuntimeStatus> _changes = Channel.CreateBounded<DeviceRuntimeStatus>(
        new BoundedChannelOptions(10_000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public void Set(DeviceRuntimeStatus status)
    {
        while (true)
        {
            if (!_statuses.TryGetValue(status.DeviceId, out var previous))
            {
                if (_statuses.TryAdd(status.DeviceId, status))
                {
                    _changes.Writer.TryWrite(status);
                    return;
                }

                continue;
            }

            var stateChanged = previous.State != status.State ||
                               previous.FaultKind != status.FaultKind ||
                               previous.Error != status.Error;
            var normalized = stateChanged ? status : status with { ChangedAt = previous.ChangedAt };
            if (!_statuses.TryUpdate(status.DeviceId, normalized, previous))
            {
                continue;
            }

            if (stateChanged)
            {
                _changes.Writer.TryWrite(normalized);
            }

            return;
        }
    }

    public IReadOnlyCollection<DeviceRuntimeStatus> GetAll() => _statuses.Values.ToArray();

    public IAsyncEnumerable<DeviceRuntimeStatus> ReadAllAsync(CancellationToken cancellationToken) =>
        _changes.Reader.ReadAllAsync(cancellationToken);
}

public sealed class AcquisitionControl : IAcquisitionControl
{
    private int _running = 1;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    public void Start() => Interlocked.Exchange(ref _running, 1);

    public void Stop() => Interlocked.Exchange(ref _running, 0);
}
