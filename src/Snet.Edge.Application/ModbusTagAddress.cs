using System.Globalization;
using Snet.Edge.Domain;

namespace Snet.Edge.Application;

public static class ModbusTagAddress
{
    public static TagDefinition Normalize(TagDefinition tag)
    {
        if (!TryParseLegacy(tag.Address, out var legacyFunctionCode, out var rawAddress))
        {
            return tag;
        }

        return tag with
        {
            Address = rawAddress,
            ModbusFunctionCode = tag.ModbusFunctionCode ?? legacyFunctionCode
        };
    }

    public static string ToHslAddress(TagDefinition tag)
    {
        if (tag.ModbusFunctionCode is not { } functionCode)
        {
            return tag.Address;
        }

        var address = TryParseLegacy(tag.Address, out _, out var legacyAddress)
            ? legacyAddress
            : tag.Address;
        return $"x={(int)functionCode};{address}";
    }

    public static bool TryParseLegacy(
        string address,
        out ModbusFunctionCode functionCode,
        out string rawAddress)
    {
        functionCode = default;
        rawAddress = address;
        if (!address.StartsWith("x=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var separatorIndex = address.IndexOf(';');
        if (separatorIndex <= 2 || separatorIndex == address.Length - 1 ||
            !int.TryParse(address.AsSpan(2, separatorIndex - 2), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            !Enum.IsDefined(typeof(ModbusFunctionCode), value))
        {
            return false;
        }

        functionCode = (ModbusFunctionCode)value;
        rawAddress = address[(separatorIndex + 1)..];
        return true;
    }
}
