using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public interface IGatewayConfigurationStore
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<GatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    ValueTask<GatewaySnapshot> GetDraftSnapshotAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ConfigurationRevision>> GetRevisionsAsync(int take, CancellationToken cancellationToken);
    ValueTask<GatewaySnapshot> GetRevisionSnapshotAsync(long version, CancellationToken cancellationToken);
    ValueTask<PublishResult> PublishDraftAsync(string reason, CancellationToken cancellationToken);
    ValueTask<PublishResult> RollbackAsync(
        long version,
        long expectedActiveVersion,
        string reason,
        CancellationToken cancellationToken);
    ValueTask DeleteRevisionAsync(
        long version,
        long expectedActiveVersion,
        string reason,
        CancellationToken cancellationToken);
    ValueTask UpsertGroupAsync(MqttGroup group, CancellationToken cancellationToken);
    ValueTask DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken);
    ValueTask UpsertDeviceAsync(DeviceDefinition device, CancellationToken cancellationToken);
    ValueTask DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken);
    ValueTask ReplaceTagsAsync(Guid deviceId, IReadOnlyCollection<TagDefinition> tags, CancellationToken cancellationToken);
    ValueTask SaveMqttOptionsAsync(MqttOptions options, CancellationToken cancellationToken);
    ValueTask ReplaceDraftAsync(GatewaySnapshot snapshot, CancellationToken cancellationToken);
}

public interface IMqttConfigurationPublisher
{
    ValueTask<PublishResult> PublishMqttOptionsAsync(
        MqttOptions options,
        string reason,
        CancellationToken cancellationToken);
}

public sealed record PointTableImportIssue(int Row, string Field, string Message);

public sealed record PointTableImportReport(
    bool IsValid,
    int TotalRows,
    int GroupCount,
    int DeviceCount,
    int TagCount,
    IReadOnlyList<PointTableImportIssue> Errors,
    IReadOnlyList<string> Warnings);

public interface IPointTableImportService
{
    ValueTask<PointTableImportReport> PreviewAsync(
        Stream stream,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken);

    ValueTask<PointTableImportReport> ImportDraftAsync(
        Stream stream,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken);
}

public sealed record DeviceTagExportFile(byte[] Content, string ContentType, string FileName);

public sealed record DeviceTagImportPreview(
    bool IsValid,
    int TotalRows,
    IReadOnlyList<TagDefinition> Tags,
    IReadOnlyList<PointTableImportIssue> Errors,
    IReadOnlyList<string> Warnings);

public interface IDeviceTagFileService
{
    ValueTask<DeviceTagExportFile> ExportAsync(Guid deviceId, CancellationToken cancellationToken);
    ValueTask<DeviceTagImportPreview> PreviewImportAsync(
        Guid deviceId,
        Stream stream,
        string fileName,
        CancellationToken cancellationToken);
}

public interface IDeviceDriver : IAsyncDisposable
{
    ProtocolKind Protocol { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken);
    ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken);
    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}

public interface IDeviceDriverFactory
{
    IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null);
}

public interface INetworkInventoryService
{
    IReadOnlyList<NetworkInterfaceInfo> GetInterfaces(bool forceRefresh = false);
}

public interface INetworkConfigurationService
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<NetworkConfigurationView> GetAsync(bool forceRefresh, CancellationToken cancellationToken);
    ValueTask<NetworkConfigurationView> SaveAsync(NetworkBindingConfiguration configuration, CancellationToken cancellationToken);
    string? ResolveLocalIpv4(NetworkUsage usage);
    string? ResolveInterfaceName(NetworkUsage usage);
    long GetRevision(NetworkUsage usage);
    bool IsWebRequestAllowed(System.Net.IPAddress? localAddress);
}

public interface INetworkDiagnosticsService
{
    ValueTask<NetworkDiagnosticResult> PingAsync(string host, NetworkUsage usage, int timeoutMs, CancellationToken cancellationToken);
    ValueTask<NetworkDiagnosticResult> TestTcpAsync(string host, int port, NetworkUsage usage, int timeoutMs, CancellationToken cancellationToken);
    ValueTask<NetworkDiagnosticResult> TestMqttAsync(MqttOptions options, int timeoutMs, CancellationToken cancellationToken);
    ValueTask<NetworkDiagnosticResult> TestPlcAsync(DeviceDefinition device, IReadOnlyList<TagDefinition> tags, int timeoutMs, CancellationToken cancellationToken);
}

public sealed record TagReadBatch(IReadOnlyList<TagDefinition> Tags, bool UseNativeMultiRead);

public sealed record DeviceReadPlan(IReadOnlyList<TagReadBatch> Batches, int SourceTagCount);

public interface ITagReadPlanCompiler
{
    DeviceReadPlan Compile(DeviceDefinition device, IReadOnlyList<TagDefinition> tags);
}

public interface ITelemetryBuffer
{
    ValueTask WriteAsync(TagSample sample, CancellationToken cancellationToken);
    IAsyncEnumerable<TagSample> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IRuntimeTagSampleStore
{
    IReadOnlyCollection<TagSample> GetByDevice(Guid deviceId);
    IReadOnlyCollection<TagSample> TakeChanges();
}

public interface IMqttMessagePublisher
{
    ValueTask PublishAsync(MqttPublishRequest request, CancellationToken cancellationToken);
}

public interface IMqttPayloadBuilder
{
    string Build(DeviceData data, MqttTemplate template, MqttPayloadContext context);
}

public interface ITopicBuilder
{
    string Build(string topicTemplate, DeviceData data, MqttPayloadContext context);
}

public interface ITemplateResolver
{
    ValueTask<MqttTemplate> ResolveAsync(Guid? templateId, CancellationToken cancellationToken);
}

public interface IMqttTemplateManager : ITemplateResolver
{
    ValueTask InitializeAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MqttTemplate>> GetAllAsync(CancellationToken cancellationToken);
    ValueTask<MqttTemplate?> GetAsync(Guid id, CancellationToken cancellationToken);
    ValueTask<MqttTemplate> UpsertAsync(MqttTemplate template, CancellationToken cancellationToken);
    ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken);
}

public interface IMqttOutboxMonitor
{
    ValueTask<MqttOutboxStatistics> GetStatisticsAsync(CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<MqttDeadLetter>> GetDeadLettersAsync(int take, CancellationToken cancellationToken);
}

public interface IMqttOutboxConfigurationSynchronizer
{
    ValueTask<int> PruneStaleAsync(GatewaySnapshot snapshot, CancellationToken cancellationToken);
}

public interface IMqttRuntimeStateStore
{
    MqttRuntimeStatus Current { get; }
    long ConfigurationRevision { get; }
    void Set(MqttRuntimeStatus status);
    void NotifyConfigurationChanged();
    ValueTask<bool> WaitForConfigurationChangeAsync(
        long observedRevision,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

public interface IRuntimeStatusStore
{
    void Set(DeviceRuntimeStatus status);
    IReadOnlyCollection<DeviceRuntimeStatus> GetAll();
}

public interface IDeviceStatusEventSource
{
    IAsyncEnumerable<DeviceRuntimeStatus> ReadAllAsync(CancellationToken cancellationToken);
}

public interface IDeviceHealthStateStore
{
    IReadOnlyCollection<DeviceHealthStatus> GetAll();
    DeviceHealthStatus? Get(Guid deviceId);
    bool SetIfChanged(DeviceHealthStatus status);
    void Remove(Guid deviceId);
}

public interface IDeviceErrorManager
{
    int Resolve(DeviceRuntimeStatus status, int communicationFailureCode);
}

public interface ISystemTagBuilder
{
    TagData BuildDeviceErrorTag(string deviceId, string tagPrefix, int errorCode);
}

public interface IAcquisitionControl
{
    bool IsRunning { get; }
    void Start();
    void Stop();
}

public interface IResourceGuard
{
    ResourceSnapshot Current { get; }
    bool AllowAcquisition { get; }
    bool AllowPersistentEnqueue { get; }
}

public interface IInitializationState
{
    bool IsReady { get; }
    DateTimeOffset? ReadyAt { get; }
    string? Error { get; }
}

public sealed record GatewayMetricSnapshot(
    long AcquisitionCyclesSucceeded,
    long AcquisitionCyclesFailed,
    long TagSamplesRead,
    long AcquisitionDurationMilliseconds,
    long MqttMessagesPublished,
    long MqttPublishFailures,
    long SignalRConnections);

public interface IGatewayMetrics
{
    GatewayMetricSnapshot Snapshot { get; }
    void RecordAcquisitionSuccess(int tagCount, TimeSpan duration);
    void RecordAcquisitionFailure(TimeSpan duration);
    void RecordMqttPublished();
    void RecordMqttPublishFailure();
    void SignalRConnected();
    void SignalRDisconnected();
}

public sealed record ConfigurationValidationResult(IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public interface IGatewayConfigurationValidator
{
    ConfigurationValidationResult Validate(GatewaySnapshot snapshot);
}

public interface IAuditLogStore
{
    bool TryWrite(AuditEvent auditEvent);
    ValueTask<IReadOnlyList<AuditEvent>> GetRecentAsync(int take, CancellationToken cancellationToken);
}

public sealed record UserAuthenticationResult(
    bool Success,
    bool IsLocked,
    DateTimeOffset? LockedUntil,
    string? Username,
    string? Role,
    bool MustChangePassword,
    string? Error);

public sealed record PasswordChangeResult(bool Success, string? Error);

public interface IUserAccountStore
{
    ValueTask<UserAuthenticationResult> AuthenticateAsync(
        string username,
        string password,
        CancellationToken cancellationToken);

    ValueTask<PasswordChangeResult> ChangePasswordAsync(
        string username,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken);
}
