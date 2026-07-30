using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.WebApi;

public static class ApiEndpoints
{
    private static readonly JsonSerializerOptions ExportJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static IEndpointRouteBuilder MapGatewayApi(this IEndpointRouteBuilder endpoints)
    {
        var api = endpoints.MapGroup("/api/v1").RequireAuthorization();
        api.AddEndpointFilter(async (context, next) =>
        {
            var method = context.HttpContext.Request.Method;
            if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || context.HttpContext.User.IsInRole("Admin"))
            {
                return await next(context);
            }

            return Results.Forbid();
        });

        api.MapGet("/configuration", (IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
            store.GetSnapshotAsync(cancellationToken));

        api.MapGet("/configuration/draft", (IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
            store.GetDraftSnapshotAsync(cancellationToken));

        api.MapGet("/configuration/export", async (IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
            var payload = JsonSerializer.SerializeToUtf8Bytes(snapshot, ExportJsonOptions);
            return Results.File(
                payload,
                "application/json; charset=utf-8",
                $"EdgeNexus_Draft_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.json");
        });

        api.MapGet("/configuration/revisions", (int? take, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
            store.GetRevisionsAsync(take ?? 50, cancellationToken));

        api.MapGet("/configuration/revisions/{version:long}/preview", async (
            long version,
            IGatewayConfigurationStore store,
            IGatewayConfigurationValidator validator,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var target = await store.GetRevisionSnapshotAsync(version, cancellationToken);
                var active = await store.GetSnapshotAsync(cancellationToken);
                var revisions = await store.GetRevisionsAsync(1, cancellationToken);
                var activeVersion = revisions.Count > 0 ? revisions[0].Version : 0;
                return Results.Ok(new RollbackPreviewResponse(
                    version,
                    activeVersion,
                    validator.Validate(target),
                    BuildRollbackImpact(active, target)));
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        api.MapPost("/configuration/revisions/{version:long}/delete", async (
            long version,
            DeleteRevisionRequest request,
            IGatewayConfigurationStore store,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await store.DeleteRevisionAsync(
                    version,
                    request.ExpectedActiveVersion,
                    request.Reason,
                    cancellationToken);
                return Results.Ok(new { version });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        api.MapPost("/configuration/validate", async (
            IGatewayConfigurationStore store,
            IGatewayConfigurationValidator validator,
            CancellationToken cancellationToken) =>
        {
            var draft = await store.GetDraftSnapshotAsync(cancellationToken);
            return Results.Ok(validator.Validate(draft));
        });

        api.MapPost("/configuration/import/preview", (
            IFormFile file,
            bool? replaceExisting,
            IPointTableImportService importer,
            CancellationToken cancellationToken) =>
            ImportPointTableAsync(file, replaceExisting ?? false, false, importer, cancellationToken))
            .DisableAntiforgery();

        api.MapPost("/configuration/import", (
            IFormFile file,
            bool? replaceExisting,
            IPointTableImportService importer,
            CancellationToken cancellationToken) =>
            ImportPointTableAsync(file, replaceExisting ?? false, true, importer, cancellationToken))
            .DisableAntiforgery();

        api.MapPost("/configuration/publish", async (
            PublishRequest request,
            IGatewayConfigurationStore store,
            IGatewayConfigurationValidator validator,
            IMqttOutboxConfigurationSynchronizer outboxSynchronizer,
            CancellationToken cancellationToken) =>
        {
            var draft = await store.GetDraftSnapshotAsync(cancellationToken);
            var validation = validator.Validate(draft);
            if (!validation.IsValid)
            {
                return Results.BadRequest(validation);
            }

            var result = await store.PublishDraftAsync(request.Reason, cancellationToken);
            var prunedMessages = await outboxSynchronizer.PruneStaleAsync(draft, cancellationToken);
            return Results.Ok(new { result.Version, result.PublishedAt, validation.Warnings, prunedMessages });
        });

        api.MapPost("/configuration/rollback/{version:long}", async (
            long version,
            RollbackRequest request,
            IGatewayConfigurationStore store,
            IGatewayConfigurationValidator validator,
            IMqttOutboxConfigurationSynchronizer outboxSynchronizer,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var target = await store.GetRevisionSnapshotAsync(version, cancellationToken);
                var validation = validator.Validate(target);
                if (!validation.IsValid)
                {
                    return Results.BadRequest(validation);
                }

                var result = await store.RollbackAsync(
                    version,
                    request.ExpectedActiveVersion,
                    request.Reason,
                    cancellationToken);
                var prunedMessages = await outboxSynchronizer.PruneStaleAsync(target, cancellationToken);
                return Results.Ok(new { result.Version, result.PublishedAt, validation.Warnings, prunedMessages });
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        api.MapGet("/protocols", () => ProtocolDescriptors.All);

        api.MapGet("/audit", (int? take, IAuditLogStore auditLogStore, CancellationToken cancellationToken) =>
            auditLogStore.GetRecentAsync(take ?? 200, cancellationToken));

        api.MapGet("/dashboard", async (
            IGatewayConfigurationStore store,
            IRuntimeStatusStore statuses,
            IDeviceHealthStateStore healthStates,
            IAcquisitionControl control,
            IResourceGuard resourceGuard,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetSnapshotAsync(cancellationToken);
            var runtime = statuses.GetAll();
            var online = runtime.Count(item => item.State == DeviceConnectionState.Online);
            return Results.Ok(new
            {
                isRunning = control.IsRunning,
                totalDevices = snapshot.Devices.Count,
                onlineDevices = online,
                offlineDevices = Math.Max(snapshot.Devices.Count - online, 0),
                groups = snapshot.Groups,
                devices = snapshot.Devices,
                runtime,
                deviceHealth = healthStates.GetAll(),
                resource = resourceGuard.Current
            });
        });

        api.MapGet("/device-health", (IDeviceHealthStateStore healthStates) => healthStates.GetAll());

        api.MapPost("/groups", async (MqttGroup group, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            var error = ValidateGroup(group);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await store.UpsertGroupAsync(group, cancellationToken);
            return Results.Ok(group);
        });

        api.MapPut("/groups/{id:guid}", async (Guid id, MqttGroup group, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            if (id != group.Id)
            {
                return Results.BadRequest(new { error = "路由ID与分组ID不一致。" });
            }

            var error = ValidateGroup(group);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await store.UpsertGroupAsync(group, cancellationToken);
            return Results.NoContent();
        });

        api.MapDelete("/groups/{id:guid}", async (Guid id, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            await store.DeleteGroupAsync(id, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/devices", async (
            DeviceDefinition device,
            IGatewayConfigurationStore store,
            IMqttTemplateManager templateManager,
            CancellationToken cancellationToken) =>
        {
            var error = await ValidateDeviceAsync(device, store, templateManager, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await store.UpsertDeviceAsync(device, cancellationToken);
            return Results.Ok(device);
        });

        api.MapPut("/devices/{id:guid}", async (
            Guid id,
            DeviceDefinition device,
            IGatewayConfigurationStore store,
            IMqttTemplateManager templateManager,
            CancellationToken cancellationToken) =>
        {
            if (id != device.Id)
            {
                return Results.BadRequest(new { error = "路由ID与设备ID不一致。" });
            }

            var error = await ValidateDeviceAsync(device, store, templateManager, cancellationToken);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            await store.UpsertDeviceAsync(device, cancellationToken);
            return Results.NoContent();
        });

        api.MapDelete("/devices/{id:guid}", async (Guid id, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            await store.DeleteDeviceAsync(id, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/devices/{id:guid}/connection-test", async (
            Guid id,
            IGatewayConfigurationStore store,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
            var device = snapshot.Devices.FirstOrDefault(item => item.Id == id);
            if (device is null)
            {
                return Results.NotFound();
            }

            var tags = snapshot.Tags.Where(item => item.DeviceId == id && item.Enabled).ToArray();
            return Results.Ok(await diagnostics.TestPlcAsync(
                device,
                tags,
                device.ConnectTimeoutMs + 1_000,
                cancellationToken));
        });

        api.MapPost("/devices/connection-test", async (
            DeviceDefinition device,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Host) || device.Port is < 1 or > 65_535)
            {
                return Results.BadRequest(new { error = "设备名称、主机地址或端口无效。" });
            }

            var errors = DeviceProtocolValidator.Validate(device);
            if (errors.Count > 0)
            {
                return Results.BadRequest(new { error = errors[0] });
            }

            return Results.Ok(await diagnostics.TestPlcAsync(
                device,
                [],
                device.ConnectTimeoutMs + 1_000,
                cancellationToken));
        });

        api.MapGet("/devices/{id:guid}/tags", async (Guid id, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
            return Results.Ok(snapshot.Tags.Where(tag => tag.DeviceId == id));
        });

        api.MapGet("/devices/{id:guid}/tags/export", async (
            Guid id,
            IDeviceTagFileService files,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var export = await files.ExportAsync(id, cancellationToken);
                return Results.File(export.Content, export.ContentType, export.FileName);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        });

        api.MapPost("/devices/{id:guid}/tags/import/preview", async (
            Guid id,
            IFormFile file,
            IDeviceTagFileService files,
            CancellationToken cancellationToken) =>
        {
            const long maxFileBytes = 20 * 1024 * 1024;
            if (file.Length is <= 0 or > maxFileBytes)
            {
                return Results.BadRequest(new { error = "点位文件必须大于0且不能超过20MB。" });
            }

            try
            {
                await using var stream = file.OpenReadStream();
                var preview = await files.PreviewImportAsync(id, stream, file.FileName, cancellationToken);
                return preview.IsValid ? Results.Ok(preview) : Results.BadRequest(preview);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        }).DisableAntiforgery();

        api.MapPut("/devices/{id:guid}/tags", async (
            Guid id,
            IReadOnlyCollection<TagDefinition> tags,
            IGatewayConfigurationStore store,
            CancellationToken cancellationToken) =>
        {
            if (tags.Any(tag => tag.DeviceId != id || string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.Address)))
            {
                return Results.BadRequest(new { error = "点位名称、地址不能为空，且DeviceId必须一致。" });
            }

            if (tags.GroupBy(tag => tag.Name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            {
                return Results.BadRequest(new { error = "同一设备内点位名称必须唯一。" });
            }

            var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
            var device = snapshot.Devices.FirstOrDefault(item => item.Id == id);
            if (device is null)
            {
                return Results.NotFound(new { error = "设备不存在。" });
            }

            var protocolErrors = tags.SelectMany(tag => TagProtocolValidator.Validate(device, tag)).ToArray();
            if (protocolErrors.Length > 0)
            {
                return Results.BadRequest(new { error = string.Join("；", protocolErrors), errors = protocolErrors });
            }

            var normalizedTags = device.Protocol == ProtocolKind.ModbusTcp
                ? tags.Select(ModbusTagAddress.Normalize).ToArray()
                : tags;
            await store.ReplaceTagsAsync(id, normalizedTags, cancellationToken);
            return Results.NoContent();
        });

        api.MapPut("/mqtt/config", async (MqttOptions options, IGatewayConfigurationStore store, CancellationToken cancellationToken) =>
        {
            if (ValidateMqttOptions(options) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            await store.SaveMqttOptionsAsync(options, cancellationToken);
            return Results.NoContent();
        });

        api.MapPost("/mqtt/config/apply", async (
            MqttOptions options,
            IMqttConfigurationPublisher publisher,
            IMqttRuntimeStateStore runtimeState,
            CancellationToken cancellationToken) =>
        {
            if (ValidateMqttOptions(options) is { } error)
            {
                return Results.BadRequest(new { error });
            }

            var result = await publisher.PublishMqttOptionsAsync(
                options,
                options.Enabled ? "MQTT配置独立应用并启用" : "MQTT配置独立应用并停用",
                cancellationToken);
            runtimeState.NotifyConfigurationChanged();
            return Results.Ok(result);
        });

        api.MapGet("/mqtt/runtime", (IMqttRuntimeStateStore runtimeState) => runtimeState.Current);

        api.MapGet("/mqtt/outbox/status", (IMqttOutboxMonitor monitor, CancellationToken cancellationToken) =>
            monitor.GetStatisticsAsync(cancellationToken));

        api.MapGet("/mqtt/outbox/dead-letters", (int? take, IMqttOutboxMonitor monitor, CancellationToken cancellationToken) =>
            monitor.GetDeadLettersAsync(take ?? 100, cancellationToken));

        api.MapGet("/mqtt/templates", (IMqttTemplateManager manager, CancellationToken cancellationToken) =>
            manager.GetAllAsync(cancellationToken));

        api.MapPost("/mqtt/templates", async (
            MqttTemplate template,
            IMqttTemplateManager manager,
            CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await manager.UpsertAsync(template, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPut("/mqtt/templates/{id:guid}", async (
            Guid id,
            MqttTemplate template,
            IMqttTemplateManager manager,
            CancellationToken cancellationToken) =>
        {
            if (id != template.Id)
            {
                return Results.BadRequest(new { error = "路由ID与MQTT模板ID不一致。" });
            }

            try
            {
                return Results.Ok(await manager.UpsertAsync(template, cancellationToken));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapDelete("/mqtt/templates/{id:guid}", async (
            Guid id,
            IMqttTemplateManager manager,
            IGatewayConfigurationStore store,
            CancellationToken cancellationToken) =>
        {
            var draft = await store.GetDraftSnapshotAsync(cancellationToken);
            if (draft.Devices.Any(device => device.MqttTemplateId == id))
            {
                return Results.Conflict(new { error = "该模板仍被设备使用，请先调整设备模板绑定。" });
            }

            try
            {
                await manager.DeleteAsync(id, cancellationToken);
                return Results.NoContent();
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { error = exception.Message });
            }
        });

        api.MapPost("/mqtt/templates/preview", async (
            MqttTemplatePreviewRequest request,
            IGatewayConfigurationStore store,
            ITemplateResolver resolver,
            IMqttPayloadBuilder payloadBuilder,
            ITopicBuilder topicBuilder,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var mqtt = (await store.GetDraftSnapshotAsync(cancellationToken)).Mqtt;
                var template = request.Template ?? await resolver.ResolveAsync(request.TemplateId, cancellationToken);
                return Results.Ok(BuildMqttPreview(request, mqtt, template, payloadBuilder, topicBuilder));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPost("/mqtt/templates/test-send", async (
            MqttTemplatePreviewRequest request,
            IGatewayConfigurationStore store,
            ITemplateResolver resolver,
            IMqttPayloadBuilder payloadBuilder,
            ITopicBuilder topicBuilder,
            IMqttMessagePublisher publisher,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var mqtt = (await store.GetSnapshotAsync(cancellationToken)).Mqtt;
                if (!mqtt.Enabled)
                {
                    return Results.BadRequest(new { error = "当前生效配置尚未启用MQTT，不能执行测试发送。" });
                }

                var template = await resolver.ResolveAsync(request.TemplateId, cancellationToken);
                var preview = BuildMqttPreview(request, mqtt, template, payloadBuilder, topicBuilder);
                await publisher.PublishAsync(ToPublishRequest(request, template), cancellationToken);
                return Results.Accepted(value: new { queued = true, preview.Topic, preview.Payload, preview.TemplateId, preview.TemplateName });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapGet("/network/interfaces", async (
            bool? refresh,
            INetworkConfigurationService networkConfiguration,
            CancellationToken cancellationToken) =>
        {
            var view = await networkConfiguration.GetAsync(refresh ?? false, cancellationToken);
            return Results.Ok(view.Interfaces);
        });

        api.MapGet("/network/configuration", (
            bool? refresh,
            INetworkConfigurationService networkConfiguration,
            CancellationToken cancellationToken) =>
            networkConfiguration.GetAsync(refresh ?? false, cancellationToken));

        api.MapPut("/network/configuration", async (
            SaveNetworkBindingsRequest request,
            INetworkConfigurationService networkConfiguration,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await networkConfiguration.SaveAsync(new NetworkBindingConfiguration
                {
                    PlcInterfaceId = request.PlcInterfaceId,
                    MqttInterfaceId = request.MqttInterfaceId,
                    WebInterfaceId = request.WebInterfaceId
                }, cancellationToken);
                return Results.Ok(result);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        api.MapPost("/network/diagnostics/ping", (
            NetworkPingRequest request,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(request.Host)
                ? ValueTask.FromResult<IResult>(Results.BadRequest(new { error = "目标地址不能为空。" }))
                : ExecuteDiagnosticAsync(() => diagnostics.PingAsync(
                    request.Host.Trim(),
                    request.Usage,
                    request.TimeoutMs,
                    cancellationToken)));

        api.MapPost("/network/diagnostics/tcp", (
            NetworkTcpTestRequest request,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(request.Host) || request.Port is < 1 or > 65_535
                ? ValueTask.FromResult<IResult>(Results.BadRequest(new { error = "目标地址或TCP端口无效。" }))
                : ExecuteDiagnosticAsync(() => diagnostics.TestTcpAsync(
                    request.Host.Trim(),
                    request.Port,
                    request.Usage,
                    request.TimeoutMs,
                    cancellationToken)));

        api.MapPost("/network/diagnostics/mqtt", async (
            NetworkMqttTestRequest request,
            IGatewayConfigurationStore store,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
        {
            var mqtt = (await store.GetDraftSnapshotAsync(cancellationToken)).Mqtt;
            return Results.Ok(await diagnostics.TestMqttAsync(mqtt, request.TimeoutMs, cancellationToken));
        });

        api.MapPost("/network/diagnostics/plc", async (
            NetworkPlcTestRequest request,
            IGatewayConfigurationStore store,
            INetworkDiagnosticsService diagnostics,
            CancellationToken cancellationToken) =>
        {
            var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
            var device = snapshot.Devices.FirstOrDefault(item => item.Id == request.DeviceId);
            if (device is null)
            {
                return Results.NotFound(new { error = "设备不存在。" });
            }

            var tags = snapshot.Tags.Where(item => item.DeviceId == device.Id && item.Enabled).ToArray();
            return Results.Ok(await diagnostics.TestPlcAsync(device, tags, request.TimeoutMs, cancellationToken));
        });

        api.MapGet("/acquisition/status", (IAcquisitionControl control) => Results.Ok(new { isRunning = control.IsRunning }));
        api.MapPost("/acquisition/start", (IAcquisitionControl control) =>
        {
            control.Start();
            return Results.Accepted();
        });
        api.MapPost("/acquisition/stop", (IAcquisitionControl control) =>
        {
            control.Stop();
            return Results.Accepted();
        });

        api.MapPost("/diagnostics/ping", async (PingRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Host))
            {
                return Results.BadRequest(new { error = "目标地址不能为空。" });
            }

            using var ping = new Ping();
            try
            {
                var reply = await ping.SendPingAsync(request.Host, TimeSpan.FromMilliseconds(Math.Clamp(request.TimeoutMs, 100, 10_000)), cancellationToken: cancellationToken);
                return Results.Ok(new
                {
                    success = reply.Status == IPStatus.Success,
                    status = reply.Status.ToString(),
                    latency = reply.Status == IPStatus.Success ? reply.RoundtripTime : 0
                });
            }
            catch (PingException exception)
            {
                return Results.Ok(new { success = false, status = "Error", error = exception.Message, latency = 0L });
            }
        });

        return endpoints;
    }

    private static async Task<IResult> ImportPointTableAsync(
        IFormFile file,
        bool replaceExisting,
        bool persist,
        IPointTableImportService importer,
        CancellationToken cancellationToken)
    {
        const long maxFileBytes = 20 * 1024 * 1024;
        if (file.Length is <= 0 or > maxFileBytes)
        {
            return Results.BadRequest(new { error = "点表文件必须大于0且不能超过20MB。" });
        }

        await using var stream = file.OpenReadStream();
        var report = persist
            ? await importer.ImportDraftAsync(stream, file.FileName, replaceExisting, cancellationToken)
            : await importer.PreviewAsync(stream, file.FileName, replaceExisting, cancellationToken);
        return report.IsValid ? Results.Ok(report) : Results.BadRequest(report);
    }

    private static async ValueTask<IResult> ExecuteDiagnosticAsync(
        Func<ValueTask<NetworkDiagnosticResult>> execute) =>
        Results.Ok(await execute());

    private static string? ValidateGroup(MqttGroup group)
    {
        if (string.IsNullOrWhiteSpace(group.Name) || string.IsNullOrWhiteSpace(group.Topic))
        {
            return "分组名称与MQTT Topic不能为空。";
        }

        var topicError = MqttTopicTemplateValidator.Validate(group.Topic);
        if (topicError is not null)
        {
            return topicError;
        }

        if (group.Line?.Length > 128)
        {
            return "产线标识不能超过128个字符。";
        }

        return group.ScanCycleMs is < GroupScheduleLimits.MinimumScanCycleMs or > GroupScheduleLimits.MaximumScanCycleMs ||
               group.UploadCycleSeconds is < GroupScheduleLimits.MinimumUploadCycleSeconds or > GroupScheduleLimits.MaximumUploadCycleSeconds
            ? "扫描周期必须为100ms到24小时，上传周期必须为1秒到24小时。"
            : null;
    }

    private static string? ValidateMqttOptions(MqttOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Host))
        {
            return "MQTT Broker地址不能为空。";
        }

        if (string.IsNullOrWhiteSpace(options.GatewayId))
        {
            return "网关标识不能为空。";
        }

        if (options.Port is < 1 or > 65_535)
        {
            return "MQTT服务端口必须在1到65535之间。";
        }

        if (options.Qos is < 0 or > 2 ||
            options.OutboxMaxMessages < 1_000 ||
            options.MaxPublishAttempts < 1 ||
            options.ReplayMaxMessagesPerSecond < 1)
        {
            return "MQTT QoS、Outbox容量或补传策略无效。";
        }

        return null;
    }

    private static async ValueTask<string?> ValidateDeviceAsync(
        DeviceDefinition device,
        IGatewayConfigurationStore store,
        IMqttTemplateManager templateManager,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Host))
        {
            return "设备名称与主机地址不能为空。";
        }

        if (device.Port is < 1 or > 65_535)
        {
            return "设备端口无效。";
        }

        if (device.MqttDeviceId is { Length: > 128 } || device.MqttDeviceId?.Contains(':') == true)
        {
            return "MQTT设备ID不能超过128个字符或包含冒号。";
        }

        var protocolErrors = DeviceProtocolValidator.Validate(device);
        if (protocolErrors.Count > 0)
        {
            return protocolErrors[0];
        }

        var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
        if (!snapshot.Groups.Any(group => group.Id == device.GroupId))
        {
            return "设备所属分组不存在。";
        }

        if (device.MqttTemplateId is not null &&
            await templateManager.GetAsync(device.MqttTemplateId.Value, cancellationToken) is null)
        {
            return "设备绑定的MQTT模板不存在。";
        }

        return null;
    }

    private static MqttBuildResult BuildMqttPreview(
        MqttTemplatePreviewRequest request,
        MqttOptions mqtt,
        MqttTemplate template,
        IMqttPayloadBuilder payloadBuilder,
        ITopicBuilder topicBuilder)
    {
        if (MqttDeviceStatusTemplateValidator.Validate(template) is { } statusConfigurationError)
        {
            throw new ArgumentException(statusConfigurationError);
        }

        var publishRequest = ToPublishRequest(request, template);
        var context = new MqttPayloadContext(
            mqtt.GatewayId,
            typeof(ApiEndpoints).Assembly.GetName().Version?.ToString(3) ?? "1.0.0",
            publishRequest.GroupName,
            publishRequest.Line);
        var topic = topicBuilder.Build(publishRequest.TopicTemplate, publishRequest.DeviceData, context);
        var payload = payloadBuilder.Build(publishRequest.DeviceData, template, context);
        return new MqttBuildResult(topic, payload, template.Id, template.Name);
    }

    private static MqttPublishRequest ToPublishRequest(MqttTemplatePreviewRequest request, MqttTemplate template)
    {
        if (string.IsNullOrWhiteSpace(request.DeviceId) ||
            string.IsNullOrWhiteSpace(request.DeviceName) ||
            string.IsNullOrWhiteSpace(request.TopicTemplate) ||
            request.Tags.Count == 0)
        {
            throw new ArgumentException("测试设备标识、设备名称、Topic模板和点位数据不能为空。");
        }

        var errorCode = request.CommunicationFailed
            ? template.CommunicationFailureCode
            : DeviceErrorCodes.Normal;
        var tags = new List<TagData>(request.Tags.Count + 1)
        {
            new($"{template.DeviceErrorTagPrefix.Trim().TrimEnd('_')}_{request.DeviceId.Trim()}", errorCode, DataQuality.Good)
        };
        tags.AddRange(request.Tags.Select(tag => new TagData(tag.TagName, tag.Value, tag.Quality)));
        var data = new DeviceData(
            request.DeviceId.Trim(),
            request.DeviceName.Trim(),
            request.DeviceIp?.Trim() ?? string.Empty,
            request.Protocol,
            request.Timestamp ?? DateTimeOffset.UtcNow,
            tags);
        return new MqttPublishRequest(
            Guid.Empty,
            string.IsNullOrWhiteSpace(request.GroupName) ? "测试分组" : request.GroupName.Trim(),
            request.Line?.Trim(),
            request.TopicTemplate.Trim(),
            request.TemplateId,
            data);
    }

    private static RollbackImpact BuildRollbackImpact(GatewaySnapshot active, GatewaySnapshot target) => new(
        Compare(active.Groups, target.Groups, item => item.Id),
        Compare(active.Devices, target.Devices, item => item.Id),
        Compare(active.Tags, target.Tags, item => item.Id),
        active.Mqtt != target.Mqtt);

    private static RevisionChangeSummary Compare<T>(
        IReadOnlyList<T> active,
        IReadOnlyList<T> target,
        Func<T, Guid> keySelector)
    {
        var activeById = active.ToDictionary(keySelector);
        var targetById = target.ToDictionary(keySelector);
        var added = targetById.Keys.Count(key => !activeById.ContainsKey(key));
        var removed = activeById.Keys.Count(key => !targetById.ContainsKey(key));
        var modified = targetById.Count(pair =>
            activeById.TryGetValue(pair.Key, out var current) &&
            !EqualityComparer<T>.Default.Equals(current, pair.Value));
        return new RevisionChangeSummary(added, removed, modified);
    }

    public sealed record PingRequest(string Host, int TimeoutMs = 1_000);
    public sealed record MqttTemplatePreviewTag(string TagName, object? Value, DataQuality Quality);
    public sealed record MqttTemplatePreviewRequest(
        Guid TemplateId,
        MqttTemplate? Template,
        string TopicTemplate,
        string DeviceId,
        string DeviceName,
        string? DeviceIp,
        ProtocolKind Protocol,
        string? GroupName,
        string? Line,
        DateTimeOffset? Timestamp,
        IReadOnlyList<MqttTemplatePreviewTag> Tags,
        bool CommunicationFailed = false);
    public sealed record PublishRequest(string Reason);
    public sealed record RollbackRequest(string Reason, long ExpectedActiveVersion);
    public sealed record DeleteRevisionRequest(string Reason, long ExpectedActiveVersion);
    public sealed record RevisionChangeSummary(int Added, int Removed, int Modified);
    public sealed record RollbackImpact(
        RevisionChangeSummary Groups,
        RevisionChangeSummary Devices,
        RevisionChangeSummary Tags,
        bool MqttChanged);
    public sealed record RollbackPreviewResponse(
        long TargetVersion,
        long ActiveVersion,
        ConfigurationValidationResult Validation,
        RollbackImpact Impact);
}

public static class ProtocolDescriptors
{
    public static readonly ProtocolDescriptor[] All =
    [
        Descriptor(ProtocolKind.SiemensS7, "西门子 S7 TCP", 102,
            Select("siemensSeries", "PLC系列", "S1500", ["S1200", "S1500", "S300", "S400", "S200Smart", "S200"]),
            Switch("siemensAutoRackSlot", "自动机架/槽位", true, "按PLC系列使用工业现场推荐值，特殊拓扑可关闭后手动设置"),
            Number("rack", "机架号", 0, 0, 7),
            Number("slot", "槽号", 0, 0, 31),
            SelectOptions("siemensConnectionType", "连接角色", "Pg",
                new ProtocolParameterOption("Pg", "PG（编程设备，推荐）"),
                new ProtocolParameterOption("Op", "OP（操作面板）"),
                new ProtocolParameterOption("S7Basic", "S7 Basic（基础连接）")),
            Number("siemensLocalTsap", "本地TSAP", 0, 0, 65_535, "0表示使用驱动默认值"),
            Number("siemensDestinationTsap", "目标TSAP", 0, 0, 65_535, "0表示使用驱动默认值")),
        Descriptor(ProtocolKind.ModbusTcp, "Modbus TCP", 502,
            Number("station", "从站地址", 1, 0, 255)),
        Descriptor(ProtocolKind.MitsubishiMc, "三菱 MC", 5000,
            SelectOptions("mitsubishiTransport", "传输方式", "Tcp",
                new ProtocolParameterOption("Tcp", "TCP"),
                new ProtocolParameterOption("Udp", "UDP")),
            SelectOptions("mitsubishiFormat", "报文格式", "Binary3E",
                new ProtocolParameterOption("Binary3E", "3E帧 · 二进制"),
                new ProtocolParameterOption("Ascii3E", "3E帧 · ASCII"),
                new ProtocolParameterOption("Binary1E", "A兼容1E帧 · 二进制（仅TCP）"),
                new ProtocolParameterOption("Ascii1E", "A兼容1E帧 · ASCII（仅TCP）")),
            Number("networkNumber", "网络号", 0, 0, 255),
            Number("plcNumber", "PLC号", 255, 0, 255),
            Number("networkStationNumber", "网络站号", 0, 0, 255),
            Number("targetIoStation", "目标IO号", 1_023, 0, 65_535)),
        Descriptor(ProtocolKind.OpcUa, "OPC UA", 4840,
            Text("opcUaUsername", "用户名", "", "留空表示匿名认证"),
            Password("opcUaPassword", "密码"),
            Switch("opcUaUseSecurity", "启用安全端点", false),
            Switch("opcUaAutoAcceptUntrusted", "自动信任未受信证书", false, "生产环境建议关闭")),
        Descriptor(ProtocolKind.PanasonicMewtocol, "松下 Mewtocol TCP", 9094,
            Number("station", "站号", 1, 0, 255)),
        Descriptor(ProtocolKind.OmronFins, "欧姆龙 FINS TCP", 9600,
            Number("finsDestinationNetwork", "目标网络号 DNA", 0, 0, 127),
            Number("finsDestinationNode", "目标节点 DA1", 0, 0, 254, "0表示连接时自动获取"),
            Number("finsDestinationUnit", "目标单元 DA2", 0, 0, 255),
            Number("finsSourceNetwork", "源网络号 SNA", 0, 0, 127),
            Number("finsSourceNode", "源节点 SA1", 0, 0, 254, "0表示连接时自动协商"),
            Number("finsSourceUnit", "源单元 SA2", 0, 0, 255),
            Number("finsReadSplits", "批量分段长度", 500, 1, 999)),
        Descriptor(ProtocolKind.OmronCip, "欧姆龙 CIP", 44818,
            Number("omronCipConnectionTimeoutMultiplier", "连接超时倍率", 2, 0, 10, "超时秒数约为2的倍率次方乘以8")),
        Descriptor(ProtocolKind.AllenBradleyEtherNetIp, "AB (Allen-Bradley) EtherNet/IP", 44818,
            Number("slot", "CPU槽号", 0, 0, 255),
            Text("cipRoutePath", "CIP路由", "", "例如1.0或1.15.2.18.1.12，留空使用槽号"),
            Switch("cipReadArrayUseSegment", "数组分段读取", true)),
        Descriptor(ProtocolKind.BeckhoffAds, "倍福 Beckhoff ADS", 851,
            Switch("beckhoffUseAutoAmsNetId", "自动获取AMS Net ID", true, "TwinCAT 3推荐开启；跨路由或特殊拓扑可关闭后手动填写"),
            Text("beckhoffTargetAmsNetId", "目标AMS Net ID", "", "例如192.168.1.20.1.1或192.168.1.20.1.1:851"),
            Text("beckhoffSenderAmsNetId", "发送方AMS Net ID", "", "通常留空使用驱动自动值；已配置静态Route时按现场填写"),
            Switch("beckhoffUseTagCache", "启用符号标签缓存", true, "提高符号变量读取效率；PLC程序下载后驱动重连会重新建立缓存")),
        Descriptor(ProtocolKind.KeyenceMc, "基恩士 MC TCP", 5000),
        Descriptor(ProtocolKind.InovanceTcp, "汇川 TCP", 502,
            Select("inovanceSeries", "PLC系列", "AM", ["AM", "H3U", "H5U", "Easy"]),
            Number("station", "站号", 1, 0, 255)),
        Descriptor(ProtocolKind.DeltaTcp, "台达 TCP", 502,
            Select("deltaSeries", "PLC系列", "Dvp", ["Dvp", "AS"]),
            Number("station", "站号", 1, 0, 255))
    ];

    private static ProtocolDescriptor Descriptor(
        ProtocolKind value,
        string label,
        int defaultPort,
        params ProtocolParameterDescriptor[] parameters) =>
        new(value, label, defaultPort, true, parameters, ProtocolPointAddressCatalog.Get(value));

    private static ProtocolParameterDescriptor Number(
        string name,
        string label,
        int defaultValue,
        int minimum,
        int maximum,
        string? help = null) => new(name, label, "number", defaultValue, minimum, maximum, null, false, help);

    private static ProtocolParameterDescriptor Text(
        string name,
        string label,
        string defaultValue,
        string? help = null) => new(name, label, "text", defaultValue, null, null, null, false, help);

    private static ProtocolParameterDescriptor Password(string name, string label) =>
        new(name, label, "password", "", null, null, null, true, null);

    private static ProtocolParameterDescriptor Switch(
        string name,
        string label,
        bool defaultValue,
        string? help = null) => new(name, label, "switch", defaultValue, null, null, null, false, help);

    private static ProtocolParameterDescriptor Select(
        string name,
        string label,
        string defaultValue,
        string[] options) => new(
            name,
            label,
            "select",
            defaultValue,
            null,
            null,
            options.Select(option => new ProtocolParameterOption(option, option)).ToArray(),
            false,
            null);

    private static ProtocolParameterDescriptor SelectOptions(
        string name,
        string label,
        string defaultValue,
        params ProtocolParameterOption[] options) => new(
            name,
            label,
            "select",
            defaultValue,
            null,
            null,
            options,
            false,
            null);
}

public sealed record ProtocolDescriptor(
    ProtocolKind Value,
    string Label,
    int DefaultPort,
    bool Available,
    IReadOnlyList<ProtocolParameterDescriptor> Parameters,
    ProtocolPointAddressGuide PointAddressGuide);

public sealed record ProtocolParameterDescriptor(
    string Name,
    string Label,
    string InputType,
    object DefaultValue,
    int? Minimum,
    int? Maximum,
    IReadOnlyList<ProtocolParameterOption>? Options,
    bool Secret,
    string? Help);

public sealed record ProtocolParameterOption(string Value, string Label);
