namespace Snet.Edge.Application;

public static class MqttTopicTemplateValidator
{
    private static readonly HashSet<string> AllowedVariables = new(StringComparer.Ordinal)
    {
        "deviceId", "deviceName", "gatewayId", "line"
    };

    public static string? Validate(string? topicTemplate)
    {
        if (string.IsNullOrWhiteSpace(topicTemplate))
        {
            return "MQTT Topic模板不能为空。";
        }

        if (topicTemplate.Contains('+') || topicTemplate.Contains('#') || topicTemplate.IndexOf('\0') >= 0)
        {
            return "MQTT发布Topic不能包含通配符或空字符。";
        }

        var position = 0;
        while ((position = topicTemplate.IndexOf("${", position, StringComparison.Ordinal)) >= 0)
        {
            var end = topicTemplate.IndexOf('}', position + 2);
            if (end < 0)
            {
                return "MQTT Topic变量缺少右花括号。";
            }

            var variable = topicTemplate[(position + 2)..end];
            if (!AllowedVariables.Contains(variable))
            {
                return $"MQTT Topic变量 ${{{variable}}} 不受支持。";
            }

            position = end + 1;
        }

        return null;
    }
}
