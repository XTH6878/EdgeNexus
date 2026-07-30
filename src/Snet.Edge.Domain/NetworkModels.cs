namespace Snet.Edge.Domain;

public enum NetworkUsage
{
    PlcAcquisition,
    MqttUpload,
    WebManagement
}

public sealed record NetworkInterfaceAddress(string Address, string SubnetMask);

public sealed record NetworkInterfaceInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string InterfaceType { get; init; }
    public required IReadOnlyList<NetworkInterfaceAddress> Ipv4Addresses { get; init; }
    public required IReadOnlyList<string> Gateways { get; init; }
    public required IReadOnlyList<string> DnsServers { get; init; }
    public required string MacAddress { get; init; }
    public required bool IsOnline { get; init; }
    public required bool? DhcpEnabled { get; init; }
    public required bool IsBindable { get; init; }
    public required long SpeedMbps { get; init; }
}

public sealed record NetworkBindingConfiguration
{
    public string? PlcInterfaceId { get; init; }
    public string? MqttInterfaceId { get; init; }
    public string? WebInterfaceId { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record EffectiveNetworkBinding(
    NetworkUsage Usage,
    string Mode,
    string? InterfaceId,
    string InterfaceName,
    string? Ipv4,
    bool Available);

public sealed record NetworkConfigurationView(
    NetworkBindingConfiguration Bindings,
    IReadOnlyList<NetworkInterfaceInfo> Interfaces,
    IReadOnlyList<EffectiveNetworkBinding> EffectiveBindings);

public sealed record NetworkDiagnosticResult(
    bool Success,
    string Status,
    long DurationMs,
    string? SourceAddress,
    string? Target,
    string? Error = null,
    IReadOnlyList<NetworkDiagnosticStep>? Steps = null,
    string? Recommendation = null);

public sealed record NetworkDiagnosticStep(
    string Name,
    bool Success,
    string Detail,
    long DurationMs);
