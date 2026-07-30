using Dapper;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Snet.Edge.Application;

namespace Snet.Edge.Infrastructure;

public sealed record AuthenticationBootstrapOptions
{
    public string AdminUsername { get; init; } = "admin";
    public string AdminPassword { get; init; } = "123456";
    public int MaximumFailedAttempts { get; init; } = 5;
    public int LockoutMinutes { get; init; } = 15;
}

public sealed class SqliteUserAccountStore(
    string connectionString,
    AuthenticationBootstrapOptions options) : IUserAccountStore, IAsyncDisposable
{
    private readonly PasswordHasher<string> _passwordHasher = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly string _dummyHash = new PasswordHasher<string>().HashPassword("unknown", Guid.NewGuid().ToString("N"));

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS gateway_user (
                    username TEXT NOT NULL PRIMARY KEY COLLATE NOCASE,
                    password_hash TEXT NOT NULL,
                    role TEXT NOT NULL,
                    failed_attempts INTEGER NOT NULL DEFAULT 0,
                    locked_until_utc TEXT NULL,
                    must_change_password INTEGER NOT NULL DEFAULT 1,
                    updated_at_utc TEXT NOT NULL
                );
                """,
                cancellationToken: cancellationToken));
            var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM gateway_user;",
                cancellationToken: cancellationToken));
            if (count > 0)
            {
                return;
            }

            var username = NormalizeUsername(options.AdminUsername);
            var passwordHash = _passwordHasher.HashPassword(username, options.AdminPassword);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO gateway_user(
                    username, password_hash, role, failed_attempts,
                    locked_until_utc, must_change_password, updated_at_utc)
                VALUES (@Username, @PasswordHash, 'Admin', 0, NULL, 1, @UpdatedAtUtc);
                """,
                new { Username = username, PasswordHash = passwordHash, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") },
                cancellationToken: cancellationToken));
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<UserAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = NormalizeUsername(username);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var user = await connection.QuerySingleOrDefaultAsync<UserRow>(new CommandDefinition(
                """
                SELECT username AS Username,
                       password_hash AS PasswordHash,
                       role AS Role,
                       failed_attempts AS FailedAttempts,
                       locked_until_utc AS LockedUntilUtc,
                       must_change_password AS MustChangePassword
                FROM gateway_user
                WHERE username = @Username;
                """,
                new { Username = normalizedUsername },
                cancellationToken: cancellationToken));
            var verification = _passwordHasher.VerifyHashedPassword(
                normalizedUsername,
                user?.PasswordHash ?? _dummyHash,
                password);
            if (user is null)
            {
                return Failed("用户名或密码错误。");
            }

            var now = DateTimeOffset.UtcNow;
            var lockedUntil = ParseTimestamp(user.LockedUntilUtc);
            if (lockedUntil > now)
            {
                return new UserAuthenticationResult(false, true, lockedUntil, null, null, false, "账户已临时锁定。");
            }

            if (verification == PasswordVerificationResult.Failed)
            {
                var failedAttempts = user.FailedAttempts + 1;
                var shouldLock = failedAttempts >= Math.Max(options.MaximumFailedAttempts, 3);
                var nextLockedUntil = shouldLock ? now.AddMinutes(Math.Max(options.LockoutMinutes, 1)) : (DateTimeOffset?)null;
                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE gateway_user
                    SET failed_attempts = @FailedAttempts,
                        locked_until_utc = @LockedUntilUtc,
                        updated_at_utc = @UpdatedAtUtc
                    WHERE username = @Username;
                    """,
                    new
                    {
                        Username = user.Username,
                        FailedAttempts = shouldLock ? 0 : failedAttempts,
                        LockedUntilUtc = nextLockedUntil?.ToString("O"),
                        UpdatedAtUtc = now.ToString("O")
                    },
                    cancellationToken: cancellationToken));
                return new UserAuthenticationResult(false, shouldLock, nextLockedUntil, null, null, false,
                    shouldLock ? "登录失败次数过多，账户已临时锁定。" : "用户名或密码错误。");
            }

            var nextHash = verification == PasswordVerificationResult.SuccessRehashNeeded
                ? _passwordHasher.HashPassword(user.Username, password)
                : user.PasswordHash;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE gateway_user
                SET password_hash = @PasswordHash,
                    failed_attempts = 0,
                    locked_until_utc = NULL,
                    updated_at_utc = @UpdatedAtUtc
                WHERE username = @Username;
                """,
                new { user.Username, PasswordHash = nextHash, UpdatedAtUtc = now.ToString("O") },
                cancellationToken: cancellationToken));
            return new UserAuthenticationResult(
                true, false, null, user.Username, user.Role, user.MustChangePassword != 0, null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<PasswordChangeResult> ChangePasswordAsync(
        string username,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken)
    {
        var passwordError = ValidateNewPassword(newPassword);
        if (passwordError is not null)
        {
            return new PasswordChangeResult(false, passwordError);
        }

        var normalizedUsername = NormalizeUsername(username);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var currentHash = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                "SELECT password_hash FROM gateway_user WHERE username = @Username;",
                new { Username = normalizedUsername },
                cancellationToken: cancellationToken));
            if (currentHash is null ||
                _passwordHasher.VerifyHashedPassword(normalizedUsername, currentHash, currentPassword) == PasswordVerificationResult.Failed)
            {
                return new PasswordChangeResult(false, "当前密码错误。");
            }

            var newHash = _passwordHasher.HashPassword(normalizedUsername, newPassword);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE gateway_user
                SET password_hash = @PasswordHash,
                    must_change_password = 0,
                    failed_attempts = 0,
                    locked_until_utc = NULL,
                    updated_at_utc = @UpdatedAtUtc
                WHERE username = @Username;
                """,
                new { Username = normalizedUsername, PasswordHash = newHash, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") },
                cancellationToken: cancellationToken));
            return new PasswordChangeResult(true, null);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private static string? ValidateNewPassword(string password)
    {
        if (password.Length < 8 ||
            !password.Any(char.IsDigit) ||
            password.All(char.IsLetterOrDigit))
        {
            return "新密码至少8位，并包含数字和特殊字符。";
        }

        return null;
    }

    private static string NormalizeUsername(string username) => username.Trim().ToLowerInvariant();

    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var timestamp) ? timestamp : null;

    private static UserAuthenticationResult Failed(string error) =>
        new(false, false, null, null, null, false, error);

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private sealed record UserRow(
        string Username,
        string PasswordHash,
        string Role,
        long FailedAttempts,
        string? LockedUntilUtc,
        long MustChangePassword);
}
