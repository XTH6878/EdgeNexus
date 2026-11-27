using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class AcquisitionWorker(
    IGatewayConfigurationStore configurationStore,
    IDeviceDriverFactory driverFactory,
    ITelemetryBuffer telemetryBuffer,
    IRuntimeStatusStore statusStore,
    IAcquisitionControl control,
    IResourceGuard resourceGuard,
    IGatewayMetrics metrics,
    AcquisitionStabilityOptions stabilityOptions,
    ILogger<AcquisitionWorker> logger,
    INetworkConfigurationService? networkConfiguration = null) : BackgroundService, IDeviceCommandExecutor
{
    private readonly ConcurrentDictionary<Guid, DeviceLoop> _deviceLoops = new();
    private readonly ConcurrentDictionary<Guid, long> _lastTagErrorLogs = new();

    public async ValueTask<DriverTagWriteExecution> ExecuteTagWriteAsync(
        MqttGroup group,
        DeviceDefinition device,
        TagDefinition tag,
        object rawValue,
        CancellationToken cancellationToken)
    {
        if (_deviceLoops.TryGetValue(device.Id, out var loop) && loop.ActiveDriver is { } activeDriver)
        {
            return await WriteAndVerifyOnDriverAsync(activeDriver, group, device, tag, rawValue, cancellationToken);
        }

        await using var transientDriver = driverFactory.Create(device, [tag]);
        await transientDriver.ConnectAsync(cancellationToken);
        try
        {
            return await WriteAndVerifyOnDriverAsync(transientDriver, group, device, tag, rawValue, cancellationToken);
        }
        finally
        {
            try
            {
                await transientDriver.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "设备 {DeviceName} 临时反写连接释放异常", device.Name);
            }
        }
    }

    private async ValueTask<DriverTagWriteExecution> WriteAndVerifyOnDriverAsync(
        IDeviceDriver driver,
        MqttGroup group,
        DeviceDefinition device,
        TagDefinition tag,
        object rawValue,
        CancellationToken cancellationToken)
    {
        await driver.WriteAsync(tag, rawValue, cancellationToken);
        var readBackRaw = await driver.ReadAsync(tag, cancellationToken);
        var readBackEngineering = Transform(readBackRaw, tag);
        var timestamp = DateTimeOffset.UtcNow;
        await telemetryBuffer.WriteAsync(new TagSample(
            group.Id,
            group.Topic,
            device.Id,
            device.Name,
            tag.Id,
            tag.Name,
            readBackEngineering,
            readBackRaw,
            DataQuality.Good,
            timestamp), cancellationToken);
        return new DriverTagWriteExecution(rawValue, readBackEngineering);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        try
        {
            await SynchronizeAsync(stoppingToken);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SynchronizeAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var loop in _deviceLoops.Values)
        {
            loop.Cancellation.Cancel();
        }

        try
        {
            await Task.WhenAll(_deviceLoops.Values.Select(loop => loop.Task).ToArray()).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        foreach (var loop in _deviceLoops.Values)
        {
            loop.Cancellation.Dispose();
        }

        _deviceLoops.Clear();
        await base.StopAsync(cancellationToken);
    }

    private async Task SynchronizeAsync(CancellationToken stoppingToken)
    {
        var snapshot = await configurationStore.GetSnapshotAsync(stoppingToken);
        var enabledDevices = snapshot.Devices.Where(device => device.Enabled).ToDictionary(device => device.Id);
        var enabledGroups = snapshot.Groups.Where(group => group.Enabled).ToDictionary(group => group.Id);
        var tagsByDevice = snapshot.Tags
            .Where(tag => tag.Enabled && enabledDevices.ContainsKey(tag.DeviceId))
            .ToLookup(tag => tag.DeviceId);
        var networkRevision = networkConfiguration?.GetRevision(NetworkUsage.PlcAcquisition) ?? 0;

        foreach (var existing in _deviceLoops.ToArray())
        {
            var hasDevice = enabledDevices.TryGetValue(existing.Key, out var current);
            var currentGroup = hasDevice
                ? enabledGroups.GetValueOrDefault(current!.GroupId)
                : null;
            TagDefinition[] currentTags = hasDevice
                ? tagsByDevice[current!.Id].ToArray()
                : [];
            if (hasDevice &&
                currentGroup is not null &&
                current == existing.Value.Device &&
                currentGroup == existing.Value.Group &&
                currentTags.SequenceEqual(existing.Value.Tags) &&
                networkRevision == existing.Value.NetworkRevision)
            {
                continue;
            }

            if (_deviceLoops.TryRemove(existing.Key, out var removed))
            {
                removed.Cancellation.Cancel();
                await AwaitLoopShutdownAsync(removed);
            }
        }

        foreach (var device in enabledDevices.Values)
        {
            if (_deviceLoops.ContainsKey(device.Id))
            {
                continue;
            }

            if (!enabledGroups.TryGetValue(device.GroupId, out var group))
            {
                continue;
            }

            var tags = tagsByDevice[device.Id].ToArray();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var loop = new DeviceLoop(device, group, tags, networkRevision, cancellation);
            _deviceLoops[device.Id] = loop;
            loop.Task = ExecuteDeviceLoopAsync(loop, group, device, tags, cancellation.Token);
        }
    }

    private async Task ExecuteDeviceLoopAsync(
        DeviceLoop loop,
        MqttGroup group,
        DeviceDefinition device,
        IReadOnlyCollection<TagDefinition> tags,
        CancellationToken cancellationToken)
    {
        var reconnectBackoff = new ReconnectBackoff(stabilityOptions);
        var stabilityTracker = new DeviceCommunicationStabilityTracker(stabilityOptions);
        string? reconnectError = null;
        var reconnectFaultKind = DeviceFaultKind.None;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!control.IsRunning || !resourceGuard.AllowAcquisition)
            {
                SetStatus(
                    device.Id,
                    DeviceConnectionState.Stopped,
                    resourceGuard.AllowAcquisition ? null : resourceGuard.Current.Reason);
                await Task.Delay(500, cancellationToken);
                continue;
            }

            await using var driver = driverFactory.Create(device, tags.ToArray());
            try
            {
                if (!stabilityTracker.HasConfirmedState && stabilityTracker.ConsecutiveFailures == 0)
                {
                    SetStatus(device.Id, DeviceConnectionState.Connecting);
                }
                else if (stabilityTracker.HasConfirmedState && !stabilityTracker.IsHealthy)
                {
                    SetStatus(
                        device.Id,
                        DeviceConnectionState.Reconnecting,
                        reconnectError,
                        faultKind: reconnectFaultKind);
                }

                await driver.ConnectAsync(cancellationToken);
                loop.ActiveDriver = driver;
                await ScanConnectedDeviceAsync(
                    group,
                    device,
                    tags,
                    driver,
                    stabilityTracker,
                    reconnectBackoff,
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                metrics.RecordAcquisitionFailure(TimeSpan.Zero);
                reconnectError = exception.Message;
                reconnectFaultKind = ClassifyException(exception);
                var transition = stabilityTracker.ObserveFailedRead();
                if (transition is CommunicationStabilityTransition.BecameFaulted or
                    CommunicationStabilityTransition.MaintainedFaulted)
                {
                    SetStatus(
                        device.Id,
                        DeviceConnectionState.Reconnecting,
                        reconnectError,
                        faultKind: reconnectFaultKind);
                }

                var reconnectDelay = reconnectBackoff.GetDelayAndAdvance();
                logger.LogWarning(
                    exception,
                    "设备 {DeviceName} 通信异常，连续失败 {FailureCount} 次，{DelaySeconds:F1} 秒后重连",
                    device.Name,
                    stabilityTracker.ConsecutiveFailures,
                    reconnectDelay.TotalSeconds);
                await DelayWithoutThrowingAsync(reconnectDelay, cancellationToken);
            }
            finally
            {
                loop.ActiveDriver = null;
                try
                {
                    await driver.DisconnectAsync(CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogDebug(exception, "设备 {DeviceName} 连接释放异常", device.Name);
                }
            }
        }

        SetStatus(device.Id, DeviceConnectionState.Stopped);
    }

    private async Task ScanConnectedDeviceAsync(
        MqttGroup group,
        DeviceDefinition device,
        IReadOnlyCollection<TagDefinition> tags,
        IDeviceDriver driver,
        DeviceCommunicationStabilityTracker stabilityTracker,
        ReconnectBackoff reconnectBackoff,
        CancellationToken cancellationToken)
    {
        var scanCycle = TimeSpan.FromMilliseconds(Math.Clamp(
            group.ScanCycleMs,
            GroupScheduleLimits.MinimumScanCycleMs,
            GroupScheduleLimits.MaximumScanCycleMs));
        var nextScanAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var reconnectBeforeScan = false;
        while (true)
        {
            if (reconnectBeforeScan)
            {
                await ReconnectDriverAsync(device, driver, cancellationToken);
            }

            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            IReadOnlyDictionary<Guid, DeviceTagReadResult> readResults;
            try
            {
                readResults = await driver.ReadManyAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await WriteDeviceFailureSamplesAsync(
                    group,
                    device,
                    tags,
                    exception,
                    cancellationToken);
                throw;
            }

            var timestamp = DateTimeOffset.UtcNow;
            var successfulTags = 0;
            var failedTags = 0;
            var faultKind = DeviceFaultKind.None;
            foreach (var tag in tags)
            {
                if (!readResults.TryGetValue(tag.Id, out var readResult))
                {
                    readResult = DeviceTagReadResult.ConfigurationFailure("通信驱动未返回该点位的读取结果。");
                }

                if (!readResult.IsSuccess)
                {
                    failedTags++;
                    faultKind = SelectFault(
                        faultKind,
                        readResult.Quality == DataQuality.Timeout ? DeviceFaultKind.Timeout : DeviceFaultKind.Read);
                    await WriteFailedSampleAsync(group, device, tag, readResult.Quality, timestamp, readResult.Error, cancellationToken);
                    LogTagFailure(device, tag, readResult.Error ?? "未知点位读取错误");
                    continue;
                }

                try
                {
                    var rawValue = readResult.Value;
                    var value = Transform(rawValue, tag);
                    await telemetryBuffer.WriteAsync(new TagSample(
                        group.Id,
                        group.Topic,
                        device.Id,
                        device.Name,
                        tag.Id,
                        tag.Name,
                        value,
                        rawValue,
                        DataQuality.Good,
                        timestamp), cancellationToken);
                    successfulTags++;
                    _lastTagErrorLogs.TryRemove(tag.Id, out _);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    failedTags++;
                    faultKind = SelectFault(faultKind, DeviceFaultKind.Parse);
                    await WriteFailedSampleAsync(
                        group,
                        device,
                        tag,
                        DataQuality.ConfigurationError,
                        timestamp,
                        exception.Message,
                        cancellationToken);
                    LogTagFailure(device, tag, exception.Message);
                }
            }

            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt);
            if (successfulTags > 0)
            {
                reconnectBackoff.ResetAfterSuccessfulRead();
                var transition = stabilityTracker.ObserveSuccessfulRead();
                if (transition is CommunicationStabilityTransition.BecameHealthy or
                    CommunicationStabilityTransition.MaintainedHealthy)
                {
                    if (failedTags == 0)
                    {
                        SetStatus(device.Id, DeviceConnectionState.Online, lastSuccessAt: timestamp);
                    }
                    else
                    {
                        SetStatus(
                            device.Id,
                            DeviceConnectionState.Degraded,
                            $"{failedTags} 个点位读取异常，{successfulTags} 个点位正常。",
                            timestamp,
                            faultKind);
                    }
                }
            }
            else
            {
                var transition = stabilityTracker.ObserveFailedRead();
                if (transition is CommunicationStabilityTransition.BecameFaulted or
                    CommunicationStabilityTransition.MaintainedFaulted)
                {
                    SetStatus(
                        device.Id,
                        DeviceConnectionState.Degraded,
                        $"全部 {failedTags} 个点位连续读取失败 {stabilityTracker.ConsecutiveFailures} 次。",
                        faultKind: faultKind);
                }

                metrics.RecordAcquisitionFailure(elapsed);
            }

            metrics.RecordAcquisitionSuccess(successfulTags, elapsed);

            nextScanAt += (long)(scanCycle.TotalSeconds * System.Diagnostics.Stopwatch.Frequency);
            if (nextScanAt < System.Diagnostics.Stopwatch.GetTimestamp())
            {
                nextScanAt = System.Diagnostics.Stopwatch.GetTimestamp();
            }

            reconnectBeforeScan = await WaitForNextScanAsync(
                group,
                device,
                tags,
                driver,
                scanCycle,
                nextScanAt,
                reconnectBeforeScan,
                cancellationToken);
        }
    }

    private async Task<bool> WaitForNextScanAsync(
        MqttGroup group,
        DeviceDefinition device,
        IReadOnlyCollection<TagDefinition> tags,
        IDeviceDriver driver,
        TimeSpan scanCycle,
        long nextScanAt,
        bool reconnectBeforeScan,
        CancellationToken cancellationToken)
    {
        var heartbeatInterval = TimeSpan.FromSeconds(Math.Clamp(
            stabilityOptions.HeartbeatIntervalSeconds,
            1,
            60));
        var heartbeatEnabled = !reconnectBeforeScan &&
                               scanCycle > heartbeatInterval &&
                               tags.Count > 0 &&
                               (device.Protocol != ProtocolKind.MitsubishiMc ||
                                device.MitsubishiTransport != MitsubishiMcTransport.Udp);
        while (true)
        {
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now >= nextScanAt)
            {
                return reconnectBeforeScan;
            }

            var remaining = TimeSpan.FromSeconds(
                (nextScanAt - now) / (double)System.Diagnostics.Stopwatch.Frequency);
            var delay = heartbeatEnabled && remaining > heartbeatInterval
                ? heartbeatInterval
                : remaining;
            await Task.Delay(delay, cancellationToken);
            if (!heartbeatEnabled || System.Diagnostics.Stopwatch.GetTimestamp() >= nextScanAt)
            {
                continue;
            }

            try
            {
                await ProbeConnectionAsync(tags, driver, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var failureException = exception;
                try
                {
                    await ReconnectDriverAsync(device, driver, cancellationToken);
                    if (await ProbeConnectionAsync(tags, driver, cancellationToken))
                    {
                        reconnectBeforeScan = true;
                        heartbeatEnabled = false;
                        logger.LogInformation(
                            "设备 {DeviceName} 长连接保活失败但重新建链读取成功，已切换为按扫描周期重建连接",
                            device.Name);
                        continue;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception reconnectException)
                {
                    failureException = reconnectException;
                }

                await WriteDeviceFailureSamplesAsync(
                    group,
                    device,
                    tags,
                    failureException,
                    cancellationToken);
                throw;
            }
        }
    }

    private static async ValueTask<bool> ProbeConnectionAsync(
        IReadOnlyCollection<TagDefinition> tags,
        IDeviceDriver driver,
        CancellationToken cancellationToken)
    {
        foreach (var tag in tags)
        {
            try
            {
                await driver.ReadAsync(tag, cancellationToken);
                return true;
            }
            catch (Exception exception) when (DeviceReadFailureClassifier.IsPointSpecific(exception))
            {
            }
        }

        return false;
    }

    private async ValueTask ReconnectDriverAsync(
        DeviceDefinition device,
        IDeviceDriver driver,
        CancellationToken cancellationToken)
    {
        try
        {
            await driver.DisconnectAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "设备 {DeviceName} 切换连接模式时释放旧连接失败", device.Name);
        }

        await driver.ConnectAsync(cancellationToken);
    }

    private async ValueTask WriteDeviceFailureSamplesAsync(
        MqttGroup group,
        DeviceDefinition device,
        IReadOnlyCollection<TagDefinition> tags,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var faultKind = ClassifyException(exception);
        var quality = faultKind == DeviceFaultKind.Timeout
            ? DataQuality.Timeout
            : DataQuality.Disconnected;
        var timestamp = DateTimeOffset.UtcNow;
        foreach (var tag in tags)
        {
            await WriteFailedSampleAsync(
                group,
                device,
                tag,
                quality,
                timestamp,
                exception.Message,
                cancellationToken);
        }
    }

    private ValueTask WriteFailedSampleAsync(
        MqttGroup group,
        DeviceDefinition device,
        TagDefinition tag,
        DataQuality quality,
        DateTimeOffset timestamp,
        string? error,
        CancellationToken cancellationToken) =>
        telemetryBuffer.WriteAsync(new TagSample(
            group.Id,
            group.Topic,
            device.Id,
            device.Name,
            tag.Id,
            tag.Name,
            null,
            null,
            quality,
            timestamp,
            error), cancellationToken);

    private void LogTagFailure(DeviceDefinition device, TagDefinition tag, string error)
    {
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        while (true)
        {
            if (!_lastTagErrorLogs.TryGetValue(tag.Id, out var previous))
            {
                if (_lastTagErrorLogs.TryAdd(tag.Id, now))
                {
                    break;
                }

                continue;
            }

            if (System.Diagnostics.Stopwatch.GetElapsedTime(previous, now) < TimeSpan.FromMinutes(1))
            {
                return;
            }

            if (_lastTagErrorLogs.TryUpdate(tag.Id, now, previous))
            {
                break;
            }
        }

        logger.LogWarning(
            "设备 {DeviceName} 点位 {TagName}({Address}) 读取失败：{Error}。该点位已隔离，其他点位继续采集",
            device.Name,
            tag.Name,
            tag.Address,
            error);
    }

    private void SetStatus(
        Guid deviceId,
        DeviceConnectionState state,
        string? error = null,
        DateTimeOffset? lastSuccessAt = null,
        DeviceFaultKind faultKind = DeviceFaultKind.None) =>
        statusStore.Set(new DeviceRuntimeStatus(deviceId, state, DateTimeOffset.UtcNow, lastSuccessAt, error)
        {
            FaultKind = faultKind
        });

    private async Task AwaitLoopShutdownAsync(DeviceLoop loop)
    {
        try
        {
            await loop.Task;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            loop.Cancellation.Dispose();
        }
    }

    private static async Task DelayWithoutThrowingAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    internal static object? Transform(object? rawValue, TagDefinition tag)
    {
        if (rawValue is null || tag.DataType is TagDataType.Bool or TagDataType.String or TagDataType.Bcd)
        {
            return rawValue;
        }

        var value = Convert.ToDecimal(rawValue, System.Globalization.CultureInfo.InvariantCulture);
        value = tag.Operator switch
        {
            "+" => value + tag.Operand,
            "-" => value - tag.Operand,
            "*" => value * tag.Operand,
            "/" when tag.Operand != 0 => value / tag.Operand,
            "/" => value,
            _ => value
        };
        return Math.Round(value, tag.Decimals, MidpointRounding.AwayFromZero);
    }

    private static DeviceFaultKind ClassifyException(Exception exception)
    {
        if (exception is TimeoutException or TaskCanceledException ||
            exception.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("超时", StringComparison.Ordinal))
        {
            return DeviceFaultKind.Timeout;
        }

        return DeviceFaultKind.Communication;
    }

    private static DeviceFaultKind SelectFault(DeviceFaultKind current, DeviceFaultKind candidate) =>
        FaultPriority(candidate) > FaultPriority(current) ? candidate : current;

    private static int FaultPriority(DeviceFaultKind faultKind) => faultKind switch
    {
        DeviceFaultKind.Timeout => 5,
        DeviceFaultKind.Communication => 4,
        DeviceFaultKind.Parse => 3,
        DeviceFaultKind.Read => 2,
        DeviceFaultKind.Offline => 1,
        _ => 0
    };

    private sealed class DeviceLoop(
        DeviceDefinition device,
        MqttGroup group,
        IReadOnlyList<TagDefinition> tags,
        long networkRevision,
        CancellationTokenSource cancellation)
    {
        public DeviceDefinition Device { get; } = device;
        public MqttGroup Group { get; } = group;
        public IReadOnlyList<TagDefinition> Tags { get; } = tags;
        public long NetworkRevision { get; } = networkRevision;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Task { get; set; } = Task.CompletedTask;
        public volatile IDeviceDriver? ActiveDriver;
    }
}
