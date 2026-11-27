using System.Globalization;
using ClosedXML.Excel;
using Microsoft.VisualBasic.FileIO;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class DeviceTagFileService(IGatewayConfigurationStore store) : IDeviceTagFileService
{
    private static readonly string[] ExportHeaders =
    [
        "TagName", "Address", "DataType", "ModbusFunctionCode", "Enabled", "ByteOrder",
        "Operator", "Operand", "Decimals", "PublishOnChange", "Deadband", "StringLength", "Remark"
    ];

    private static readonly HashSet<string> RequiredHeaders = new(StringComparer.Ordinal)
    {
        "tagname", "address", "datatype"
    };

    public async ValueTask<DeviceTagExportFile> ExportAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
        var device = snapshot.Devices.FirstOrDefault(item => item.Id == deviceId)
            ?? throw new KeyNotFoundException("设备不存在。");
        var tags = snapshot.Tags
            .Where(tag => tag.DeviceId == deviceId)
            .OrderBy(tag => tag.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        using var workbook = new XLWorkbook();
        var worksheet = workbook.AddWorksheet("Points");
        for (var column = 0; column < ExportHeaders.Length; column++)
        {
            worksheet.Cell(1, column + 1).Value = ExportHeaders[column];
        }

        for (var index = 0; index < tags.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = tags[index];
            var row = index + 2;
            worksheet.Cell(row, 1).Value = tag.Name;
            worksheet.Cell(row, 2).Value = tag.Address;
            worksheet.Cell(row, 3).Value = tag.DataType.ToString();
            worksheet.Cell(row, 4).Value = tag.ModbusFunctionCode is null
                ? string.Empty
                : ((int)tag.ModbusFunctionCode.Value).ToString("00", CultureInfo.InvariantCulture);
            worksheet.Cell(row, 5).Value = tag.Enabled;
            worksheet.Cell(row, 6).Value = tag.ByteOrder;
            worksheet.Cell(row, 7).Value = tag.Operator;
            worksheet.Cell(row, 8).Value = tag.Operand;
            worksheet.Cell(row, 9).Value = tag.Decimals;
            worksheet.Cell(row, 10).Value = tag.PublishOnChange;
            worksheet.Cell(row, 11).Value = tag.Deadband;
            worksheet.Cell(row, 12).Value = tag.StringLength;
            worksheet.Cell(row, 13).Value = tag.Remark ?? string.Empty;
        }

        var header = worksheet.Range(1, 1, 1, ExportHeaders.Length);
        header.Style.Font.Bold = true;
        header.Style.Font.FontColor = XLColor.White;
        header.Style.Fill.BackgroundColor = XLColor.FromHtml("#0B6F94");
        worksheet.SheetView.FreezeRows(1);
        worksheet.Columns().AdjustToContents(8, 40);
        worksheet.Column(2).Width = Math.Max(worksheet.Column(2).Width, 20);
        worksheet.Column(13).Width = Math.Max(worksheet.Column(13).Width, 24);
        worksheet.RangeUsed()?.SetAutoFilter();

        var instructions = workbook.AddWorksheet("说明");
        instructions.Cell("A1").Value = "EdgeNexus 设备点位表";
        instructions.Cell("A2").Value = "设备名称";
        instructions.Cell("B2").Value = device.Name;
        instructions.Cell("A3").Value = "通讯协议";
        instructions.Cell("B3").Value = device.Protocol.ToString();
        instructions.Cell("A5").Value = "必填列";
        instructions.Cell("B5").Value = "TagName、Address、DataType";
        instructions.Cell("A6").Value = "Modbus功能码";
        instructions.Cell("B6").Value = "01/02/03/04，仅Modbus TCP使用";
        instructions.Cell("A7").Value = "导入规则";
        instructions.Cell("B7").Value = "同名点位保留原ID；导入先进入页面，点击保存后写入草稿，发布后生效。";
        instructions.Columns().AdjustToContents(12, 80);

        await using var output = new MemoryStream();
        workbook.SaveAs(output);
        return new DeviceTagExportFile(
            output.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{SanitizeFileName(device.Name)}_Points_{DateTimeOffset.Now:yyyyMMdd_HHmmss}.xlsx");
    }

    public async ValueTask<DeviceTagImportPreview> PreviewImportAsync(
        Guid deviceId,
        Stream stream,
        string fileName,
        CancellationToken cancellationToken)
    {
        var snapshot = await store.GetDraftSnapshotAsync(cancellationToken);
        var device = snapshot.Devices.FirstOrDefault(item => item.Id == deviceId)
            ?? throw new KeyNotFoundException("设备不存在。");
        var errors = new List<PointTableImportIssue>();
        var rows = ReadRows(stream, fileName, errors, cancellationToken);
        if (rows.Count == 0 && errors.Count == 0)
        {
            errors.Add(new PointTableImportIssue(0, "file", "点位文件没有数据行。"));
        }

        var existingByName = snapshot.Tags
            .Where(tag => tag.DeviceId == deviceId)
            .ToDictionary(tag => tag.Name, StringComparer.OrdinalIgnoreCase);
        var importedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<TagDefinition>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = ParseTag(row, device, existingByName, errors);
            if (tag is null)
            {
                continue;
            }

            if (!importedNames.Add(tag.Name))
            {
                errors.Add(new PointTableImportIssue(row.Number, "tagname", $"点位名称 {tag.Name} 重复。"));
                continue;
            }

            tags.Add(tag);
        }

        return new DeviceTagImportPreview(
            errors.Count == 0,
            rows.Count,
            tags,
            errors,
            ["导入结果将替换当前页面点位列表；保存后写入配置草稿，发布后才影响运行。"]);
    }

    private static TagDefinition? ParseTag(
        ImportRow row,
        DeviceDefinition device,
        IReadOnlyDictionary<string, TagDefinition> existingByName,
        ICollection<PointTableImportIssue> errors)
    {
        var tagName = Required(row, "tagname", errors);
        var address = Required(row, "address", errors);
        if (tagName is null || address is null || !TryDataType(row, out var dataType, errors))
        {
            return null;
        }

        existingByName.TryGetValue(tagName, out var existing);
        var normalizedAddress = address;
        ModbusFunctionCode? functionCode = null;
        if (device.Protocol == ProtocolKind.ModbusTcp)
        {
            var defaultCode = existing?.ModbusFunctionCode ?? (dataType == TagDataType.Bool
                ? ModbusFunctionCode.ReadCoils
                : ModbusFunctionCode.ReadHoldingRegisters);
            if (ModbusTagAddress.TryParseLegacy(address, out var legacyCode, out var legacyAddress))
            {
                defaultCode = legacyCode;
                normalizedAddress = legacyAddress;
            }

            functionCode = OptionalFunctionCode(row, defaultCode, errors);
        }
        else if (HasValue(row, "modbusfunctioncode"))
        {
            errors.Add(new PointTableImportIssue(row.Number, "modbusfunctioncode", "只有Modbus TCP设备允许配置功能码。"));
        }

        var byteOrder = Optional(row, "byteorder", existing?.ByteOrder ?? "ABCD").ToUpperInvariant();
        if (byteOrder is not ("ABCD" or "CDAB" or "BADC" or "DCBA"))
        {
            errors.Add(new PointTableImportIssue(row.Number, "byteorder", "字节序必须为ABCD、CDAB、BADC或DCBA。"));
        }

        var operation = Optional(row, "operator", existing?.Operator ?? "*");
        if (operation is not ("+" or "-" or "*" or "/"))
        {
            errors.Add(new PointTableImportIssue(row.Number, "operator", "运算符必须为+、-、*或/。"));
        }

        var tag = new TagDefinition
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            DeviceId = device.Id,
            Name = tagName,
            Address = normalizedAddress,
            DataType = dataType,
            ModbusFunctionCode = functionCode,
            Enabled = OptionalBool(row, "enabled", existing?.Enabled ?? true, errors),
            ByteOrder = byteOrder,
            Operator = operation,
            Operand = OptionalDecimal(row, "operand", existing?.Operand ?? 1M, decimal.MinValue, decimal.MaxValue, errors),
            Decimals = OptionalInt(row, "decimals", existing?.Decimals ?? 2, 0, 4, errors),
            PublishOnChange = OptionalBool(row, "publishonchange", existing?.PublishOnChange ?? false, errors),
            Deadband = OptionalDecimal(row, "deadband", existing?.Deadband ?? 0M, 0M, decimal.MaxValue, errors),
            StringLength = OptionalInt(row, "stringlength", existing?.StringLength ?? 32, 1, 65_535, errors),
            Writable = OptionalBool(row, "writable", existing?.Writable ?? false, errors),
            MinWriteValue = existing?.MinWriteValue,
            MaxWriteValue = existing?.MaxWriteValue,
            Remark = Optional(row, "remark", existing?.Remark ?? string.Empty)
        };

        foreach (var message in TagProtocolValidator.Validate(device, tag))
        {
            errors.Add(new PointTableImportIssue(row.Number, "protocol", message));
        }

        return tag;
    }

    private static List<ImportRow> ReadRows(
        Stream stream,
        string fileName,
        ICollection<PointTableImportIssue> errors,
        CancellationToken cancellationToken)
    {
        try
        {
            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".xlsx" => ReadExcel(stream, errors, cancellationToken),
                ".csv" => ReadCsv(stream, errors, cancellationToken),
                var extension => Unsupported(extension, errors)
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            errors.Add(new PointTableImportIssue(0, "file", $"无法读取点位文件：{exception.Message}"));
            return [];
        }
    }

    private static List<ImportRow> ReadExcel(
        Stream stream,
        ICollection<PointTableImportIssue> errors,
        CancellationToken cancellationToken)
    {
        using var workbook = new XLWorkbook(stream);
        var worksheet = workbook.Worksheets.FirstOrDefault(sheet => sheet.Name.Equals("Points", StringComparison.OrdinalIgnoreCase))
            ?? workbook.Worksheets.FirstOrDefault();
        var used = worksheet?.RangeUsed();
        if (used is null)
        {
            errors.Add(new PointTableImportIssue(0, "file", "Excel点位表为空。"));
            return [];
        }

        var headers = BuildHeaders(used.FirstRow().Cells().Select(cell => cell.GetString()), errors);
        var rows = new List<ImportRow>();
        foreach (var excelRow in used.RowsUsed().Skip(1))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = new string[headers.Count];
            for (var index = 0; index < headers.Count; index++)
            {
                values[index] = excelRow.Cell(index + 1).GetFormattedString().Trim();
            }

            if (!values.All(string.IsNullOrWhiteSpace))
            {
                rows.Add(new ImportRow(excelRow.RowNumber(), ToFields(headers, values)));
            }
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
            errors.Add(new PointTableImportIssue(0, "file", "CSV点位表为空。"));
            return [];
        }

        var headers = BuildHeaders(parser.ReadFields() ?? [], errors);
        var rows = new List<ImportRow>();
        while (!parser.EndOfData)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = checked((int)parser.LineNumber);
            var values = parser.ReadFields() ?? [];
            if (!values.All(string.IsNullOrWhiteSpace))
            {
                rows.Add(new ImportRow(line, ToFields(headers, values)));
            }
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

        foreach (var duplicate in headers.Where(value => value.Length > 0).GroupBy(value => value).Where(group => group.Count() > 1))
        {
            errors.Add(new PointTableImportIssue(1, duplicate.Key, $"列 {duplicate.Key} 重复。"));
        }

        return headers;
    }

    private static IReadOnlyDictionary<string, string> ToFields(IReadOnlyList<string> headers, IReadOnlyList<string> values)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < headers.Count; index++)
        {
            if (headers[index].Length > 0)
            {
                fields[headers[index]] = index < values.Count ? values[index].Trim() : string.Empty;
            }
        }

        return fields;
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

    private static bool TryDataType(ImportRow row, out TagDataType result, ICollection<PointTableImportIssue> errors)
    {
        var value = Required(row, "datatype", errors);
        if (value is not null && Enum.TryParse<TagDataType>(value, true, out result) && Enum.IsDefined(result))
        {
            return true;
        }

        var normalized = NormalizeHeader(value ?? string.Empty);
        var alias = normalized switch
        {
            "boolean" or "布尔" => TagDataType.Bool,
            "short" => TagDataType.Int16,
            "word" => TagDataType.UInt16,
            "int" or "dint" => TagDataType.Int32,
            "dword" => TagDataType.UInt32,
            "float" or "real" or "single" => TagDataType.Float32,
            "double" or "lreal" => TagDataType.Float64,
            "字符串" => TagDataType.String,
            _ => (TagDataType?)null
        };
        if (alias is not null)
        {
            result = alias.Value;
            return true;
        }

        result = default;
        if (value is not null)
        {
            errors.Add(new PointTableImportIssue(row.Number, "datatype", $"不支持的数据类型：{value}。"));
        }

        return false;
    }

    private static ModbusFunctionCode OptionalFunctionCode(
        ImportRow row,
        ModbusFunctionCode defaultValue,
        ICollection<PointTableImportIssue> errors)
    {
        if (!row.Fields.TryGetValue("modbusfunctioncode", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        var normalized = value.Trim();
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) &&
            Enum.IsDefined(typeof(ModbusFunctionCode), numeric))
        {
            return (ModbusFunctionCode)numeric;
        }

        if (Enum.TryParse<ModbusFunctionCode>(normalized, true, out var parsed) && Enum.IsDefined(parsed))
        {
            return parsed;
        }

        errors.Add(new PointTableImportIssue(row.Number, "modbusfunctioncode", "功能码必须为01、02、03或04。"));
        return defaultValue;
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

        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= minimum && parsed <= maximum)
        {
            return parsed;
        }

        errors.Add(new PointTableImportIssue(row.Number, field, $"必须是{minimum}到{maximum}的整数。"));
        return defaultValue;
    }

    private static decimal OptionalDecimal(
        ImportRow row,
        string field,
        decimal defaultValue,
        decimal minimum,
        decimal maximum,
        ICollection<PointTableImportIssue> errors)
    {
        if (!row.Fields.TryGetValue(field, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= minimum && parsed <= maximum)
        {
            return parsed;
        }

        errors.Add(new PointTableImportIssue(row.Number, field, $"数值超出允许范围：{value}。"));
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

        return value.Trim().ToLowerInvariant() switch
        {
            "true" or "1" or "yes" or "是" => true,
            "false" or "0" or "no" or "否" => false,
            _ => InvalidBool(row, field, defaultValue, errors)
        };
    }

    private static bool InvalidBool(
        ImportRow row,
        string field,
        bool defaultValue,
        ICollection<PointTableImportIssue> errors)
    {
        errors.Add(new PointTableImportIssue(row.Number, field, "必须是true/false、1/0、yes/no或是/否。"));
        return defaultValue;
    }

    private static bool HasValue(ImportRow row, string field) =>
        row.Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value);

    private static string Optional(ImportRow row, string field, string defaultValue) =>
        row.Fields.TryGetValue(field, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : defaultValue;

    private static string NormalizeHeader(string value) => value.Trim().ToLowerInvariant()
        .Replace("_", string.Empty, StringComparison.Ordinal)
        .Replace(" ", string.Empty, StringComparison.Ordinal) switch
    {
        "点位名称" or "变量名称" or "变量名" => "tagname",
        "地址" or "点位地址" => "address",
        "数据类型" => "datatype",
        "功能码" or "modbus功能码" => "modbusfunctioncode",
        "启用" or "是否启用" => "enabled",
        "字节序" => "byteorder",
        "运算符" or "操作符" => "operator",
        "运算值" or "操作数" => "operand",
        "小数位" or "保留小数" => "decimals",
        "变化上传" => "publishonchange",
        "变化阈值" => "deadband",
        "字符串长度" => "stringlength",
        "备注" => "remark",
        var normalized => normalized
    };

    private static List<ImportRow> Unsupported(string extension, ICollection<PointTableImportIssue> errors)
    {
        errors.Add(new PointTableImportIssue(0, "file", $"不支持{extension}文件，仅支持.xlsx和.csv。"));
        return [];
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Trim().Select(character => invalid.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "Device" : sanitized;
    }

    private sealed record ImportRow(int Number, IReadOnlyDictionary<string, string> Fields);
}
