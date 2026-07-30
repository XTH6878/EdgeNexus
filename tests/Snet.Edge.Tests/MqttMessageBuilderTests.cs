using System.Text.Json;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure.GatewayMqtt;

namespace Snet.Edge.Tests;

public sealed class MqttMessageBuilderTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 29, 2, 53, 8, TimeSpan.Zero);

    [Fact]
    public void StandardTemplateBuildsTypedDevicePayload()
    {
        var template = CreateTemplate() with
        {
            IncludeDeviceName = true,
            IncludeDeviceIp = true,
            IncludeProtocol = true,
            IncludeGatewayId = true,
            IncludeUnixTime = true,
            CustomFields = [new MqttCustomField { Name = "factoryId", Value = "F001-${deviceId}" }]
        };
        var builder = new MqttPayloadBuilder(new MqttVariableResolver());

        using var document = JsonDocument.Parse(builder.Build(CreateData(), template, CreateContext()));
        var root = document.RootElement;
        Assert.Equal("PLC001", root.GetProperty("deviceId").GetString());
        Assert.Equal("F001-PLC001", root.GetProperty("factoryId").GetString());
        Assert.Equal(Timestamp.ToUnixTimeSeconds(), root.GetProperty("unixTime").GetInt64());
        var point = Assert.Single(root.GetProperty("d").EnumerateArray());
        Assert.Equal("PLC001:Temperature", point.GetProperty("tag").GetString());
        Assert.Equal(35.6M, point.GetProperty("value").GetDecimal());
        Assert.Equal(0, point.GetProperty("quality").GetInt32());
    }

    [Fact]
    public void AdvantechTemplateBuildsCompatibleTimestampAndDataArray()
    {
        var template = CreateTemplate() with
        {
            PayloadStyle = MqttPayloadStyle.Advantech,
            IncludeDeviceId = false
        };
        var builder = new MqttPayloadBuilder(new MqttVariableResolver());

        using var document = JsonDocument.Parse(builder.Build(CreateData(), template, CreateContext()));
        var root = document.RootElement;
        Assert.False(root.TryGetProperty("deviceId", out _));
        Assert.Equal("2026-07-29T02:53:08+0000", root.GetProperty("ts").GetString());
        Assert.Equal("PLC001:Temperature", root.GetProperty("d")[0].GetProperty("tag").GetString());
    }

    [Fact]
    public void CustomFieldsResolveFixedDeviceAndFirstPlcTagVariables()
    {
        var template = CreateTemplate() with
        {
            PayloadStyle = MqttPayloadStyle.Advantech,
            CustomFields =
            [
                new MqttCustomField { Name = "factoryId", Value = "F001" },
                new MqttCustomField { Name = "asset", Value = "${deviceId}-${line}" },
                new MqttCustomField { Name = "sourceTag", Value = "${tag}" },
                new MqttCustomField { Name = "sourceTagName", Value = "${tagName}" },
                new MqttCustomField { Name = "sourceValue", Value = "${value}" },
                new MqttCustomField { Name = "sourceQuality", Value = "${quality}" }
            ]
        };
        var data = CreateData() with
        {
            Tags =
            [
                new TagData("#DEVICE_ERROR_PLC001", 0, DataQuality.Good),
                new TagData("Temperature", 35.6M, DataQuality.Good)
            ]
        };

        using var document = JsonDocument.Parse(
            new MqttPayloadBuilder(new MqttVariableResolver()).Build(data, template, CreateContext()));
        var root = document.RootElement;

        Assert.Equal("F001", root.GetProperty("factoryId").GetString());
        Assert.Equal("PLC001-LINE01", root.GetProperty("asset").GetString());
        Assert.Equal("PLC001:Temperature", root.GetProperty("sourceTag").GetString());
        Assert.Equal("Temperature", root.GetProperty("sourceTagName").GetString());
        Assert.Equal("35.6", root.GetProperty("sourceValue").GetString());
        Assert.Equal("0", root.GetProperty("sourceQuality").GetString());
    }

    [Fact]
    public void DeviceStatusTagStaysInPayloadDataArray()
    {
        var data = CreateData() with
        {
            Tags =
            [
                new TagData("#DEVICE_ERROR_PLC001", -9001, DataQuality.Good),
                new TagData("Temperature", 35.6M, DataQuality.Good)
            ]
        };

        using var document = JsonDocument.Parse(
            new MqttPayloadBuilder(new MqttVariableResolver()).Build(
                data,
                CreateTemplate(),
                CreateContext()));
        var points = document.RootElement.GetProperty("d");

        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal("#DEVICE_ERROR_PLC001", points[0].GetProperty("tag").GetString());
        Assert.Equal(-9001, points[0].GetProperty("value").GetInt32());
        Assert.Equal(0, points[0].GetProperty("quality").GetInt32());
        Assert.Equal("PLC001:Temperature", points[1].GetProperty("tag").GetString());
    }

    [Fact]
    public void TopicBuilderResolvesIndustrialVariablesAndRejectsWildcards()
    {
        var builder = new MqttTopicBuilder(new MqttVariableResolver());
        var topic = builder.Build("factory/${line}/${deviceId}/${gatewayId}/data", CreateData(), CreateContext());

        Assert.Equal("factory/LINE01/PLC001/GW01/data", topic);
        Assert.Throws<InvalidOperationException>(() =>
            builder.Build("factory/+/data", CreateData(), CreateContext()));
    }

    [Fact]
    public async Task TemplateManagerPersistsChangesAndResolvesThemWithoutRestart()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"mqtt-template-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        try
        {
            await using var manager = new MqttTemplateManager(connectionString);
            await manager.InitializeAsync(CancellationToken.None);
            var builtIn = Assert.Single(
                await manager.GetAllAsync(CancellationToken.None),
                item => item.IsBuiltIn);
            Assert.Equal(MqttTemplateManager.StandardTemplateId, builtIn.Id);
            Assert.Equal("EdgeNexus默认模板", builtIn.Name);
            Assert.Equal(MqttPayloadStyle.Advantech, builtIn.PayloadStyle);
            Assert.Equal("#DEVICE_ERROR", builtIn.DeviceErrorTagPrefix);
            Assert.Equal(1001, builtIn.CommunicationFailureCode);
            Assert.Equal(
                MqttTemplateManager.StandardTemplateId,
                (await manager.ResolveAsync(MqttTemplateManager.AdvantechTemplateId, CancellationToken.None)).Id);
            var custom = CreateTemplate() with
            {
                Id = Guid.NewGuid(),
                Name = "产线A模板",
                DeviceErrorTagPrefix = "#LINE_LINK_",
                CommunicationFailureCode = -9001,
                CustomFields = [new MqttCustomField { Name = "factoryId", Value = "F001-${deviceId}" }]
            };

            var saved = await manager.UpsertAsync(custom, CancellationToken.None);
            var resolved = await manager.ResolveAsync(saved.Id, CancellationToken.None);
            Assert.Equal("产线A模板", resolved.Name);
            Assert.Equal("#LINE_LINK", resolved.DeviceErrorTagPrefix);
            Assert.Equal(-9001, resolved.CommunicationFailureCode);
            Assert.Equal("F001-${deviceId}", Assert.Single(resolved.CustomFields).Value);

            var updated = await manager.UpsertAsync(saved with { IncludeGatewayId = true }, CancellationToken.None);
            Assert.True((await manager.ResolveAsync(saved.Id, CancellationToken.None)).IncludeGatewayId);
            Assert.True(updated.UpdateTime >= saved.UpdateTime);
            Assert.Contains(await manager.GetAllAsync(CancellationToken.None), item => item.Id == saved.Id);
            await using var reloadedManager = new MqttTemplateManager(connectionString);
            await reloadedManager.InitializeAsync(CancellationToken.None);
            var reloaded = await reloadedManager.GetAsync(saved.Id, CancellationToken.None);
            Assert.NotNull(reloaded);
            Assert.Equal("F001-${deviceId}", Assert.Single(reloaded.CustomFields).Value);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await manager.UpsertAsync(custom with { Id = Guid.NewGuid(), Name = "无效状态码", CommunicationFailureCode = 0 }, CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    private static MqttTemplate CreateTemplate() => new()
    {
        Id = Guid.NewGuid(),
        Name = "测试模板",
        IncludeDeviceId = true,
        IncludeTimestamp = true,
        IncludeTag = true,
        IncludeValue = true,
        IncludeQuality = true
    };

    private static DeviceData CreateData() => new(
        "PLC001",
        "一号PLC",
        "192.168.1.10",
        ProtocolKind.ModbusTcp,
        Timestamp,
        [new TagData("Temperature", 35.6M, DataQuality.Good)]);

    private static MqttPayloadContext CreateContext() => new("GW01", "1.0.0", "一号线", "LINE01");
}
