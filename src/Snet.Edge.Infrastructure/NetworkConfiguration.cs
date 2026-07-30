using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Dapper;
using Microsoft.Data.Sqlite;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class SystemNetworkInventoryService : INetworkInventoryService
{
    private readonly object _gate = new();
    private DateTimeOffset _expiresAt;
    private IReadOnlyList<NetworkInterfaceInfo> _cache = [];

    public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces(bool forceRefresh = false)
    {
        lock (_gate)
        {
            if (!forceRefresh && DateTimeOffset.UtcNow < _expiresAt)
            {
                return _cache;
            }

            _cache = ReadInterfaces();
            _expiresAt = DateTimeOffset.UtcNow.AddSeconds(5);
            return _cache;
        }
    }

    private static IReadOnlyList<NetworkInterfaceInfo> ReadInterfaces()
    {
        var interfaces = new List<NetworkInterfaceInfo>();
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var properties = networkInterface.GetIPProperties();
                var addresses = properties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => new NetworkInterfaceAddress(
                        address.Address.ToString(),
                        address.IPv4Mask?.ToString() ?? PrefixLengthToMask(address.PrefixLength)))
                    .ToArray();
                if (!ShouldDisplay(networkInterface, addresses.Length > 0))
                {
                    continue;
                }

                var gateways = properties.GatewayAddresses
                    .Select(item => item.Address)
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .ToArray();
                var dnsServers = properties.DnsAddresses
                    .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.ToString())
                    .ToArray();
                var dhcpEnabled = TryGetDhcpEnabled(networkInterface, properties, addresses.Length > 0);
                var physicalAddress = networkInterface.GetPhysicalAddress().GetAddressBytes();
                interfaces.Add(new NetworkInterfaceInfo
                {
                    Id = networkInterface.Id,
                    Name = networkInterface.Name,
                    Description = networkInterface.Description,
                    InterfaceType = networkInterface.NetworkInterfaceType.ToString(),
                    Ipv4Addresses = addresses,
                    Gateways = gateways,
                    DnsServers = dnsServers,
                    MacAddress = string.Join(':', physicalAddress.Select(value => value.ToString("X2"))),
                    IsOnline = networkInterface.OperationalStatus == OperationalStatus.Up,
                    DhcpEnabled = dhcpEnabled,
                    IsBindable = networkInterface.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel && addresses.Length > 0,
                    SpeedMbps = Math.Max(networkInterface.Speed, 0) / 1_000_000
                });
            }
            catch (NetworkInformationException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }
        }

        return interfaces
            .OrderByDescending(item => item.IsOnline)
            .ThenByDescending(item => item.IsBindable)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool? TryGetDhcpEnabled(
        NetworkInterface networkInterface,
        IPInterfaceProperties properties,
        bool hasIpv4)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return properties.GetIPv4Properties()?.IsDhcpEnabled;
            }
            catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
            {
                return null;
            }
        }

        if (!OperatingSystem.IsLinux() || !hasIpv4)
        {
            return null;
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ip",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-4");
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add("addr");
            startInfo.ArgumentList.Add("show");
            startInfo.ArgumentList.Add("dev");
            startInfo.ArgumentList.Add(networkInterface.Name);
            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null || !process.WaitForExit(500))
            {
                process?.Kill(true);
                return null;
            }

            return process.StandardOutput.ReadToEnd().Contains(" dynamic ", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool ShouldDisplay(NetworkInterface networkInterface, bool hasIpv4)
    {
        var identity = $"{networkInterface.Name} {networkInterface.Description}";
        string[] excludedComponents =
        [
            "Npcap Packet Driver",
            "Packet Scheduler",
            "Native WiFi Filter",
            "Virtual WiFi Filter Driver",
            "LightWeight Filter",
            "Kernel Debug Network Adapter",
            "WAN Miniport"
        ];
        if (excludedComponents.Any(component => identity.Contains(component, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return hasIpv4 || networkInterface.NetworkInterfaceType is
            NetworkInterfaceType.Ethernet or
            NetworkInterfaceType.Ethernet3Megabit or
            NetworkInterfaceType.FastEthernetFx or
            NetworkInterfaceType.FastEthernetT or
            NetworkInterfaceType.GigabitEthernet or
            NetworkInterfaceType.Wireless80211;
    }

    private static string PrefixLengthToMask(int prefixLength)
    {
        if (prefixLength is < 0 or > 32)
        {
            return string.Empty;
        }

        var mask = prefixLength == 0 ? 0U : uint.MaxValue << (32 - prefixLength);
        return new IPAddress([
            (byte)(mask >> 24),
            (byte)(mask >> 16),
            (byte)(mask >> 8),
            (byte)mask
        ]).ToString();
    }
}

public sealed class SqliteNetworkConfigurationService(
    string connectionString,
    INetworkInventoryService inventoryService) : INetworkConfigurationService, IDisposable
{
    private const int SingletonId = 1;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private NetworkBindingConfiguration _current = new();
    private long _plcRevision = 1;
    private long _mqttRevision = 1;
    private long _webRevision = 1;

    public async ValueTask InitializeAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                CREATE TABLE IF NOT EXISTS network_configuration (
                    id INTEGER NOT NULL PRIMARY KEY CHECK (id = 1),
                    plc_interface_id TEXT NULL,
                    mqtt_interface_id TEXT NULL,
                    web_interface_id TEXT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """,
                cancellationToken: cancellationToken));
            var row = await connection.QuerySingleOrDefaultAsync<NetworkConfigurationRow>(new CommandDefinition(
                """
                SELECT plc_interface_id AS PlcInterfaceId,
                       mqtt_interface_id AS MqttInterfaceId,
                       web_interface_id AS WebInterfaceId,
                       updated_at_utc AS UpdatedAtUtc
                FROM network_configuration WHERE id = @Id;
                """,
                new { Id = SingletonId },
                cancellationToken: cancellationToken));
            if (row is null)
            {
                var bindable = inventoryService.GetInterfaces(true).Where(item => item.IsBindable).ToArray();
                var defaultInterfaceId = bindable.Length == 1 ? bindable[0].Id : null;
                _current = new NetworkBindingConfiguration
                {
                    PlcInterfaceId = defaultInterfaceId,
                    MqttInterfaceId = defaultInterfaceId,
                    WebInterfaceId = defaultInterfaceId,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await InsertAsync(connection, _current, cancellationToken);
            }
            else
            {
                _current = FromRow(row);
            }
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<NetworkConfigurationView> GetAsync(bool forceRefresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(BuildView(_current, inventoryService.GetInterfaces(forceRefresh)));
    }

    public async ValueTask<NetworkConfigurationView> SaveAsync(
        NetworkBindingConfiguration configuration,
        CancellationToken cancellationToken)
    {
        var interfaces = inventoryService.GetInterfaces(true);
        ValidateInterface(configuration.PlcInterfaceId, "PLC采集网络", interfaces);
        ValidateInterface(configuration.MqttInterfaceId, "MQTT上传网络", interfaces);
        ValidateInterface(configuration.WebInterfaceId, "Web管理网络", interfaces);
        var normalized = configuration with
        {
            PlcInterfaceId = Normalize(configuration.PlcInterfaceId),
            MqttInterfaceId = Normalize(configuration.MqttInterfaceId),
            WebInterfaceId = Normalize(configuration.WebInterfaceId),
            UpdatedAt = DateTimeOffset.UtcNow
        };

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            var previous = _current;
            await using var connection = await OpenAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO network_configuration(id, plc_interface_id, mqtt_interface_id, web_interface_id, updated_at_utc)
                VALUES (@Id, @PlcInterfaceId, @MqttInterfaceId, @WebInterfaceId, @UpdatedAtUtc)
                ON CONFLICT(id) DO UPDATE SET
                    plc_interface_id = excluded.plc_interface_id,
                    mqtt_interface_id = excluded.mqtt_interface_id,
                    web_interface_id = excluded.web_interface_id,
                    updated_at_utc = excluded.updated_at_utc;
                """,
                new
                {
                    Id = SingletonId,
                    normalized.PlcInterfaceId,
                    normalized.MqttInterfaceId,
                    normalized.WebInterfaceId,
                    UpdatedAtUtc = normalized.UpdatedAt.ToString("O")
                },
                cancellationToken: cancellationToken));
            Volatile.Write(ref _current, normalized);
            if (!string.Equals(previous.PlcInterfaceId, normalized.PlcInterfaceId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _plcRevision);
            }

            if (!string.Equals(previous.MqttInterfaceId, normalized.MqttInterfaceId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _mqttRevision);
            }

            if (!string.Equals(previous.WebInterfaceId, normalized.WebInterfaceId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _webRevision);
            }
        }
        finally
        {
            _writeGate.Release();
        }

        return BuildView(normalized, interfaces);
    }

    public string? ResolveLocalIpv4(NetworkUsage usage)
    {
        var interfaceId = GetInterfaceId(_current, usage);
        if (interfaceId is null)
        {
            return null;
        }

        return inventoryService.GetInterfaces()
            .FirstOrDefault(item => item.Id == interfaceId && item.IsBindable)?
            .Ipv4Addresses.FirstOrDefault()?.Address;
    }

    public string? ResolveInterfaceName(NetworkUsage usage)
    {
        var interfaceId = GetInterfaceId(_current, usage);
        return interfaceId is null
            ? null
            : inventoryService.GetInterfaces().FirstOrDefault(item => item.Id == interfaceId)?.Name;
    }

    public long GetRevision(NetworkUsage usage) => usage switch
    {
        NetworkUsage.PlcAcquisition => Volatile.Read(ref _plcRevision),
        NetworkUsage.MqttUpload => Volatile.Read(ref _mqttRevision),
        NetworkUsage.WebManagement => Volatile.Read(ref _webRevision),
        _ => 0
    };

    public bool IsWebRequestAllowed(IPAddress? localAddress)
    {
        if (localAddress is null || IPAddress.IsLoopback(localAddress) || _current.WebInterfaceId is null)
        {
            return true;
        }

        return string.Equals(
            ResolveLocalIpv4(NetworkUsage.WebManagement),
            localAddress.MapToIPv4().ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _writeGate.Dispose();

    private NetworkConfigurationView BuildView(
        NetworkBindingConfiguration configuration,
        IReadOnlyList<NetworkInterfaceInfo> interfaces) =>
        new(configuration, interfaces, [
            ResolveEffective(NetworkUsage.PlcAcquisition, configuration.PlcInterfaceId, interfaces),
            ResolveEffective(NetworkUsage.MqttUpload, configuration.MqttInterfaceId, interfaces),
            ResolveEffective(NetworkUsage.WebManagement, configuration.WebInterfaceId, interfaces)
        ]);

    private static EffectiveNetworkBinding ResolveEffective(
        NetworkUsage usage,
        string? interfaceId,
        IReadOnlyList<NetworkInterfaceInfo> interfaces)
    {
        if (interfaceId is null)
        {
            var single = interfaces.Where(item => item.IsBindable && item.IsOnline).ToArray();
            return single.Length == 1
                ? new EffectiveNetworkBinding(usage, "Auto", null, $"自动（{single[0].Name}）", single[0].Ipv4Addresses.FirstOrDefault()?.Address, true)
                : new EffectiveNetworkBinding(usage, "Auto", null, "操作系统自动路由", null, true);
        }

        var selected = interfaces.FirstOrDefault(item => item.Id == interfaceId);
        return selected is null
            ? new EffectiveNetworkBinding(usage, "Specific", interfaceId, "网卡已不存在", null, false)
            : new EffectiveNetworkBinding(
                usage,
                "Specific",
                selected.Id,
                selected.Name,
                selected.Ipv4Addresses.FirstOrDefault()?.Address,
                selected.IsBindable && selected.IsOnline);
    }

    private static void ValidateInterface(
        string? interfaceId,
        string field,
        IReadOnlyList<NetworkInterfaceInfo> interfaces)
    {
        var normalized = Normalize(interfaceId);
        if (normalized is not null && !interfaces.Any(item => item.Id == normalized && item.IsBindable))
        {
            throw new ArgumentException($"{field}选择的网卡不存在或没有可绑定的IPv4地址。");
        }
    }

    private static string? GetInterfaceId(NetworkBindingConfiguration configuration, NetworkUsage usage) => usage switch
    {
        NetworkUsage.PlcAcquisition => configuration.PlcInterfaceId,
        NetworkUsage.MqttUpload => configuration.MqttInterfaceId,
        NetworkUsage.WebManagement => configuration.WebInterfaceId,
        _ => null
    };

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private async ValueTask<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async ValueTask InsertAsync(
        SqliteConnection connection,
        NetworkBindingConfiguration configuration,
        CancellationToken cancellationToken) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO network_configuration(id, plc_interface_id, mqtt_interface_id, web_interface_id, updated_at_utc)
            VALUES (@Id, @PlcInterfaceId, @MqttInterfaceId, @WebInterfaceId, @UpdatedAtUtc);
            """,
            new
            {
                Id = SingletonId,
                configuration.PlcInterfaceId,
                configuration.MqttInterfaceId,
                configuration.WebInterfaceId,
                UpdatedAtUtc = configuration.UpdatedAt.ToString("O")
            },
            cancellationToken: cancellationToken));

    private static NetworkBindingConfiguration FromRow(NetworkConfigurationRow row) => new()
    {
        PlcInterfaceId = row.PlcInterfaceId,
        MqttInterfaceId = row.MqttInterfaceId,
        WebInterfaceId = row.WebInterfaceId,
        UpdatedAt = DateTimeOffset.Parse(row.UpdatedAtUtc, System.Globalization.CultureInfo.InvariantCulture)
    };

    private sealed record NetworkConfigurationRow(
        string? PlcInterfaceId,
        string? MqttInterfaceId,
        string? WebInterfaceId,
        string UpdatedAtUtc);
}
