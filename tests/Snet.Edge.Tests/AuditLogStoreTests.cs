using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class AuditLogStoreTests
{
    [Fact]
    public async Task StopDoesNotThrowWhenHostShutdownTokenAlreadyCanceled()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-audit-{Guid.NewGuid():N}.db");
        try
        {
            var store = new AuditLogStore(
                $"Data Source={databasePath};Pooling=False",
                NullLogger<AuditLogStore>.Instance);
            await store.InitializeAsync(CancellationToken.None);
            await store.StartAsync(CancellationToken.None);

            using var shutdown = new CancellationTokenSource();
            shutdown.Cancel();
            await store.StopAsync(shutdown.Token);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }
}
