using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class UserAccountStoreTests
{
    [Fact]
    public async Task BootstrapAdminCanAuthenticateAndChangePassword()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-auth-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = CreateStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);

            var login = await store.AuthenticateAsync("ADMIN", "123456", CancellationToken.None);
            var change = await store.ChangePasswordAsync("admin", "123456", "SecureEdge#2026", CancellationToken.None);
            var oldPassword = await store.AuthenticateAsync("admin", "123456", CancellationToken.None);
            var newPassword = await store.AuthenticateAsync("admin", "SecureEdge#2026", CancellationToken.None);

            Assert.True(login.Success);
            Assert.True(login.MustChangePassword);
            Assert.True(change.Success);
            Assert.False(oldPassword.Success);
            Assert.True(newPassword.Success);
            Assert.False(newPassword.MustChangePassword);
            Assert.Equal("Admin", newPassword.Role);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RepeatedFailuresTemporarilyLockAccount()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-auth-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = CreateStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);

            await store.AuthenticateAsync("admin", "wrong-1", CancellationToken.None);
            await store.AuthenticateAsync("admin", "wrong-2", CancellationToken.None);
            var locked = await store.AuthenticateAsync("admin", "wrong-3", CancellationToken.None);
            var correctWhileLocked = await store.AuthenticateAsync("admin", "123456", CancellationToken.None);

            Assert.False(locked.Success);
            Assert.True(locked.IsLocked);
            Assert.NotNull(locked.LockedUntil);
            Assert.False(correctWhileLocked.Success);
            Assert.True(correctWhileLocked.IsLocked);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task PasswordDoesNotRequireMixedLetterCase()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-auth-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = CreateStore(databasePath);
            await store.InitializeAsync(CancellationToken.None);

            var change = await store.ChangePasswordAsync("admin", "123456", "gateway#2026", CancellationToken.None);
            var login = await store.AuthenticateAsync("admin", "gateway#2026", CancellationToken.None);

            Assert.True(change.Success);
            Assert.True(login.Success);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static SqliteUserAccountStore CreateStore(string databasePath) => new(
        $"Data Source={databasePath};Pooling=False",
        new AuthenticationBootstrapOptions
        {
            AdminUsername = "admin",
            AdminPassword = "123456",
            MaximumFailedAttempts = 3,
            LockoutMinutes = 1
        });

    private static void DeleteDatabase(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete($"{databasePath}-shm");
        File.Delete($"{databasePath}-wal");
    }
}
