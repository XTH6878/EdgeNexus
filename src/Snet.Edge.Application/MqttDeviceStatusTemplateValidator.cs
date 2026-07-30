using System.Text.RegularExpressions;
using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public static partial class MqttDeviceStatusTemplateValidator
{
    public static string? Validate(MqttTemplate template)
    {
        var tagPrefix = template.DeviceErrorTagPrefix?.Trim().TrimEnd('_') ?? string.Empty;
        if (!DeviceErrorTagPrefixRegex().IsMatch(tagPrefix) || tagPrefix.Length > 128)
        {
            return "设备通讯状态Tag前缀无效，仅支持字母、数字、#、下划线、点和短横线，且不能超过128个字符。";
        }

        return template.CommunicationFailureCode == DeviceErrorCodes.Normal
            ? "通讯失败码不能为0；0固定表示设备通讯正常。"
            : null;
    }

    [GeneratedRegex("^[#A-Za-z_][#A-Za-z0-9_.-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex DeviceErrorTagPrefixRegex();
}
