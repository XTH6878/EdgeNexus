using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class ConfigurationStoreTests
{
    [Fact]
    public async Task PublishingMqttOptionsPreservesOtherDraftChanges()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-edge-mqtt-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var draftGroup = new MqttGroup
            {
                Id = Guid.NewGuid(),
                Name = "待发布产线",
                Topic = "edge/pending"
            };
            var mqtt = new MqttOptions
            {
                Enabled = true,
                GatewayId = "EDGE_TEST",
                Host = "192.168.10.20",
                Port = 1884
            };

            await store.UpsertGroupAsync(draftGroup, CancellationToken.None);
            var publication = await store.PublishMqttOptionsAsync(mqtt, "MQTT独立发布测试", CancellationToken.None);

            var active = await store.GetSnapshotAsync(CancellationToken.None);
            var draft = await store.GetDraftSnapshotAsync(CancellationToken.None);
            Assert.Equal(2, publication.Version);
            Assert.Empty(active.Groups);
            Assert.Equal(mqtt, active.Mqtt);
            Assert.Equal(draftGroup, Assert.Single(draft.Groups));
            Assert.Equal(mqtt, draft.Mqtt);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task ConfigurationRoundTripPersistsGroupDeviceAndTags()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-edge-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var group = new MqttGroup
            {
                Id = Guid.NewGuid(),
                Name = "一号产线",
                Topic = "edge/line1/telemetry"
            };
            var device = new DeviceDefinition
            {
                Id = Guid.NewGuid(),
                GroupId = group.Id,
                Name = "注塑机01",
                Protocol = ProtocolKind.ModbusTcp,
                Host = "192.168.1.10",
                Port = 502
            };
            var tag = new TagDefinition
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                Name = "模具温度",
                Address = "D100",
                DataType = TagDataType.Float32
            };

            await store.UpsertGroupAsync(group, CancellationToken.None);
            await store.UpsertDeviceAsync(device, CancellationToken.None);
            await store.ReplaceTagsAsync(device.Id, [tag], CancellationToken.None);

            var activeBeforePublish = await store.GetSnapshotAsync(CancellationToken.None);
            Assert.Empty(activeBeforePublish.Groups);

            var draft = await store.GetDraftSnapshotAsync(CancellationToken.None);
            Assert.Equal(group, Assert.Single(draft.Groups));

            var publication = await store.PublishDraftAsync("自动化测试发布", CancellationToken.None);
            var snapshot = await store.GetSnapshotAsync(CancellationToken.None);

            Assert.Equal(group, Assert.Single(snapshot.Groups));
            Assert.Equal(device, Assert.Single(snapshot.Devices));
            Assert.Equal(tag, Assert.Single(snapshot.Tags));
            Assert.Equal(2, publication.Version);

            await store.DeleteDeviceAsync(device.Id, CancellationToken.None);
            await store.PublishDraftAsync("删除设备", CancellationToken.None);
            Assert.Empty((await store.GetSnapshotAsync(CancellationToken.None)).Devices);

            var rollback = await store.RollbackAsync(publication.Version, 3, "恢复设备", CancellationToken.None);
            Assert.Equal(4, rollback.Version);
            Assert.Equal(device, Assert.Single((await store.GetSnapshotAsync(CancellationToken.None)).Devices));

            var revisions = await store.GetRevisionsAsync(10, CancellationToken.None);
            Assert.Equal(4, revisions[0].Version);
            Assert.True(revisions[0].IsActive);
            Assert.All(revisions.Skip(1), revision => Assert.False(revision.IsActive));

            var restoredRevision = await store.GetRevisionSnapshotAsync(publication.Version, CancellationToken.None);
            Assert.Equal(device, Assert.Single(restoredRevision.Devices));

            var conflict = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.RollbackAsync(publication.Version, 3, "并发冲突测试", CancellationToken.None));
            Assert.Contains("已从 V3 变更为 V4", conflict.Message);
            Assert.Equal(4, (await store.GetRevisionsAsync(1, CancellationToken.None))[0].Version);

            var activeDelete = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await store.DeleteRevisionAsync(4, 4, "错误删除当前版本", CancellationToken.None));
            Assert.Contains("当前生效版本不能删除", activeDelete.Message);

            await store.DeleteRevisionAsync(3, 4, "清理历史测试版本", CancellationToken.None);
            Assert.DoesNotContain(await store.GetRevisionsAsync(10, CancellationToken.None), item => item.Version == 3);
            await Assert.ThrowsAsync<KeyNotFoundException>(async () =>
                await store.GetRevisionSnapshotAsync(3, CancellationToken.None));
            Assert.Equal(4, (await store.GetRevisionsAsync(1, CancellationToken.None))[0].Version);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }
}
