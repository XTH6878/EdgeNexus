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

                foreach (var protocolError in TagProtocolValidator.Validate(device, tag))
                {
                    errors.Add(protocolError);
                }
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
