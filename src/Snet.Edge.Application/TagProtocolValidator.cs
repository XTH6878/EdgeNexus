using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public static class TagProtocolValidator
{
    public static IReadOnlyList<string> Validate(DeviceDefinition device, TagDefinition tag)
    {
        var errors = new List<string>();
        if (device.Protocol != ProtocolKind.ModbusTcp)
        {
            if (tag.ModbusFunctionCode is not null)
            {
                errors.Add($"点位 {tag.Name} 仅在 Modbus TCP 协议下允许配置功能码。");
            }

            return errors;
        }

        var hasLegacyCode = ModbusTagAddress.TryParseLegacy(tag.Address, out var legacyCode, out var rawAddress);
        var functionCode = tag.ModbusFunctionCode ?? (hasLegacyCode ? legacyCode : null);
        if (functionCode is null)
        {
            errors.Add($"Modbus 点位 {tag.Name} 必须配置功能码 01、02、03 或 04。");
            return errors;
        }

        if (tag.Address.StartsWith("x=", StringComparison.OrdinalIgnoreCase) && !hasLegacyCode)
        {
            errors.Add($"Modbus 点位 {tag.Name} 的旧式功能码地址无效。");
        }
        else if (hasLegacyCode && tag.ModbusFunctionCode is not null && legacyCode != tag.ModbusFunctionCode)
        {
            errors.Add($"Modbus 点位 {tag.Name} 的功能码与地址前缀不一致。");
        }

        if (string.IsNullOrWhiteSpace(rawAddress))
        {
            errors.Add($"Modbus 点位 {tag.Name} 的寄存器地址不能为空。");
        }

        var isBitFunction = functionCode is ModbusFunctionCode.ReadCoils or ModbusFunctionCode.ReadDiscreteInputs;
        if (isBitFunction && tag.DataType != TagDataType.Bool)
        {
            errors.Add($"Modbus 点位 {tag.Name} 使用功能码 01/02 时，数据类型必须为 Bool。");
        }
        else if (!isBitFunction && tag.DataType == TagDataType.Bool)
        {
            errors.Add($"Modbus 点位 {tag.Name} 使用功能码 03/04 时，数据类型不能为 Bool。");
        }

        if (tag.Writable && functionCode is ModbusFunctionCode.ReadDiscreteInputs or ModbusFunctionCode.ReadInputRegisters)
        {
            errors.Add($"Modbus 点位 {tag.Name} 使用只读功能码 02/04 时不允许开启反写。");
        }

        return errors;
    }
}
