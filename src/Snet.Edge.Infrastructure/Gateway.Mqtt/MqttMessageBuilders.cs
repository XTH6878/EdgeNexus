using System.Globalization;
using System.Text;
using System.Text.Json;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure.GatewayMqtt;

public sealed class MqttVariableResolver
{
    public string Resolve(
        string source,
        DeviceData data,
        MqttPayloadContext context,
        TagData? tag = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var fullTag = tag is null ? string.Empty : TelemetryKey.Create(data.DeviceId, tag.TagName);
        return source
            .Replace("${deviceId}", data.DeviceId, StringComparison.Ordinal)
            .Replace("${deviceName}", data.DeviceName, StringComparison.Ordinal)
            .Replace("${deviceIp}", data.DeviceIp, StringComparison.Ordinal)
            .Replace("${protocol}", data.Protocol.ToString(), StringComparison.Ordinal)
            .Replace("${gatewayId}", context.GatewayId, StringComparison.Ordinal)
            .Replace("${gatewayVersion}", context.GatewayVersion, StringComparison.Ordinal)
            .Replace("${groupName}", context.GroupName, StringComparison.Ordinal)
            .Replace("${line}", context.Line ?? context.GroupName, StringComparison.Ordinal)
            .Replace("${timestamp}", data.Timestamp.ToString("O", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("${unixTime}", data.Timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("${tag}", fullTag, StringComparison.Ordinal)
            .Replace("${tagName}", tag?.TagName ?? string.Empty, StringComparison.Ordinal)
            .Replace("${value}", Convert.ToString(tag?.Value, CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal)
            .Replace("${quality}", ((int?)tag?.Quality)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, StringComparison.Ordinal);
    }
}

public sealed class MqttTopicBuilder(MqttVariableResolver variableResolver) : ITopicBuilder
{
    public string Build(string topicTemplate, DeviceData data, MqttPayloadContext context)
    {
        var validationError = MqttTopicTemplateValidator.Validate(topicTemplate);
        if (validationError is not null)
        {
            throw new InvalidOperationException(validationError);
        }

        var topic = variableResolver.Resolve(topicTemplate.Trim(), data, context);
        if (topic.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"MQTT Topic包含不支持或未解析的变量：{topic}");
        }

        if (topic.Contains('+') || topic.Contains('#') || topic.IndexOf('\0') >= 0)
        {
            throw new InvalidOperationException("MQTT发布Topic不能包含通配符或空字符。");
        }

        if (Encoding.UTF8.GetByteCount(topic) > ushort.MaxValue)
        {
            throw new InvalidOperationException("MQTT Topic超过协议允许的65535字节。");
        }

        return topic;
    }
}

public sealed class MqttPayloadBuilder(MqttVariableResolver variableResolver) : IMqttPayloadBuilder
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public string Build(DeviceData data, MqttTemplate template, MqttPayloadContext context)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(template);
        if (data.Tags.Count == 0)
        {
            throw new InvalidOperationException("设备数据中没有可发布点位。");
        }

        using var stream = new MemoryStream(capacity: Math.Max(256, data.Tags.Count * 80));
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            WriteDeviceAndGatewayFields(writer, data, template, context);
            WriteCustomFields(writer, data, template, context);
            if (template.PayloadStyle == MqttPayloadStyle.Standard)
            {
                WriteTimeFields(writer, data, template, false);
            }

            WriteDataArray(writer, data, template);
            if (template.PayloadStyle == MqttPayloadStyle.Advantech)
            {
                WriteTimeFields(writer, data, template, true);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteDeviceAndGatewayFields(
        Utf8JsonWriter writer,
        DeviceData data,
        MqttTemplate template,
        MqttPayloadContext context)
    {
        if (template.IncludeDeviceId)
        {
            writer.WriteString("deviceId", data.DeviceId);
        }

        if (template.IncludeDeviceName)
        {
            writer.WriteString("deviceName", data.DeviceName);
        }

        if (template.IncludeDeviceIp)
        {
            writer.WriteString("deviceIp", data.DeviceIp);
        }

        if (template.IncludeProtocol)
        {
            writer.WriteString("protocol", data.Protocol.ToString());
        }

        if (template.IncludeGatewayId)
        {
            writer.WriteString("gatewayId", context.GatewayId);
        }

        if (template.IncludeGatewayVersion)
        {
            writer.WriteString("gatewayVersion", context.GatewayVersion);
        }
    }

    private void WriteCustomFields(
        Utf8JsonWriter writer,
        DeviceData data,
        MqttTemplate template,
        MqttPayloadContext context)
    {
        var representativeTag = FindRepresentativeTag(data.Tags);
        foreach (var field in template.CustomFields)
        {
            writer.WriteString(field.Name, variableResolver.Resolve(field.Value, data, context, representativeTag));
        }
    }

    private static TagData? FindRepresentativeTag(IReadOnlyList<TagData> tags)
    {
        for (var index = 0; index < tags.Count; index++)
        {
            if (!tags[index].TagName.StartsWith('#'))
            {
                return tags[index];
            }
        }

        return tags.Count == 0 ? null : tags[0];
    }

    private static void WriteTimeFields(
        Utf8JsonWriter writer,
        DeviceData data,
        MqttTemplate template,
        bool advantech)
    {
        if (template.IncludeTimestamp)
        {
            writer.WriteString(
                advantech ? "ts" : "timestamp",
                advantech ? FormatAdvantechTimestamp(data.Timestamp) : data.Timestamp.ToString("O", CultureInfo.InvariantCulture));
        }

        if (template.IncludeUnixTime)
        {
            writer.WriteNumber("unixTime", data.Timestamp.ToUnixTimeSeconds());
        }
    }

    private static void WriteDataArray(Utf8JsonWriter writer, DeviceData data, MqttTemplate template)
    {
        writer.WriteStartArray("d");
        foreach (var tag in data.Tags)
        {
            writer.WriteStartObject();
            if (template.IncludeTag)
            {
                writer.WriteString("tag", TelemetryKey.Create(data.DeviceId, tag.TagName));
            }

            if (template.IncludeValue)
            {
                writer.WritePropertyName("value");
                if (tag.Value is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    JsonSerializer.Serialize(writer, tag.Value, tag.Value.GetType(), SerializerOptions);
                }
            }

            if (template.IncludeQuality)
            {
                writer.WriteNumber("quality", (int)tag.Quality);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static string FormatAdvantechTimestamp(DateTimeOffset value)
    {
        var timestamp = value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
        return timestamp.Remove(timestamp.Length - 3, 1);
    }
}
