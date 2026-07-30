using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using HslCommunication.Profinet.Melsec;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class DriverFactoryTests
{
    public static TheoryData<ProtocolKind, int> Protocols => new()
    {
        { ProtocolKind.SiemensS7, 102 },
        { ProtocolKind.ModbusTcp, 502 },
        { ProtocolKind.MitsubishiMc, 5000 },
        { ProtocolKind.OpcUa, 4840 },
        { ProtocolKind.PanasonicMewtocol, 9094 },
        { ProtocolKind.OmronFins, 9600 },
        { ProtocolKind.OmronCip, 44818 },
        { ProtocolKind.AllenBradleyEtherNetIp, 44818 },
        { ProtocolKind.BeckhoffAds, 851 },
        { ProtocolKind.KeyenceMc, 5000 },
        { ProtocolKind.InovanceTcp, 502 },
        { ProtocolKind.DeltaTcp, 502 }
    };

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task FactoryCreatesCrossPlatformHslDrivers(ProtocolKind protocol, int port)
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        var definition = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "测试设备",
            Protocol = protocol,
            Host = "127.0.0.1",
            Port = port
        };

        await using var driver = factory.Create(definition);

        Assert.Equal(protocol, driver.Protocol);
    }

    [Fact]
    public void DriverMatrixCoversEveryDeclaredProtocol()
    {
        var covered = Protocols.Select(row => (ProtocolKind)row[0]).Order().ToArray();
        var declared = Enum.GetValues<ProtocolKind>().Order().ToArray();

        Assert.Equal(declared, covered);
    }

    [Theory]
    [MemberData(nameof(Protocols))]
    public async Task EveryProtocolConnectionPathReturnsWithinDeadlineWhenPeerRejectsHandshake(
        ProtocolKind protocol,
        int _)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var rejectPeer = AcceptAndRejectAsync(listener);
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        var definition = Create(protocol, port) with
        {
            ConnectTimeoutMs = 500,
            ReceiveTimeoutMs = 500
        };
        await using var driver = factory.Create(definition);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await driver.ConnectAsync(deadline.Token);
            await driver.DisconnectAsync(deadline.Token);
        }
        catch (Exception)
        {
        }
        finally
        {
            listener.Stop();
        }

        await rejectPeer.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(4), $"{protocol} failure path exceeded its deadline.");
    }

    private static async Task AcceptAndRejectAsync(TcpListener listener)
    {
        try
        {
            using var peer = await listener.AcceptTcpClientAsync();
            peer.Client.LingerState = new LingerOption(true, 0);
        }
        catch (SocketException)
        {
        }
    }

    [Fact]
    public void TelemetryKeyCombinesDeviceAndTagNames()
    {
        Assert.Equal("PLC001:模具温度", TelemetryKey.Create("PLC001", "模具温度"));
    }

    [Fact]
    public async Task FactoryAppliesAdvancedProtocolOptionsWithoutFallback()
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        DeviceDefinition[] definitions =
        [
            Create(ProtocolKind.SiemensS7, 102) with { SiemensSeries = SiemensPlcSeries.S300, Rack = 0, Slot = 2 },
            Create(ProtocolKind.MitsubishiMc, 5000) with { MitsubishiFormat = MitsubishiMcFormat.Ascii3E },
            Create(ProtocolKind.MitsubishiMc, 5000) with { MitsubishiFormat = MitsubishiMcFormat.Binary1E },
            Create(ProtocolKind.MitsubishiMc, 5000) with { MitsubishiFormat = MitsubishiMcFormat.Ascii1E },
            Create(ProtocolKind.MitsubishiMc, 5000) with { MitsubishiTransport = MitsubishiMcTransport.Udp, MitsubishiFormat = MitsubishiMcFormat.Binary3E },
            Create(ProtocolKind.MitsubishiMc, 5000) with { MitsubishiTransport = MitsubishiMcTransport.Udp, MitsubishiFormat = MitsubishiMcFormat.Ascii3E },
            Create(ProtocolKind.OmronFins, 9600) with { FinsDestinationNode = 10, FinsSourceNode = 20, FinsReadSplits = 999 },
            Create(ProtocolKind.OmronCip, 44818) with { OmronCipConnectionTimeoutMultiplier = 6 },
            Create(ProtocolKind.AllenBradleyEtherNetIp, 44818) with { Slot = 1, CipRoutePath = "1.1" },
            Create(ProtocolKind.BeckhoffAds, 851) with { BeckhoffUseAutoAmsNetId = true, BeckhoffUseTagCache = true },
            Create(ProtocolKind.InovanceTcp, 502) with { Station = 2, InovanceSeries = InovancePlcSeries.H5U },
            Create(ProtocolKind.DeltaTcp, 502) with { Station = 3, DeltaSeries = DeltaPlcSeries.AS }
        ];

        foreach (var definition in definitions)
        {
            await using var driver = factory.Create(definition);
            Assert.Equal(definition.Protocol, driver.Protocol);
        }
    }

    [Theory]
    [InlineData(MitsubishiMcTransport.Tcp, false)]
    [InlineData(MitsubishiMcTransport.Udp, true)]
    public async Task MitsubishiFactoryUsesSelectedTransport(MitsubishiMcTransport transport, bool expectedDatagram)
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        var definition = Create(ProtocolKind.MitsubishiMc, 5000) with
        {
            MitsubishiTransport = transport
        };

        await using var driver = Assert.IsType<HslDeviceDriver>(factory.Create(definition));

        Assert.Equal(expectedDatagram, driver.IsDatagramTransport);
    }

    [Theory]
    [InlineData(MitsubishiMcFormat.Binary1E, typeof(MelsecA1ENet))]
    [InlineData(MitsubishiMcFormat.Ascii1E, typeof(MelsecA1EAsciiNet))]
    public async Task MitsubishiFactoryUsesSelectedACompatible1EClient(
        MitsubishiMcFormat format,
        Type expectedClientType)
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        var definition = Create(ProtocolKind.MitsubishiMc, 5000) with
        {
            MitsubishiTransport = MitsubishiMcTransport.Tcp,
            MitsubishiFormat = format,
            PlcNumber = 255
        };

        await using var driver = Assert.IsType<HslDeviceDriver>(factory.Create(definition));

        Assert.False(driver.IsDatagramTransport);
        Assert.Equal(expectedClientType, driver.ClientType);
    }

    [Theory]
    [InlineData(MitsubishiMcFormat.Binary1E)]
    [InlineData(MitsubishiMcFormat.Ascii1E)]
    public void MitsubishiACompatible1ERejectsUdp(MitsubishiMcFormat format)
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        var definition = Create(ProtocolKind.MitsubishiMc, 5000) with
        {
            MitsubishiTransport = MitsubishiMcTransport.Udp,
            MitsubishiFormat = format
        };

        var exception = Assert.Throws<NotSupportedException>(() => factory.Create(definition));

        Assert.Contains("1E", exception.Message, StringComparison.Ordinal);
        Assert.Contains("TCP", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(SiemensPlcSeries.S1200, 0, 1)]
    [InlineData(SiemensPlcSeries.S1500, 0, 1)]
    [InlineData(SiemensPlcSeries.S300, 0, 2)]
    [InlineData(SiemensPlcSeries.S400, 0, 3)]
    [InlineData(SiemensPlcSeries.S200Smart, 0, 0)]
    [InlineData(SiemensPlcSeries.S200, 0, 0)]
    public void SiemensAutomaticProfileUsesSeriesRecommendedRackSlot(
        SiemensPlcSeries series,
        byte expectedRack,
        byte expectedSlot)
    {
        var device = Create(ProtocolKind.SiemensS7, 1102) with
        {
            SiemensSeries = series,
            SiemensAutoRackSlot = true,
            Rack = 7,
            Slot = 31,
            SiemensConnectionType = SiemensConnectionType.Op
        };

        var profile = SiemensConnectionProfile.Resolve(device);

        Assert.Equal(1102, profile.Port);
        Assert.Equal(expectedRack, profile.Rack);
        Assert.Equal(expectedSlot, profile.Slot);
        Assert.Equal((byte)SiemensConnectionType.Op, profile.ConnectionType);
    }

    [Fact]
    public void SiemensManualProfilePreservesConfiguredRackSlot()
    {
        var device = Create(ProtocolKind.SiemensS7, 102) with
        {
            SiemensAutoRackSlot = false,
            Rack = 1,
            Slot = 4,
            SiemensConnectionType = SiemensConnectionType.S7Basic
        };

        var profile = SiemensConnectionProfile.Resolve(device);

        Assert.Equal((byte)1, profile.Rack);
        Assert.Equal((byte)4, profile.Slot);
        Assert.Equal((byte)SiemensConnectionType.S7Basic, profile.ConnectionType);
    }

    [Theory]
    [InlineData("VD2634", "V2634")]
    [InlineData("VW100", "V100")]
    [InlineData("VB10", "V10")]
    [InlineData("DB1.DBD0", "DB1.0")]
    [InlineData("DB1.DBW10", "DB1.10")]
    [InlineData("DB1.DBX0.1", "DB1.0.1")]
    [InlineData("M100", "M100")]
    [InlineData("SM0.0", "SM0.0")]
    [InlineData("PIW0", "PIW0")]
    public void SiemensRawReadNormalizesEngineeringAddressNotation(string source, string expected)
    {
        Assert.Equal(expected, SiemensTagAddress.NormalizeForRawRead(source));
    }

    private static DeviceDefinition Create(ProtocolKind protocol, int port) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Name = "高级参数测试",
        Protocol = protocol,
        Host = "127.0.0.1",
        Port = port
    };
}
