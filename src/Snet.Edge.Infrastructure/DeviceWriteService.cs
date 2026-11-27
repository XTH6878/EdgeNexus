using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class DeviceWriteService(
    IGatewayConfigurationStore configurationStore,
    IDeviceCommandExecutor commandExecutor,
    IAuditLogStore auditLogStore,
    ILogger<DeviceWriteService> logger) : IDeviceWriteService
{
    private static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (DateTimeOffset ExpiresAt, DeviceWriteAck Ack)> _executedCommands =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, DeviceRateWindow> _rateWindows = new();

    public async ValueTask<DeviceWriteAck> ExecuteAsync(
        DeviceWriteCommand command,
        string sourceChannel,
        CancellationToken cancellationToken)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        var commandId = string.IsNullOrWhiteSpace(command.CommandId)
            ? Guid.NewGuid().ToString("N")
            : command.CommandId.Trim();
        var deviceKey = command.DeviceId?.Trim() ?? string.Empty;
        var actor = string.IsNullOrWhiteSpace(command.Operator) ? sourceChannel : $"{sourceChannel}:{command.Operator.Trim()}";

        PruneExpiredCommands(now);

        if (!string.IsNullOrWhiteSpace(command.CommandId) &&
            _executedCommands.TryGetValue(commandId, out var cached) &&
            cached.ExpiresAt > now)
        {
            return cached.Ack with
            {
                Deduplicated = true,
                ElapsedMs = 0,
                Timestamp = now
            };
        }

        if (command.Writes is null || command.Writes.Count == 0)
        {
            return RecordAndReturn(
                new DeviceWriteAck(commandId, deviceKey, false, false, 0, now, [], "反写点位列表不能为空。"),
                actor,
                sourceChannel,
                400,
                cacheResult: false);
        }

        var snapshot = await configurationStore.GetSnapshotAsync(cancellationToken);
        if (!snapshot.Mqtt.WriteBackEnabled && string.Equals(sourceChannel, "MQTT", StringComparison.OrdinalIgnoreCase))
        {
            return RecordAndReturn(
                new DeviceWriteAck(commandId, deviceKey, false, false, 0, now, [], "网关已禁用MQTT远程反写功能。"),
                actor,
                sourceChannel,
                403,
                cacheResult: false);
        }

        var effectiveTtlMs = command.TtlMs is > 0
            ? Math.Clamp(command.TtlMs.Value, 500, 300_000)
            : Math.Clamp(snapshot.Mqtt.WriteCommandDefaultTtlMs, 500, 300_000);

        if (command.Timestamp != default)
        {
            var ageMs = (now - command.Timestamp).TotalMilliseconds;
            if (ageMs > effectiveTtlMs || ageMs < -60_000)
            {
                var expiredMessage = $"反写指令已过期 (延迟 {ageMs:F0}ms, 有效期 {effectiveTtlMs}ms)，已被安全护栏拦截。";
                var expiredResults = command.Writes
                    .Select(item => new TagWriteResult(item.Tag, TagWriteStatus.Expired, NormalizeJsonValue(item.Value), null, null, expiredMessage))
                    .ToArray();
                var expiredAck = new DeviceWriteAck(
                    commandId,
                    deviceKey,
                    false,
                    false,
                    (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                    now,
                    expiredResults,
                    expiredMessage);
                return RecordAndReturn(expiredAck, actor, sourceChannel, 408, cacheResult: true);
            }
        }

        var device = ResolveDevice(snapshot.Devices, deviceKey);
        var group = device is not null
            ? snapshot.Groups.FirstOrDefault(item => item.Id == device.GroupId)
            : null;
        if (device is null || !device.Enabled || group is null)
        {
            var offlineMsg = $"设备 {deviceKey} 不存在或未启用。";
            var offlineResults = command.Writes
                .Select(item => new TagWriteResult(item.Tag, TagWriteStatus.DeviceOffline, NormalizeJsonValue(item.Value), null, null, offlineMsg))
                .ToArray();
            var offlineAck = new DeviceWriteAck(
                commandId,
                deviceKey,
                false,
                false,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                now,
                offlineResults,
                offlineMsg);
            return RecordAndReturn(offlineAck, actor, sourceChannel, 404, cacheResult: false);
        }

        var resolvedDeviceId = string.IsNullOrWhiteSpace(device.MqttDeviceId) ? device.Name : device.MqttDeviceId;
        var maxRequestsPerSecond = Math.Clamp(snapshot.Mqtt.WriteMaxRequestsPerSecondPerDevice, 1, 1_000);
        var rateWindow = _rateWindows.GetOrAdd(device.Id, static _ => new DeviceRateWindow());
        if (!rateWindow.TryAcquire(now, maxRequestsPerSecond))
        {
            var rateMsg = $"设备 {device.Name} 反写频率超过安全阈值 ({maxRequestsPerSecond} 次/秒)，已触发流控保护。";
            var rateResults = command.Writes
                .Select(item => new TagWriteResult(item.Tag, TagWriteStatus.RateLimited, NormalizeJsonValue(item.Value), null, null, rateMsg))
                .ToArray();
            var rateAck = new DeviceWriteAck(
                commandId,
                resolvedDeviceId,
                false,
                false,
                (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds,
                now,
                rateResults,
                rateMsg);
            return RecordAndReturn(rateAck, actor, sourceChannel, 429, cacheResult: false);
        }

        var deviceTags = snapshot.Tags
            .Where(tag => tag.DeviceId == device.Id)
            .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);

        var results = new List<TagWriteResult>(command.Writes.Count);
        foreach (var writeItem in command.Writes)
        {
            var requestedValue = NormalizeJsonValue(writeItem.Value);
            if (string.IsNullOrWhiteSpace(writeItem.Tag) || !deviceTags.TryGetValue(writeItem.Tag.Trim(), out var tag) || !tag.Enabled)
            {
                results.Add(new TagWriteResult(
                    writeItem.Tag ?? string.Empty,
                    TagWriteStatus.TagNotFound,
                    requestedValue,
                    null,
                    null,
                    $"点位 {writeItem.Tag} 不存在或未启用。"));
                continue;
            }

            if (!tag.Writable)
            {
                results.Add(new TagWriteResult(
                    tag.Name,
                    TagWriteStatus.RejectedReadOnly,
                    requestedValue,
                    null,
                    null,
                    $"点位 {tag.Name} 为只读点位 (Writable=false)，禁止反写。"));
                continue;
            }

            if (device.Protocol == ProtocolKind.ModbusTcp && IsReadOnlyModbusTag(tag))
            {
                results.Add(new TagWriteResult(
                    tag.Name,
                    TagWriteStatus.RejectedReadOnly,
                    requestedValue,
                    null,
                    null,
                    $"Modbus点位 {tag.Name} 为只读功能码 (02/04)，禁止反写。"));
                continue;
            }

            var preparation = PrepareRawWriteValue(tag, requestedValue);
            if (preparation.Status != TagWriteStatus.Success || preparation.RawValue is null)
            {
                results.Add(new TagWriteResult(
                    tag.Name,
                    preparation.Status,
                    requestedValue,
                    null,
                    null,
                    preparation.Error));
                continue;
            }

            try
            {
                var execution = await commandExecutor.ExecuteTagWriteAsync(
                    group,
                    device,
                    tag,
                    preparation.RawValue,
                    cancellationToken);

                if (!execution.IsSuccess)
                {
                    results.Add(new TagWriteResult(
                        tag.Name,
                        TagWriteStatus.ProtocolError,
                        preparation.NormalizedRequestedValue,
                        execution.WrittenRawValue,
                        execution.ReadBackEngineeringValue,
                        execution.Error));
                    continue;
                }

                if (!VerifyReadBackValue(tag, preparation.NormalizedRequestedValue, execution.ReadBackEngineeringValue))
                {
                    results.Add(new TagWriteResult(
                        tag.Name,
                        TagWriteStatus.ReadBackMismatch,
                        preparation.NormalizedRequestedValue,
                        execution.WrittenRawValue,
                        execution.ReadBackEngineeringValue,
                        $"点位 {tag.Name} 写后回读校验不一致：期望 {preparation.NormalizedRequestedValue}，实际回读 {execution.ReadBackEngineeringValue}。"));
                    continue;
                }

                results.Add(new TagWriteResult(
                    tag.Name,
                    TagWriteStatus.Success,
                    preparation.NormalizedRequestedValue,
                    execution.WrittenRawValue,
                    execution.ReadBackEngineeringValue));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "设备 {DeviceName} 点位 {TagName} 反写失败: {Message}",
                    device.Name,
                    tag.Name,
                    exception.Message);
                results.Add(new TagWriteResult(
                    tag.Name,
                    TagWriteStatus.ProtocolError,
                    preparation.NormalizedRequestedValue,
                    preparation.RawValue,
                    null,
                    exception.Message));
            }
        }

        var allSucceeded = results.Count > 0 && results.All(result => result.Status == TagWriteStatus.Success);
        var elapsedMs = (long)Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;
        var summaryError = allSucceeded
            ? null
            : string.Join("; ", results.Where(r => r.Error is not null).Select(r => r.Error));
        var ack = new DeviceWriteAck(
            commandId,
            resolvedDeviceId,
            allSucceeded,
            false,
            elapsedMs,
            DateTimeOffset.UtcNow,
            results,
            summaryError);

        return RecordAndReturn(ack, actor, sourceChannel, allSucceeded ? 200 : 422, cacheResult: true);
    }

    private DeviceWriteAck RecordAndReturn(
        DeviceWriteAck ack,
        string actor,
        string sourceChannel,
        int statusCode,
        bool cacheResult)
    {
        if (cacheResult && !string.IsNullOrWhiteSpace(ack.CommandId))
        {
            _executedCommands[ack.CommandId] = (DateTimeOffset.UtcNow.Add(DeduplicationWindow), ack);
        }

        var tagNames = ack.Results.Count > 0
            ? string.Join(",", ack.Results.Select(r => r.Tag))
            : "empty";
        auditLogStore.TryWrite(new AuditEvent(
            Guid.NewGuid(),
            ack.CommandId,
            "WRITE",
            $"/write/{sourceChannel.ToLowerInvariant()}/{ack.DeviceId}/{tagNames}",
            statusCode,
            actor,
            sourceChannel,
            ack.ElapsedMs,
            ack.Timestamp));
        return ack;
    }

    private void PruneExpiredCommands(DateTimeOffset now)
    {
        if (_executedCommands.Count < 128)
        {
            return;
        }

        foreach (var pair in _executedCommands)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _executedCommands.TryRemove(pair.Key, out _);
            }
        }
    }

    private static DeviceDefinition? ResolveDevice(IReadOnlyList<DeviceDefinition> devices, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(deviceKey))
        {
            return null;
        }

        var byExternalId = devices.FirstOrDefault(device =>
            !string.IsNullOrWhiteSpace(device.MqttDeviceId) &&
            string.Equals(device.MqttDeviceId, deviceKey, StringComparison.OrdinalIgnoreCase));
        if (byExternalId is not null)
        {
            return byExternalId;
        }

        var byName = devices.FirstOrDefault(device =>
            string.Equals(device.Name, deviceKey, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            return byName;
        }

        return Guid.TryParse(deviceKey, out var guid)
            ? devices.FirstOrDefault(device => device.Id == guid)
            : null;
    }

    private static bool IsReadOnlyModbusTag(TagDefinition tag)
    {
        var hasLegacy = ModbusTagAddress.TryParseLegacy(tag.Address, out var legacyCode, out _);
        var fc = tag.ModbusFunctionCode ?? (hasLegacy ? legacyCode : null);
        return fc is ModbusFunctionCode.ReadDiscreteInputs or ModbusFunctionCode.ReadInputRegisters;
    }

    private static (TagWriteStatus Status, object? NormalizedRequestedValue, object? RawValue, string? Error) PrepareRawWriteValue(
        TagDefinition tag,
        object? requestedValue)
    {
        if (requestedValue is null)
        {
            return (TagWriteStatus.InvalidValue, null, null, $"点位 {tag.Name} 的写入值不能为空。");
        }

        if (tag.DataType == TagDataType.Bool)
        {
            if (!TryParseBool(requestedValue, out var boolVal))
            {
                return (TagWriteStatus.InvalidValue, requestedValue, null, $"点位 {tag.Name} 需要布尔值 (true/false 或 1/0)。");
            }

            return (TagWriteStatus.Success, boolVal, boolVal, null);
        }

        if (tag.DataType == TagDataType.String)
        {
            var strVal = Convert.ToString(requestedValue, CultureInfo.InvariantCulture) ?? string.Empty;
            if (strVal.Length > tag.StringLength)
            {
                return (TagWriteStatus.OutOfRange, strVal, null, $"点位 {tag.Name} 字符串长度 ({strVal.Length}) 超过最大允许长度 ({tag.StringLength})。");
            }

            return (TagWriteStatus.Success, strVal, strVal, null);
        }

        if (!TryParseDecimal(requestedValue, out var engineeringValue))
        {
            return (TagWriteStatus.InvalidValue, requestedValue, null, $"点位 {tag.Name} 的写入值 '{requestedValue}' 不是有效数值。");
        }

        if (tag.MinWriteValue.HasValue && engineeringValue < tag.MinWriteValue.Value)
        {
            return (
                TagWriteStatus.OutOfRange,
                engineeringValue,
                null,
                $"点位 {tag.Name} 写入值 {engineeringValue} 小于安全下限 {tag.MinWriteValue.Value}。");
        }

        if (tag.MaxWriteValue.HasValue && engineeringValue > tag.MaxWriteValue.Value)
        {
            return (
                TagWriteStatus.OutOfRange,
                engineeringValue,
                null,
                $"点位 {tag.Name} 写入值 {engineeringValue} 大于安全上限 {tag.MaxWriteValue.Value}。");
        }

        var rawDecimal = InverseScale(engineeringValue, tag);
        try
        {
            object rawTyped = tag.DataType switch
            {
                TagDataType.Byte => (object)checked((byte)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.Int16 => (object)checked((short)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.UInt16 or TagDataType.Bcd => (object)checked((ushort)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.Int32 => (object)checked((int)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.UInt32 => (object)checked((uint)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.Int64 => (object)checked((long)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.UInt64 => (object)checked((ulong)Math.Round(rawDecimal, 0, MidpointRounding.AwayFromZero)),
                TagDataType.Float32 => (object)(float)rawDecimal,
                TagDataType.Float64 => (object)(double)rawDecimal,
                _ => throw new NotSupportedException($"不支持反写数据类型 {tag.DataType}。")
            };
            return (TagWriteStatus.Success, engineeringValue, rawTyped, null);
        }
        catch (OverflowException)
        {
            return (
                TagWriteStatus.OutOfRange,
                engineeringValue,
                null,
                $"点位 {tag.Name} 逆向换算后的寄存器原始值 ({rawDecimal}) 超出 {tag.DataType} 类型范围。");
        }
    }

    private static decimal InverseScale(decimal engineeringValue, TagDefinition tag)
    {
        if (tag.DataType == TagDataType.Bcd)
        {
            return engineeringValue;
        }

        return tag.Operator switch
        {
            "+" => engineeringValue - tag.Operand,
            "-" => engineeringValue + tag.Operand,
            "*" when tag.Operand != 0 => engineeringValue / tag.Operand,
            "/" when tag.Operand != 0 => engineeringValue * tag.Operand,
            _ => engineeringValue
        };
    }

    private static bool VerifyReadBackValue(TagDefinition tag, object? expectedEngineering, object? actualEngineering)
    {
        if (expectedEngineering is null || actualEngineering is null)
        {
            return false;
        }

        if (tag.DataType == TagDataType.Bool)
        {
            return TryParseBool(expectedEngineering, out var b1) &&
                   TryParseBool(actualEngineering, out var b2) &&
                   b1 == b2;
        }

        if (tag.DataType == TagDataType.String)
        {
            var s1 = (Convert.ToString(expectedEngineering, CultureInfo.InvariantCulture) ?? string.Empty).TrimEnd('\0');
            var s2 = (Convert.ToString(actualEngineering, CultureInfo.InvariantCulture) ?? string.Empty).TrimEnd('\0');
            return string.Equals(s1, s2, StringComparison.Ordinal);
        }

        if (!TryParseDecimal(expectedEngineering, out var d1) || !TryParseDecimal(actualEngineering, out var d2))
        {
            return false;
        }

        var stepTolerance = tag.Operator is "*" && tag.Operand != 0 ? Math.Abs(tag.Operand) : 0.001m;
        var decimalTolerance = (decimal)Math.Pow(10, -Math.Clamp(tag.Decimals, 0, 6));
        var tolerance = Math.Max(stepTolerance, decimalTolerance);
        return Math.Abs(d1 - d2) <= tolerance;
    }

    private static object? NormalizeJsonValue(object? raw)
    {
        if (raw is not JsonElement element)
        {
            return raw;
        }

        return element.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when element.TryGetDecimal(out var dec) => dec,
            JsonValueKind.Number when element.TryGetDouble(out var dbl) => dbl,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => element.GetRawText()
        };
    }

    private static bool TryParseBool(object value, out bool result)
    {
        switch (value)
        {
            case bool b:
                result = b;
                return true;
            case sbyte or byte or short or ushort or int or uint or long or ulong or decimal or double or float:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture) != 0;
                return true;
            case string text:
                if (bool.TryParse(text.Trim(), out result))
                {
                    return true;
                }

                if (text.Trim() == "1")
                {
                    result = true;
                    return true;
                }

                if (text.Trim() == "0")
                {
                    result = false;
                    return true;
                }

                return false;
            default:
                result = false;
                return false;
        }
    }

    private static bool TryParseDecimal(object value, out decimal result)
    {
        switch (value)
        {
            case decimal dec:
                result = dec;
                return true;
            case byte or sbyte or short or ushort or int or uint or long or ulong or float or double:
                try
                {
                    result = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                    return true;
                }
                catch
                {
                    result = 0;
                    return false;
                }
            case string text:
                return decimal.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out result);
            default:
                result = 0;
                return false;
        }
    }

    private sealed class DeviceRateWindow
    {
        private readonly object _gate = new();
        private long _windowSecond;
        private int _count;

        public bool TryAcquire(DateTimeOffset now, int maxPerSecond)
        {
            var currentSecond = now.ToUnixTimeSeconds();
            lock (_gate)
            {
                if (_windowSecond != currentSecond)
                {
                    _windowSecond = currentSecond;
                    _count = 1;
                    return true;
                }

                if (_count >= maxPerSecond)
                {
                    return false;
                }

                _count++;
                return true;
            }
        }
    }
}
