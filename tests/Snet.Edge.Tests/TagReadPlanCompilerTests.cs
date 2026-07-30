using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class TagReadPlanCompilerTests
{
    private readonly TagReadPlanCompiler _compiler = new();

    [Fact]
    public void SiemensTwentyNumericTagsAreSplitIntoBoundedNativeBatches()
    {
        var device = CreateDevice(ProtocolKind.SiemensS7);
        var tags = Enumerable.Range(0, 20)
            .Select(index => CreateTag(device.Id, $"Tag{index}", TagDataType.Int16))
            .ToArray();

        var plan = _compiler.Compile(device, tags);

        Assert.Equal(2, plan.Batches.Count);
        Assert.Equal(16, plan.Batches[0].Tags.Count);
        Assert.Equal(4, plan.Batches[1].Tags.Count);
        Assert.All(plan.Batches, batch => Assert.True(batch.UseNativeMultiRead));
    }

    [Fact]
    public void BooleanTagsAlwaysUseSafeSingleReads()
    {
        var device = CreateDevice(ProtocolKind.SiemensS7);
        var tags = new[]
        {
            CreateTag(device.Id, "Running", TagDataType.Bool),
            CreateTag(device.Id, "Alarm", TagDataType.Bool)
        };

        var plan = _compiler.Compile(device, tags);

        Assert.Equal(2, plan.Batches.Count);
        Assert.All(plan.Batches, batch =>
        {
            Assert.Single(batch.Tags);
            Assert.False(batch.UseNativeMultiRead);
        });
    }

    [Fact]
    public void ModbusUsesProtocolSafeSingleReadFallback()
    {
        var device = CreateDevice(ProtocolKind.ModbusTcp);
        var tags = Enumerable.Range(0, 3)
            .Select(index => CreateTag(device.Id, $"Register{index}", TagDataType.UInt16))
            .ToArray();

        var plan = _compiler.Compile(device, tags);

        Assert.Equal(3, plan.Batches.Count);
        Assert.All(plan.Batches, batch => Assert.False(batch.UseNativeMultiRead));
    }

    private static DeviceDefinition CreateDevice(ProtocolKind protocol) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Name = "PLC-01",
        Protocol = protocol,
        Host = "127.0.0.1",
        Port = 102
    };

    private static TagDefinition CreateTag(Guid deviceId, string name, TagDataType dataType) => new()
    {
        Id = Guid.NewGuid(),
        DeviceId = deviceId,
        Name = name,
        Address = $"DB1.{name}",
        DataType = dataType
    };
}
