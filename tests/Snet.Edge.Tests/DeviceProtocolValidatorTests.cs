using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Tests;

public sealed class DeviceProtocolValidatorTests
{
    [Theory]
    [InlineData(MitsubishiMcFormat.Binary1E)]
    [InlineData(MitsubishiMcFormat.Ascii1E)]
    public void MitsubishiACompatible1EAcceptsTcpAndIgnoresThreeEOnlyRoutingFields(MitsubishiMcFormat format)
    {
        var device = CreateMitsubishi(format, MitsubishiMcTransport.Tcp) with
        {
            NetworkNumber = 999,
            NetworkStationNumber = 999,
            TargetIoStation = 999_999
        };

        var errors = DeviceProtocolValidator.Validate(device);

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(MitsubishiMcFormat.Binary1E)]
    [InlineData(MitsubishiMcFormat.Ascii1E)]
    public void MitsubishiACompatible1ERejectsUdpDuringConfigurationValidation(MitsubishiMcFormat format)
    {
        var device = CreateMitsubishi(format, MitsubishiMcTransport.Udp);

        var errors = DeviceProtocolValidator.Validate(device);

        Assert.Contains(errors, error => error.Contains("1E", StringComparison.Ordinal) && error.Contains("TCP", StringComparison.OrdinalIgnoreCase));
    }

    private static DeviceDefinition CreateMitsubishi(
        MitsubishiMcFormat format,
        MitsubishiMcTransport transport) => new()
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "Mitsubishi-A1E",
            Protocol = ProtocolKind.MitsubishiMc,
            Host = "127.0.0.1",
            Port = 5000,
            MitsubishiFormat = format,
            MitsubishiTransport = transport,
            PlcNumber = 255
        };
}
