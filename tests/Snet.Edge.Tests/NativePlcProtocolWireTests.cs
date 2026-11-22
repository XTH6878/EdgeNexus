using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;

namespace Snet.Edge.Tests;

public sealed class NativePlcProtocolWireTests
{
    [Fact]
    public async Task SiemensS7PerformsCotpAndSetupHandshakeAndBatchReadsMultipleVariables()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // 1. Receive COTP CR
            var cr = new byte[22];
            await stream.ReadExactlyAsync(cr);
            Assert.Equal(0x03, cr[0]);
            Assert.Equal(0xE0, cr[5]);
            byte[] cc = [0x03, 0x00, 0x00, 0x0B, 0x06, 0xD0, 0x00, 0x01, 0x00, 0x01, 0x00];
            await stream.WriteAsync(cc);

            // 2. Receive S7 Setup Communication
            var setup = new byte[25];
            await stream.ReadExactlyAsync(setup);
            Assert.Equal(0x32, setup[7]);
            Assert.Equal(0xF0, setup[17]);
            byte[] setupAck =
            [
                0x03, 0x00, 0x00, 0x1B,
                0x02, 0xF0, 0x80,
                0x32, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
                0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0
            ];
            await stream.WriteAsync(setupAck);

            // 3. Receive S7 Multi-Var Read (2 items: Int16 = 100, Float32 = 25.5f)
            var readReq = new byte[19 + 24];
            await stream.ReadExactlyAsync(readReq);
            Assert.Equal(0x04, readReq[17]);
            Assert.Equal(0x02, readReq[18]);

            byte[] readAck =
            [
                0x03, 0x00, 0x00, 0x23,
                0x02, 0xF0, 0x80,
                0x32, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x0E, 0x00, 0x00,
                0x04, 0x02,
                0xFF, 0x04, 0x00, 0x10, 0x00, 0x64,
                0xFF, 0x04, 0x00, 0x20, 0x41, 0xCC, 0x00, 0x00
            ];
            await stream.WriteAsync(readAck);
        });

        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "S7-Loopback",
            Protocol = ProtocolKind.SiemensS7,
            Host = "127.0.0.1",
            Port = port
        };
        TagDefinition[] tags =
        [
            new() { Id = Guid.NewGuid(), DeviceId = device.Id, Name = "Speed", Address = "DB1.DBW2", DataType = TagDataType.Int16 },
            new() { Id = Guid.NewGuid(), DeviceId = device.Id, Name = "Temp", Address = "DB1.DBD4", DataType = TagDataType.Float32 }
        ];

        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        await using var driver = factory.Create(device, tags);
        await driver.ConnectAsync(CancellationToken.None);
        var results = await driver.ReadManyAsync(CancellationToken.None);

        Assert.True(results[tags[0].Id].IsSuccess);
        Assert.Equal((short)100, results[tags[0].Id].Value);
        Assert.True(results[tags[1].Id].IsSuccess);
        Assert.Equal(25.5f, Assert.IsType<float>(results[tags[1].Id].Value), 2);
        await serverTask;
    }

    [Fact]
    public async Task ModbusTcpReadsHoldingRegistersAndIsolatesIllegalAddressException()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // 1. Valid read FC 03 (2 registers -> 123456)
            var req1 = new byte[12];
            await stream.ReadExactlyAsync(req1);
            Assert.Equal(0x03, req1[7]);
            byte[] resp1 = [req1[0], req1[1], 0x00, 0x00, 0x00, 0x07, req1[6], 0x03, 0x04, 0x00, 0x01, 0xE2, 0x40];
            await stream.WriteAsync(resp1);

            // 2. Invalid address read -> Modbus exception 0x83, code 0x02 (Illegal Data Address)
            var req2 = new byte[12];
            await stream.ReadExactlyAsync(req2);
            byte[] resp2 = [req2[0], req2[1], 0x00, 0x00, 0x00, 0x03, req2[6], 0x83, 0x02];
            await stream.WriteAsync(resp2);
        });

        var device = new DeviceDefinition
        {
            Id = Guid.NewGuid(),
            GroupId = Guid.NewGuid(),
            Name = "Modbus-Loopback",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = port
        };
        TagDefinition[] tags =
        [
            new()
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                Name = "GoodCounter",
                Address = "10",
                DataType = TagDataType.Int32,
                ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters
            },
            new()
            {
                Id = Guid.NewGuid(),
                DeviceId = device.Id,
                Name = "BadAddress",
                Address = "9999",
                DataType = TagDataType.Int16,
                ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters
            }
        ];

        var factory = new HslDeviceDriverFactory(new TagReadPlanCompiler());
        await using var driver = factory.Create(device, tags);
        await driver.ConnectAsync(CancellationToken.None);
        var results = await driver.ReadManyAsync(CancellationToken.None);

        Assert.True(results[tags[0].Id].IsSuccess);
        Assert.Equal(123456, results[tags[0].Id].Value);
        Assert.False(results[tags[1].Id].IsSuccess);
        Assert.Equal(DataQuality.ConfigurationError, results[tags[1].Id].Quality);
        await serverTask;
    }

    [Fact]
    public async Task MitsubishiMc3ETcpAndUdpFramesDecodeWordData()
    {
        using var udpServer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var udpPort = ((IPEndPoint)udpServer.Client.LocalEndPoint!).Port;

        var serverTask = Task.Run(async () =>
        {
            var received = await udpServer.ReceiveAsync();
            Assert.Equal(21, received.Buffer.Length);
            Assert.Equal(0x50, received.Buffer[0]);
            Assert.Equal(0xA8, received.Buffer[18]); // D register code
            byte[] response = [0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x04, 0x00, 0x00, 0x00, 0x01, 0xF4];
            await udpServer.SendAsync(response, received.RemoteEndPoint);
        });

        using var client = new MelsecMcUdp("127.0.0.1", udpPort);
        var connect = await client.ConnectServerAsync();
        Assert.True(connect.IsSuccess);
        var read = await client.ReadInt16Async("D100");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)500, read.Content);
        await serverTask;
    }

    [Fact]
    public async Task OmronFinsPerformsNodeHandshakeAndReadsMemoryArea()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            var handshake = new byte[20];
            await stream.ReadExactlyAsync(handshake);
            Assert.Equal((byte)'F', handshake[0]);
            byte[] handshakeResp =
            [
                0x46, 0x49, 0x4E, 0x53,
                0x00, 0x00, 0x00, 0x10,
                0x00, 0x00, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x0B,
                0x00, 0x00, 0x00, 0x01
            ];
            await stream.WriteAsync(handshakeResp);

            var readReq = new byte[34];
            await stream.ReadExactlyAsync(readReq);
            Assert.Equal(0x01, readReq[26]);
            Assert.Equal(0x01, readReq[27]);
            Assert.Equal(0x82, readReq[28]); // DM word code

            byte[] readResp =
            [
                0x46, 0x49, 0x4E, 0x53,
                0x00, 0x00, 0x00, 0x18,
                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x00,
                0xC0, 0x00, 0x02, 0x00, 0x0B, 0x00, 0x00, 0x01, 0x00, readReq[25],
                0x01, 0x01, 0x00, 0x00,
                0x03, 0xE8
            ];
            await stream.WriteAsync(readResp);
        });

        using var fins = new OmronFinsNet("127.0.0.1", port);
        Assert.True((await fins.ConnectServerAsync()).IsSuccess);
        var val = await fins.ReadUInt16Async("D100");
        Assert.True(val.IsSuccess, val.Message);
        Assert.Equal((ushort)1000, val.Content);
        await serverTask;
    }

    [Fact]
    public async Task PanasonicMewtocolReadsDataRegisterWithBcc()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            var buf = new byte[64];
            var len = await stream.ReadAsync(buf);
            var reqText = Encoding.ASCII.GetString(buf, 0, len);
            Assert.StartsWith("%01#RDDD0010000100", reqText, StringComparison.Ordinal);

            // Return 0x0064 (100 in little-endian hex words: "6400")
            var reply = PanasonicHelper.AppendBccAndCr("%01$RD6400");
            await stream.WriteAsync(Encoding.ASCII.GetBytes(reply));
        });

        using var plc = new PanasonicMewtocolOverTcp("127.0.0.1", port, 1);
        Assert.True((await plc.ConnectServerAsync()).IsSuccess);
        var val = await plc.ReadInt16Async("DT100");
        Assert.True(val.IsSuccess, val.Message);
        Assert.Equal((short)100, val.Content);
        await serverTask;
    }

    [Fact]
    public async Task BeckhoffAdsResolvesSymbolHandleCachesItAndExecutesSumRead()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // Read 2 symbol handle requests (0x0009 ReadWrite, IndexGroup=0xF003)
            for (uint handle = 1; handle <= 2; handle++)
            {
                var header = new byte[38];
                await stream.ReadExactlyAsync(header);
                var dataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(26, 4));
                var cmdData = new byte[dataLen];
                await stream.ReadExactlyAsync(cmdData);
                Assert.Equal(0xF003u, BinaryPrimitives.ReadUInt32LittleEndian(cmdData.AsSpan(0, 4)));

                var respPayload = new byte[12];
                BinaryPrimitives.WriteUInt32LittleEndian(respPayload.AsSpan(4, 4), 4);
                BinaryPrimitives.WriteUInt32LittleEndian(respPayload.AsSpan(8, 4), handle);
                await WriteAdsResponseAsync(stream, header, respPayload);
            }

            // Read 1 SumRead request (0x0009 ReadWrite, IndexGroup=0xF080, count=2)
            var sumHeader = new byte[38];
            await stream.ReadExactlyAsync(sumHeader);
            var sumDataLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(sumHeader.AsSpan(26, 4));
            var sumData = new byte[sumDataLen];
            await stream.ReadExactlyAsync(sumData);
            Assert.Equal(0xF080u, BinaryPrimitives.ReadUInt32LittleEndian(sumData.AsSpan(0, 4)));

            // Response: 2 status codes (0, 0) + 2 bytes (0x00, 0x2A = 42) + 2 bytes (0x00, 0x54 = 84)
            byte[] sumValues = [0, 0, 0, 0, 0, 0, 0, 0, 0x00, 0x2A, 0x00, 0x54];
            var sumResp = new byte[8 + sumValues.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(sumResp.AsSpan(4, 4), (uint)sumValues.Length);
            Buffer.BlockCopy(sumValues, 0, sumResp, 8, sumValues.Length);
            await WriteAdsResponseAsync(stream, sumHeader, sumResp);
        });

        using var ads = new BeckhoffAdsNet("127.0.0.1", port) { UseTagCache = true };
        Assert.True((await ads.ConnectServerAsync()).IsSuccess);
        var batch = await ads.ReadAsync(["s=MAIN.a", "s=MAIN.b"], [2, 2]);
        Assert.True(batch.IsSuccess, batch.Message);
        Assert.Equal(42, ads.ByteTransform.TransInt16(batch.Content, 0));
        Assert.Equal(84, ads.ByteTransform.TransInt16(batch.Content, 2));
        await serverTask;
    }

    [Fact]
    public async Task AllenBradleyCipRegistersSessionAndReadsTag()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(1);
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serverTask = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync();
            using var stream = client.GetStream();

            // 1. RegisterSession (28 bytes)
            var reg = new byte[28];
            await stream.ReadExactlyAsync(reg);
            Assert.Equal(0x0065, BinaryPrimitives.ReadUInt16LittleEndian(reg.AsSpan(0, 2)));
            BinaryPrimitives.WriteUInt32LittleEndian(reg.AsSpan(4, 4), 0x12345678u);
            await stream.WriteAsync(reg);

            // 2. SendRRData
            var eipHead = new byte[24];
            await stream.ReadExactlyAsync(eipHead);
            var bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(eipHead.AsSpan(2, 2));
            var body = new byte[bodyLen];
            await stream.ReadExactlyAsync(body);

            // Reply with 0xCC Read Tag Reply, Type=0xC3 (INT), Value=0x00, 0x7B (123 in BigEndian transform)
            byte[] cipReply = [0xCC, 0x00, 0x00, 0x00, 0xC3, 0x00, 0x00, 0x7B];
            var respBody = new byte[16 + cipReply.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(respBody.AsSpan(6, 2), 2);
            BinaryPrimitives.WriteUInt16LittleEndian(respBody.AsSpan(12, 2), 0x00B2);
            BinaryPrimitives.WriteUInt16LittleEndian(respBody.AsSpan(14, 2), (ushort)cipReply.Length);
            Buffer.BlockCopy(cipReply, 0, respBody, 16, cipReply.Length);

            var respHead = new byte[24];
            BinaryPrimitives.WriteUInt16LittleEndian(respHead.AsSpan(0, 2), 0x006F);
            BinaryPrimitives.WriteUInt16LittleEndian(respHead.AsSpan(2, 2), (ushort)respBody.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(respHead.AsSpan(4, 4), 0x12345678u);
            await stream.WriteAsync(respHead);
            await stream.WriteAsync(respBody);
        });

        using var ab = new AllenBradleyNet("127.0.0.1", port);
        Assert.True((await ab.ConnectServerAsync()).IsSuccess);
        var read = await ab.ReadInt16Async("Motor.Speed");
        Assert.True(read.IsSuccess, read.Message);
        Assert.Equal((short)123, read.Content);
        await serverTask;
    }

    [Fact]
    public async Task ConfigurationStoreReturnsCachedActiveSnapshotInstanceUntilPublished()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"snet-edge-cache-{Guid.NewGuid():N}.db");
        try
        {
            await using var store = new SqliteGatewayConfigurationStore($"Data Source={databasePath};Pooling=False");
            await store.InitializeAsync(CancellationToken.None);

            var first = await store.GetSnapshotAsync(CancellationToken.None);
            var second = await store.GetSnapshotAsync(CancellationToken.None);
            Assert.Same(first, second);

            var group = new MqttGroup
            {
                Id = Guid.NewGuid(),
                Name = "缓存刷新测试组",
                Topic = "edge/cache/test"
            };
            await store.UpsertGroupAsync(group, CancellationToken.None);
            Assert.Same(first, await store.GetSnapshotAsync(CancellationToken.None));

            await store.PublishDraftAsync("发布刷新缓存", CancellationToken.None);
            var afterPublish = await store.GetSnapshotAsync(CancellationToken.None);
            Assert.NotSame(first, afterPublish);
            Assert.Single(afterPublish.Groups);
            Assert.Same(afterPublish, await store.GetSnapshotAsync(CancellationToken.None));
        }
        finally
        {
            File.Delete(databasePath);
            File.Delete($"{databasePath}-shm");
            File.Delete($"{databasePath}-wal");
        }
    }

    private static async Task WriteAdsResponseAsync(NetworkStream stream, byte[] requestHeader, byte[] payload)
    {
        var amsLen = 32 + payload.Length;
        var frame = new byte[6 + amsLen];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)amsLen);
        Buffer.BlockCopy(requestHeader, 14, frame, 6, 8);
        Buffer.BlockCopy(requestHeader, 6, frame, 14, 8);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(22, 2), BinaryPrimitives.ReadUInt16LittleEndian(requestHeader.AsSpan(22, 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24, 2), 0x0005);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(26, 4), (uint)payload.Length);
        Buffer.BlockCopy(requestHeader, 34, frame, 34, 4);
        Buffer.BlockCopy(payload, 0, frame, 38, payload.Length);
        await stream.WriteAsync(frame);
    }
}
