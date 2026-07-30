namespace Snet.Edge.Infrastructure;

public sealed record AcquisitionStabilityOptions
{
    public int FailureThreshold { get; init; } = 3;
    public int SuccessThreshold { get; init; } = 2;
    public int HeartbeatIntervalSeconds { get; init; } = 10;
    public int[] ReconnectDelaysSeconds { get; init; } = [1, 2, 5, 10, 30];
    public double ReconnectJitterRatio { get; init; } = 0.2;
}

public enum CommunicationStabilityTransition
{
    Pending,
    MaintainedHealthy,
    BecameHealthy,
    MaintainedFaulted,
    BecameFaulted
}

public sealed class DeviceCommunicationStabilityTracker
{
    private readonly int _failureThreshold;
    private readonly int _successThreshold;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;

    public DeviceCommunicationStabilityTracker(AcquisitionStabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _failureThreshold = Math.Max(1, options.FailureThreshold);
        _successThreshold = Math.Max(1, options.SuccessThreshold);
    }

    public bool HasConfirmedState { get; private set; }
    public bool IsHealthy { get; private set; }
    public int ConsecutiveFailures => _consecutiveFailures;
    public int ConsecutiveSuccesses => _consecutiveSuccesses;

    public CommunicationStabilityTransition ObserveSuccessfulRead()
    {
        _consecutiveFailures = 0;
        _consecutiveSuccesses = Math.Min(_consecutiveSuccesses + 1, _successThreshold);
        if (HasConfirmedState && IsHealthy)
        {
            return CommunicationStabilityTransition.MaintainedHealthy;
        }

        if (_consecutiveSuccesses < _successThreshold)
        {
            return CommunicationStabilityTransition.Pending;
        }

        HasConfirmedState = true;
        IsHealthy = true;
        return CommunicationStabilityTransition.BecameHealthy;
    }

    public CommunicationStabilityTransition ObserveFailedRead()
    {
        _consecutiveSuccesses = 0;
        _consecutiveFailures = Math.Min(_consecutiveFailures + 1, _failureThreshold);
        if (HasConfirmedState && !IsHealthy)
        {
            return CommunicationStabilityTransition.MaintainedFaulted;
        }

        if (_consecutiveFailures < _failureThreshold)
        {
            return CommunicationStabilityTransition.Pending;
        }

        HasConfirmedState = true;
        IsHealthy = false;
        return CommunicationStabilityTransition.BecameFaulted;
    }
}

public sealed class ReconnectBackoff
{
    private readonly TimeSpan[] _delays;
    private readonly double _jitterRatio;
    private int _index;

    public ReconnectBackoff(AcquisitionStabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var configuredDelays = options.ReconnectDelaysSeconds
            .Where(seconds => seconds > 0)
            .Distinct()
            .Order()
            .Select(seconds => TimeSpan.FromSeconds(seconds))
            .ToArray();
        _delays = configuredDelays.Length == 0
            ? [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30)]
            : configuredDelays;
        _jitterRatio = Math.Clamp(options.ReconnectJitterRatio, 0, 0.5);
    }

    public int Attempt => _index;

    public TimeSpan GetDelayAndAdvance()
    {
        var delay = _delays[Math.Min(_index, _delays.Length - 1)];
        if (_index < _delays.Length - 1)
        {
            _index++;
        }

        if (_jitterRatio == 0)
        {
            return delay;
        }

        var jitter = 1 + ((Random.Shared.NextDouble() * 2) - 1) * _jitterRatio;
        return TimeSpan.FromMilliseconds(Math.Max(100, delay.TotalMilliseconds * jitter));
    }

    public void ResetAfterSuccessfulRead() => _index = 0;
}
