using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public static class DeviceProtocolValidator
{
    public static IReadOnlyList<string> Validate(DeviceDefinition device)
    {
        var errors = new List<string>();
        if (device.ConnectTimeoutMs is < 100 or > 120_000 || device.ReceiveTimeoutMs is < 100 or > 120_000)
        {
            errors.Add($"设备 {device.Name} 的连接或接收超时必须在100到120000毫秒之间。");
        }

        switch (device.Protocol)
        {
            case ProtocolKind.SiemensS7:
                AddRange(errors, device, device.Rack, 0, 7, "机架号");
                AddRange(errors, device, device.Slot, 0, 31, "槽号");
                AddRange(errors, device, device.SiemensLocalTsap, 0, 65_535, "本地TSAP");
                AddRange(errors, device, device.SiemensDestinationTsap, 0, 65_535, "目标TSAP");
                if (!Enum.IsDefined(device.SiemensConnectionType))
                {
                    errors.Add($"设备 {device.Name} 的西门子连接角色无效。");
                }
                break;
            case ProtocolKind.ModbusTcp or ProtocolKind.PanasonicMewtocol or ProtocolKind.InovanceTcp or ProtocolKind.DeltaTcp:
                AddRange(errors, device, device.Station, 0, 255, "站号");
                break;
            case ProtocolKind.MitsubishiMc:
                if (!Enum.IsDefined(device.MitsubishiFormat) || !Enum.IsDefined(device.MitsubishiTransport))
                {
                    errors.Add($"设备 {device.Name} 的三菱报文格式或传输方式无效。");
                    break;
                }

                if (device.MitsubishiFormat is MitsubishiMcFormat.Binary1E or MitsubishiMcFormat.Ascii1E)
                {
                    AddRange(errors, device, device.PlcNumber, 0, 255, "PLC编号");
                    if (device.MitsubishiTransport != MitsubishiMcTransport.Tcp)
                    {
                        errors.Add($"设备 {device.Name} 的三菱A兼容1E帧仅支持TCP传输。");
                    }

                    break;
                }
                AddRange(errors, device, device.NetworkNumber, 0, 255, "网络号");
                AddRange(errors, device, device.PlcNumber, 0, 255, "PLC号");
                AddRange(errors, device, device.NetworkStationNumber, 0, 255, "网络站号");
                AddRange(errors, device, device.TargetIoStation, 0, 65_535, "目标IO号");
                break;
            case ProtocolKind.OmronFins:
                AddRange(errors, device, device.FinsDestinationNetwork, 0, 127, "目标网络号");
                AddRange(errors, device, device.FinsDestinationNode, 0, 254, "目标节点号");
                AddRange(errors, device, device.FinsDestinationUnit, 0, 255, "目标单元号");
                AddRange(errors, device, device.FinsSourceNetwork, 0, 127, "源网络号");
                AddRange(errors, device, device.FinsSourceNode, 0, 254, "源节点号");
                AddRange(errors, device, device.FinsSourceUnit, 0, 255, "源单元号");
                AddRange(errors, device, device.FinsReadSplits, 1, 999, "批量分段长度");
                break;
            case ProtocolKind.OmronCip:
                AddRange(errors, device, device.OmronCipConnectionTimeoutMultiplier, 0, 10, "连接超时倍率");
                break;
            case ProtocolKind.AllenBradleyEtherNetIp:
                AddRange(errors, device, device.Slot, 0, 255, "槽号");
                ValidateRoutePath(errors, device);
                break;
            case ProtocolKind.BeckhoffAds:
                if (!device.BeckhoffUseAutoAmsNetId && !IsValidAmsNetId(device.BeckhoffTargetAmsNetId))
                {
                    errors.Add($"设备 {device.Name} 未启用自动AMS Net ID时，目标AMS Net ID必须为六段0到255整数，例如192.168.1.20.1.1或192.168.1.20.1.1:851。");
                }

                if (!string.IsNullOrWhiteSpace(device.BeckhoffSenderAmsNetId) && !IsValidAmsNetId(device.BeckhoffSenderAmsNetId))
                {
                    errors.Add($"设备 {device.Name} 的发送方AMS Net ID格式无效。");
                }
                break;
        }

        return errors;
    }

    private static void AddRange(
        ICollection<string> errors,
        DeviceDefinition device,
        int value,
        int minimum,
        int maximum,
        string field)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"设备 {device.Name} 的{field}必须在{minimum}到{maximum}之间。");
        }
    }

    private static void ValidateRoutePath(ICollection<string> errors, DeviceDefinition device)
    {
        if (string.IsNullOrWhiteSpace(device.CipRoutePath))
        {
            return;
        }

        var segments = device.CipRoutePath.Split('.', StringSplitOptions.TrimEntries);
        if (segments.Length == 0 || segments.Any(segment => string.IsNullOrEmpty(segment) || !byte.TryParse(segment, out _)))
        {
            errors.Add($"设备 {device.Name} 的CIP路由必须是点号分隔的0到255整数，例如1.0或1.15.2.18.1.12。");
        }
    }

    private static bool IsValidAmsNetId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 2 ||
            parts[0].Split('.', StringSplitOptions.TrimEntries) is not { Length: 6 } segments ||
            segments.Any(segment => !byte.TryParse(segment, out _)))
        {
            return false;
        }

        return parts.Length == 1 || ushort.TryParse(parts[1], out var port) && port > 0;
    }
}
