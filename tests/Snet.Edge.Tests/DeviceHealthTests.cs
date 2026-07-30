using System.Text.Json;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure.GatewayDeviceHealth;
using Snet.Edge.Infrastructure.GatewayMqtt;

namespace Snet.Edge.Tests;

public sealed class DeviceHealthTests
{
    [Theory]
    [InlineData(DeviceConnectionState.Online, DeviceFaultKind.None, 0)]
    [InlineData(DeviceConnectionState.Reconnecting, DeviceFaultKind.Communication, -77)]
    [InlineData(DeviceConnectionState.Reconnecting, DeviceFaultKind.Timeout, -77)]
    [InlineData(DeviceConnectionState.Degraded, DeviceFaultKind.Read, -77)]
    [InlineData(DeviceConnectionState.Degraded, DeviceFaultKind.Parse, -77)]
    [InlineData(DeviceConnectionState.Offline, DeviceFaultKind.Offline, -77)]
    public void ErrorManagerMapsRuntimeFaults(
        DeviceConnectionState state,
        DeviceFaultKind faultKind,
        int expectedCode)
    {
        var status = new DeviceRuntimeStatus(Guid.NewGuid(), state, DateTimeOffset.UtcNow, null, null)
        {
            FaultKind = faultKind
        };

        Assert.Equal(expectedCode, new DeviceErrorManager().Resolve(status, -77));
    }

    [Fact]
    public void ErrorManagerUsesCustomCodesWithoutChangingNormalCode()
    {
        var manager = new DeviceErrorManager();
        var communicationFailure = new DeviceRuntimeStatus(
            Guid.NewGuid(),
            DeviceConnectionState.Reconnecting,
            DateTimeOffset.UtcNow,
            null,
            "连接失败")
        {
            FaultKind = DeviceFaultKind.Communication
        };
        var online = communicationFailure with
        {
            State = DeviceConnectionState.Online,
            FaultKind = DeviceFaultKind.None,
            Error = null
        };

        Assert.Equal(-9001, manager.Resolve(communicationFailure, -9001));
        Assert.Equal(DeviceErrorCodes.Normal, manager.Resolve(online, -9001));
    }

    [Fact]
    public void DegradedDeviceWithAtLeastOneSuccessfulPointKeepsCommunicationHealthy()
    {
        var status = new DeviceRuntimeStatus(
            Guid.NewGuid(),
            DeviceConnectionState.Degraded,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "2 of 10 points failed")
        {
            FaultKind = DeviceFaultKind.Read
        };

        Assert.Equal(DeviceErrorCodes.Normal, new DeviceErrorManager().Resolve(status, -9001));
    }

    [Fact]
    public void SystemErrorTagUsesAbsoluteAdvantechCompatibleName()
    {
        var systemTag = new SystemTagBuilder().BuildDeviceErrorTag("4004_1_1_zz_1", "#PLC_LINK", -9001);
        var data = new DeviceData(
            "4004_1_1_zz_1",
            "设备",
            "192.168.1.10",
            ProtocolKind.ModbusTcp,
            DateTimeOffset.UtcNow,
            [systemTag, new TagData("leftLowSpeed", 26, DataQuality.Good)]);
        var template = new MqttTemplate
        {
            Id = Guid.NewGuid(),
            Name = "研华格式",
            PayloadStyle = MqttPayloadStyle.Advantech,
            IncludeTimestamp = true,
            IncludeTag = true,
            IncludeValue = true,
            IncludeQuality = true
        };

        var payload = new MqttPayloadBuilder(new MqttVariableResolver()).Build(
            data,
            template,
            new MqttPayloadContext("GW01", "1.0.0", "一号线", null));
        using var document = JsonDocument.Parse(payload);
        var tags = document.RootElement.GetProperty("d").EnumerateArray().ToArray();

        Assert.Equal("#PLC_LINK_4004_1_1_zz_1", tags[0].GetProperty("tag").GetString());
        Assert.Equal(-9001, tags[0].GetProperty("value").GetInt32());
        Assert.Equal(0, tags[0].GetProperty("quality").GetInt32());
        Assert.Equal("4004_1_1_zz_1:leftLowSpeed", tags[1].GetProperty("tag").GetString());
    }
}
