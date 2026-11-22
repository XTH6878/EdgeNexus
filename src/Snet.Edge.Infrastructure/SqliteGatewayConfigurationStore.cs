using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Data.Sqlite;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class SqliteGatewayConfigurationStore(string connectionString) :
    IGatewayConfigurationStore,
    IMqttConfigurationPublisher,
    IAsyncDisposable
{
    private const int SingletonId = 1;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private GatewaySnapshot? _cachedActiveSnapshot;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            CREATE TABLE IF NOT EXISTS gateway_state (
                id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                active_version INTEGER NOT NULL,
                active_payload TEXT NOT NULL,
                draft_payload TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gateway_revision (
                version INTEGER NOT NULL PRIMARY KEY,
                payload TEXT NOT NULL,
                reason TEXT NOT NULL,
                created_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS gateway_revision_deletion (
                version INTEGER NOT NULL PRIMARY KEY,
                reason TEXT NOT NULL,
                deleted_at_utc TEXT NOT NULL,
                FOREIGN KEY(version) REFERENCES gateway_revision(version)
            );
            """,
            cancellationToken: cancellationToken));

        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM gateway_state WHERE id = @Id;",
            new { Id = SingletonId },
            cancellationToken: cancellationToken));
        if (exists != 0)
        {
            var existingPayload = await connection.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT active_payload FROM gateway_state WHERE id = @Id;",
                new { Id = SingletonId },
                cancellationToken: cancellationToken));
            Volatile.Write(ref _cachedActiveSnapshot, Deserialize(existingPayload));
            return;
        }

        var initial = await TryReadLegacySnapshotAsync(connection, cancellationToken) ?? CreateEmptySnapshot();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertInitialStateAsync(connection, initial, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        Volatile.Write(ref _cachedActiveSnapshot, initial);
    }

    public async ValueTask<GatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var cached = Volatile.Read(ref _cachedActiveSnapshot);
        if (cached is not null)
        {
            return cached;
        }

        var loaded = await ReadPayloadAsync("active_payload", cancellationToken);
        Volatile.Write(ref _cachedActiveSnapshot, loaded);
        return loaded;
    }

    public ValueTask<GatewaySnapshot> GetDraftSnapshotAsync(CancellationToken cancellationToken) =>
        ReadPayloadAsync("draft_payload", cancellationToken);

    public async ValueTask<IReadOnlyList<ConfigurationRevision>> GetRevisionsAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<RevisionRow>(new CommandDefinition(
            """
            SELECT revision.version,
                   revision.payload,
                   revision.reason,
                   revision.created_at_utc AS CreatedAtUtc,
                   CASE WHEN revision.version = state.active_version THEN 1 ELSE 0 END AS IsActive
            FROM gateway_revision AS revision
            CROSS JOIN gateway_state AS state
            WHERE state.id = @Id
              AND NOT EXISTS (
                  SELECT 1
                  FROM gateway_revision_deletion AS deletion
                  WHERE deletion.version = revision.version)
            ORDER BY revision.version DESC
            LIMIT @Take;
            """,
            new { Id = SingletonId, Take = Math.Clamp(take, 1, 200) },
            cancellationToken: cancellationToken));
        return rows.Select(row =>
        {
            var snapshot = Deserialize(row.Payload);
            return new ConfigurationRevision(
                row.Version,
                DateTimeOffset.Parse(row.CreatedAtUtc, System.Globalization.CultureInfo.InvariantCulture),
                row.Reason,
                snapshot.Groups.Count,
                snapshot.Devices.Count,
                snapshot.Tags.Count,
                row.IsActive != 0);
        }).ToArray();
    }

    public async ValueTask<GatewaySnapshot> GetRevisionSnapshotAsync(long version, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            """
            SELECT revision.payload
            FROM gateway_revision AS revision
            WHERE revision.version = @Version
              AND NOT EXISTS (
                  SELECT 1
                  FROM gateway_revision_deletion AS deletion
                  WHERE deletion.version = revision.version);
            """,
            new { Version = version },
            cancellationToken: cancellationToken));
        return payload is null
            ? throw new KeyNotFoundException($"配置版本 {version} 不存在。")
            : Deserialize(payload);
    }

    public ValueTask<PublishResult> PublishDraftAsync(string reason, CancellationToken cancellationToken) =>
        PublishCoreAsync(null, null, reason, cancellationToken);

    public ValueTask<PublishResult> RollbackAsync(
        long version,
        long expectedActiveVersion,
        string reason,
        CancellationToken cancellationToken) =>
        PublishCoreAsync(version, expectedActiveVersion, reason, cancellationToken);

    public async ValueTask DeleteRevisionAsync(
        long version,
        long expectedActiveVersion,
        string reason,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("删除原因不能为空。", nameof(reason));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var activeVersion = await connection.QuerySingleAsync<long>(new CommandDefinition(
                "SELECT active_version FROM gateway_state WHERE id = @Id;",
                new { Id = SingletonId },
                transaction,
                cancellationToken: cancellationToken));
            if (activeVersion != expectedActiveVersion)
            {
                throw new InvalidOperationException(
                    $"当前生效版本已从 V{expectedActiveVersion} 变更为 V{activeVersion}，请刷新版本列表后重试。");
            }

            if (version == activeVersion)
            {
                throw new InvalidOperationException("当前生效版本不能删除。");
            }

            var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                """
                SELECT COUNT(1)
                FROM gateway_revision AS revision
                WHERE revision.version = @Version
                  AND NOT EXISTS (
                      SELECT 1
                      FROM gateway_revision_deletion AS deletion
                      WHERE deletion.version = revision.version);
                """,
                new { Version = version },
                transaction,
                cancellationToken: cancellationToken));
            if (exists == 0)
            {
                throw new KeyNotFoundException($"配置版本 {version} 不存在或已删除。");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO gateway_revision_deletion(version, reason, deleted_at_utc)
                VALUES (@Version, @Reason, @DeletedAtUtc);
                """,
                new
                {
                    Version = version,
                    Reason = reason.Trim(),
                    DeletedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask UpsertGroupAsync(MqttGroup group, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot => snapshot with
        {
            Groups = Upsert(snapshot.Groups, group, item => item.Id == group.Id)
        }, cancellationToken);

    public ValueTask DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot =>
        {
            var removedDeviceIds = snapshot.Devices
                .Where(device => device.GroupId == groupId)
                .Select(device => device.Id)
                .ToHashSet();
            return snapshot with
            {
                Groups = [.. snapshot.Groups.Where(item => item.Id != groupId)],
                Devices = [.. snapshot.Devices.Where(item => item.GroupId != groupId)],
                Tags = [.. snapshot.Tags.Where(tag => !removedDeviceIds.Contains(tag.DeviceId))]
            };
        }, cancellationToken);

    public ValueTask UpsertDeviceAsync(DeviceDefinition device, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot => snapshot with
        {
            Devices = Upsert(snapshot.Devices, device, item => item.Id == device.Id)
        }, cancellationToken);

    public ValueTask DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot => snapshot with
        {
            Devices = [.. snapshot.Devices.Where(item => item.Id != deviceId)],
            Tags = [.. snapshot.Tags.Where(item => item.DeviceId != deviceId)]
        }, cancellationToken);

    public ValueTask ReplaceTagsAsync(Guid deviceId, IReadOnlyCollection<TagDefinition> tags, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot => snapshot with
        {
            Tags = [.. snapshot.Tags.Where(item => item.DeviceId != deviceId), .. tags]
        }, cancellationToken);

    public ValueTask SaveMqttOptionsAsync(MqttOptions options, CancellationToken cancellationToken) =>
        MutateDraftAsync(snapshot => snapshot with { Mqtt = options }, cancellationToken);

    public async ValueTask<PublishResult> PublishMqttOptionsAsync(
        MqttOptions options,
        string reason,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var state = await connection.QuerySingleAsync<MqttPublishStateRow>(new CommandDefinition(
                """
                SELECT active_version AS ActiveVersion,
                       active_payload AS ActivePayload,
                       draft_payload AS DraftPayload
                FROM gateway_state
                WHERE id = @Id;
                """,
                new { Id = SingletonId },
                transaction,
                cancellationToken: cancellationToken));

            var nextActiveSnapshot = Deserialize(state.ActivePayload) with { Mqtt = options };
            var activePayload = Serialize(nextActiveSnapshot);
            var draftPayload = Serialize(Deserialize(state.DraftPayload) with { Mqtt = options });
            var nextVersion = state.ActiveVersion + 1;
            var publishedAt = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE gateway_state
                SET active_version = @Version,
                    active_payload = @ActivePayload,
                    draft_payload = @DraftPayload,
                    updated_at_utc = @PublishedAt
                WHERE id = @Id;
                INSERT INTO gateway_revision(version, payload, reason, created_at_utc)
                VALUES (@Version, @ActivePayload, @Reason, @PublishedAt);
                """,
                new
                {
                    Id = SingletonId,
                    Version = nextVersion,
                    ActivePayload = activePayload,
                    DraftPayload = draftPayload,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "MQTT配置独立应用" : reason.Trim(),
                    PublishedAt = publishedAt.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            Volatile.Write(ref _cachedActiveSnapshot, nextActiveSnapshot);
            return new PublishResult(nextVersion, publishedAt);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask ReplaceDraftAsync(GatewaySnapshot snapshot, CancellationToken cancellationToken) =>
        MutateDraftAsync(_ => snapshot, cancellationToken);

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask<GatewaySnapshot> ReadPayloadAsync(string column, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var payload = await connection.QuerySingleAsync<string>(new CommandDefinition(
            $"SELECT {column} FROM gateway_state WHERE id = @Id;",
            new { Id = SingletonId },
            cancellationToken: cancellationToken));
        return Deserialize(payload);
    }

    private async ValueTask MutateDraftAsync(Func<GatewaySnapshot, GatewaySnapshot> mutate, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var payload = await connection.QuerySingleAsync<string>(new CommandDefinition(
                "SELECT draft_payload FROM gateway_state WHERE id = @Id;",
                new { Id = SingletonId },
                transaction,
                cancellationToken: cancellationToken));
            var updated = Serialize(mutate(Deserialize(payload)));
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE gateway_state
                SET draft_payload = @Payload, updated_at_utc = @UpdatedAtUtc
                WHERE id = @Id;
                """,
                new { Id = SingletonId, Payload = updated, UpdatedAtUtc = DateTimeOffset.UtcNow.ToString("O") },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<PublishResult> PublishCoreAsync(
        long? rollbackVersion,
        long? expectedActiveVersion,
        string reason,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var state = await connection.QuerySingleAsync<StateRow>(new CommandDefinition(
                "SELECT active_version AS ActiveVersion, draft_payload AS DraftPayload FROM gateway_state WHERE id = @Id;",
                new { Id = SingletonId },
                transaction,
                cancellationToken: cancellationToken));
            if (expectedActiveVersion is not null && state.ActiveVersion != expectedActiveVersion.Value)
            {
                throw new InvalidOperationException(
                    $"当前生效版本已从 V{expectedActiveVersion.Value} 变更为 V{state.ActiveVersion}，请刷新版本列表后重试。");
            }

            var payload = rollbackVersion is null
                ? state.DraftPayload
                : await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                    """
                    SELECT revision.payload
                    FROM gateway_revision AS revision
                    WHERE revision.version = @Version
                      AND NOT EXISTS (
                          SELECT 1
                          FROM gateway_revision_deletion AS deletion
                          WHERE deletion.version = revision.version);
                    """,
                    new { Version = rollbackVersion.Value },
                    transaction,
                    cancellationToken: cancellationToken))
                    ?? throw new KeyNotFoundException($"配置版本 {rollbackVersion.Value} 不存在。");
            var nextVersion = state.ActiveVersion + 1;
            var publishedAt = DateTimeOffset.UtcNow;
            await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE gateway_state
                SET active_version = @Version,
                    active_payload = @Payload,
                    draft_payload = @Payload,
                    updated_at_utc = @PublishedAt
                WHERE id = @Id;
                INSERT INTO gateway_revision(version, payload, reason, created_at_utc)
                VALUES (@Version, @Payload, @Reason, @PublishedAt);
                """,
                new
                {
                    Id = SingletonId,
                    Version = nextVersion,
                    Payload = payload,
                    Reason = string.IsNullOrWhiteSpace(reason) ? "未填写发布说明" : reason.Trim(),
                    PublishedAt = publishedAt.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
            Volatile.Write(ref _cachedActiveSnapshot, Deserialize(payload));
            return new PublishResult(nextVersion, publishedAt);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async ValueTask<GatewaySnapshot?> TryReadLegacySnapshotAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var tableExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM sqlite_master WHERE type = 'table' AND name = 'gateway_config';",
            cancellationToken: cancellationToken));
        if (tableExists == 0)
        {
            return null;
        }

        var payload = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT payload FROM gateway_config WHERE id = @Id;",
            new { Id = SingletonId },
            cancellationToken: cancellationToken));
        return payload is null ? null : Deserialize(payload);
    }

    private async ValueTask InsertInitialStateAsync(
        SqliteConnection connection,
        GatewaySnapshot snapshot,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var payload = Serialize(snapshot);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO gateway_state(id, active_version, active_payload, draft_payload, updated_at_utc)
            VALUES (@Id, 1, @Payload, @Payload, @Now);
            INSERT INTO gateway_revision(version, payload, reason, created_at_utc)
            VALUES (1, @Payload, '系统初始化', @Now);
            """,
            new { Id = SingletonId, Payload = payload, Now = now },
            transaction,
            cancellationToken: cancellationToken));
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private GatewaySnapshot Deserialize(string payload) =>
        JsonSerializer.Deserialize<GatewaySnapshot>(payload, _jsonOptions)
        ?? throw new InvalidOperationException("网关配置反序列化失败。");

    private string Serialize(GatewaySnapshot snapshot) => JsonSerializer.Serialize(snapshot, _jsonOptions);

    private static IReadOnlyList<T> Upsert<T>(IReadOnlyList<T> source, T value, Func<T, bool> predicate)
    {
        var result = source.ToList();
        var index = result.FindIndex(item => predicate(item));
        if (index >= 0)
        {
            result[index] = value;
        }
        else
        {
            result.Add(value);
        }

        return result;
    }

    private static GatewaySnapshot CreateEmptySnapshot() => new([], [], [], new MqttOptions());

    private sealed record StateRow(long ActiveVersion, string DraftPayload);
    private sealed record MqttPublishStateRow(long ActiveVersion, string ActivePayload, string DraftPayload);
    private sealed record RevisionRow(long Version, string Payload, string Reason, string CreatedAtUtc, long IsActive);
}
