using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class HslDeviceDriverByteOrderTests
{
    [Theory]
    [InlineData("ABCD", "ABCD")]
    [InlineData("BADC", "BADC")]
    [InlineData("CDAB", "CDAB")]
    [InlineData("DCBA", "DCBA")]
    public async Task MitsubishiUsesPlainAddressAndConfiguredByteOrder(string byteOrder, string expectedFormat)
    {
        var client = new RecordingMelsecClient();
        await using var driver = new HslDeviceDriver(
            ProtocolKind.MitsubishiMc,
            new StubHslClientSession(client),
            new DeviceReadPlan([], 0));
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Counter",
            Address = "D100",
            DataType = TagDataType.UInt64,
            ByteOrder = byteOrder
        };

        var value = await driver.ReadAsync(tag, CancellationToken.None);

        Assert.Equal(42UL, value);
        Assert.Equal("D100", client.LastAddress);
        Assert.Equal(Enum.Parse<DataFormat>(expectedFormat), client.ReadDataFormat);
    }

    [Fact]
    public void InvalidByteOrderFallsBackToAbcd()
    {
        Assert.Equal(DataFormat.ABCD, HslDeviceDriver.ResolveDataFormat("invalid"));
        Assert.Equal(DataFormat.ABCD, HslDeviceDriver.ResolveDataFormat(null));
    }

    [Fact]
    public async Task OmronFinsReadsNumericValuesAsWordsAndValidatesPayloadLength()
    {
        var client = new RecordingOmronClient([0x41, 0x97, 0x0A, 0x3D]);
        await using var driver = new HslDeviceDriver(
            ProtocolKind.OmronFins,
            new StubHslClientSession(client),
            new DeviceReadPlan([], 0));
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Temperature",
            Address = "D263",
            DataType = TagDataType.Float32,
            ByteOrder = "ABCD"
        };

        var value = await driver.ReadAsync(tag, CancellationToken.None);

        Assert.Equal(18.88F, Assert.IsType<float>(value), 2);
        Assert.Equal("D263", client.LastAddress);
        Assert.Equal((ushort)2, client.LastWordLength);
    }

    [Fact]
    public async Task OmronFinsRejectsSuccessfulResponseWithEmptyPayload()
    {
        var client = new RecordingOmronClient([]);
        await using var driver = new HslDeviceDriver(
            ProtocolKind.OmronFins,
            new StubHslClientSession(client),
            new DeviceReadPlan([], 0));
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Temperature",
            Address = "D263",
            DataType = TagDataType.Float32
        };

        var exception = await Assert.ThrowsAsync<IOException>(
            async () => await driver.ReadAsync(tag, CancellationToken.None));

        Assert.Contains("返回长度不足", exception.Message);
        Assert.Contains("实际 0 字节", exception.Message);
    }

    [Theory]
    [InlineData(ProtocolKind.ModbusTcp, "100", "x=3;100", ModbusFunctionCode.ReadHoldingRegisters)]
    [InlineData(ProtocolKind.MitsubishiMc, "D100", "D100", null)]
    [InlineData(ProtocolKind.PanasonicMewtocol, "DT100", "DT100", null)]
    [InlineData(ProtocolKind.OmronFins, "D100", "D100", null)]
    [InlineData(ProtocolKind.OmronCip, "Counter", "Counter", null)]
    [InlineData(ProtocolKind.AllenBradleyEtherNetIp, "Counter", "Counter", null)]
    [InlineData(ProtocolKind.BeckhoffAds, "s=MAIN.Counter", "s=MAIN.Counter", null)]
    [InlineData(ProtocolKind.KeyenceMc, "D100", "D100", null)]
    [InlineData(ProtocolKind.InovanceTcp, "D100", "D100", null)]
    [InlineData(ProtocolKind.DeltaTcp, "D100", "D100", null)]
    public async Task SharedHslProtocolsDoNotPrefixByteOrderIntoAddress(
        ProtocolKind protocol,
        string address,
        string expectedAddress,
        ModbusFunctionCode? functionCode)
    {
        var client = new RecordingMelsecClient();
        await using var driver = new HslDeviceDriver(
            protocol,
            new StubHslClientSession(client),
            new DeviceReadPlan([], 0));
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = Guid.NewGuid(),
            Name = "Counter",
            Address = address,
            DataType = TagDataType.UInt64,
            ByteOrder = "CDAB",
            ModbusFunctionCode = functionCode
        };

        await driver.ReadAsync(tag, CancellationToken.None);

        Assert.Equal(expectedAddress, client.LastAddress);
        Assert.DoesNotContain("format=", client.LastAddress, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DataFormat.CDAB, client.ReadDataFormat);
    }

    private sealed class RecordingMelsecClient : MelsecMcNet
    {
        public string? LastAddress { get; private set; }
        public DataFormat? ReadDataFormat { get; private set; }

        public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
        {
            LastAddress = address;
            ReadDataFormat = ByteTransform.DataFormat;
            return Task.FromResult(OperateResult.CreateSuccessResult(ByteTransform.TransByte(42UL)));
        }
    }

    private sealed class RecordingOmronClient(byte[] content) : OmronFinsNet
    {
        public string? LastAddress { get; private set; }
        public ushort? LastWordLength { get; private set; }

        public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
        {
            LastAddress = address;
            LastWordLength = length;
            return Task.FromResult(OperateResult.CreateSuccessResult(content));
        }
    }

    private sealed class StubHslClientSession(IReadWriteNet client) : IHslClientSession
    {
        public IReadWriteNet Client => client;
        public bool IsDatagram => false;

        public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
