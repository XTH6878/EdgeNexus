using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class ProtocolPointAddressCatalogTests
{
    [Theory]
    [InlineData("DB1.DBX0.0")]
    [InlineData("DB1.DBW2")]
    [InlineData("DB1.DBD4")]
    [InlineData("M10.0")]
    [InlineData("VD100")]
    public void SiemensExamplesPassTheHslAddressParser(string address)
    {
        var result = S7AddressData.ParseFrom(SiemensTagAddress.NormalizeForRawRead(address), 1);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData("D100", false)]
    [InlineData("D200", false)]
    [InlineData("M100", true)]
    [InlineData("X0", true)]
    [InlineData("Y0", true)]
    public void MitsubishiExamplesPassTheHslAddressParser(string address, bool isBit)
    {
        using var client = new MelsecMcNet();
        var result = client.McAnalysisAddress(address, 1, isBit);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData("D100", false)]
    [InlineData("M100", true)]
    [InlineData("X017", true)]
    [InlineData("Y10", true)]
    public void MitsubishiACompatible1EExamplesPassTheHslCommandBuilder(string address, bool isBit)
    {
        var binary = MelsecA1ENet.BuildReadCommand(address, 1, isBit, 0xFF);
        var ascii = MelsecA1EAsciiNet.BuildReadCommand(address, 1, isBit, 0xFF);

        Assert.True(binary.IsSuccess, binary.Message);
        Assert.True(ascii.IsSuccess, ascii.Message);
    }

    [Theory]
    [InlineData("DT100")]
    [InlineData("DT200")]
    [InlineData("R0")]
    [InlineData("X0")]
    [InlineData("Y0")]
    public void PanasonicExamplesPassTheHslAddressParser(string address)
    {
        var result = PanasonicHelper.AnalysisAddress(address);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData("D100")]
    [InlineData("D100.03")]
    [InlineData("CIO100")]
    [InlineData("W100")]
    [InlineData("H100.00")]
    public void OmronFinsExamplesPassTheHslAddressParser(string address)
    {
        var result = OmronFinsAddress.ParseFrom(address);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData("DM100", false)]
    [InlineData("DM200", false)]
    [InlineData("R015", true)]
    [InlineData("MR015", true)]
    [InlineData("LR015", true)]
    public void KeyenceExamplesPassTheHslAddressParser(string address, bool isBit)
    {
        using var client = new KeyenceMcNet();
        var result = client.McAnalysisAddress(address, 1, isBit);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData(InovancePlcSeries.AM, "SD100", 3)]
    [InlineData(InovancePlcSeries.AM, "Q0", 1)]
    [InlineData(InovancePlcSeries.AM, "M100", 1)]
    [InlineData(InovancePlcSeries.H3U, "D100", 3)]
    [InlineData(InovancePlcSeries.H5U, "X0", 1)]
    public void InovanceExamplesPassTheHslAddressTranslator(InovancePlcSeries series, string address, byte functionCode)
    {
        using var client = new InovanceTcpNet
        {
            Series = series switch
            {
                InovancePlcSeries.AM => InovanceSeries.AM,
                InovancePlcSeries.H3U => InovanceSeries.H3U,
                InovancePlcSeries.H5U => InovanceSeries.H5U,
                _ => InovanceSeries.Easy
            }
        };
        var result = client.TranslateToModbusAddress(address, functionCode);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Theory]
    [InlineData("D100", 3)]
    [InlineData("D200", 3)]
    [InlineData("M100", 1)]
    [InlineData("X0", 1)]
    [InlineData("Y0", 1)]
    public void DeltaExamplesPassTheHslAddressTranslator(string address, byte functionCode)
    {
        using var client = new DeltaTcpNet();
        var result = client.TranslateToModbusAddress(address, functionCode);

        Assert.True(result.IsSuccess, result.Message);
    }

    [Fact]
    public void EveryDeclaredProtocolHasACompletePointAddressGuide()
    {
        var guides = ProtocolPointAddressCatalog.GetAll();
        var protocols = Enum.GetValues<ProtocolKind>();

        Assert.Equal(protocols.Length, guides.Count);
        foreach (var protocol in protocols)
        {
            var guide = ProtocolPointAddressCatalog.Get(protocol);
            Assert.False(string.IsNullOrWhiteSpace(guide.Summary));
            Assert.False(string.IsNullOrWhiteSpace(guide.AddressPlaceholder));
            Assert.InRange(guide.Examples.Count, 3, 5);
            Assert.NotEmpty(guide.Notes);
            Assert.All(guide.Examples, example =>
            {
                Assert.False(string.IsNullOrWhiteSpace(example.Address));
                Assert.False(string.IsNullOrWhiteSpace(example.Area));
                Assert.False(string.IsNullOrWhiteSpace(example.Purpose));
            });
        }
    }

    [Fact]
    public void EveryGuideExampleCanBuildAndValidateAProtocolTag()
    {
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var device = CreateDevice(protocol);
            foreach (var example in ProtocolPointAddressCatalog.Get(protocol).Examples)
            {
                var tag = new TagDefinition
                {
                    Id = Guid.NewGuid(),
                    DeviceId = device.Id,
                    Name = example.Purpose,
                    Address = example.Address,
                    DataType = example.DataType,
                    ModbusFunctionCode = example.ModbusFunctionCode
                };

                Assert.Empty(TagProtocolValidator.Validate(device, tag));
            }
        }
    }

    [Fact]
    public async Task EveryProtocolDriverAcceptsItsGuideExamplesDuringPlanCompilation()
    {
        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var device = CreateDevice(protocol);
            var tags = ProtocolPointAddressCatalog.Get(protocol).Examples
                .Select(example => new TagDefinition
                {
                    Id = Guid.NewGuid(),
                    DeviceId = device.Id,
                    Name = example.Purpose,
                    Address = example.Address,
                    DataType = example.DataType,
                    ModbusFunctionCode = example.ModbusFunctionCode
                })
                .ToArray();

            await using var driver = factory.Create(device, tags);
            Assert.Equal(protocol, driver.Protocol);
        }
    }

    [Fact]
    public void EveryProtocolReadPlanIsBoundedAndKeepsEveryGuidePoint()
    {
        var compiler = new TagReadPlanCompiler();
        foreach (var protocol in Enum.GetValues<ProtocolKind>())
        {
            var device = CreateDevice(protocol);
            var tags = Enumerable.Range(0, 80)
                .Select(index =>
                {
                    var example = ProtocolPointAddressCatalog.Get(protocol).Examples[index % ProtocolPointAddressCatalog.Get(protocol).Examples.Count];
                    return new TagDefinition
                    {
                        Id = Guid.NewGuid(),
                        DeviceId = device.Id,
                        Name = $"Point{index}",
                        Address = example.Address,
                        DataType = example.DataType,
                        ModbusFunctionCode = example.ModbusFunctionCode
                    };
                })
                .ToArray();

            var plan = compiler.Compile(device, tags);

            Assert.Equal(tags.Length, plan.SourceTagCount);
            Assert.Equal(tags.Length, plan.Batches.Sum(batch => batch.Tags.Count));
            Assert.All(plan.Batches, batch =>
            {
                Assert.InRange(batch.Tags.Count, 1, 16);
                Assert.InRange(batch.Tags.Sum(tag => (int)TagReadPlanCompiler.GetByteLength(tag)), 1, 200);
            });
        }
    }

    private static DeviceDefinition CreateDevice(ProtocolKind protocol) => new()
    {
        Id = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Name = $"{protocol}-Test",
        Protocol = protocol,
        Host = "127.0.0.1",
        Port = protocol switch
        {
            ProtocolKind.SiemensS7 => 102,
            ProtocolKind.ModbusTcp or ProtocolKind.InovanceTcp or ProtocolKind.DeltaTcp => 502,
            ProtocolKind.MitsubishiMc or ProtocolKind.KeyenceMc => 5000,
            ProtocolKind.OpcUa => 4840,
            ProtocolKind.PanasonicMewtocol => 9094,
            ProtocolKind.OmronFins => 9600,
            ProtocolKind.OmronCip or ProtocolKind.AllenBradleyEtherNetIp => 44818,
            ProtocolKind.BeckhoffAds => 851,
            _ => throw new ArgumentOutOfRangeException(nameof(protocol), protocol, null)
        }
    };
}
