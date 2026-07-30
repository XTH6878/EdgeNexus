using Microsoft.Extensions.DependencyInjection;
using Snet.Edge.Application;
using Snet.Edge.Infrastructure.GatewayDeviceHealth;
using Snet.Edge.Infrastructure.GatewayMqtt;

namespace Snet.Edge.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddEdgeInfrastructure(
        this IServiceCollection services,
        string connectionString,
        string dataDirectory,
        ResourceProtectionOptions? resourceOptions = null,
        AuthenticationBootstrapOptions? authenticationOptions = null,
        AcquisitionStabilityOptions? acquisitionStabilityOptions = null)
    {
        services.AddSingleton(new SqliteGatewayConfigurationStore(connectionString));
        services.AddSingleton<IGatewayConfigurationStore>(provider => provider.GetRequiredService<SqliteGatewayConfigurationStore>());
        services.AddSingleton<IMqttConfigurationPublisher>(provider => provider.GetRequiredService<SqliteGatewayConfigurationStore>());
        services.AddSingleton<INetworkInventoryService, SystemNetworkInventoryService>();
        services.AddSingleton<SqliteNetworkConfigurationService>(provider => new SqliteNetworkConfigurationService(
            connectionString,
            provider.GetRequiredService<INetworkInventoryService>()));
        services.AddSingleton<INetworkConfigurationService>(provider => provider.GetRequiredService<SqliteNetworkConfigurationService>());
        services.AddSingleton<MqttVariableResolver>();
        services.AddSingleton<IMqttPayloadBuilder, MqttPayloadBuilder>();
        services.AddSingleton<ITopicBuilder, MqttTopicBuilder>();
        services.AddSingleton(new MqttTemplateManager(connectionString));
        services.AddSingleton<IMqttTemplateManager>(provider => provider.GetRequiredService<MqttTemplateManager>());
        services.AddSingleton<ITemplateResolver>(provider => provider.GetRequiredService<MqttTemplateManager>());
        services.AddSingleton<MqttOutbox>(provider => new MqttOutbox(
            connectionString,
            provider.GetRequiredService<IGatewayConfigurationStore>(),
            provider.GetRequiredService<IResourceGuard>(),
            provider.GetRequiredService<ITemplateResolver>(),
            provider.GetRequiredService<IMqttPayloadBuilder>(),
            provider.GetRequiredService<ITopicBuilder>()));
        services.AddSingleton<IMqttMessagePublisher>(provider => provider.GetRequiredService<MqttOutbox>());
        services.AddSingleton<IMqttOutboxMonitor>(provider => provider.GetRequiredService<MqttOutbox>());
        services.AddSingleton<IMqttOutboxConfigurationSynchronizer>(provider => provider.GetRequiredService<MqttOutbox>());
        services.AddSingleton<MqttRuntimeStateStore>();
        services.AddSingleton<IMqttRuntimeStateStore>(provider => provider.GetRequiredService<MqttRuntimeStateStore>());
        services.AddSingleton<IGatewayConfigurationValidator, GatewayConfigurationValidator>();
        services.AddSingleton<IDeviceDriverFactory, HslDeviceDriverFactory>();
        services.AddSingleton<INetworkDiagnosticsService, NetworkDiagnosticsService>();
        services.AddSingleton<ITagReadPlanCompiler, TagReadPlanCompiler>();
        services.AddSingleton<IPointTableImportService, PointTableImportService>();
        services.AddSingleton<IDeviceTagFileService, DeviceTagFileService>();
        services.AddSingleton(authenticationOptions ?? new AuthenticationBootstrapOptions());
        services.AddSingleton(acquisitionStabilityOptions ?? new AcquisitionStabilityOptions());
        services.AddSingleton(provider => new SqliteUserAccountStore(
            connectionString,
            provider.GetRequiredService<AuthenticationBootstrapOptions>()));
        services.AddSingleton<IUserAccountStore>(provider => provider.GetRequiredService<SqliteUserAccountStore>());
        services.AddSingleton<TelemetryBuffer>();
        services.AddSingleton<ITelemetryBuffer>(provider => provider.GetRequiredService<TelemetryBuffer>());
        services.AddSingleton<IRuntimeTagSampleStore>(provider => provider.GetRequiredService<TelemetryBuffer>());
        services.AddSingleton<RuntimeStatusStore>();
        services.AddSingleton<IRuntimeStatusStore>(provider => provider.GetRequiredService<RuntimeStatusStore>());
        services.AddSingleton<IDeviceStatusEventSource>(provider => provider.GetRequiredService<RuntimeStatusStore>());
        services.AddSingleton<IDeviceHealthStateStore, DeviceHealthStateStore>();
        services.AddSingleton<IDeviceErrorManager, DeviceErrorManager>();
        services.AddSingleton<ISystemTagBuilder, SystemTagBuilder>();
        services.AddSingleton<InitializationState>();
        services.AddSingleton<IInitializationState>(provider => provider.GetRequiredService<InitializationState>());
        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<IGatewayMetrics>(provider => provider.GetRequiredService<GatewayMetrics>());
        services.AddSingleton<AcquisitionControl>();
        services.AddSingleton<IAcquisitionControl>(provider => provider.GetRequiredService<AcquisitionControl>());
        services.AddSingleton(resourceOptions ?? new ResourceProtectionOptions());
        services.AddSingleton(dataDirectory);
        services.AddSingleton<ResourceGuard>();
        services.AddSingleton<IResourceGuard>(provider => provider.GetRequiredService<ResourceGuard>());
        services.AddSingleton<AuditLogStore>(provider => new AuditLogStore(
            connectionString,
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AuditLogStore>>()));
        services.AddSingleton<IAuditLogStore>(provider => provider.GetRequiredService<AuditLogStore>());
        services.AddHostedService<InfrastructureInitializer>();
        services.AddHostedService<ResourceMonitorWorker>();
        services.AddHostedService(provider => provider.GetRequiredService<AuditLogStore>());
        services.AddHostedService<AcquisitionWorker>();
        services.AddHostedService<TelemetryAggregationWorker>();
        services.AddHostedService<DeviceHealthMonitor>();
        services.AddHostedService<MqttStoreForwardWorker>();
        return services;
    }
}
