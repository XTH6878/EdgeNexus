using System.Text;
using ClosedXML.Excel;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class DeviceTagFileServiceTests
{
    [Fact]
    public async Task ExportedWorkbookContainsDevicePointsAndInstructions()
    {
        var databasePath = TemporaryDatabasePath();
        try
        {
            await using var store = await CreateStoreAsync(databasePath);
            var (device, tag) = await SeedDeviceAsync(store);
            var service = new DeviceTagFileService(store);

            var export = await service.ExportAsync(device.Id, CancellationToken.None);

            Assert.EndsWith(".xlsx", export.FileName, StringComparison.OrdinalIgnoreCase);
            using var workbook = new XLWorkbook(new MemoryStream(export.Content));
            var points = workbook.Worksheet("Points");
            Assert.Equal("TagName", points.Cell("A1").GetString());
            Assert.Equal(tag.Name, points.Cell("A2").GetString());
            Assert.Equal("03", points.Cell("D2").GetString());
            Assert.Equal(device.Name, workbook.Worksheet("说明").Cell("B2").GetString());
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ImportPreviewPreservesExistingTagIdAndParsesAllEditableFields()
    {
        var databasePath = TemporaryDatabasePath();
        try
        {
            await using var store = await CreateStoreAsync(databasePath);
            var (device, existingTag) = await SeedDeviceAsync(store);
            var service = new DeviceTagFileService(store);
            await using var csv = new MemoryStream(Encoding.UTF8.GetBytes(
                "TagName,Address,DataType,ModbusFunctionCode,Enabled,ByteOrder,Operator,Operand,Decimals,PublishOnChange,Deadband,StringLength,Remark\n" +
                "温度,100,Float32,03,true,CDAB,*,0.1,2,true,0.5,32,主温度\n" +
                "运行,5,Bool,02,true,ABCD,*,1,0,false,0,32,运行反馈"));

            var preview = await service.PreviewImportAsync(device.Id, csv, "points.csv", CancellationToken.None);

            Assert.True(preview.IsValid);
            Assert.Equal(2, preview.TotalRows);
            var temperature = preview.Tags.Single(tag => tag.Name == "温度");
            Assert.Equal(existingTag.Id, temperature.Id);
            Assert.Equal(ModbusFunctionCode.ReadHoldingRegisters, temperature.ModbusFunctionCode);
            Assert.Equal("CDAB", temperature.ByteOrder);
            Assert.True(temperature.PublishOnChange);
            Assert.Equal(0.5M, temperature.Deadband);
            var running = preview.Tags.Single(tag => tag.Name == "运行");
            Assert.Equal(ModbusFunctionCode.ReadDiscreteInputs, running.ModbusFunctionCode);
            Assert.Equal(TagDataType.Bool, running.DataType);
            Assert.Single((await store.GetDraftSnapshotAsync(CancellationToken.None)).Tags);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task ImportPreviewRejectsProtocolSpecificMismatchWithoutChangingDraft()
    {
        var databasePath = TemporaryDatabasePath();
        try
        {
            await using var store = await CreateStoreAsync(databasePath);
            var (device, _) = await SeedDeviceAsync(store);
            var service = new DeviceTagFileService(store);
            await using var csv = new MemoryStream(Encoding.UTF8.GetBytes(
                "TagName,Address,DataType,ModbusFunctionCode\n" +
                "错误线圈,1,Int16,01"));

            var preview = await service.PreviewImportAsync(device.Id, csv, "points.csv", CancellationToken.None);

            Assert.False(preview.IsValid);
            Assert.Contains(preview.Errors, issue => issue.Message.Contains("必须为 Bool", StringComparison.Ordinal));
            Assert.Single((await store.GetDraftSnapshotAsync(CancellationToken.None)).Tags);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    private static async ValueTask<SqliteGatewayConfigurationStore> CreateStoreAsync(string databasePath)
    {
        var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
        await store.InitializeAsync(CancellationToken.None);
        return store;
    }

    private static async ValueTask<(DeviceDefinition Device, TagDefinition Tag)> SeedDeviceAsync(
        SqliteGatewayConfigurationStore store)
    {
        var group = new MqttGroup { Id = Guid.NewGuid(), Name = "一号线", Topic = "edge/line1" };
        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(), GroupId = group.Id, Name = "Modbus机台", Protocol = ProtocolKind.ModbusTcp,
            Host = "192.168.1.10", Port = 502
        };
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(), DeviceId = device.Id, Name = "温度", Address = "100",
            DataType = TagDataType.Float32, ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters
        };
        await store.UpsertGroupAsync(group, CancellationToken.None);
        await store.UpsertDeviceAsync(device, CancellationToken.None);
        await store.ReplaceTagsAsync(device.Id, [tag], CancellationToken.None);
        return (device, tag);
    }

    private static string TemporaryDatabasePath() =>
        Path.Combine(Path.GetTempPath(), $"snet-device-tags-{Guid.NewGuid():N}.db");

    private static void DeleteDatabase(string databasePath)
    {
        File.Delete(databasePath);
        File.Delete($"{databasePath}-shm");
        File.Delete($"{databasePath}-wal");
    }
}
