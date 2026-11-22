using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MQTTnet;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure.GatewayMqtt;

namespace Snet.Edge.Infrastructure;

public sealed class MqttOutbox(
    string connectionString,
    IGatewayConfigurationStore configurationStore,
    IResourceGuard resourceGuard,
    ITemplateResolver templateResolver,
    IMqttPayloadBuilder payloadBuilder,
    ITopicBuilder topicBuilder) : IMqttMessagePublisher, IMqttOutboxMonitor, IMqttOutboxConfigurationSynchronizer
{
    private static readonly string GatewayVersion = typeof(MqttOutbox).Assembly.GetName().Version?.ToString(3) ?? "1.1.0";
    private long _pendingCount = -1;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS mqtt_outbox (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                topic TEXT NOT NULL,
                payload TEXT NOT NULL,
                qos INTEGER NOT NULL,
                retain INTEGER NOT NULL,
                created_at_utc TEXT NOT NULL,
                attempts INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_mqtt_outbox_id ON mqtt_outbox(id);
            CREATE TABLE IF NOT EXISTS mqtt_dead_letter (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                original_id INTEGER NOT NULL,
                topic TEXT NOT NULL,
                payload TEXT NOT NULL,
                attempts INTEGER NOT NULL,
                error TEXT NOT NULL,
                failed_at_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS mqtt_sequence (
                group_id TEXT NOT NULL PRIMARY KEY,
                sequence INTEGER NOT NULL
            );
            """,
            cancellationToken: cancellationToken));
        await EnsureOutboxColumnAsync(connection, "source_device_id", "TEXT NULL", cancellationToken);
        await EnsureOutboxColumnAsync(connection, "source_tag_ids", "TEXT NOT NULL DEFAULT '[]'", cancellationToken);
        var initialCount = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM mqtt_outbox;",
            cancellationToken: cancellationToken));
        Interlocked.Exchange(ref _pendingCount, initialCount);
    }

    public async ValueTask PublishAsync(MqttPublishRequest request, CancellationToken cancellationToken)
    {
        if (!resourceGuard.AllowPersistentEnqueue)
        {
            throw new IOException($"资源保护模式禁止写入MQTT离线队列：{resourceGuard.Current.Reason}");
        }

        var mqtt = (await configurationStore.GetSnapshotAsync(cancellationToken)).Mqtt;
        if (!mqtt.Enabled)
        {
            return;
        }

        var maxMessages = Math.Max(mqtt.OutboxMaxMessages, 1_000);
        var cachedPending = Interlocked.Read(ref _pendingCount);
        if (cachedPending >= maxMessages)
        {
            throw new IOException($"MQTT离线队列已达到容量上限 {mqtt.OutboxMaxMessages}，拒绝继续写入。");
        }

        var template = await templateResolver.ResolveAsync(request.TemplateId, cancellationToken);
        var context = new MqttPayloadContext(mqtt.GatewayId, GatewayVersion, request.GroupName, request.Line);
        var topic = topicBuilder.Build(request.TopicTemplate, request.DeviceData, context);
        var payload = payloadBuilder.Build(request.DeviceData, template, context);

        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (cachedPending < 0)
        {
            cachedPending = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM mqtt_outbox;",
                transaction: transaction,
                cancellationToken: cancellationToken));
            Interlocked.Exchange(ref _pendingCount, cachedPending);
            if (cachedPending >= maxMessages)
            {
                throw new IOException($"MQTT离线队列已达到容量上限 {mqtt.OutboxMaxMessages}，拒绝继续写入。");
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO mqtt_outbox(
                topic, payload, qos, retain, created_at_utc, source_device_id, source_tag_ids)
            VALUES (
                @Topic, @Payload, @Qos, @Retain, @CreatedAtUtc, @SourceDeviceId, @SourceTagIds);
            """,
            new
            {
                Topic = topic,
                Payload = payload,
                Qos = Math.Clamp(mqtt.Qos, 0, 2),
                Retain = mqtt.Retain ? 1 : 0,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                SourceDeviceId = request.SourceDeviceId == Guid.Empty ? null : request.SourceDeviceId.ToString("D"),
                SourceTagIds = System.Text.Json.JsonSerializer.Serialize(request.SourceTagIds)
            },
            transaction,
            cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        Interlocked.Increment(ref _pendingCount);
    }

    public async ValueTask<IReadOnlyList<MqttOutboxItem>> ReadBatchAsync(int size, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<MqttOutboxItem>(new CommandDefinition(
            """
            SELECT id, topic, payload, qos, retain, attempts,
                   created_at_utc AS CreatedAtUtc,
                   source_device_id AS SourceDeviceId,
                   source_tag_ids AS SourceTagIds
            FROM mqtt_outbox
            ORDER BY id
            LIMIT @Size;
            """,
            new { Size = Math.Clamp(size, 1, 1_000) },
            cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async ValueTask MarkPublishedAsync(long id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var deleted = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mqtt_outbox WHERE id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        if (deleted > 0)
        {
            Interlocked.Add(ref _pendingCount, -deleted);
        }
    }

    public async ValueTask DiscardAsync(long id, CancellationToken cancellationToken)
    {
        await MarkPublishedAsync(id, cancellationToken);
    }

    public async ValueTask DiscardPendingAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM mqtt_outbox;",
            cancellationToken: cancellationToken));
        Interlocked.Exchange(ref _pendingCount, 0);
    }

    public async ValueTask<int> PruneStaleAsync(GatewaySnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var items = await connection.QueryAsync<MqttOutboxItem>(new CommandDefinition(
            """
            SELECT id, topic, payload, qos, retain, attempts,
                   created_at_utc AS CreatedAtUtc,
                   source_device_id AS SourceDeviceId,
                   source_tag_ids AS SourceTagIds
            FROM mqtt_outbox;
            """,
            cancellationToken: cancellationToken));
        var staleIds = items.Where(item => !IsCurrent(item, snapshot)).Select(item => item.Id).ToArray();
        var totalDeleted = 0;
        foreach (var batch in staleIds.Chunk(500))
        {
            totalDeleted += await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mqtt_outbox WHERE id IN @Ids;",
                new { Ids = batch },
                cancellationToken: cancellationToken));
        }

        if (totalDeleted > 0)
        {
            Interlocked.Add(ref _pendingCount, -totalDeleted);
        }

        return staleIds.Length;
    }

    public static bool IsCurrent(MqttOutboxItem item, GatewaySnapshot snapshot)
    {
        if (!Guid.TryParse(item.SourceDeviceId, out var deviceId))
        {
            return false;
        }

        var device = snapshot.Devices.FirstOrDefault(candidate => candidate.Id == deviceId && candidate.Enabled);
        if (device is null || !snapshot.Groups.Any(group => group.Id == device.GroupId && group.Enabled))
        {
            return false;
        }

        Guid[] sourceTagIds;
        try
        {
            sourceTagIds = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(item.SourceTagIds) ?? [];
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }

        if (sourceTagIds.Length == 0)
        {
            return true;
        }

        var activeTagIds = snapshot.Tags
            .Where(tag => tag.Enabled && tag.DeviceId == deviceId)
            .Select(tag => tag.Id)
            .ToHashSet();
        return sourceTagIds.All(activeTagIds.Contains);
    }

    public async ValueTask MarkFailedAsync(MqttOutboxItem item, string error, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        if (item.Attempts + 1 >= maxAttempts)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO mqtt_dead_letter(original_id, topic, payload, attempts, error, failed_at_utc)
                VALUES (@Id, @Topic, @Payload, @Attempts, @Error, @FailedAtUtc);
                DELETE FROM mqtt_outbox WHERE id = @Id;
                """,
                new
                {
                    item.Id,
                    item.Topic,
                    item.Payload,
                    Attempts = item.Attempts + 1,
                    Error = Limit(error, 2_000),
                    FailedAtUtc = DateTimeOffset.UtcNow.ToString("O")
                },
                transaction,
                cancellationToken: cancellationToken));
        }
        else
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE mqtt_outbox SET attempts = attempts + 1 WHERE id = @Id;",
                new { item.Id },
                transaction,
                cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
        if (item.Attempts + 1 >= maxAttempts)
        {
            Interlocked.Decrement(ref _pendingCount);
        }
    }

    public async ValueTask<MqttOutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<StatisticsRow>(new CommandDefinition(
            """
            SELECT
                (SELECT COUNT(1) FROM mqtt_outbox) AS PendingMessages,
                (SELECT COUNT(1) FROM mqtt_dead_letter) AS DeadLetterMessages,
                COALESCE((SELECT MIN(created_at_utc) FROM mqtt_outbox), '') AS OldestCreatedAtUtc;
            """,
            cancellationToken: cancellationToken));
        Interlocked.Exchange(ref _pendingCount, row.PendingMessages);
        var oldestAge = string.IsNullOrEmpty(row.OldestCreatedAtUtc)
            ? 0
            : Math.Max(0, (long)(DateTimeOffset.UtcNow - DateTimeOffset.Parse(row.OldestCreatedAtUtc)).TotalSeconds);
        return new MqttOutboxStatistics(row.PendingMessages, row.DeadLetterMessages, oldestAge, DateTimeOffset.UtcNow);
    }

    public async ValueTask<IReadOnlyList<MqttDeadLetter>> GetDeadLettersAsync(int take, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        var rows = await connection.QueryAsync<DeadLetterRow>(new CommandDefinition(
            """
            SELECT id, topic, payload, attempts, error, failed_at_utc AS FailedAtUtc
            FROM mqtt_dead_letter
            ORDER BY id DESC
            LIMIT @Take;
            """,
            new { Take = Math.Clamp(take, 1, 500) },
            cancellationToken: cancellationToken));
        return rows.Select(row => new MqttDeadLetter(
            row.Id,
            row.Topic,
            row.Payload,
            checked((int)row.Attempts),
            row.Error,
            DateTimeOffset.Parse(row.FailedAtUtc))).ToArray();
    }

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask EnsureOutboxColumnAsync(
        SqliteConnection connection,
        string columnName,
        string declaration,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM pragma_table_info('mqtt_outbox') WHERE name = @ColumnName;",
            new { ColumnName = columnName },
            cancellationToken: cancellationToken));
        if (exists == 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                $"ALTER TABLE mqtt_outbox ADD COLUMN {columnName} {declaration};",
                cancellationToken: cancellationToken));
        }
    }

    private static string Limit(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];

    private sealed record StatisticsRow(long PendingMessages, long DeadLetterMessages, string OldestCreatedAtUtc);
    private sealed record DeadLetterRow(long Id, string Topic, string Payload, long Attempts, string Error, string FailedAtUtc);
}

public sealed class MqttStoreForwardWorker(
    MqttOutbox outbox,
    IGatewayConfigurationStore configurationStore,
    INetworkConfigurationService networkConfiguration,
    IMqttRuntimeStateStore runtimeState,
    IGatewayMetrics metrics,
    ILogger<MqttStoreForwardWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var factory = new MqttClientFactory();
        using var client = factory.CreateMqttClient();
        var reconnectDelay = TimeSpan.FromSeconds(1);
        bool? previousStoreAndForwardEnabled = null;
        var appliedNetworkRevision = -1L;
        MqttOptions? appliedOptions = null;

        while (!stoppingToken.IsCancellationRequested)
        {
            MqttOptions? currentOptions = null;
            var configurationRevision = runtimeState.ConfigurationRevision;
            try
            {
                var snapshot = await configurationStore.GetSnapshotAsync(stoppingToken);
                var mqtt = snapshot.Mqtt;
                currentOptions = mqtt;
                if (!mqtt.StoreAndForwardEnabled && previousStoreAndForwardEnabled is not false)
                {
                    await outbox.DiscardPendingAsync(stoppingToken);
                    logger.LogInformation("MQTT断点续传已关闭，历史Outbox积压已清理");
                }

                previousStoreAndForwardEnabled = mqtt.StoreAndForwardEnabled;
                var networkRevision = networkConfiguration.GetRevision(NetworkUsage.MqttUpload);
                if (!mqtt.Enabled)
                {
                    await DisconnectIfNeededAsync(client, stoppingToken);
                    appliedOptions = null;
                    SetRuntimeStatus(
                        MqttConnectionState.Disabled,
                        mqtt,
                        lastAttemptAt: null,
                        connectedAt: null,
                        nextRetryAt: null,
                        lastError: null);
                    await runtimeState.WaitForConfigurationChangeAsync(
                        configurationRevision,
                        TimeSpan.FromSeconds(1),
                        stoppingToken);
                    continue;
                }

                if (client.IsConnected && (appliedNetworkRevision != networkRevision || appliedOptions != mqtt))
                {
                    SetRuntimeStatus(
                        MqttConnectionState.Reconnecting,
                        mqtt,
                        runtimeState.Current.LastAttemptAt,
                        runtimeState.Current.ConnectedAt,
                        null,
                        null);
                    await DisconnectIfNeededAsync(client, stoppingToken);
                    appliedOptions = null;
                    logger.LogInformation("MQTT生效配置或上传网卡绑定已变更，正在重建连接");
                }

                if (!client.IsConnected)
                {
                    var attemptAt = DateTimeOffset.UtcNow;
                    var isReconnect = runtimeState.Current.State is MqttConnectionState.Connected or
                        MqttConnectionState.Reconnecting or MqttConnectionState.Faulted;
                    SetRuntimeStatus(
                        isReconnect ? MqttConnectionState.Reconnecting : MqttConnectionState.Connecting,
                        mqtt,
                        attemptAt,
                        runtimeState.Current.ConnectedAt,
                        null,
                        null);
                    try
                    {
                        var options = MqttNetworkOptionsFactory.Build(
                            mqtt,
                            networkConfiguration.ResolveLocalIpv4(NetworkUsage.MqttUpload));
                        var connectionResult = await client.ConnectAsync(options, stoppingToken);
                        if (connectionResult.ResultCode != MqttClientConnectResultCode.Success)
                        {
                            throw new IOException($"MQTT连接失败: {connectionResult.ResultCode}");
                        }

                        reconnectDelay = TimeSpan.FromSeconds(1);
                        appliedNetworkRevision = networkRevision;
                        appliedOptions = mqtt;
                        SetRuntimeStatus(
                            MqttConnectionState.Connected,
                            mqtt,
                            attemptAt,
                            DateTimeOffset.UtcNow,
                            null,
                            null);
                        logger.LogInformation("MQTT已连接 {Host}:{Port}", mqtt.Host, mqtt.Port);
                    }
                    catch
                    {
                        if (!mqtt.StoreAndForwardEnabled)
                        {
                            await outbox.DiscardPendingAsync(stoppingToken);
                        }

                        throw;
                    }
                }

                var batch = await outbox.ReadBatchAsync(200, stoppingToken);
                if (batch.Count == 0)
                {
                    await runtimeState.WaitForConfigurationChangeAsync(
                        configurationRevision,
                        TimeSpan.FromMilliseconds(250),
                        stoppingToken);
                    continue;
                }

                var replayDelay = TimeSpan.FromMilliseconds(1_000D / Math.Clamp(mqtt.ReplayMaxMessagesPerSecond, 1, 10_000));
                foreach (var item in batch)
                {
                    try
                    {
                        if (!MqttOutbox.IsCurrent(item, snapshot))
                        {
                            await outbox.DiscardAsync(item.Id, stoppingToken);
                            continue;
                        }

                        var message = new MqttApplicationMessageBuilder()
                            .WithTopic(item.Topic)
                            .WithPayload(item.Payload)
                            .WithQualityOfServiceLevel((MQTTnet.Protocol.MqttQualityOfServiceLevel)item.Qos)
                            .WithRetainFlag(item.Retain != 0)
                            .Build();
                        var publishResult = await client.PublishAsync(message, stoppingToken);
                        if (!publishResult.IsSuccess)
                        {
                            throw new IOException($"MQTT发布失败: {publishResult.ReasonCode}");
                        }

                        await outbox.MarkPublishedAsync(item.Id, stoppingToken);
                        metrics.RecordMqttPublished();
                        await Task.Delay(replayDelay, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        metrics.RecordMqttPublishFailure();
                        if (mqtt.StoreAndForwardEnabled)
                        {
                            await outbox.MarkFailedAsync(
                                item,
                                exception.Message,
                                Math.Max(mqtt.MaxPublishAttempts, 1),
                                stoppingToken);
                        }
                        else
                        {
                            await outbox.DiscardAsync(item.Id, stoppingToken);
                        }

                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                var retryAt = DateTimeOffset.UtcNow.Add(reconnectDelay);
                if (currentOptions is not null)
                {
                    SetRuntimeStatus(
                        MqttConnectionState.Faulted,
                        currentOptions,
                        runtimeState.Current.LastAttemptAt ?? DateTimeOffset.UtcNow,
                        runtimeState.Current.ConnectedAt,
                        retryAt,
                        LimitError(exception.Message));
                }

                logger.LogWarning(exception, "MQTT链路异常，{DelaySeconds} 秒后重连", reconnectDelay.TotalSeconds);
                try
                {
                    await DisconnectIfNeededAsync(client, CancellationToken.None);
                }
                catch (Exception disconnectException)
                {
                    logger.LogDebug(disconnectException, "MQTT断开连接异常");
                }
                try
                {
                    var changed = await runtimeState.WaitForConfigurationChangeAsync(
                        configurationRevision,
                        reconnectDelay,
                        stoppingToken);
                    reconnectDelay = changed
                        ? TimeSpan.FromSeconds(1)
                        : TimeSpan.FromSeconds(Math.Min(reconnectDelay.TotalSeconds * 2, 60));
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        try
        {
            await DisconnectIfNeededAsync(client, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "MQTT服务停止时断开连接异常");
        }
    }

    private void SetRuntimeStatus(
        MqttConnectionState state,
        MqttOptions mqtt,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? connectedAt,
        DateTimeOffset? nextRetryAt,
        string? lastError)
    {
        var current = runtimeState.Current;
        var changedAt = current.State == state &&
                        current.Enabled == mqtt.Enabled &&
                        current.Host == mqtt.Host &&
                        current.Port == mqtt.Port &&
                        current.LastError == lastError
            ? current.ChangedAt
            : DateTimeOffset.UtcNow;
        runtimeState.Set(new MqttRuntimeStatus(
            state,
            mqtt.Enabled,
            mqtt.Host,
            mqtt.Port,
            changedAt,
            lastAttemptAt,
            connectedAt,
            nextRetryAt,
            lastError));
    }

    private static string LimitError(string error) => error.Length <= 1_000 ? error : error[..1_000];

    private static async ValueTask DisconnectIfNeededAsync(IMqttClient client, CancellationToken cancellationToken)
    {
        if (client.IsConnected)
        {
            await client.DisconnectAsync(cancellationToken: cancellationToken);
        }
    }

}

public sealed record MqttOutboxItem(
    long Id,
    string Topic,
    string Payload,
    long Qos,
    long Retain,
    long Attempts,
    string CreatedAtUtc,
    string? SourceDeviceId,
    string SourceTagIds);
