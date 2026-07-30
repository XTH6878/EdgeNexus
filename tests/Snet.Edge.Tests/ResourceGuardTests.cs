using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class ResourceGuardTests
{
    [Fact]
    public void CriticalLevelStopsCollectionAndPersistentWrites()
    {
        var guard = new ResourceGuard();
        guard.Update(new ResourceSnapshot(
            ResourceProtectionLevel.Critical,
            90,
            4_096,
            2_048,
            96,
            100,
            50,
            DateTimeOffset.UtcNow,
            "磁盘临界"));

        Assert.False(guard.AllowAcquisition);
        Assert.False(guard.AllowPersistentEnqueue);
    }
}
