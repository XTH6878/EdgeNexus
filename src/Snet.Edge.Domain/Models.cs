namespace Snet.Edge.Domain;

public enum ProtocolKind
{
    SiemensS7,
    ModbusTcp,
    MitsubishiMc,
    OpcUa,
    PanasonicMewtocol,
    OmronFins,
    OmronCip,
    AllenBradleyEtherNetIp,
    BeckhoffAds,
    KeyenceMc,
    InovanceTcp,
    DeltaTcp
}

public enum SiemensPlcSeries
{
    S1200,
    S1500,
    S300,
    S400,
    S200Smart,
    S200
}

public enum SiemensConnectionType
{
    Pg = 1,
    Op = 2,
    S7Basic = 3
}

public enum MitsubishiMcFormat
{
    Binary3E,
    Ascii3E,
    Binary1E,
    Ascii1E
}

public enum MitsubishiMcTransport
{
    Tcp,
    Udp
}

public enum InovancePlcSeries
{
    AM,
    H3U,
    H5U,
    Easy
}

public enum DeltaPlcSeries
{
    Dvp,
    AS
}

public enum TagDataType
{
    Bool,
    Byte,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float32,
    Float64,
    String,
    Bcd
}

public enum ModbusFunctionCode
{
    ReadCoils = 1,
    ReadDiscreteInputs = 2,
    ReadHoldingRegisters = 3,
    ReadInputRegisters = 4
}

public enum DataQuality
{
    Good,
    Uncertain,
    Bad,
    Disconnected,
    Timeout,
    ConfigurationError
}

public enum DeviceConnectionState
{
    Stopped,
    Connecting,
    Online,
    Degraded,
    Reconnecting,
    Offline
}

public enum DeviceFaultKind
{
    None,
    Communication,
    Timeout,
    Read,
    Parse,
    Offline
}

public static class DeviceErrorCodes
{
    public const int Normal = 0;
    public const int CommunicationFailure = 1001;
    public const string DefaultTagPrefix = "#DEVICE_ERROR";
}

public static class GroupScheduleLimits
{
    public const int MinimumScanCycleMs = 100;
    public const int MaximumScanCycleMs = 86_400_000;
    public const int MinimumUploadCycleSeconds = 1;
    public const int MaximumUploadCycleSeconds = 86_400;
}

public sealed record MqttGroup
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string Topic { get; init; }
    public string? Line { get; init; }
    public int ScanCycleMs { get; init; } = 1_000;
    public int UploadCycleSeconds { get; init; } = 10;
    public bool Enabled { get; init; } = true;
    public bool WriteBackEnabled { get; init; }
    public string? WriteCommandTopic { get; init; }
    public string? WriteAckTopic { get; init; }
}

public sealed record DeviceDefinition
{
    public required Guid Id { get; init; }
    public required Guid GroupId { get; init; }
    public required string Name { get; init; }
    public string? MqttDeviceId { get; init; }
    public Guid? MqttTemplateId { get; init; }
    public required ProtocolKind Protocol { get; init; }
    public required string Host { get; init; }
    public required int Port { get; init; }
    public bool Enabled { get; init; } = true;
    public int ConnectTimeoutMs { get; init; } = 3_000;
    public int ReceiveTimeoutMs { get; init; } = 3_000;
    public int Station { get; init; } = 1;
    public int Rack { get; init; }
    public int Slot { get; init; }
    public SiemensPlcSeries SiemensSeries { get; init; } = SiemensPlcSeries.S1500;
    public bool SiemensAutoRackSlot { get; init; } = true;
    public SiemensConnectionType SiemensConnectionType { get; init; } = SiemensConnectionType.Pg;
    public int SiemensLocalTsap { get; init; }
    public int SiemensDestinationTsap { get; init; }
    public MitsubishiMcTransport MitsubishiTransport { get; init; } = MitsubishiMcTransport.Tcp;
    public MitsubishiMcFormat MitsubishiFormat { get; init; } = MitsubishiMcFormat.Binary3E;
    public int NetworkNumber { get; init; }
    public int PlcNumber { get; init; } = 255;
    public int NetworkStationNumber { get; init; }
    public int TargetIoStation { get; init; } = 1_023;
    public int FinsDestinationNetwork { get; init; }
    public int FinsDestinationNode { get; init; }
    public int FinsDestinationUnit { get; init; }
    public int FinsSourceNetwork { get; init; }
    public int FinsSourceNode { get; init; }
    public int FinsSourceUnit { get; init; }
    public int FinsReadSplits { get; init; } = 500;
    public int OmronCipConnectionTimeoutMultiplier { get; init; } = 2;
    public string? CipRoutePath { get; init; }
    public bool CipReadArrayUseSegment { get; init; } = true;
    public bool BeckhoffUseAutoAmsNetId { get; init; } = true;
    public string? BeckhoffTargetAmsNetId { get; init; }
    public string? BeckhoffSenderAmsNetId { get; init; }
    public bool BeckhoffUseTagCache { get; init; } = true;
    public InovancePlcSeries InovanceSeries { get; init; } = InovancePlcSeries.AM;
    public DeltaPlcSeries DeltaSeries { get; init; } = DeltaPlcSeries.Dvp;
    public string? OpcUaUsername { get; init; }
    public string? OpcUaPassword { get; init; }
    public bool OpcUaUseSecurity { get; init; }
    public bool OpcUaAutoAcceptUntrusted { get; init; }
}

public sealed record TagDefinition
{
    public required Guid Id { get; init; }
    public required Guid DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required TagDataType DataType { get; init; }
    public ModbusFunctionCode? ModbusFunctionCode { get; init; }
    public bool Enabled { get; init; } = true;
    public string ByteOrder { get; init; } = "ABCD";
    public string Operator { get; init; } = "*";
    public decimal Operand { get; init; } = 1M;
    public int Decimals { get; init; } = 2;
    public bool PublishOnChange { get; init; }
    public decimal Deadband { get; init; }
    public int StringLength { get; init; } = 32;
    public bool Writable { get; init; }
    public decimal? MinWriteValue { get; init; }
    public decimal? MaxWriteValue { get; init; }
    public string? Remark { get; init; }
}

public sealed record MqttOptions
{
    public bool Enabled { get; init; }
    public string GatewayId { get; init; } = "EDGENEXUS_01";
    public string Host { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 1883;
    public string ClientId { get; init; } = "EDGENEXUS_01";
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int Qos { get; init; } = 1;
    public bool Retain { get; init; }
    public bool UseTls { get; init; }
    public string LastWillTopic { get; init; } = "edge/status";
    public string LastWillPayload { get; init; } = "offline";
    public bool StoreAndForwardEnabled { get; init; } = true;
    public int OutboxMaxMessages { get; init; } = 500_000;
    public int MaxPublishAttempts { get; init; } = 20;
    public int ReplayMaxMessagesPerSecond { get; init; } = 200;
    public bool WriteBackEnabled { get; init; } = true;
    public int WriteCommandDefaultTtlMs { get; init; } = 10_000;
    public int WriteMaxRequestsPerSecondPerDevice { get; init; } = 20;
}

public enum TagWriteStatus
{
    Success,
    RejectedReadOnly,
    OutOfRange,
    TagNotFound,
    InvalidValue,
    DeviceOffline,
    ProtocolError,
    ReadBackMismatch,
    Expired,
    RateLimited
}

public sealed record TagWriteItem(
    string Tag,
    object? Value);

public sealed record DeviceWriteCommand(
    string CommandId,
    string DeviceId,
    DateTimeOffset Timestamp,
    IReadOnlyList<TagWriteItem> Writes,
    int? TtlMs = null,
    string? Operator = null,
    Guid? ScopedGroupId = null);

public sealed record TagWriteResult(
    string Tag,
    TagWriteStatus Status,
    object? RequestedValue,
    object? WrittenRawValue,
    object? ReadBackValue,
    string? Error = null);

public sealed record DeviceWriteAck(
    string CommandId,
    string DeviceId,
    bool Success,
    bool Deduplicated,
    long ElapsedMs,
    DateTimeOffset Timestamp,
    IReadOnlyList<TagWriteResult> Results,
    string? Error = null);

public enum MqttConnectionState
{
    Disabled,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}

public sealed record MqttRuntimeStatus(
    MqttConnectionState State,
    bool Enabled,
    string Host,
    int Port,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? ConnectedAt,
    DateTimeOffset? NextRetryAt,
    string? LastError);

public sealed record TagSample(
    Guid GroupId,
    string Topic,
    Guid DeviceId,
    string DeviceName,
    Guid TagId,
    string TagName,
    object? Value,
    object? RawValue,
    DataQuality Quality,
    DateTimeOffset Timestamp,
    string? Error = null);

public sealed record TagData(
    string TagName,
    object? Value,
    DataQuality Quality);

public sealed record DeviceData(
    string DeviceId,
    string DeviceName,
    string DeviceIp,
    ProtocolKind Protocol,
    DateTimeOffset Timestamp,
    IReadOnlyList<TagData> Tags);

public sealed record MqttPublishRequest(
    Guid GroupId,
    string GroupName,
    string? Line,
    string TopicTemplate,
    Guid? TemplateId,
    DeviceData DeviceData)
{
    public Guid SourceDeviceId { get; init; }
    public IReadOnlyList<Guid> SourceTagIds { get; init; } = [];
}

public enum MqttPayloadStyle
{
    Standard,
    Advantech
}

public sealed record MqttCustomField
{
    public required string Name { get; init; }
    public required string Value { get; init; }
}

public sealed record MqttTemplate
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsBuiltIn { get; init; }
    public MqttPayloadStyle PayloadStyle { get; init; }
    public bool IncludeDeviceId { get; init; } = true;
    public bool IncludeDeviceName { get; init; }
    public bool IncludeDeviceIp { get; init; }
    public bool IncludeProtocol { get; init; }
    public bool IncludeGatewayId { get; init; }
    public bool IncludeGatewayVersion { get; init; }
    public bool IncludeTimestamp { get; init; } = true;
    public bool IncludeUnixTime { get; init; }
    public bool IncludeTag { get; init; } = true;
    public bool IncludeValue { get; init; } = true;
    public bool IncludeQuality { get; init; } = true;
    public string DeviceErrorTagPrefix { get; init; } = DeviceErrorCodes.DefaultTagPrefix;
    public int CommunicationFailureCode { get; init; } = DeviceErrorCodes.CommunicationFailure;
    public IReadOnlyList<MqttCustomField> CustomFields { get; init; } = [];
    public DateTimeOffset CreateTime { get; init; }
    public DateTimeOffset UpdateTime { get; init; }
}

public sealed record MqttPayloadContext(
    string GatewayId,
    string GatewayVersion,
    string GroupName,
    string? Line);

public sealed record MqttBuildResult(string Topic, string Payload, Guid TemplateId, string TemplateName);

public sealed record DeviceTagReadResult(
    object? Value,
    DataQuality Quality,
    string? Error = null)
{
    public bool IsSuccess => Quality == DataQuality.Good;

    public static DeviceTagReadResult Success(object? value) => new(value, DataQuality.Good);

    public static DeviceTagReadResult ConfigurationFailure(string error) =>
        new(null, DataQuality.ConfigurationError, error);
}

public sealed record DeviceRuntimeStatus(
    Guid DeviceId,
    DeviceConnectionState State,
    DateTimeOffset ChangedAt,
    DateTimeOffset? LastSuccessAt,
    string? Error)
{
    public DeviceFaultKind FaultKind { get; init; }
}

public sealed record DeviceHealthStatus(
    Guid DeviceId,
    string DeviceExternalId,
    string DeviceName,
    int ErrorCode,
    DeviceConnectionState ConnectionState,
    DateTimeOffset ChangedAt,
    string? Error)
{
    public bool IsHealthy => ErrorCode == DeviceErrorCodes.Normal;
}

public sealed record GatewaySnapshot(
    IReadOnlyList<MqttGroup> Groups,
    IReadOnlyList<DeviceDefinition> Devices,
    IReadOnlyList<TagDefinition> Tags,
    MqttOptions Mqtt);

public sealed record ConfigurationRevision(
    long Version,
    DateTimeOffset CreatedAt,
    string Reason,
    int GroupCount,
    int DeviceCount,
    int TagCount,
    bool IsActive);

public sealed record PublishResult(long Version, DateTimeOffset PublishedAt);

public enum ResourceProtectionLevel
{
    Normal,
    Warning,
    Restricted,
    Critical
}

public sealed record ResourceSnapshot(
    ResourceProtectionLevel Level,
    double CpuPercent,
    long WorkingSetMb,
    long ManagedHeapMb,
    double DiskUsedPercent,
    long DiskFreeMb,
    int ThreadCount,
    DateTimeOffset SampledAt,
    string? Reason);

public static class TelemetryKey
{
    public static string Create(string deviceId, string tagName) =>
        tagName.StartsWith('#') ? tagName : $"{deviceId}:{tagName}";
}

public sealed record MqttOutboxStatistics(
    long PendingMessages,
    long DeadLetterMessages,
    long OldestPendingAgeSeconds,
    DateTimeOffset SampledAt);

public sealed record MqttDeadLetter(
    long Id,
    string Topic,
    string Payload,
    int Attempts,
    string Error,
    DateTimeOffset FailedAt);

public sealed record AuditEvent(
    Guid Id,
    string TraceId,
    string Method,
    string Path,
    int StatusCode,
    string Actor,
    string? RemoteAddress,
    long ElapsedMilliseconds,
    DateTimeOffset CreatedAt);
