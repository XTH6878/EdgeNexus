using Snet.Edge.Domain;

namespace Snet.Edge.WebApi;

public sealed record SaveNetworkBindingsRequest(
    string? PlcInterfaceId,
    string? MqttInterfaceId,
    string? WebInterfaceId);

public sealed record NetworkPingRequest(
    string Host,
    NetworkUsage Usage = NetworkUsage.PlcAcquisition,
    int TimeoutMs = 3_000);

public sealed record NetworkTcpTestRequest(
    string Host,
    int Port,
    NetworkUsage Usage = NetworkUsage.PlcAcquisition,
    int TimeoutMs = 3_000);

public sealed record NetworkMqttTestRequest(int TimeoutMs = 5_000);

public sealed record NetworkPlcTestRequest(Guid DeviceId, int TimeoutMs = 5_000);
