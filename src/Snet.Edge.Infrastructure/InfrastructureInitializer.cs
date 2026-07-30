using Microsoft.Extensions.Hosting;
using Snet.Edge.Application;

namespace Snet.Edge.Infrastructure;

public sealed class InfrastructureInitializer(
    SqliteGatewayConfigurationStore configurationStore,
    INetworkConfigurationService networkConfigurationService,
    IMqttTemplateManager mqttTemplateManager,
    MqttOutbox mqttOutbox,
    AuditLogStore auditLogStore,
    SqliteUserAccountStore userAccountStore,
    InitializationState initializationState) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await configurationStore.InitializeAsync(cancellationToken);
            await networkConfigurationService.InitializeAsync(cancellationToken);
            await mqttTemplateManager.InitializeAsync(cancellationToken);
            await mqttOutbox.InitializeAsync(cancellationToken);
            await auditLogStore.InitializeAsync(cancellationToken);
            await userAccountStore.InitializeAsync(cancellationToken);
            initializationState.MarkReady();
        }
        catch (Exception exception)
        {
            initializationState.MarkFailed(exception);
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
