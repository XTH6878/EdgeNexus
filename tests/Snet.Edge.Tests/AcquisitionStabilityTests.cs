using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class AcquisitionStabilityTests
{
    private static readonly AcquisitionStabilityOptions Options = new()
    {
        FailureThreshold = 3,
        SuccessThreshold = 2,
        ReconnectDelaysSeconds = [1, 2, 5, 10, 30],
        ReconnectJitterRatio = 0
    };

    [Fact]
    public void CommunicationStateRequiresConfiguredFailureAndRecoveryThresholds()
    {
        var tracker = new DeviceCommunicationStabilityTracker(Options);

        Assert.Equal(CommunicationStabilityTransition.Pending, tracker.ObserveSuccessfulRead());
        Assert.Equal(CommunicationStabilityTransition.BecameHealthy, tracker.ObserveSuccessfulRead());
        Assert.True(tracker.IsHealthy);

        Assert.Equal(CommunicationStabilityTransition.Pending, tracker.ObserveFailedRead());
        Assert.Equal(CommunicationStabilityTransition.Pending, tracker.ObserveFailedRead());
        Assert.True(tracker.IsHealthy);
        Assert.Equal(CommunicationStabilityTransition.BecameFaulted, tracker.ObserveFailedRead());
        Assert.False(tracker.IsHealthy);

        Assert.Equal(CommunicationStabilityTransition.Pending, tracker.ObserveSuccessfulRead());
        Assert.False(tracker.IsHealthy);
        Assert.Equal(CommunicationStabilityTransition.BecameHealthy, tracker.ObserveSuccessfulRead());
        Assert.True(tracker.IsHealthy);
    }

    [Fact]
    public void SuccessfulReadResetsFailureDebounce()
    {
        var tracker = new DeviceCommunicationStabilityTracker(Options);
        tracker.ObserveSuccessfulRead();
        tracker.ObserveSuccessfulRead();

        tracker.ObserveFailedRead();
        tracker.ObserveFailedRead();
        Assert.Equal(CommunicationStabilityTransition.MaintainedHealthy, tracker.ObserveSuccessfulRead());
        Assert.Equal(0, tracker.ConsecutiveFailures);

        Assert.Equal(CommunicationStabilityTransition.Pending, tracker.ObserveFailedRead());
        Assert.True(tracker.IsHealthy);
    }

    [Fact]
    public void ReconnectBackoffResetsOnlyWhenExplicitReadSuccessIsReported()
    {
        var backoff = new ReconnectBackoff(Options);

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.GetDelayAndAdvance());
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.GetDelayAndAdvance());
        Assert.Equal(TimeSpan.FromSeconds(5), backoff.GetDelayAndAdvance());

        backoff.ResetAfterSuccessfulRead();

        Assert.Equal(TimeSpan.FromSeconds(1), backoff.GetDelayAndAdvance());
    }
}
