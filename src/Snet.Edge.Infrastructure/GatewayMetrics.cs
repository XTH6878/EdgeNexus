using System.Diagnostics.Metrics;
using Snet.Edge.Application;

namespace Snet.Edge.Infrastructure;

public sealed class InitializationState : IInitializationState
{
    private int _ready;
    private long _readyAtUnixMilliseconds;
    private string? _error;

    public bool IsReady => Volatile.Read(ref _ready) == 1;
    public DateTimeOffset? ReadyAt
    {
        get
        {
            var value = Interlocked.Read(ref _readyAtUnixMilliseconds);
            return value == 0 ? null : DateTimeOffset.FromUnixTimeMilliseconds(value);
        }
    }
    public string? Error => Volatile.Read(ref _error);

    public void MarkReady()
    {
        Volatile.Write(ref _error, null);
        Interlocked.Exchange(ref _readyAtUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        Volatile.Write(ref _ready, 1);
    }

    public void MarkFailed(Exception exception)
    {
        Volatile.Write(ref _ready, 0);
        Volatile.Write(ref _error, exception.GetType().Name);
    }
}

public sealed class GatewayMetrics : IGatewayMetrics, IDisposable
{
    public const string MeterName = "Snet.Edge.Gateway";

    private readonly Meter _meter = new(MeterName, "1.1.0");
    private readonly Counter<long> _acquisitionSuccess;
    private readonly Counter<long> _acquisitionFailure;
    private readonly Counter<long> _tagSamples;
    private readonly Histogram<double> _acquisitionDuration;
    private readonly Counter<long> _mqttPublished;
    private readonly Counter<long> _mqttFailures;
    private long _acquisitionCyclesSucceeded;
    private long _acquisitionCyclesFailed;
    private long _tagSamplesRead;
    private long _acquisitionDurationMilliseconds;
    private long _mqttMessagesPublished;
    private long _mqttPublishFailures;
    private long _signalRConnections;

    public GatewayMetrics()
    {
        _acquisitionSuccess = _meter.CreateCounter<long>("gateway.acquisition.cycles.succeeded");
        _acquisitionFailure = _meter.CreateCounter<long>("gateway.acquisition.cycles.failed");
        _tagSamples = _meter.CreateCounter<long>("gateway.tags.samples");
        _acquisitionDuration = _meter.CreateHistogram<double>("gateway.acquisition.duration", "ms");
        _mqttPublished = _meter.CreateCounter<long>("gateway.mqtt.messages.published");
        _mqttFailures = _meter.CreateCounter<long>("gateway.mqtt.publish.failures");
        _meter.CreateObservableGauge("gateway.signalr.connections", () => Interlocked.Read(ref _signalRConnections));
    }

    public GatewayMetricSnapshot Snapshot => new(
        Interlocked.Read(ref _acquisitionCyclesSucceeded),
        Interlocked.Read(ref _acquisitionCyclesFailed),
        Interlocked.Read(ref _tagSamplesRead),
        Interlocked.Read(ref _acquisitionDurationMilliseconds),
        Interlocked.Read(ref _mqttMessagesPublished),
        Interlocked.Read(ref _mqttPublishFailures),
        Interlocked.Read(ref _signalRConnections));

    public void RecordAcquisitionSuccess(int tagCount, TimeSpan duration)
    {
        var elapsedMilliseconds = Math.Max(0, (long)duration.TotalMilliseconds);
        Interlocked.Increment(ref _acquisitionCyclesSucceeded);
        Interlocked.Add(ref _tagSamplesRead, Math.Max(tagCount, 0));
        Interlocked.Add(ref _acquisitionDurationMilliseconds, elapsedMilliseconds);
        _acquisitionSuccess.Add(1);
        _tagSamples.Add(Math.Max(tagCount, 0));
        _acquisitionDuration.Record(duration.TotalMilliseconds);
    }

    public void RecordAcquisitionFailure(TimeSpan duration)
    {
        var elapsedMilliseconds = Math.Max(0, (long)duration.TotalMilliseconds);
        Interlocked.Increment(ref _acquisitionCyclesFailed);
        Interlocked.Add(ref _acquisitionDurationMilliseconds, elapsedMilliseconds);
        _acquisitionFailure.Add(1);
        _acquisitionDuration.Record(duration.TotalMilliseconds);
    }

    public void RecordMqttPublished()
    {
        Interlocked.Increment(ref _mqttMessagesPublished);
        _mqttPublished.Add(1);
    }

    public void RecordMqttPublishFailure()
    {
        Interlocked.Increment(ref _mqttPublishFailures);
        _mqttFailures.Add(1);
    }

    public void SignalRConnected() => Interlocked.Increment(ref _signalRConnections);

    public void SignalRDisconnected()
    {
        while (true)
        {
            var current = Interlocked.Read(ref _signalRConnections);
            if (current == 0 || Interlocked.CompareExchange(ref _signalRConnections, current - 1, current) == current)
            {
                return;
            }
        }
    }

    public void Dispose() => _meter.Dispose();
}
