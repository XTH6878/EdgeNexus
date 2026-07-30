using System.Text.Json;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;
using Snet.Edge.Infrastructure.GatewayMqtt;

namespace Snet.Edge.Tests;

public sealed class MqttOutboxTests
{
    [Fact]
    public async Task DeviceSpecificTemplateAndMqttIdentityShapeFinalMessage()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-outbox-device-template-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using var configurationStore = new SqliteGatewayConfigurationStore(connectionString);
            await configurationStore.InitializeAsync(CancellationToken.None);
            await configurationStore.SaveMqttOptionsAsync(new MqttOptions
            {
                Enabled = true,
                GatewayId = "GW-TEST"
            }, CancellationToken.None);
            await configurationStore.PublishDraftAsync("Enable MQTT device template test", CancellationToken.None);

            await using var templateManager = new MqttTemplateManager(connectionString);
            await templateManager.InitializeAsync(CancellationToken.None);
            var template = await templateManager.UpsertAsync(new MqttTemplate
            {
                Id = Guid.NewGuid(),
                Name = "Device A Template",
                IncludeDeviceId = true,
                IncludeDeviceName = true,
                IncludeTimestamp = true,
                IncludeTag = true,
                IncludeValue = true,
                IncludeQuality = false,
                CustomFields = [new MqttCustomField { Name = "templateMarker", Value = "A-${deviceId}" }]
            }, CancellationToken.None);
            var variableResolver = new MqttVariableResolver();
            var outbox = new MqttOutbox(
                connectionString,
                configurationStore,
                new ResourceGuard(),
                templateManager,
                new MqttPayloadBuilder(variableResolver),
                new MqttTopicBuilder(variableResolver));
            await outbox.InitializeAsync(CancellationToken.None);

            await outbox.PublishAsync(new MqttPublishRequest(
                Guid.NewGuid(),
                "Line A",
                "LINE-A",
                "factory/${deviceId}/data",
                template.Id,
                new DeviceData(
                    "PLC-BIZ-007",
                    "Machine A",
                    "192.168.1.10",
                    ProtocolKind.ModbusTcp,
                    DateTimeOffset.UtcNow,
                    [new TagData("Temperature", 25.5M, DataQuality.Good)])),
                CancellationToken.None);

            var message = Assert.Single(await outbox.ReadBatchAsync(10, CancellationToken.None));
            Assert.Equal("factory/PLC-BIZ-007/data", message.Topic);
            using var payload = JsonDocument.Parse(message.Payload);
            Assert.Equal("PLC-BIZ-007", payload.RootElement.GetProperty("deviceId").GetString());
            Assert.Equal("Machine A", payload.RootElement.GetProperty("deviceName").GetString());
            Assert.Equal("A-PLC-BIZ-007", payload.RootElement.GetProperty("templateMarker").GetString());
            var point = Assert.Single(payload.RootElement.GetProperty("d").EnumerateArray());
            Assert.Equal("PLC-BIZ-007:Temperature", point.GetProperty("tag").GetString());
            Assert.False(point.TryGetProperty("quality", out _));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task ConfigurationPruneRemovesQueuedPayloadContainingDeletedTag()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-outbox-prune-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
            var device = new DeviceDefinition
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                Name = "测试2",
                MqttDeviceId = "PLC002",
                Protocol = ProtocolKind.ModbusTcp,
                Host = "192.168.1.20",
                Port = 502
            };
            var tag = new TagDefinition
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                Name = "新测点2",
                Address = "0",
                DataType = TagDataType.Int16
            };
            await using var configurationStore = new SqliteGatewayConfigurationStore(connectionString);
            await configurationStore.InitializeAsync(CancellationToken.None);
            await configurationStore.ReplaceDraftAsync(
                new GatewaySnapshot([group], [device], [tag], new MqttOptions { Enabled = true }),
                CancellationToken.None);
            await configurationStore.PublishDraftAsync("准备失效队列测试", CancellationToken.None);
            await using var templateManager = new MqttTemplateManager(connectionString);
            await templateManager.InitializeAsync(CancellationToken.None);
            var resolver = new MqttVariableResolver();
            var outbox = new MqttOutbox(
                connectionString,
                configurationStore,
                new ResourceGuard(),
                templateManager,
                new MqttPayloadBuilder(resolver),
                new MqttTopicBuilder(resolver));
            await outbox.InitializeAsync(CancellationToken.None);
            await outbox.PublishAsync(new MqttPublishRequest(
                group.Id,
                group.Name,
                null,
                group.Topic,
                null,
                new DeviceData(
                    "PLC002",
                    device.Name,
                    device.Host,
                    device.Protocol,
                    DateTimeOffset.UtcNow,
                    [new TagData(tag.Name, 2, DataQuality.Good)]))
            {
                SourceDeviceId = device.Id,
                SourceTagIds = [tag.Id]
            }, CancellationToken.None);

            var removed = await outbox.PruneStaleAsync(
                new GatewaySnapshot([group], [device], [], new MqttOptions { Enabled = true }),
                CancellationToken.None);

            Assert.Equal(1, removed);
            Assert.Equal(0, (await outbox.GetStatisticsAsync(CancellationToken.None)).PendingMessages);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task OutboxPersistsBuiltPayloadAndMovesPoisonMessageToDeadLetter()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-outbox-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using var configurationStore = new SqliteGatewayConfigurationStore(connectionString);
            await configurationStore.InitializeAsync(CancellationToken.None);
            await configurationStore.SaveMqttOptionsAsync(new MqttOptions
            {
                Enabled = true,
                GatewayId = "GW-TEST",
                OutboxMaxMessages = 1_000,
                MaxPublishAttempts = 1
            }, CancellationToken.None);
            await configurationStore.PublishDraftAsync("启用测试MQTT", CancellationToken.None);

            await using var templateManager = new MqttTemplateManager(connectionString);
            await templateManager.InitializeAsync(CancellationToken.None);
            var variableResolver = new MqttVariableResolver();
            var outbox = new MqttOutbox(
                connectionString,
                configurationStore,
                new ResourceGuard(),
                templateManager,
                new MqttPayloadBuilder(variableResolver),
                new MqttTopicBuilder(variableResolver));
            await outbox.InitializeAsync(CancellationToken.None);
            Assert.Equal(0, (await outbox.GetStatisticsAsync(CancellationToken.None)).PendingMessages);
            var groupId = Guid.NewGuid();
            var message = new MqttPublishRequest(
                groupId,
                "一号线",
                "LINE01",
                "edge/line1",
                null,
                new DeviceData(
                    "PLC001",
                    "设备1",
                    "192.168.1.10",
                    ProtocolKind.ModbusTcp,
                    DateTimeOffset.UtcNow,
                    [new TagData("温度", 25.5M, DataQuality.Good)]));

            await outbox.PublishAsync(message, CancellationToken.None);
            await outbox.PublishAsync(message, CancellationToken.None);

            var batch = await outbox.ReadBatchAsync(10, CancellationToken.None);
            Assert.Equal(2, batch.Count);
            using var firstPayload = JsonDocument.Parse(batch[0].Payload);
            Assert.False(firstPayload.RootElement.TryGetProperty("deviceId", out _));
            Assert.True(firstPayload.RootElement.TryGetProperty("ts", out _));
            var point = Assert.Single(firstPayload.RootElement.GetProperty("d").EnumerateArray());
            Assert.Equal("PLC001:温度", point.GetProperty("tag").GetString());
            Assert.True(batch[1].Id > batch[0].Id);

            await outbox.MarkFailedAsync(batch[0], "模拟不可恢复错误", 1, CancellationToken.None);
            var statistics = await outbox.GetStatisticsAsync(CancellationToken.None);
            var deadLetters = await outbox.GetDeadLettersAsync(10, CancellationToken.None);

            Assert.Equal(1, statistics.PendingMessages);
            Assert.Equal(1, statistics.DeadLetterMessages);
            Assert.Equal("模拟不可恢复错误", Assert.Single(deadLetters).Error);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task DiscardOperationsRemoveMessagesWithoutCreatingDeadLetters()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-outbox-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using var configurationStore = new SqliteGatewayConfigurationStore(connectionString);
            await configurationStore.InitializeAsync(CancellationToken.None);
            await configurationStore.SaveMqttOptionsAsync(new MqttOptions
            {
                Enabled = true,
                StoreAndForwardEnabled = false
            }, CancellationToken.None);
            await configurationStore.PublishDraftAsync("禁用断点续传测试", CancellationToken.None);

            await using var templateManager = new MqttTemplateManager(connectionString);
            await templateManager.InitializeAsync(CancellationToken.None);
            var variableResolver = new MqttVariableResolver();
            var outbox = new MqttOutbox(
                connectionString,
                configurationStore,
                new ResourceGuard(),
                templateManager,
                new MqttPayloadBuilder(variableResolver),
                new MqttTopicBuilder(variableResolver));
            await outbox.InitializeAsync(CancellationToken.None);
            var message = new MqttPublishRequest(
                Guid.NewGuid(),
                "测试组",
                null,
                "edge/test",
                null,
                new DeviceData(
                    "PLC001",
                    "设备",
                    "127.0.0.1",
                    ProtocolKind.ModbusTcp,
                    DateTimeOffset.UtcNow,
                    [new TagData("点位", 1, DataQuality.Good)]));

            await outbox.PublishAsync(message, CancellationToken.None);
            await outbox.PublishAsync(message, CancellationToken.None);
            var first = Assert.Single((await outbox.ReadBatchAsync(1, CancellationToken.None)));
            await outbox.DiscardAsync(first.Id, CancellationToken.None);
            Assert.Equal(1, (await outbox.GetStatisticsAsync(CancellationToken.None)).PendingMessages);

            await outbox.DiscardPendingAsync(CancellationToken.None);
            var statistics = await outbox.GetStatisticsAsync(CancellationToken.None);
            Assert.Equal(0, statistics.PendingMessages);
            Assert.Equal(0, statistics.DeadLetterMessages);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }
}
