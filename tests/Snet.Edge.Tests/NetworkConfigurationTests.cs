using System.Net;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class NetworkConfigurationTests
{
    [Fact]
    public async Task SingleBindableInterfaceBecomesDefaultForAllUsagesAndPersistsChanges()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"edgenexus-network-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var inventory = new FakeNetworkInventory([CreateInterface("lan1", "LAN1", "10.168.4.2")]);
        try
        {
            using (var service = new SqliteNetworkConfigurationService(connectionString, inventory))
            {
                await service.InitializeAsync(CancellationToken.None);
                var initial = await service.GetAsync(false, CancellationToken.None);
                Assert.Equal("lan1", initial.Bindings.PlcInterfaceId);
                Assert.Equal("lan1", initial.Bindings.MqttInterfaceId);
                Assert.Equal("lan1", initial.Bindings.WebInterfaceId);
                Assert.Equal("10.168.4.2", service.ResolveLocalIpv4(NetworkUsage.PlcAcquisition));

                await service.SaveAsync(initial.Bindings with
                {
                    PlcInterfaceId = null,
                    MqttInterfaceId = "lan1",
                    WebInterfaceId = null
                }, CancellationToken.None);
            }

            using var reloaded = new SqliteNetworkConfigurationService(connectionString, inventory);
            await reloaded.InitializeAsync(CancellationToken.None);
            var persisted = await reloaded.GetAsync(false, CancellationToken.None);
            Assert.Null(persisted.Bindings.PlcInterfaceId);
            Assert.Equal("lan1", persisted.Bindings.MqttInterfaceId);
            Assert.Null(persisted.Bindings.WebInterfaceId);
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    [Fact]
    public async Task InvalidInterfaceIsRejectedAndLoopbackRemainsAWebRecoveryPath()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"edgenexus-network-{Guid.NewGuid():N}.db");
        var connectionString = $"Data Source={databasePath};Pooling=False";
        var inventory = new FakeNetworkInventory([
            CreateInterface("lan1", "LAN1", "10.168.4.2"),
            CreateInterface("lan2", "LAN2", "192.168.1.88")
        ]);
        try
        {
            using var service = new SqliteNetworkConfigurationService(connectionString, inventory);
            await service.InitializeAsync(CancellationToken.None);
            await Assert.ThrowsAsync<ArgumentException>(async () =>
                await service.SaveAsync(new NetworkBindingConfiguration { PlcInterfaceId = "missing" }, CancellationToken.None));

            await service.SaveAsync(new NetworkBindingConfiguration { WebInterfaceId = "lan2" }, CancellationToken.None);
            Assert.True(service.IsWebRequestAllowed(IPAddress.Loopback));
            Assert.True(service.IsWebRequestAllowed(IPAddress.Parse("192.168.1.88")));
            Assert.False(service.IsWebRequestAllowed(IPAddress.Parse("10.168.4.2")));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    private static NetworkInterfaceInfo CreateInterface(string id, string name, string ipv4) => new()
    {
        Id = id,
        Name = name,
        Description = $"{name} Adapter",
        InterfaceType = "Ethernet",
        Ipv4Addresses = [new NetworkInterfaceAddress(ipv4, "255.255.255.0")],
        Gateways = [],
        DnsServers = [],
        MacAddress = "00:11:22:33:44:55",
        IsOnline = true,
        DhcpEnabled = false,
        IsBindable = true,
        SpeedMbps = 1_000
    };

    private sealed class FakeNetworkInventory(IReadOnlyList<NetworkInterfaceInfo> interfaces) : INetworkInventoryService
    {
        public IReadOnlyList<NetworkInterfaceInfo> GetInterfaces(bool forceRefresh = false) => interfaces;
    }
}
