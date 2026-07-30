using System.Threading.Channels;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class MqttRuntimeStateStore : IMqttRuntimeStateStore
{
    private readonly Channel<bool> _configurationChanges = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private MqttRuntimeStatus _current = new(
        MqttConnectionState.Disabled,
        false,
        string.Empty,
        0,
        DateTimeOffset.UtcNow,
        null,
        null,
        null,
        null);
    private long _configurationRevision;

    public MqttRuntimeStatus Current => Volatile.Read(ref _current);

    public long ConfigurationRevision => Interlocked.Read(ref _configurationRevision);

    public void Set(MqttRuntimeStatus status) => Volatile.Write(ref _current, status);

    public void NotifyConfigurationChanged()
    {
        Interlocked.Increment(ref _configurationRevision);
        _configurationChanges.Writer.TryWrite(true);
    }

    public async ValueTask<bool> WaitForConfigurationChangeAsync(
        long observedRevision,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (ConfigurationRevision != observedRevision)
        {
            return true;
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await _configurationChanges.Reader.ReadAsync(timeoutSource.Token);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
