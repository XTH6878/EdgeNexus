using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Data.Sqlite;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure.GatewayMqtt;

public sealed partial class MqttTemplateManager(string connectionString) : IMqttTemplateManager, IAsyncDisposable
{
    public static readonly Guid StandardTemplateId = Guid.Parse("6f5c31ec-8a04-4d8a-b7b3-58b21d890001");
    public static readonly Guid AdvantechTemplateId = Guid.Parse("6f5c31ec-8a04-4d8a-b7b3-58b21d890002");

    private static readonly HashSet<string> ReservedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "deviceId", "deviceName", "deviceIp", "protocol", "gatewayId", "gatewayVersion",
        "timestamp", "unixTime", "ts", "d", "tag", "value", "quality"
    };

    private readonly ConcurrentDictionary<Guid, MqttTemplate> _templates = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS mqtt_template (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                enabled INTEGER NOT NULL,
                is_built_in INTEGER NOT NULL DEFAULT 0,
                include_device_id INTEGER NOT NULL,
                include_device_name INTEGER NOT NULL,
                include_device_ip INTEGER NOT NULL,
                include_protocol INTEGER NOT NULL,
                include_gateway_id INTEGER NOT NULL,
                include_gateway_version INTEGER NOT NULL,
                include_timestamp INTEGER NOT NULL,
                include_unix_time INTEGER NOT NULL,
                include_tag INTEGER NOT NULL,
                include_value INTEGER NOT NULL,
                include_quality INTEGER NOT NULL,
                payload_config TEXT NOT NULL,
                custom_fields TEXT NOT NULL,
                create_time_utc TEXT NOT NULL,
                update_time_utc TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS ux_mqtt_template_name ON mqtt_template(name COLLATE NOCASE);
            """,
            cancellationToken: cancellationToken));

        var now = DateTimeOffset.UtcNow;
        await MigrateLegacyBuiltInTemplatesAsync(connection, now, cancellationToken);
        await SeedAsync(connection, CreateStandardTemplate(now), cancellationToken);
        await ReloadAsync(connection, cancellationToken);
    }

    public ValueTask<IReadOnlyList<MqttTemplate>> GetAllAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<MqttTemplate> templates = _templates.Values
            .OrderByDescending(template => template.IsBuiltIn)
            .ThenBy(template => template.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return ValueTask.FromResult(templates);
    }

    public ValueTask<MqttTemplate?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id == AdvantechTemplateId)
        {
            id = StandardTemplateId;
        }

        _templates.TryGetValue(id, out var template);
        return ValueTask.FromResult(template);
    }

    public ValueTask<MqttTemplate> ResolveAsync(Guid? templateId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (templateId == AdvantechTemplateId)
        {
            templateId = StandardTemplateId;
        }

        if (templateId is not null &&
            _templates.TryGetValue(templateId.Value, out var selected) &&
            selected.Enabled)
        {
            return ValueTask.FromResult(selected);
        }

        if (_templates.TryGetValue(StandardTemplateId, out var standard) && standard.Enabled)
        {
            return ValueTask.FromResult(standard);
        }

        var fallback = _templates.Values.FirstOrDefault(template => template.Enabled)
            ?? throw new InvalidOperationException("没有可用的MQTT报文模板，请至少启用一个模板。");
        return ValueTask.FromResult(fallback);
    }

    public async ValueTask<MqttTemplate> UpsertAsync(MqttTemplate template, CancellationToken cancellationToken)
    {
        Validate(template);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            _templates.TryGetValue(template.Id, out var existing);
            var normalized = template with
            {
                Name = template.Name.Trim(),
                DeviceErrorTagPrefix = template.DeviceErrorTagPrefix.Trim().TrimEnd('_'),
                IsBuiltIn = existing?.IsBuiltIn == true || IsBuiltInId(template.Id),
                CreateTime = existing?.CreateTime ?? (template.CreateTime == default ? now : template.CreateTime),
                UpdateTime = now,
                CustomFields = template.CustomFields
                    .Select(field => field with
                    {
                        Name = field.Name.Trim(),
                        Value = field.Value.Trim()
                    })
                    .ToArray()
            };

            await using var connection = await OpenAsync(cancellationToken);
            await WriteAsync(connection, normalized, cancellationToken);
            _templates[normalized.Id] = normalized;
            return normalized;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            if (!_templates.TryGetValue(id, out var template))
            {
                return;
            }

            if (template.IsBuiltIn)
            {
                throw new InvalidOperationException("系统内置模板不能删除，可以停用或复制后修改。");
            }

            await using var connection = await OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM mqtt_template WHERE id = @Id;",
                new { Id = id.ToString("N") },
                cancellationToken: cancellationToken));
            _templates.TryRemove(id, out _);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _writeGate.Dispose();
        return ValueTask.CompletedTask;
    }

    private async ValueTask ReloadAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        var rows = await connection.QueryAsync<TemplateRow>(new CommandDefinition(
            """
            SELECT id, name, enabled, is_built_in AS IsBuiltIn,
                   include_device_id AS IncludeDeviceId, include_device_name AS IncludeDeviceName,
                   include_device_ip AS IncludeDeviceIp, include_protocol AS IncludeProtocol,
                   include_gateway_id AS IncludeGatewayId, include_gateway_version AS IncludeGatewayVersion,
                   include_timestamp AS IncludeTimestamp, include_unix_time AS IncludeUnixTime,
                   include_tag AS IncludeTag, include_value AS IncludeValue, include_quality AS IncludeQuality,
                   payload_config AS PayloadConfig, custom_fields AS CustomFields,
                   create_time_utc AS CreateTimeUtc, update_time_utc AS UpdateTimeUtc
            FROM mqtt_template;
            """,
            cancellationToken: cancellationToken));

        _templates.Clear();
        foreach (var row in rows)
        {
            var template = FromRow(row);
            _templates[template.Id] = template;
        }
    }

    private async ValueTask SeedAsync(
        SqliteConnection connection,
        MqttTemplate template,
        CancellationToken cancellationToken)
    {
        var exists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM mqtt_template WHERE id = @Id;",
            new { Id = template.Id.ToString("N") },
            cancellationToken: cancellationToken));
        if (exists == 0)
        {
            await WriteAsync(connection, template, cancellationToken);
        }
    }

    private async ValueTask MigrateLegacyBuiltInTemplatesAsync(
        SqliteConnection connection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var legacyId = AdvantechTemplateId.ToString("N");
        var defaultId = StandardTemplateId.ToString("N");
        var legacyExists = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM mqtt_template WHERE id = @LegacyId;",
            new { LegacyId = legacyId },
            cancellationToken: cancellationToken));

        if (legacyExists != 0)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                """
                DELETE FROM mqtt_template WHERE id = @DefaultId;
                UPDATE mqtt_template
                SET id = @DefaultId, name = @Name, is_built_in = 1, update_time_utc = @UpdateTimeUtc
                WHERE id = @LegacyId;
                """,
                new
                {
                    LegacyId = legacyId,
                    DefaultId = defaultId,
                    Name = "EdgeNexus默认模板",
                    UpdateTimeUtc = now.ToString("O")
                },
                cancellationToken: cancellationToken));
            return;
        }

        var defaultTemplate = CreateStandardTemplate(now);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE mqtt_template
            SET name = @Name,
                payload_config = @PayloadConfig,
                include_device_id = 0,
                include_timestamp = 1,
                include_tag = 1,
                include_value = 1,
                include_quality = 1,
                update_time_utc = @UpdateTimeUtc
            WHERE id = @DefaultId AND payload_config LIKE '%Standard%';
            """,
            new
            {
                DefaultId = defaultId,
                defaultTemplate.Name,
                PayloadConfig = JsonSerializer.Serialize(
                    new PayloadConfiguration
                    {
                        Style = defaultTemplate.PayloadStyle,
                        DeviceErrorTagPrefix = defaultTemplate.DeviceErrorTagPrefix,
                        CommunicationFailureCode = defaultTemplate.CommunicationFailureCode
                    },
                    _jsonOptions),
                UpdateTimeUtc = now.ToString("O")
            },
            cancellationToken: cancellationToken));
    }

    private ValueTask WriteAsync(
        SqliteConnection connection,
        MqttTemplate template,
        CancellationToken cancellationToken) => new(connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO mqtt_template(
                id, name, enabled, is_built_in,
                include_device_id, include_device_name, include_device_ip, include_protocol,
                include_gateway_id, include_gateway_version, include_timestamp, include_unix_time,
                include_tag, include_value, include_quality, payload_config, custom_fields,
                create_time_utc, update_time_utc)
            VALUES (
                @Id, @Name, @Enabled, @IsBuiltIn,
                @IncludeDeviceId, @IncludeDeviceName, @IncludeDeviceIp, @IncludeProtocol,
                @IncludeGatewayId, @IncludeGatewayVersion, @IncludeTimestamp, @IncludeUnixTime,
                @IncludeTag, @IncludeValue, @IncludeQuality, @PayloadConfig, @CustomFields,
                @CreateTimeUtc, @UpdateTimeUtc)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name, enabled = excluded.enabled, is_built_in = excluded.is_built_in,
                include_device_id = excluded.include_device_id, include_device_name = excluded.include_device_name,
                include_device_ip = excluded.include_device_ip, include_protocol = excluded.include_protocol,
                include_gateway_id = excluded.include_gateway_id, include_gateway_version = excluded.include_gateway_version,
                include_timestamp = excluded.include_timestamp, include_unix_time = excluded.include_unix_time,
                include_tag = excluded.include_tag, include_value = excluded.include_value,
                include_quality = excluded.include_quality, payload_config = excluded.payload_config,
                custom_fields = excluded.custom_fields, update_time_utc = excluded.update_time_utc;
            """,
            ToParameters(template),
            cancellationToken: cancellationToken)));

    private object ToParameters(MqttTemplate template) => new
    {
        Id = template.Id.ToString("N"),
        template.Name,
        Enabled = template.Enabled ? 1 : 0,
        IsBuiltIn = template.IsBuiltIn ? 1 : 0,
        IncludeDeviceId = Flag(template.IncludeDeviceId),
        IncludeDeviceName = Flag(template.IncludeDeviceName),
        IncludeDeviceIp = Flag(template.IncludeDeviceIp),
        IncludeProtocol = Flag(template.IncludeProtocol),
        IncludeGatewayId = Flag(template.IncludeGatewayId),
        IncludeGatewayVersion = Flag(template.IncludeGatewayVersion),
        IncludeTimestamp = Flag(template.IncludeTimestamp),
        IncludeUnixTime = Flag(template.IncludeUnixTime),
        IncludeTag = Flag(template.IncludeTag),
        IncludeValue = Flag(template.IncludeValue),
        IncludeQuality = Flag(template.IncludeQuality),
        PayloadConfig = JsonSerializer.Serialize(new PayloadConfiguration
        {
            Style = template.PayloadStyle,
            DeviceErrorTagPrefix = template.DeviceErrorTagPrefix,
            CommunicationFailureCode = template.CommunicationFailureCode
        }, _jsonOptions),
        CustomFields = JsonSerializer.Serialize(template.CustomFields, _jsonOptions),
        CreateTimeUtc = template.CreateTime.ToString("O"),
        UpdateTimeUtc = template.UpdateTime.ToString("O")
    };

    private MqttTemplate FromRow(TemplateRow row)
    {
        var payload = JsonSerializer.Deserialize<PayloadConfiguration>(row.PayloadConfig, _jsonOptions)
            ?? new PayloadConfiguration();
        var customFields = JsonSerializer.Deserialize<MqttCustomField[]>(row.CustomFields, _jsonOptions) ?? [];
        return new MqttTemplate
        {
            Id = Guid.ParseExact(row.Id, "N"),
            Name = row.Name,
            Enabled = row.Enabled != 0,
            IsBuiltIn = row.IsBuiltIn != 0,
            PayloadStyle = payload.Style,
            IncludeDeviceId = row.IncludeDeviceId != 0,
            IncludeDeviceName = row.IncludeDeviceName != 0,
            IncludeDeviceIp = row.IncludeDeviceIp != 0,
            IncludeProtocol = row.IncludeProtocol != 0,
            IncludeGatewayId = row.IncludeGatewayId != 0,
            IncludeGatewayVersion = row.IncludeGatewayVersion != 0,
            IncludeTimestamp = row.IncludeTimestamp != 0,
            IncludeUnixTime = row.IncludeUnixTime != 0,
            IncludeTag = row.IncludeTag != 0,
            IncludeValue = row.IncludeValue != 0,
            IncludeQuality = row.IncludeQuality != 0,
            DeviceErrorTagPrefix = payload.DeviceErrorTagPrefix,
            CommunicationFailureCode = payload.CommunicationFailureCode,
            CustomFields = customFields,
            CreateTime = DateTimeOffset.Parse(row.CreateTimeUtc, System.Globalization.CultureInfo.InvariantCulture),
            UpdateTime = DateTimeOffset.Parse(row.UpdateTimeUtc, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    private static void Validate(MqttTemplate template)
    {
        if (template.Id == Guid.Empty || string.IsNullOrWhiteSpace(template.Name) || template.Name.Trim().Length > 100)
        {
            throw new ArgumentException("模板ID或名称无效。");
        }

        if (!template.IncludeTag && !template.IncludeValue && !template.IncludeQuality)
        {
            throw new ArgumentException("至少选择一个数据字段：tag、value或quality。");
        }

        if (MqttDeviceStatusTemplateValidator.Validate(template) is { } statusConfigurationError)
        {
            throw new ArgumentException(statusConfigurationError);
        }

        if (template.CustomFields.Count > 64)
        {
            throw new ArgumentException("单个模板最多允许64个自定义字段。");
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in template.CustomFields)
        {
            if (field is null)
            {
                throw new ArgumentException("自定义字段项不能为空。");
            }

            var name = field.Name?.Trim() ?? string.Empty;
            var value = field.Value ?? string.Empty;
            if (!FieldNameRegex().IsMatch(name) || name.Length > 64 || value.Length > 1_024)
            {
                throw new ArgumentException($"自定义字段 {name} 的名称或值无效。");
            }

            if (ReservedFields.Contains(name) || !names.Add(name))
            {
                throw new ArgumentException($"自定义字段 {name} 与系统字段冲突或重复。");
            }
        }
    }

    private static MqttTemplate CreateStandardTemplate(DateTimeOffset now) => new()
    {
        Id = StandardTemplateId,
        Name = "EdgeNexus默认模板",
        Enabled = true,
        IsBuiltIn = true,
        PayloadStyle = MqttPayloadStyle.Advantech,
        IncludeDeviceId = false,
        IncludeTimestamp = true,
        IncludeTag = true,
        IncludeValue = true,
        IncludeQuality = true,
        DeviceErrorTagPrefix = DeviceErrorCodes.DefaultTagPrefix,
        CommunicationFailureCode = DeviceErrorCodes.CommunicationFailure,
        CreateTime = now,
        UpdateTime = now
    };

    private static bool IsBuiltInId(Guid id) => id == StandardTemplateId;
    private static int Flag(bool value) => value ? 1 : 0;

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldNameRegex();

    private sealed record PayloadConfiguration
    {
        public MqttPayloadStyle Style { get; init; } = MqttPayloadStyle.Standard;
        public string DeviceErrorTagPrefix { get; init; } = DeviceErrorCodes.DefaultTagPrefix;
        public int CommunicationFailureCode { get; init; } = DeviceErrorCodes.CommunicationFailure;
    }

    private sealed record TemplateRow(
        string Id,
        string Name,
        long Enabled,
        long IsBuiltIn,
        long IncludeDeviceId,
        long IncludeDeviceName,
        long IncludeDeviceIp,
        long IncludeProtocol,
        long IncludeGatewayId,
        long IncludeGatewayVersion,
        long IncludeTimestamp,
        long IncludeUnixTime,
        long IncludeTag,
        long IncludeValue,
        long IncludeQuality,
        string PayloadConfig,
        string CustomFields,
        string CreateTimeUtc,
        string UpdateTimeUtc);
}
