using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Tests;

public sealed class ModbusTagAddressTests
{
    [Fact]
    public void StronglyTypedFunctionCodeIsConvertedAtDriverBoundary()
    {
        var tag = CreateTag("100", ModbusFunctionCode.ReadInputRegisters);

        var address = ModbusTagAddress.ToHslAddress(tag);

        Assert.Equal("x=4;100", address);
    }

    [Fact]
    public void ExistingLegacyPrefixIsNotDuplicated()
    {
        var tag = CreateTag("x=3;100", ModbusFunctionCode.ReadHoldingRegisters);

        var address = ModbusTagAddress.ToHslAddress(tag);

        Assert.Equal("x=3;100", address);
    }

    [Fact]
    public void LegacyAddressIsNormalizedForPersistence()
    {
        var tag = CreateTag("x=4;100", ModbusFunctionCode.ReadInputRegisters) with
        {
            ModbusFunctionCode = null
        };

        var normalized = ModbusTagAddress.Normalize(tag);

        Assert.Equal("100", normalized.Address);
        Assert.Equal(ModbusFunctionCode.ReadInputRegisters, normalized.ModbusFunctionCode);
    }

    private static TagDefinition CreateTag(string address, ModbusFunctionCode functionCode) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = Guid.NewGuid(),
        Name = "点位",
        Address = address,
        DataType = TagDataType.UInt16,
        ModbusFunctionCode = functionCode
    };
}
