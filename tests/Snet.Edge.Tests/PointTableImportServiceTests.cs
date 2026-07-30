using System.Text;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class PointTableImportServiceTests
{
    [Fact]
    public async Task ValidCsvIsWrittenToDraftButNotActiveConfiguration()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-import-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var importer = new PointTableImportService(store, new GatewayConfigurationValidator());
            await using var stream = Csv(
                "GroupName,GroupTopic,DeviceName,Protocol,Host,Port,Station,TagName,Address,DataType,ByteOrder\n" +
                "一号产线,edge/line1,PLC-01,ModbusTcp,192.168.1.10,502,1,温度,40001,Float32,ABCD\n" +
                "一号产线,edge/line1,PLC-01,ModbusTcp,192.168.1.10,502,1,运行,B0,Bool,ABCD");

            var report = await importer.ImportDraftAsync(stream, "points.csv", true, CancellationToken.None);

            Assert.True(report.IsValid);
            Assert.Equal(2, report.TotalRows);
            Assert.Single((await store.GetDraftSnapshotAsync(CancellationToken.None)).Devices);
            Assert.Equal(2, (await store.GetDraftSnapshotAsync(CancellationToken.None)).Tags.Count);
            Assert.Empty((await store.GetSnapshotAsync(CancellationToken.None)).Devices);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InvalidCsvDoesNotPartiallyChangeDraft()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-import-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var importer = new PointTableImportService(store, new GatewayConfigurationValidator());
            await using var stream = Csv(
                "GroupName,GroupTopic,DeviceName,Protocol,Host,Port,TagName,Address,DataType\n" +
                "一号产线,edge/line1,PLC-01,UnknownProtocol,192.168.1.10,502,温度,40001,Float32");

            var report = await importer.ImportDraftAsync(stream, "points.csv", true, CancellationToken.None);

            Assert.False(report.IsValid);
            Assert.Contains(report.Errors, issue => issue.Field == "protocol");
            Assert.Empty((await store.GetDraftSnapshotAsync(CancellationToken.None)).Groups);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ProtocolSpecificColumnsAreImported()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-import-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var importer = new PointTableImportService(store, new GatewayConfigurationValidator());
            await using var stream = Csv(
                "GroupName,GroupTopic,DeviceName,Protocol,Host,Port,SiemensSeries,Rack,Slot,TagName,Address,DataType\n" +
                "一号产线,edge/line1,S7-300,SiemensS7,192.168.1.10,102,S300,0,2,温度,DB1.0,Float32");

            var report = await importer.ImportDraftAsync(stream, "points.csv", true, CancellationToken.None);
            var device = Assert.Single((await store.GetDraftSnapshotAsync(CancellationToken.None)).Devices);

            Assert.True(report.IsValid);
            Assert.Equal(SiemensPlcSeries.S300, device.SiemensSeries);
            Assert.Equal(2, device.Slot);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ModbusFunctionCodeIsImportedAsStronglyTypedTagField()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-import-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);
            var importer = new PointTableImportService(store, new GatewayConfigurationValidator());
            await using var stream = Csv(
                "GroupName,GroupTopic,DeviceName,Protocol,Host,Port,TagName,Address,ModbusFunctionCode,DataType\n" +
                "一号产线,edge/line1,PLC-01,ModbusTcp,192.168.1.10,502,运行,100,02,Bool");

            var report = await importer.ImportDraftAsync(stream, "points.csv", true, CancellationToken.None);
            var tag = Assert.Single((await store.GetDraftSnapshotAsync(CancellationToken.None)).Tags);

            Assert.True(report.IsValid);
            Assert.Equal("100", tag.Address);
            Assert.Equal(ModbusFunctionCode.ReadDiscreteInputs, tag.ModbusFunctionCode);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static MemoryStream Csv(string content) => new(Encoding.UTF8.GetBytes(content));

    private static void DeleteDatabase(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete($"{databasePath}-shm");
        File.Delete($"{databasePath}-wal");
    }
}
