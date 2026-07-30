using System.Globalization;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class PointTableImportService(
    IGatewayConfigurationStore store,
    IGatewayConfigurationValidator validator) : IPointTableImportService
{
    private static readonly string[] RequiredHeaders =
    [
        "groupname", "grouptopic", "devicename", "protocol", "host", "port", "tagname", "address", "datatype"
    ];

    public async ValueTask<PointTableImportReport> PreviewAsync(
        Stream stream,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var result = await AnalyzeAsync(stream, fileName, replaceExisting, cancellationToken);
        return result.Report;
    }

    public async ValueTask<PointTableImportReport> ImportDraftAsync(
        Stream stream,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var result = await AnalyzeAsync(stream, fileName, replaceExisting, cancellationToken);
        if (result.Report.IsValid && result.Snapshot is not null)
        {
            await store.ReplaceDraftAsync(result.Snapshot, cancellationToken);
        }

        return result.Report;
    }

    private async ValueTask<AnalysisResult> AnalyzeAsync(
        Stream stream,
        string fileName,
        bool replaceExisting,
        CancellationToken cancellationToken)
    {
        var errors = new List<PointTableImportIssue>();
        var rows = ReadRows(stream, fileName, errors, cancellationToken);
        if (errors.Count > 0)
        {
            return Invalid(rows.Count, errors);
        }

        var draft = await store.GetDraftSnapshotAsync(cancellationToken);
        var groups = replaceExisting ? [] : draft.Groups.ToList();
        var devices = replaceExisting ? [] : draft.Devices.ToList();
        var tags = replaceExisting ? [] : draft.Tags.ToList();
        var importedGroups = new Dictionary<string, MqttGroup>(StringComparer.OrdinalIgnoreCase);
        var importedDevices = new Dictionary<string, DeviceDefinition>(StringComparer.OrdinalIgnoreCase);
        var importedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParseRow(row, groups, devices, tags, importedGroups, importedDevices, importedTags, errors);
        }

        if (errors.Count > 0)
        {
            return Invalid(rows.Count, errors);
        }

        var snapshot = new GatewaySnapshot(groups, devices, tags, draft.Mqtt);
        var validation = validator.Validate(snapshot);
        errors.AddRange(validation.Errors.Select(message => new PointTableImportIssue(0, "configuration", message)));
        var report = new PointTableImportReport(
            errors.Count == 0,
            rows.Count,
            groups.Count,
            devices.Count,
            tags.Count,
            errors,
            validation.Warnings);
        return new AnalysisResult(errors.Count == 0 ? snapshot : null, report);
    }

    private static List<ImportRow> ReadRows(
        Stream stream,
        string fileName,
        ICollection<PointTableImportIssue> errors,
        CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(fileName);
        try
        {
            return extension.ToLowerInvariant() switch
            {
                ".xlsx" => ReadExcel(stream, errors, cancellationToken),
                ".csv" => ReadCsv(stream, errors, cancellationToken),
                _ => Unsupported(errors, extension)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(new PointTableImportIssue(0, "file", $"无法读取点表：{exception.Message}"));
            return [];
        }
    }

    private static List<ImportRow> ReadExcel(
        Stream stream,
        ICollection<PointTableImportIssue> errors,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault();
        if (worksheet is null)
        {
            errors.Add(new PointTableImportIssue(0, "file", "Excel 文件没有工作表。"));
            return [];
        }

        var used = worksheet.RangeUsed();
        if (used is null)
        {
            errors.Add(new PointTableImportIssue(0, "file", "Excel 点表为空。"));
            return [];
        }

        var headerRow = used.FirstRow();
        var headers = BuildHeaders(headerRow.Cells().Select(cell => cell.GetString()), errors);
        var rows = new List<ImportRow>();
        foreach (var excelRow in used.RowsUsed().Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new string[headers.Count];
            for (var index = 0; index < headers.Count; index++)
            {
                values[index] = excelRow.Cell(index + 1).GetFormattedString().Trim();
            }

            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(new ImportRow(excelRow.RowNumber(), ToFields(headers, values)));
        }

        return rows;
    }

    private static List<ImportRow> ReadCsv(
        Stream stream,
        ICollection<PointTableImportIssue> errors,
        CancellationToken cancellationToken)
    {
        using var parser = new TextFieldParser(stream, System.Text.Encoding.UTF8, true, true)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = true
        };
        parser.SetDelimiters(",");
        if (parser.EndOfData)
        {
            errors.Add(new PointTableImportIssue(0, "file", "CSV 点表为空。"));
            return [];
        }

        var headers = BuildHeaders(parser.ReadFields() ?? [], errors);
        var rows = new List<ImportRow>();
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = checked((int)parser.LineNumber);
            var values = parser.ReadFields() ?? [];
            if (values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            rows.Add(new ImportRow(line, ToFields(headers, values)));
        }

        return rows;
    }

    private static IReadOnlyList<string> BuildHeaders(
        IEnumerable<string> values,
        ICollection<PointTableImportIssue> errors)
    {
        var headers = values.Select(NormalizeHeader).ToArray();
        foreach (var required in RequiredHeaders)
        {
            if (!headers.Contains(required, StringComparer.Ordinal))
            {
                errors.Add(new PointTableImportIssue(1, required, $"缺少必填列 {required}。"));
            }
        }

        return headers;
    }

    private static Dictionary<string, string> ToFields(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Count; index++)
        {
            if (!string.IsNullOrEmpty(headers[index]))
            {
                fields[headers[index]] = index < values.Count ? values[index].Trim() : string.Empty;
            }
        }

        return fields;
    }

    private static void ParseRow(
        ImportRow row,
        List<MqttGroup> groups,
        List<DeviceDefinition> devices,
        List<TagDefinition> tags,
        IDictionary<string, MqttGroup> importedGroups,
        IDictionary<string, DeviceDefinition> importedDevices,
        ISet<string> importedTags,
        ICollection<PointTableImportIssue> errors)
    {
        var groupName = Required(row, "groupname", errors);
        var groupTopic = Required(row, "grouptopic", errors);
        var deviceName = Required(row, "devicename", errors);
        var host = Required(row, "host", errors);
        var tagName = Required(row, "tagname", errors);
        var address = Required(row, "address", errors);
        if (!TryEnum(row, "protocol", ParseProtocol, out ProtocolKind protocol, errors) ||
            !TryEnum(row, "datatype", ParseDataType, out TagDataType dataType, errors) ||
            !TryInt(row, "port", 1, 65_535, out var port, errors) ||
            groupName is null || groupTopic is null || deviceName is null || host is null || tagName is null || address is null)
        {
            return;
        }

        if (groupTopic.Contains('#') || groupTopic.Contains('+'))
        {
            errors.Add(new PointTableImportIssue(row.Number, "grouptopic", "发布 Topic 不能包含 + 或 # 通配符。"));
            return;
        }

        var group = groups.FirstOrDefault(item => item.Name.Equals(groupName, StringComparison.OrdinalIgnoreCase));
        if (importedGroups.TryGetValue(groupName, out var importedGroup) && !importedGroup.Topic.Equals(groupTopic, StringComparison.Ordinal))
        {
            errors.Add(new PointTableImportIssue(row.Number, "grouptopic", $"分组 {groupName} 出现了不一致的 Topic。"));
            return;
        }

        group ??= new MqttGroup { Id = Guid.NewGuid(), Name = groupName, Topic = groupTopic };
        group = group with
        {
            Topic = groupTopic,
            ScanCycleMs = OptionalInt(row, "scancyclems", group.ScanCycleMs, GroupScheduleLimits.MinimumScanCycleMs, GroupScheduleLimits.MaximumScanCycleMs, errors),
            UploadCycleSeconds = OptionalInt(row, "uploadcycleseconds", group.UploadCycleSeconds, GroupScheduleLimits.MinimumUploadCycleSeconds, GroupScheduleLimits.MaximumUploadCycleSeconds, errors)
        };
        Upsert(groups, group, item => item.Id == group.Id);
        importedGroups[groupName] = group;

        var deviceKey = $"{groupName}\u001f{deviceName}";
        var device = devices.FirstOrDefault(item => item.GroupId == group.Id && item.Name.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
        if (importedDevices.TryGetValue(deviceKey, out var importedDevice) &&
            (importedDevice.Protocol != protocol || !importedDevice.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || importedDevice.Port != port))
        {
            errors.Add(new PointTableImportIssue(row.Number, "device", $"设备 {deviceName} 的协议或连接参数不一致。"));
            return;
        }

        device ??= new DeviceDefinition
        {
            Id = Guid.NewGuid(), GroupId = group.Id, Name = deviceName, Protocol = protocol, Host = host, Port = port
        };
        device = device with
        {
            GroupId = group.Id,
            Protocol = protocol,
            Host = host,
            Port = port,
            ConnectTimeoutMs = OptionalInt(row, "connecttimeoutms", device.ConnectTimeoutMs, 100, 120_000, errors),
            ReceiveTimeoutMs = OptionalInt(row, "receivetimeoutms", device.ReceiveTimeoutMs, 100, 120_000, errors),
            Station = OptionalInt(row, "station", device.Station, 0, 255, errors),
            Rack = OptionalInt(row, "rack", device.Rack, 0, 255, errors),
            Slot = OptionalInt(row, "slot", device.Slot, 0, 255, errors),
            SiemensSeries = OptionalEnum(row, "siemensseries", device.SiemensSeries, errors),
            SiemensLocalTsap = OptionalInt(row, "siemenslocaltsap", device.SiemensLocalTsap, 0, 65_535, errors),
            SiemensDestinationTsap = OptionalInt(row, "siemensdestinationtsap", device.SiemensDestinationTsap, 0, 65_535, errors),
            MitsubishiTransport = OptionalEnum(row, "mitsubishitransport", device.MitsubishiTransport, errors),
            MitsubishiFormat = OptionalEnum(row, "mitsubishiformat", device.MitsubishiFormat, errors),
            NetworkNumber = OptionalInt(row, "networknumber", device.NetworkNumber, 0, 255, errors),
            PlcNumber = OptionalInt(row, "plcnumber", device.PlcNumber, 0, 255, errors),
            NetworkStationNumber = OptionalInt(row, "networkstationnumber", device.NetworkStationNumber, 0, 255, errors),
            TargetIoStation = OptionalInt(row, "targetiostation", device.TargetIoStation, 0, 65_535, errors),
            FinsDestinationNetwork = OptionalInt(row, "finsdestinationnetwork", device.FinsDestinationNetwork, 0, 127, errors),
            FinsDestinationNode = OptionalInt(row, "finsdestinationnode", device.FinsDestinationNode, 0, 254, errors),
            FinsDestinationUnit = OptionalInt(row, "finsdestinationunit", device.FinsDestinationUnit, 0, 255, errors),
            FinsSourceNetwork = OptionalInt(row, "finssourcenetwork", device.FinsSourceNetwork, 0, 127, errors),
            FinsSourceNode = OptionalInt(row, "finssourcenode", device.FinsSourceNode, 0, 254, errors),
            FinsSourceUnit = OptionalInt(row, "finssourceunit", device.FinsSourceUnit, 0, 255, errors),
            FinsReadSplits = OptionalInt(row, "finsreadsplits", device.FinsReadSplits, 1, 999, errors),
            OmronCipConnectionTimeoutMultiplier = OptionalInt(row, "omroncipconnectiontimeoutmultiplier", device.OmronCipConnectionTimeoutMultiplier, 0, 10, errors),
            CipRoutePath = Optional(row, "ciproutepath", device.CipRoutePath ?? string.Empty),
            CipReadArrayUseSegment = OptionalBool(row, "cipreadarrayusesegment", device.CipReadArrayUseSegment, errors),
            BeckhoffUseAutoAmsNetId = OptionalBool(row, "beckhoffuseautoamsnetid", device.BeckhoffUseAutoAmsNetId, errors),
            BeckhoffTargetAmsNetId = Optional(row, "beckhofftargetamsnetid", device.BeckhoffTargetAmsNetId ?? string.Empty),
            BeckhoffSenderAmsNetId = Optional(row, "beckhoffsenderamsnetid", device.BeckhoffSenderAmsNetId ?? string.Empty),
            BeckhoffUseTagCache = OptionalBool(row, "beckhoffusetagcache", device.BeckhoffUseTagCache, errors),
            InovanceSeries = OptionalEnum(row, "inovanceseries", device.InovanceSeries, errors),
            DeltaSeries = OptionalEnum(row, "deltaseries", device.DeltaSeries, errors),
            OpcUaUsername = Optional(row, "opcuausername", device.OpcUaUsername ?? string.Empty),
            OpcUaPassword = Optional(row, "opcuapassword", device.OpcUaPassword ?? string.Empty),
            OpcUaUseSecurity = OptionalBool(row, "opcuausesecurity", device.OpcUaUseSecurity, errors),
            OpcUaAutoAcceptUntrusted = OptionalBool(row, "opcuaautoacceptuntrusted", device.OpcUaAutoAcceptUntrusted, errors)
        };
        Upsert(devices, device, item => item.Id == device.Id);
        importedDevices[deviceKey] = device;

        var tagKey = $"{deviceKey}\u001f{tagName}";
        if (!importedTags.Add(tagKey))
        {
            errors.Add(new PointTableImportIssue(row.Number, "tagname", $"设备 {deviceName} 的点位 {tagName} 重复。"));
            return;
        }

        var tag = tags.FirstOrDefault(item => item.DeviceId == device.Id && item.Name.Equals(tagName, StringComparison.OrdinalIgnoreCase));
        var normalizedAddress = address;
        ModbusFunctionCode? modbusFunctionCode = null;
        if (protocol == ProtocolKind.ModbusTcp)
        {
            var defaultFunctionCode = tag?.ModbusFunctionCode ?? (dataType == TagDataType.Bool
                ? ModbusFunctionCode.ReadCoils
                : ModbusFunctionCode.ReadHoldingRegisters);
            if (ModbusTagAddress.TryParseLegacy(address, out var legacyFunctionCode, out var legacyAddress))
            {
                defaultFunctionCode = legacyFunctionCode;
                normalizedAddress = legacyAddress;
            }

            modbusFunctionCode = OptionalModbusFunctionCode(row, defaultFunctionCode, errors);
        }
        else if (row.Fields.TryGetValue("modbusfunctioncode", out var configuredFunctionCode) &&
                 !string.IsNullOrWhiteSpace(configuredFunctionCode))
        {
            errors.Add(new PointTableImportIssue(row.Number, "modbusfunctioncode", "只有 Modbus TCP 协议允许配置功能码。"));
        }

        tag ??= new TagDefinition { Id = Guid.NewGuid(), DeviceId = device.Id, Name = tagName, Address = normalizedAddress, DataType = dataType };
        tag = tag with
        {
            DeviceId = device.Id,
            Address = normalizedAddress,
            DataType = dataType,
            ModbusFunctionCode = modbusFunctionCode,
            ByteOrder = Optional(row, "byteorder", tag.ByteOrder).ToUpperInvariant(),
            StringLength = OptionalInt(row, "stringlength", tag.StringLength, 1, 65_535, errors),
            Remark = Optional(row, "remark", tag.Remark ?? string.Empty)
        };
        Upsert(tags, tag, item => item.Id == tag.Id);
    }

    private static string? Required(ImportRow row, string field, ICollection<PointTableImportIssue> errors)
    {
        if (row.Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            return value.Trim();
        }

        errors.Add(new PointTableImportIssue(row.Number, field, "必填值不能为空。"));
        return null;
    }

    private static bool TryInt(
        ImportRow row,
        string field,
        int minimum,
        int maximum,
        out int result,
        ICollection<PointTableImportIssue> errors)
    {
        var value = Required(row, field, errors);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) && result >= minimum && result <= maximum)
        {
            return true;
        }

        if (value is not null)
        {
            errors.Add(new PointTableImportIssue(row.Number, field, $"必须是 {minimum} 到 {maximum} 的整数。"));
        }

        return false;
    }

    private static int OptionalInt(
        ImportRow row,
        string field,
        int defaultValue,
        int minimum,
        int maximum,
        ICollection<PointTableImportIssue> errors)
    {
        if (!row.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) && result >= minimum && result <= maximum)
        {
            return result;
        }

        errors.Add(new PointTableImportIssue(row.Number, field, $"必须是 {minimum} 到 {maximum} 的整数。"));
        return defaultValue;
    }

    private static T OptionalEnum<T>(
        ImportRow row,
        string field,
        T defaultValue,
        ICollection<PointTableImportIssue> errors)
        where T : struct, Enum
    {
        if (!row.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (Enum.TryParse<T>(value, true, out var result) && Enum.IsDefined(result))
        {
            return result;
        }

        errors.Add(new PointTableImportIssue(row.Number, field, $"不支持的值：{value}。"));
        return defaultValue;
    }

    private static ModbusFunctionCode OptionalModbusFunctionCode(
        ImportRow row,
        ModbusFunctionCode defaultValue,
        ICollection<PointTableImportIssue> errors)
    {
        if (!row.Fields.TryGetValue("modbusfunctioncode", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.None, CultureInfo.InvariantCulture, out var numericValue) &&
            Enum.IsDefined(typeof(ModbusFunctionCode), numericValue))
        {
            return (ModbusFunctionCode)numericValue;
        }

        if (Enum.TryParse<ModbusFunctionCode>(normalized, true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        errors.Add(new PointTableImportIssue(row.Number, "modbusfunctioncode", "功能码必须为 01、02、03 或 04。"));
        return defaultValue;
    }

    private static bool OptionalBool(
        ImportRow row,
        string field,
        bool defaultValue,
        ICollection<PointTableImportIssue> errors)
    {
        if (!row.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim().ToLowerInvariant();
        if (normalized is "true" or "1" or "yes" or "是")
        {
            return true;
        }

        if (normalized is "false" or "0" or "no" or "否")
        {
            return false;
        }

        errors.Add(new PointTableImportIssue(row.Number, field, "必须是true/false、1/0、yes/no或是/否。"));
        return defaultValue;
    }

    private static bool TryEnum<T>(
        ImportRow row,
        string field,
        Func<string, (bool Success, T Value)> parser,
        out T result,
        ICollection<PointTableImportIssue> errors)
    {
        var value = Required(row, field, errors);
        var parsed = value is null ? default : parser(value);
        if (value is not null && parsed.Success)
        {
            result = parsed.Value;
            return true;
        }

        result = default!;
        if (value is not null)
        {
            errors.Add(new PointTableImportIssue(row.Number, field, $"不支持的值：{value}。"));
        }

        return false;
    }

    private static (bool, ProtocolKind) ParseProtocol(string value)
    {
        if (Enum.TryParse<ProtocolKind>(value, true, out var parsed))
        {
            return (true, parsed);
        }

        return NormalizeHeader(value) switch
        {
            "s7" or "siemens" or "西门子" => (true, ProtocolKind.SiemensS7),
            "modbus" or "modbustcp" => (true, ProtocolKind.ModbusTcp),
            "mitsubishi" or "三菱" or "mc" => (true, ProtocolKind.MitsubishiMc),
            "opcua" => (true, ProtocolKind.OpcUa),
            "panasonic" or "松下" => (true, ProtocolKind.PanasonicMewtocol),
            "omronfins" or "欧姆龙fins" => (true, ProtocolKind.OmronFins),
            "omroncip" or "欧姆龙cip" => (true, ProtocolKind.OmronCip),
            "ab" or "allenbradley" => (true, ProtocolKind.AllenBradleyEtherNetIp),
            "beckhoff" or "ads" or "倍福" => (true, ProtocolKind.BeckhoffAds),
            "keyence" or "基恩士" => (true, ProtocolKind.KeyenceMc),
            "inovance" or "汇川" => (true, ProtocolKind.InovanceTcp),
            "delta" or "台达" => (true, ProtocolKind.DeltaTcp),
            _ => (false, default)
        };
    }

    private static (bool, TagDataType) ParseDataType(string value)
    {
        if (Enum.TryParse<TagDataType>(value, true, out var parsed))
        {
            return (true, parsed);
        }

        return NormalizeHeader(value) switch
        {
            "boolean" or "布尔" => (true, TagDataType.Bool),
            "short" or "int16" => (true, TagDataType.Int16),
            "ushort" or "word" or "uint16" => (true, TagDataType.UInt16),
            "int" or "dint" or "int32" => (true, TagDataType.Int32),
            "uint" or "dword" or "uint32" => (true, TagDataType.UInt32),
            "long" or "lint" or "int64" => (true, TagDataType.Int64),
            "ulong" or "uint64" => (true, TagDataType.UInt64),
            "float" or "real" or "single" => (true, TagDataType.Float32),
            "double" or "lreal" => (true, TagDataType.Float64),
            "字符串" => (true, TagDataType.String),
            _ => (false, default)
        };
    }

    private static string Optional(ImportRow row, string field, string defaultValue) =>
        row.Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;

    private static string NormalizeHeader(string value) => value.Trim().ToLowerInvariant()
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal) switch
    {
        "分组名称" => "groupname",
        "mqtttopic" or "分组topic" or "主题" => "grouptopic",
        "设备名称" => "devicename",
        "协议" or "通讯协议" => "protocol",
        "ip" or "ip地址" or "主机" => "host",
        "端口" => "port",
        "站号" => "station",
        "机架" => "rack",
        "槽号" => "slot",
        "连接超时" or "连接超时ms" => "connecttimeoutms",
        "接收超时" or "接收超时ms" => "receivetimeoutms",
        "西门子系列" => "siemensseries",
        "本地tsap" => "siemenslocaltsap",
        "目标tsap" => "siemensdestinationtsap",
        "三菱报文格式" => "mitsubishiformat",
        "三菱传输方式" => "mitsubishitransport",
        "网络号" => "networknumber",
        "plc号" => "plcnumber",
        "网络站号" => "networkstationnumber",
        "目标io号" => "targetiostation",
        "fins目标网络" => "finsdestinationnetwork",
        "fins目标节点" => "finsdestinationnode",
        "fins目标单元" => "finsdestinationunit",
        "fins源网络" => "finssourcenetwork",
        "fins源节点" => "finssourcenode",
        "fins源单元" => "finssourceunit",
        "fins分段长度" => "finsreadsplits",
        "欧姆龙cip超时倍率" => "omroncipconnectiontimeoutmultiplier",
        "cip路由" => "ciproutepath",
        "cip数组分段" => "cipreadarrayusesegment",
        "倍福自动ams" => "beckhoffuseautoamsnetid",
        "倍福目标ams" => "beckhofftargetamsnetid",
        "倍福发送方ams" => "beckhoffsenderamsnetid",
        "倍福标签缓存" => "beckhoffusetagcache",
        "汇川系列" => "inovanceseries",
        "台达系列" => "deltaseries",
        "opcua用户名" => "opcuausername",
        "opcua密码" => "opcuapassword",
        "opcua安全" => "opcuausesecurity",
        "opcua自动信任" => "opcuaautoacceptuntrusted",
        "点位名称" or "变量名称" => "tagname",
        "地址" or "点位地址" => "address",
        "功能码" or "modbus功能码" => "modbusfunctioncode",
        "数据类型" => "datatype",
        "字节序" => "byteorder",
        "字符串长度" => "stringlength",
        "采集周期" or "采集周期ms" => "scancyclems",
        "上传周期" or "上传周期秒" => "uploadcycleseconds",
        "备注" => "remark",
        var normalized => normalized
    };

    private static void Upsert<T>(List<T> values, T value, Func<T, bool> predicate)
    {
        var index = values.FindIndex(item => predicate(item));
        if (index < 0)
        {
            values.Add(value);
        }
        else
        {
            values[index] = value;
        }
    }

    private static List<ImportRow> Unsupported(ICollection<PointTableImportIssue> errors, string extension)
    {
        errors.Add(new PointTableImportIssue(0, "file", $"不支持 {extension} 文件，仅支持 .xlsx 和 .csv。"));
        return [];
    }

    private static AnalysisResult Invalid(int totalRows, IReadOnlyList<PointTableImportIssue> errors) =>
        new(null, new PointTableImportReport(false, totalRows, 0, 0, 0, errors, []));

    private sealed record ImportRow(int Number, IReadOnlyDictionary<string, string> Fields);
    private sealed record AnalysisResult(GatewaySnapshot? Snapshot, PointTableImportReport Report);
}
