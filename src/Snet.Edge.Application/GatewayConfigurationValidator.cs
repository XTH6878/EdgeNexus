using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public sealed class GatewayConfigurationValidator : IGatewayConfigurationValidator
{
    public ConfigurationValidationResult Validate(GatewaySnapshot snapshot)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var groupIds = snapshot.Groups.Select(group => group.Id).ToHashSet();
        var deviceIds = snapshot.Devices.Select(device => device.Id).ToHashSet();

        AddDuplicates(snapshot.Groups.Select(group => group.Id), "分组ID", errors);
        AddDuplicates(snapshot.Groups.Select(group => group.Name), "分组名称", errors, StringComparer.OrdinalIgnoreCase);
        AddDuplicates(snapshot.Groups.Select(group => group.Topic), "MQTT Topic", errors, StringComparer.Ordinal);
        AddDuplicates(snapshot.Devices.Select(device => device.Id), "设备ID", errors);

        foreach (var group in snapshot.Groups)
        {
            if (string.IsNullOrWhiteSpace(group.Name) || string.IsNullOrWhiteSpace(group.Topic))
            {
                errors.Add($"分组 {group.Id} 的名称和Topic不能为空。");
            }

            var topicError = MqttTopicTemplateValidator.Validate(group.Topic);
            if (topicError is not null)
            {
                errors.Add($"分组 {group.Name}: {topicError}");
            }

            if (group.Line?.Length > 128)
            {
                errors.Add($"分组 {group.Name} 的产线标识不能超过128个字符。");
            }

            if (group.ScanCycleMs is < GroupScheduleLimits.MinimumScanCycleMs or > GroupScheduleLimits.MaximumScanCycleMs ||
                group.UploadCycleSeconds is < GroupScheduleLimits.MinimumUploadCycleSeconds or > GroupScheduleLimits.MaximumUploadCycleSeconds)
            {
                errors.Add($"分组 {group.Name} 的采集或上传周期无效。");
            }
        }

        foreach (var device in snapshot.Devices)
        {
            if (!groupIds.Contains(device.GroupId))
            {
                errors.Add($"设备 {device.Name} 引用了不存在的分组。");
            }

            if (string.IsNullOrWhiteSpace(device.Name) || string.IsNullOrWhiteSpace(device.Host) || device.Port is < 1 or > 65_535)
            {
                errors.Add($"设备 {device.Id} 的名称、主机或端口无效。");
            }

            if (device.MqttDeviceId is { Length: > 128 } || device.MqttDeviceId?.Contains(':') == true)
            {
                errors.Add($"设备 {device.Name} 的MQTT设备ID不能超过128个字符或包含冒号。");
            }

            foreach (var protocolError in DeviceProtocolValidator.Validate(device))
            {
                errors.Add(protocolError);
            }
        }

        foreach (var tagGroup in snapshot.Tags.GroupBy(tag => tag.DeviceId))
        {
            if (!deviceIds.Contains(tagGroup.Key))
            {
                errors.Add($"设备 {tagGroup.Key} 的点位引用无效。");
                continue;
            }

            AddDuplicates(tagGroup.Select(tag => tag.Name), $"设备 {tagGroup.Key} 点位名称", errors, StringComparer.Ordinal);
            var device = snapshot.Devices.First(item => item.Id == tagGroup.Key);
            foreach (var tag in tagGroup)
            {
                if (string.IsNullOrWhiteSpace(tag.Name) || string.IsNullOrWhiteSpace(tag.Address))
                {
                    errors.Add($"点位 {tag.Id} 的名称和地址不能为空。");
                }

                if (tag.MinWriteValue.HasValue && tag.MaxWriteValue.HasValue && tag.MinWriteValue.Value > tag.MaxWriteValue.Value)
                {
                    errors.Add($"点位 {tag.Name} 的反写最小值不能大于最大值。");
                }

                foreach (var protocolError in TagProtocolValidator.Validate(device, tag))
                {
                    errors.Add(protocolError);
                }
            }
        }

        if (snapshot.Mqtt.WriteBackEnabled)
        {
            var cmdError = MqttTopicTemplateValidator.Validate(snapshot.Mqtt.WriteCommandTopicTemplate);
            if (cmdError is not null)
            {
                errors.Add($"MQTT反写命令Topic: {cmdError}");
            }

            var ackError = MqttTopicTemplateValidator.Validate(snapshot.Mqtt.WriteAckTopicTemplate);
            if (ackError is not null)
            {
                errors.Add($"MQTT反写回执Topic: {ackError}");
            }

            if (string.Equals(snapshot.Mqtt.WriteCommandTopicTemplate, snapshot.Mqtt.WriteAckTopicTemplate, StringComparison.Ordinal))
            {
                errors.Add("MQTT反写命令Topic与回执Topic不能相同。");
            }

            if (snapshot.Groups.Any(group => string.Equals(group.Topic, snapshot.Mqtt.WriteCommandTopicTemplate, StringComparison.Ordinal)))
            {
                errors.Add("MQTT反写命令Topic不能与采集上报Topic相同，以防止读写回环。");
            }

            if (snapshot.Mqtt.WriteCommandDefaultTtlMs is < 500 or > 300_000)
            {
                errors.Add("MQTT反写命令默认有效期(TTL)必须在 500ms 到 300000ms 之间。");
            }

            if (snapshot.Mqtt.WriteMaxRequestsPerSecondPerDevice is < 1 or > 1_000)
            {
                errors.Add("单设备每秒最大反写请求限制必须在 1 到 1000 之间。");
            }
        }

        if (snapshot.Devices.Count > 1_000)
        {
            warnings.Add("设备数量超过1000台，发布前应执行现场容量评估和分片规划。");
        }

        if (snapshot.Tags.Count > 100_000)
        {
            warnings.Add("点位数量超过10万，建议启用多实例采集分片。");
        }

        return new ConfigurationValidationResult(errors, warnings);
    }

    private static void AddDuplicates<T>(
        IEnumerable<T> values,
        string field,
        ICollection<string> errors,
        IEqualityComparer<T>? comparer = null)
    {
        foreach (var duplicate in values.GroupBy(value => value, comparer).Where(group => group.Count() > 1))
        {
            errors.Add($"{field}重复: {duplicate.Key}");
        }
    }
}
