using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Tests;

public sealed class ConfigurationValidatorTests
{
    [Theory]
    [InlineData(99, 1)]
    [InlineData(86400001, 1)]
    [InlineData(100, 0)]
    [InlineData(100, 86401)]
    public void ValidatorRejectsGroupScheduleOutsideIndustrialBounds(int scanCycleMs, int uploadCycleSeconds)
    {
        var group = new MqttGroup
        {
            Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line",
            ScanCycleMs = scanCycleMs, UploadCycleSeconds = uploadCycleSeconds
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [], [], new MqttOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("周期无效", StringComparison.Ordinal));
    }

    [Fact]
    public void MqttStatusTemplateAcceptsPositiveCommunicationFailureCode()
    {
        var template = CreateMqttTemplate() with { CommunicationFailureCode = 9001 };

        Assert.Null(MqttDeviceStatusTemplateValidator.Validate(template));
    }

    [Fact]
    public void MqttStatusTemplateAcceptsNegativeCommunicationFailureCode()
    {
        var template = CreateMqttTemplate() with
        {
            DeviceErrorTagPrefix = "#PLC_LINK",
            CommunicationFailureCode = -9001
        };

        Assert.Null(MqttDeviceStatusTemplateValidator.Validate(template));
    }

    [Fact]
    public void MqttStatusTemplateRejectsZeroCommunicationFailureCode()
    {
        var error = MqttDeviceStatusTemplateValidator.Validate(
            CreateMqttTemplate() with { CommunicationFailureCode = 0 });

        Assert.Contains("不能为0", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidatorRejectsDuplicateTopicsAndOrphanDevices()
    {
        var firstGroup = new MqttGroup
        {
            Id = Guid.NewGuid(),
            Name = "一号线",
            Topic = "edge/shared"
        };
        var secondGroup = firstGroup with
        {
            Id = Guid.NewGuid(),
            Name = "二号线"
        };
        var orphanDevice = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "孤立设备",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = 502
        };
        var snapshot = new GatewaySnapshot([firstGroup, secondGroup], [orphanDevice], [], new MqttOptions());

        var result = new GatewayConfigurationValidator().Validate(snapshot);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("MQTT Topic重复", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("不存在的分组", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsInvalidProtocolSpecificParameters()
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "FINS设备",
            Protocol = ProtocolKind.OmronFins,
            Host = "192.168.1.10",
            Port = 9600,
            FinsDestinationNetwork = 128,
            FinsReadSplits = 1_000
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [device], [], new MqttOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("目标网络号", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("批量分段长度", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAcceptsProtocolSpecificParameters()
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "S7设备",
            Protocol = ProtocolKind.SiemensS7,
            Host = "192.168.1.10",
            Port = 102,
            SiemensSeries = SiemensPlcSeries.S300,
            Rack = 0,
            Slot = 2
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [device], [], new MqttOptions()));

        Assert.True(result.IsValid);
    }

    [Fact]
    public void ValidatorRejectsMalformedCipRoute()
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = group.Id,
            Name = "AB设备",
            Protocol = ProtocolKind.AllenBradleyEtherNetIp,
            Host = "192.168.1.10",
            Port = 44_818,
            CipRoutePath = "1..2"
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [device], [], new MqttOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("CIP路由", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorRejectsModbusFunctionCodeDataTypeMismatch()
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(), GroupId = group.Id, Name = "Modbus设备", Protocol = ProtocolKind.ModbusTcp,
            Host = "192.168.1.10", Port = 502
        };
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(), DeviceId = device.Id, Name = "线圈", Address = "100",
            DataType = TagDataType.Int16, ModbusFunctionCode = ModbusFunctionCode.ReadCoils
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [device], [tag], new MqttOptions()));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("01/02", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidatorAcceptsLegacyModbusFunctionCodeAddress()
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "产线", Topic = "edge/line" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(), GroupId = group.Id, Name = "Modbus设备", Protocol = ProtocolKind.ModbusTcp,
            Host = "192.168.1.10", Port = 502
        };
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(), DeviceId = device.Id, Name = "离散输入", Address = "x=2;100",
            DataType = TagDataType.Bool
        };

        var result = new GatewayConfigurationValidator().Validate(new GatewaySnapshot([group], [device], [tag], new MqttOptions()));

        Assert.True(result.IsValid);
    }

    private static MqttTemplate CreateMqttTemplate() => new()
    {
        Id = Guid.NewGuid(),
        Name = "状态模板"
    };
}
