using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Application;
using Snet.Edge.Domain;
using Snet.Edge.Infrastructure;
using Snet.Edge.Infrastructure.GatewayMqtt;
using Xunit;

namespace Snet.Edge.Tests;

public sealed class DeviceWriteBackTests
{
    [Fact]
    public async Task WriteService_RejectsReadOnlyTag_ByDefault()
    {
        var harness = await WriteTestHarness.CreateAsync(new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = WriteTestHarness.DefaultDeviceId,
            Name = "TargetSpeed",
            Address = "100",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Writable = false
        });

        var ack = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-readonly-01",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("TargetSpeed", 120)]),
            "MQTT",
            CancellationToken.None);

        Assert.False(ack.Success);
        var item = Assert.Single(ack.Results);
        Assert.Equal(TagWriteStatus.RejectedReadOnly, item.Status);
        Assert.Equal(0, harness.Executor.WriteCount);
    }

    [Fact]
    public async Task WriteService_RejectsOutOfRangeEngineeringValue()
    {
        var harness = await WriteTestHarness.CreateAsync(new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = WriteTestHarness.DefaultDeviceId,
            Name = "SetTemperature",
            Address = "102",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Writable = true,
            Operator = "*",
            Operand = 0.1m,
            Decimals = 1,
            MinWriteValue = 10.0m,
            MaxWriteValue = 80.0m
        });

        var tooLowAck = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-range-low",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("SetTemperature", 5.5m)]),
            "REST",
            CancellationToken.None);

        Assert.False(tooLowAck.Success);
        Assert.Equal(TagWriteStatus.OutOfRange, Assert.Single(tooLowAck.Results).Status);

        var tooHighAck = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-range-high",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("SetTemperature", 85.0m)]),
            "REST",
            CancellationToken.None);

        Assert.False(tooHighAck.Success);
        Assert.Equal(TagWriteStatus.OutOfRange, Assert.Single(tooHighAck.Results).Status);
        Assert.Equal(0, harness.Executor.WriteCount);
    }

    [Fact]
    public async Task WriteService_RejectsExpiredCommand_BeyondTtl()
    {
        var harness = await WriteTestHarness.CreateAsync(new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = WriteTestHarness.DefaultDeviceId,
            Name = "StartSignal",
            Address = "10",
            DataType = TagDataType.Bool,
            ModbusFunctionCode = ModbusFunctionCode.ReadCoils,
            Writable = true
        });

        var expiredTimestamp = DateTimeOffset.UtcNow.AddSeconds(-25);
        var ack = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-expired-01",
                "PLC_01",
                expiredTimestamp,
                [new TagWriteItem("StartSignal", true)],
                TtlMs: 5_000),
            "MQTT",
            CancellationToken.None);

        Assert.False(ack.Success);
        Assert.Equal(TagWriteStatus.Expired, Assert.Single(ack.Results).Status);
        Assert.Equal(0, harness.Executor.WriteCount);
    }

    [Fact]
    public async Task WriteService_DeduplicatesRepeatedCommandId_AndEnforcesPerDeviceRateLimit()
    {
        var harness = await WriteTestHarness.CreateAsync(
            new TagDefinition
            {
                Id = Guid.NewGuid(),
                DeviceId = WriteTestHarness.DefaultDeviceId,
                Name = "RecipeNo",
                Address = "200",
                DataType = TagDataType.UInt16,
                ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                Writable = true
            },
            maxRequestsPerSecond: 3);

        var cmd = new DeviceWriteCommand(
            "cmd-dedup-001",
            "PLC_01",
            DateTimeOffset.UtcNow,
            [new TagWriteItem("RecipeNo", 18)]);

        var firstAck = await harness.WriteService.ExecuteAsync(cmd, "MQTT", CancellationToken.None);
        var secondAck = await harness.WriteService.ExecuteAsync(cmd, "MQTT", CancellationToken.None);

        Assert.True(firstAck.Success);
        Assert.False(firstAck.Deduplicated);
        Assert.True(secondAck.Success);
        Assert.True(secondAck.Deduplicated);
        Assert.Equal(1, harness.Executor.WriteCount);

        // Execute 2 more distinct commands to reach the 3 req/s limit, then the 4th in the same second is rate-limited
        _ = await harness.WriteService.ExecuteAsync(cmd with { CommandId = "cmd-rate-2" }, "MQTT", CancellationToken.None);
        _ = await harness.WriteService.ExecuteAsync(cmd with { CommandId = "cmd-rate-3" }, "MQTT", CancellationToken.None);
        var rateLimitedAck = await harness.WriteService.ExecuteAsync(cmd with { CommandId = "cmd-rate-4" }, "MQTT", CancellationToken.None);

        Assert.False(rateLimitedAck.Success);
        Assert.Equal(TagWriteStatus.RateLimited, Assert.Single(rateLimitedAck.Results).Status);
    }

    [Fact]
    public async Task WriteService_InverseScalesEngineeringValue_VerifiesReadBack_AndUpdatesTelemetryImmediately()
    {
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = WriteTestHarness.DefaultDeviceId,
            Name = "OvenTempTarget",
            Address = "300",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Writable = true,
            Operator = "*",
            Operand = 0.1m,
            Decimals = 1,
            MinWriteValue = 0m,
            MaxWriteValue = 200m
        };

        var harness = await WriteTestHarness.CreateAsync(tag);
        var ack = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-scale-01",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("OvenTempTarget", 45.6m)],
                Operator: "mes-operator"),
            "REST",
            CancellationToken.None);

        Assert.True(ack.Success);
        var result = Assert.Single(ack.Results);
        Assert.Equal(TagWriteStatus.Success, result.Status);
        Assert.Equal((short)456, result.WrittenRawValue);
        Assert.Equal(45.6m, result.ReadBackValue);

        var latestSamples = harness.TelemetryBuffer.GetByDevice(WriteTestHarness.DefaultDeviceId);
        var sample = Assert.Single(latestSamples);
        Assert.Equal("OvenTempTarget", sample.TagName);
        Assert.Equal((short)456, sample.RawValue);
        Assert.Equal(45.6m, sample.Value);
        Assert.Equal(DataQuality.Good, sample.Quality);
    }

    [Fact]
    public async Task AcquisitionWorker_ReusesActiveConnectionForWrite_AndNeverTriggersWriteFromReadLoop()
    {
        var groupId = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var tag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            Name = "LineSpeed",
            Address = "D100",
            DataType = TagDataType.Int16,
            Writable = true,
            Operator = "*",
            Operand = 0.1m,
            Decimals = 1
        };
        var group = new MqttGroup
        {
            Id = groupId,
            Name = "Line1",
            Topic = "edge/line1/telemetry",
            ScanCycleMs = 150,
            UploadCycleSeconds = 2,
            Enabled = true
        };
        var device = new DeviceDefinition
        {
            Id = deviceId,
            GroupId = groupId,
            Name = "MC_PLC_01",
            MqttDeviceId = "MC_PLC_01",
            Protocol = ProtocolKind.MitsubishiMc,
            Host = "127.0.0.1",
            Port = 5000,
            Enabled = true
        };

        var store = new InMemorySnapshotStore(new GatewaySnapshot([group], [device], [tag], new MqttOptions()));
        var fakeDriverFactory = new SpyReusableDriverFactory((short)250);
        var telemetry = new TelemetryBuffer();
        var statusStore = new RuntimeStatusStore();
        var control = new AcquisitionControl();
        var metrics = new GatewayMetrics();
        var worker = new AcquisitionWorker(
            store,
            fakeDriverFactory,
            telemetry,
            statusStore,
            control,
            new AlwaysAllowResourceGuard(),
            metrics,
            new AcquisitionStabilityOptions(),
            NullLogger<AcquisitionWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
            while (fakeDriverFactory.TotalReads < 2 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(30);
            }

            // Verify normal polling never issues any write call
            Assert.True(fakeDriverFactory.TotalReads >= 2);
            Assert.Equal(0, fakeDriverFactory.TotalWrites);
            Assert.Equal(1, fakeDriverFactory.CreateCount);

            // Now execute a write command while acquisition loop is actively running
            var writeResult = await worker.ExecuteTagWriteAsync(group, device, tag, (short)365, CancellationToken.None);
            Assert.True(writeResult.IsSuccess);
            Assert.Equal((short)365, writeResult.WrittenRawValue);
            Assert.Equal(36.5m, writeResult.ReadBackEngineeringValue);

            // Verify the write reused the single active connected driver (CreateCount is still 1!)
            Assert.Equal(1, fakeDriverFactory.CreateCount);
            Assert.Equal(1, fakeDriverFactory.TotalWrites);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ConfigurationValidator_PreventsGroupWriteBackTopicCollision_AndModbusReadOnlyWriteBack()
    {
        var groupId1 = Guid.NewGuid();
        var groupId2 = Guid.NewGuid();
        var deviceId = Guid.NewGuid();
        var group1 = new MqttGroup
        {
            Id = groupId1,
            Name = "G1",
            Line = "LINE_1",
            Topic = "factory/${line}/${deviceId}/cmd",
            WriteBackEnabled = true,
            WriteCommandTopic = "factory/${line}/${deviceId}/cmd",
            WriteAckTopic = "factory/${line}/${deviceId}/cmd"
        };
        var group2 = new MqttGroup
        {
            Id = groupId2,
            Name = "G2",
            Line = "LINE_1",
            Topic = "factory/line2/${deviceId}/data",
            WriteBackEnabled = true,
            WriteCommandTopic = "factory/${line}/${deviceId}/cmd",
            WriteAckTopic = "factory/line2/${deviceId}/ack"
        };
        var device = new DeviceDefinition
        {
            Id = deviceId,
            GroupId = groupId1,
            Name = "Modbus01",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = 502
        };
        var readOnlyInputTag = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceId,
            Name = "InputTemp",
            Address = "10",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadInputRegisters,
            Writable = true,
            MinWriteValue = 100m,
            MaxWriteValue = 50m
        };
        var mqtt = new MqttOptions
        {
            Enabled = true,
            GatewayId = "GW_01",
            WriteBackEnabled = true
        };

        var validator = new GatewayConfigurationValidator();
        var result = validator.Validate(new GatewaySnapshot([group1, group2], [device], [readOnlyInputTag], mqtt));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("只读功能码 02/04 时不允许开启反写"));
        Assert.Contains(result.Errors, e => e.Contains("反写最小值不能大于最大值"));
        Assert.Contains(result.Errors, e => e.Contains("反写命令Topic与回执Topic不能相同"));
        Assert.Contains(result.Errors, e => e.Contains("防止读写回环"));
        Assert.Contains(result.Errors, e => e.Contains("反写命令Topic路由重复"));

        var filter = MqttStoreForwardWorker.BuildCommandSubscriptionFilter(
            "factory/${line}/${deviceId}/cmd",
            "GW_01",
            "LINE_A",
            "LineAGroup");
        Assert.Equal("factory/LINE_A/+/cmd", filter);
        Assert.True(MqttStoreForwardWorker.TryMatchCommandTopic(
            "factory/${line}/${deviceId}/cmd",
            "GW_01",
            "LINE_A",
            "LineAGroup",
            "factory/LINE_A/S7_1500_A/cmd",
            out var extractedDevice));
        Assert.Equal("S7_1500_A", extractedDevice);
        Assert.False(MqttStoreForwardWorker.TryMatchCommandTopic(
            "factory/${line}/${deviceId}/cmd",
            "GW_01",
            "LINE_B",
            "LineBGroup",
            "factory/LINE_A/S7_1500_A/cmd",
            out _));
        Assert.Equal(
            "factory/LINE_A/S7_1500_A/ack",
            MqttStoreForwardWorker.BuildAckTopic(
                "factory/${line}/${deviceId}/ack",
                "GW_01",
                "LINE_A",
                "LineAGroup",
                "S7_1500_A",
                "一号PLC"));
    }

    [Fact]
    public async Task WriteService_RejectsCrossGroupUnauthorizedWrite_AndDisabledGroupWrite()
    {
        var harness = await WriteTestHarness.CreateAsync(new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = WriteTestHarness.DefaultDeviceId,
            Name = "TargetSpeed",
            Address = "100",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Writable = true
        });

        var otherGroupId = Guid.NewGuid();
        var crossAck = await harness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-cross-group-01",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("TargetSpeed", 120)],
                ScopedGroupId: otherGroupId),
            "MQTT",
            CancellationToken.None);

        Assert.False(crossAck.Success);
        Assert.Equal(TagWriteStatus.RejectedReadOnly, Assert.Single(crossAck.Results).Status);
        Assert.Contains("跨分组越权拒绝", crossAck.Error);
        Assert.Equal(0, harness.Executor.WriteCount);

        var disabledGroupHarness = await WriteTestHarness.CreateAsync(
            new TagDefinition
            {
                Id = Guid.NewGuid(),
                DeviceId = WriteTestHarness.DefaultDeviceId,
                Name = "TargetSpeed",
                Address = "100",
                DataType = TagDataType.Int16,
                ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
                Writable = true
            },
            groupWriteBackEnabled: false);

        var disabledAck = await disabledGroupHarness.WriteService.ExecuteAsync(
            new DeviceWriteCommand(
                "cmd-disabled-group-01",
                "PLC_01",
                DateTimeOffset.UtcNow,
                [new TagWriteItem("TargetSpeed", 120)]),
            "REST",
            CancellationToken.None);

        Assert.False(disabledAck.Success);
        Assert.Contains("未启用反写功能", disabledAck.Error);
        Assert.Equal(0, disabledGroupHarness.Executor.WriteCount);
    }

    [Fact]
    public async Task MqttStoreForwardWorker_SubscribesGroupWriteTopics_RoutesWriteAndPublishesGroupAckOverTcpBroker()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"snet-mqtt-wb-{Guid.NewGuid():N}.db");
        var groupAId = Guid.NewGuid();
        var groupBId = Guid.NewGuid();
        var deviceAId = Guid.NewGuid();
        var groupA = new MqttGroup
        {
            Id = groupAId,
            Name = "LineA",
            Line = "LINE_A",
            Topic = "factory/${line}/${deviceId}/data",
            WriteBackEnabled = true,
            WriteCommandTopic = "factory/${line}/${deviceId}/cmd",
            WriteAckTopic = "factory/${line}/${deviceId}/ack",
            Enabled = true
        };
        var groupB = new MqttGroup
        {
            Id = groupBId,
            Name = "LineB",
            Line = "LINE_B",
            Topic = "factory/${line}/${deviceId}/data",
            WriteBackEnabled = true,
            WriteCommandTopic = "factory/${line}/${deviceId}/cmd",
            WriteAckTopic = "factory/${line}/${deviceId}/ack",
            Enabled = true
        };
        var deviceA = new DeviceDefinition
        {
            Id = deviceAId,
            GroupId = groupAId,
            Name = "PLC_A",
            MqttDeviceId = "PLC_A",
            Protocol = ProtocolKind.ModbusTcp,
            Host = "127.0.0.1",
            Port = 502,
            Enabled = true
        };
        var tagA = new TagDefinition
        {
            Id = Guid.NewGuid(),
            DeviceId = deviceAId,
            Name = "TempSet",
            Address = "100",
            DataType = TagDataType.Int16,
            ModbusFunctionCode = ModbusFunctionCode.ReadHoldingRegisters,
            Writable = true,
            Operator = "*",
            Operand = 0.1m,
            Decimals = 1
        };

        var subscribedTopics = new ConcurrentQueue<string>();
        var publishedAcks = new ConcurrentQueue<(string Topic, string Payload)>();
        var twoSubscriptionsReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ackPublishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var crossAckPublishedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        NetworkStream? brokerStream = null;
        var streamReady = new TaskCompletionSource<NetworkStream>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var broker = await LoopbackPlcServer.StartAsync(async stream =>
        {
            brokerStream = stream;
            streamReady.TrySetResult(stream);
            byte protoVersion = 5;
            while (true)
            {
                var packet = await ReadMqttPacketAsync(stream);
                if (packet is null)
                {
                    break;
                }

                var (typeAndFlags, body) = packet.Value;
                var packetType = (byte)(typeAndFlags >> 4);
                if (packetType == 1) // CONNECT -> CONNACK
                {
                    var nameLen = (body[0] << 8) | body[1];
                    protoVersion = body[2 + nameLen];
                    byte[] connack = protoVersion == 5
                        ? [0x20, 0x03, 0x00, 0x00, 0x00]
                        : [0x20, 0x02, 0x00, 0x00];
                    await stream.WriteAsync(connack);
                }
                else if (packetType == 8) // SUBSCRIBE -> SUBACK
                {
                    var packetIdHi = body[0];
                    var packetIdLo = body[1];
                    var pos = 2;
                    if (protoVersion == 5)
                    {
                        var propLen = ReadVarInt(body, ref pos);
                        pos += propLen;
                    }

                    var granted = new List<byte>();
                    while (pos + 2 < body.Length)
                    {
                        var len = (body[pos] << 8) | body[pos + 1];
                        pos += 2;
                        var topic = Encoding.UTF8.GetString(body, pos, len);
                        pos += len + 1;
                        subscribedTopics.Enqueue(topic);
                        granted.Add(0x01);
                    }

                    byte[] suback = protoVersion == 5
                        ? [0x90, (byte)(3 + granted.Count), packetIdHi, packetIdLo, 0x00, .. granted]
                        : [0x90, (byte)(2 + granted.Count), packetIdHi, packetIdLo, .. granted];
                    await stream.WriteAsync(suback);
                    if (subscribedTopics.Count >= 2)
                    {
                        twoSubscriptionsReady.TrySetResult();
                    }
                }
                else if (packetType == 3) // PUBLISH from gateway (ACK or outbox)
                {
                    var qos = (typeAndFlags >> 1) & 0x03;
                    var tLen = (body[0] << 8) | body[1];
                    var topic = Encoding.UTF8.GetString(body, 2, tLen);
                    var pos = 2 + tLen;
                    if (qos > 0)
                    {
                        var pidHi = body[pos];
                        var pidLo = body[pos + 1];
                        pos += 2;
                        await stream.WriteAsync(new byte[] { 0x40, 0x02, pidHi, pidLo }); // PUBACK
                    }

                    if (protoVersion == 5)
                    {
                        var propLen = ReadVarInt(body, ref pos);
                        pos += propLen;
                    }

                    var payload = Encoding.UTF8.GetString(body, pos, body.Length - pos);
                    publishedAcks.Enqueue((topic, payload));
                    if (topic == "factory/LINE_A/PLC_A/ack")
                    {
                        ackPublishedTcs.TrySetResult();
                    }
                    else if (topic == "factory/LINE_B/PLC_A/ack")
                    {
                        crossAckPublishedTcs.TrySetResult();
                    }
                }
                else if (packetType == 12) // PINGREQ -> PINGRESP
                {
                    await stream.WriteAsync(new byte[] { 0xD0, 0x00 });
                }
            }
        });

        try
        {
            var mqtt = new MqttOptions
            {
                Enabled = true,
                GatewayId = "GW_TEST",
                Host = "127.0.0.1",
                Port = broker.Port,
                ClientId = "EdgeTestClient",
                Qos = 1,
                WriteBackEnabled = true,
                StoreAndForwardEnabled = false
            };
            var snapshot = new GatewaySnapshot([groupA, groupB], [deviceA], [tagA], mqtt);
            var store = new InMemorySnapshotStore(snapshot);
            var telemetry = new TelemetryBuffer();
            var executor = new FakeDeviceCommandExecutor(telemetry);
            var audit = new InMemoryAuditLogStore();
            var writeService = new DeviceWriteService(store, executor, audit, NullLogger<DeviceWriteService>.Instance);

            var templateManager = new MqttTemplateManager($"Data Source={dbPath};Pooling=False");
            await templateManager.InitializeAsync(CancellationToken.None);
            var resolver = new MqttVariableResolver();
            var outbox = new MqttOutbox(
                $"Data Source={dbPath};Pooling=False",
                store,
                new AlwaysAllowResourceGuard(),
                templateManager,
                new MqttPayloadBuilder(resolver),
                new MqttTopicBuilder(resolver));
            await outbox.InitializeAsync(CancellationToken.None);

            var networkService = new StubNetworkConfigurationService();
            var worker = new MqttStoreForwardWorker(
                outbox,
                store,
                networkService,
                new MqttRuntimeStateStore(),
                new GatewayMetrics(),
                NullLogger<MqttStoreForwardWorker>.Instance,
                writeService);

            await worker.StartAsync(CancellationToken.None);
            try
            {
                await twoSubscriptionsReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Contains("factory/LINE_A/+/cmd", subscribedTopics);
                Assert.Contains("factory/LINE_B/+/cmd", subscribedTopics);

                var stream = await streamReady.Task;

                // 1) Publish valid command on LineA topic -> succeeds and publishes ACK on LineA ack topic
                var validPayload = JsonSerializer.Serialize(new
                {
                    commandId = "mqtt-e2e-ok",
                    timestamp = DateTimeOffset.UtcNow,
                    writes = new[] { new { tag = "TempSet", value = 45.6m } }
                });
                await SendMqttPublishToClientAsync(stream, "factory/LINE_A/PLC_A/cmd", validPayload, retain: false);
                await ackPublishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, executor.WriteCount);

                // 2) Publish cross-group command on LineB topic targeting PLC_A (belongs to LineA) -> rejected!
                var crossPayload = JsonSerializer.Serialize(new
                {
                    commandId = "mqtt-e2e-cross",
                    timestamp = DateTimeOffset.UtcNow,
                    writes = new[] { new { tag = "TempSet", value = 55.0m } }
                });
                await SendMqttPublishToClientAsync(stream, "factory/LINE_B/PLC_A/cmd", crossPayload, retain: false);
                await crossAckPublishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(1, executor.WriteCount); // WriteCount stays 1!

                // 3) Retained command must be ignored completely
                var retainedPayload = JsonSerializer.Serialize(new
                {
                    commandId = "mqtt-e2e-retain",
                    timestamp = DateTimeOffset.UtcNow,
                    writes = new[] { new { tag = "TempSet", value = 88.0m } }
                });
                await SendMqttPublishToClientAsync(stream, "factory/LINE_A/PLC_A/cmd", retainedPayload, retain: true);
                await Task.Delay(150);
                Assert.Equal(1, executor.WriteCount);

                var okAck = publishedAcks.First(a => a.Topic == "factory/LINE_A/PLC_A/ack");
                Assert.Contains("\"success\":true", okAck.Payload, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("456", okAck.Payload);

                var crossAck = publishedAcks.First(a => a.Topic == "factory/LINE_B/PLC_A/ack");
                Assert.Contains("\"success\":false", crossAck.Payload, StringComparison.OrdinalIgnoreCase);
                Assert.Contains("跨分组越权拒绝", crossAck.Payload);
            }
            finally
            {
                await worker.StopAsync(CancellationToken.None);
            }
        }
        finally
        {
            foreach (var f in new[] { dbPath, $"{dbPath}-shm", $"{dbPath}-wal" })
            {
                if (File.Exists(f))
                {
                    File.Delete(f);
                }
            }
        }
    }

    private static async Task<(byte FirstByte, byte[] Body)?> ReadMqttPacketAsync(NetworkStream stream)
    {
        var first = new byte[1];
        if (await stream.ReadAsync(first) <= 0)
        {
            return null;
        }

        var remainingLength = 0;
        var multiplier = 1;
        var b = new byte[1];
        while (true)
        {
            if (await stream.ReadAsync(b) <= 0)
            {
                return null;
            }

            remainingLength += (b[0] & 0x7F) * multiplier;
            if ((b[0] & 0x80) == 0)
            {
                break;
            }

            multiplier *= 128;
        }

        var body = new byte[remainingLength];
        if (remainingLength > 0 && !await ReadFullAsync(stream, body))
        {
            return null;
        }

        return (first[0], body);
    }

    private static int ReadVarInt(byte[] buffer, ref int pos)
    {
        var value = 0;
        var multiplier = 1;
        while (pos < buffer.Length)
        {
            var b = buffer[pos++];
            value += (b & 0x7F) * multiplier;
            if ((b & 0x80) == 0)
            {
                break;
            }

            multiplier *= 128;
        }

        return value;
    }

    private static async Task SendMqttPublishToClientAsync(
        NetworkStream stream,
        string topic,
        string payload,
        bool retain)
    {
        var topicBytes = Encoding.UTF8.GetBytes(topic);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var bodyLen = 2 + topicBytes.Length + 1 + payloadBytes.Length; // +1 for MQTT 5.0 zero PropertiesLength

        using var ms = new MemoryStream();
        ms.WriteByte(retain ? (byte)0x31 : (byte)0x30);
        var len = bodyLen;
        do
        {
            var encodedByte = (byte)(len % 128);
            len /= 128;
            if (len > 0)
            {
                encodedByte |= 0x80;
            }

            ms.WriteByte(encodedByte);
        }
        while (len > 0);

        ms.WriteByte((byte)(topicBytes.Length >> 8));
        ms.WriteByte((byte)(topicBytes.Length & 0xFF));
        ms.Write(topicBytes);
        ms.WriteByte(0x00); // MQTT 5.0 PropertiesLength = 0
        ms.Write(payloadBytes);
        await stream.WriteAsync(ms.ToArray());
    }

    private sealed class StubNetworkConfigurationService : INetworkConfigurationService
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<NetworkConfigurationView> GetAsync(bool forceRefresh, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NetworkConfigurationView(new NetworkBindingConfiguration(), [], []));
        public ValueTask<NetworkConfigurationView> SaveAsync(NetworkBindingConfiguration configuration, CancellationToken cancellationToken) =>
            ValueTask.FromResult(new NetworkConfigurationView(configuration, [], []));
        public string? ResolveLocalIpv4(NetworkUsage usage) => null;
        public string? ResolveInterfaceName(NetworkUsage usage) => null;
        public long GetRevision(NetworkUsage usage) => 0;
        public bool IsWebRequestAllowed(IPAddress? localAddress) => true;
    }

    [Fact]
    public async Task NativeProtocols_Modbus_S7_Mc_Fins_Mewtocol_Ads_Cip_WriteAndReadBackOverLoopback()
    {
        // 1. Modbus TCP write coil (0x05), single register (0x06), and multi-register (0x10)
        await using (var modbusServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var registers = new Dictionary<ushort, ushort>();
            var coils = new Dictionary<ushort, bool>();
            var header = new byte[7];
            while (await ReadFullAsync(stream, header))
            {
                var txId = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0, 2));
                var len = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4, 2));
                var unit = header[6];
                var pdu = new byte[len - 1];
                if (!await ReadFullAsync(stream, pdu))
                {
                    break;
                }

                var fc = pdu[0];
                var addr = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1, 2));
                if (fc == 0x05)
                {
                    coils[addr] = pdu[3] == 0xFF;
                    byte[] resp = [.. header[..4], 0x00, 0x06, unit, .. pdu[..5]];
                    await stream.WriteAsync(resp);
                }
                else if (fc == 0x01)
                {
                    var val = coils.GetValueOrDefault(addr) ? (byte)1 : (byte)0;
                    byte[] resp = [.. header[..4], 0x00, 0x04, unit, 0x01, 0x01, val];
                    await stream.WriteAsync(resp);
                }
                else if (fc == 0x06)
                {
                    registers[addr] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    byte[] resp = [.. header[..4], 0x00, 0x06, unit, .. pdu[..5]];
                    await stream.WriteAsync(resp);
                }
                else if (fc == 0x10)
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    for (var i = 0; i < count; i++)
                    {
                        registers[(ushort)(addr + i)] = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(6 + (i * 2), 2));
                    }

                    byte[] resp = [.. header[..4], 0x00, 0x06, unit, 0x10, pdu[1], pdu[2], pdu[3], pdu[4]];
                    await stream.WriteAsync(resp);
                }
                else if (fc == 0x03)
                {
                    var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3, 2));
                    var resp = new byte[9 + (count * 2)];
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(0, 2), txId);
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(4, 2), (ushort)(3 + (count * 2)));
                    resp[6] = unit;
                    resp[7] = 0x03;
                    resp[8] = (byte)(count * 2);
                    for (var i = 0; i < count; i++)
                    {
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(9 + (i * 2), 2), registers.GetValueOrDefault((ushort)(addr + i)));
                    }

                    await stream.WriteAsync(resp);
                }
            }
        }))
        {
            using var modbus = new ModbusTcpNet("127.0.0.1", modbusServer.Port, 1);
            Assert.True((await modbus.ConnectServerAsync()).IsSuccess);
            Assert.True((await modbus.WriteBoolAsync("x=1;10", true)).IsSuccess);
            Assert.True((await modbus.ReadBoolAsync("x=1;10")).Content);
            Assert.True((await modbus.WriteAsync("x=3;100", (short)1234)).IsSuccess);
            Assert.Equal((short)1234, (await modbus.ReadInt16Async("x=3;100")).Content);
            Assert.True((await modbus.WriteAsync("x=3;200", 98765432)).IsSuccess);
            Assert.Equal(98765432, (await modbus.ReadInt32Async("x=3;200")).Content);
        }

        // 2. Siemens S7 Write (0x05) & ReadBack (0x04)
        await using (var s7Server = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var lastWritten = new byte[4];
            var hdr = new byte[4];
            while (await ReadFullAsync(stream, hdr))
            {
                var totalLen = BinaryPrimitives.ReadUInt16BigEndian(hdr.AsSpan(2, 2));
                var body = new byte[totalLen - 4];
                if (!await ReadFullAsync(stream, body))
                {
                    break;
                }

                if ((body[1] & 0xF0) == 0xE0)
                {
                    await stream.WriteAsync(new byte[] { 0x03, 0x00, 0x00, 0x0B, 0x06, 0xD0, 0x00, 0x01, 0x00, 0x00, 0x00 });
                }
                else if (body[3] == 0x32 && body[13] == 0xF0)
                {
                    await stream.WriteAsync(new byte[]
                    {
                        0x03, 0x00, 0x00, 0x1B, 0x02, 0xF0, 0x80, 0x32, 0x03, 0x00, 0x00,
                        body[7], body[8], 0x00, 0x08, 0x00, 0x00, 0x00, 0x00,
                        0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0
                    });
                }
                else if (body[3] == 0x32 && body[13] == 0x05)
                {
                    var dataLen = BinaryPrimitives.ReadUInt16BigEndian(body.AsSpan(11, 2)) - 4;
                    lastWritten = body.AsSpan(31, dataLen).ToArray();
                    await stream.WriteAsync(new byte[]
                    {
                        0x03, 0x00, 0x00, 0x16, 0x02, 0xF0, 0x80, 0x32, 0x03, 0x00, 0x00,
                        body[7], body[8], 0x00, 0x02, 0x00, 0x01, 0x00, 0x00,
                        0x05, 0x01, 0xFF
                    });
                }
                else if (body[3] == 0x32 && body[13] == 0x04)
                {
                    var resp = new byte[25 + lastWritten.Length];
                    resp[0] = 0x03;
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), (ushort)resp.Length);
                    resp[4] = 0x02;
                    resp[5] = 0xF0;
                    resp[6] = 0x80;
                    resp[7] = 0x32;
                    resp[8] = 0x03;
                    resp[11] = body[7];
                    resp[12] = body[8];
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(13, 2), 2);
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(15, 2), (ushort)(4 + lastWritten.Length));
                    resp[19] = 0x04;
                    resp[20] = 0x01;
                    resp[21] = 0xFF;
                    resp[22] = 0x04;
                    BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(23, 2), (ushort)(lastWritten.Length * 8));
                    Buffer.BlockCopy(lastWritten, 0, resp, 25, lastWritten.Length);
                    await stream.WriteAsync(resp);
                }
            }
        }))
        {
            using var s7 = new SiemensS7Net(SiemensPLCS.S1500, "127.0.0.1") { Port = s7Server.Port };
            Assert.True((await s7.ConnectServerAsync()).IsSuccess);
            Assert.True((await s7.WriteAsync("DB1.DBD0", 20260403)).IsSuccess);
            Assert.Equal(20260403, (await s7.ReadInt32Async("DB1.DBD0")).Content);
        }

        // 3. Mitsubishi MC 3E Binary Write (0x1401) & ReadBack (0x0401)
        await using (var mcServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var lastWord = new byte[2];
            var head = new byte[11];
            while (await ReadFullAsync(stream, head))
            {
                var bodyLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(7, 2)) - 2;
                var body = new byte[bodyLen];
                if (!await ReadFullAsync(stream, body))
                {
                    break;
                }

                var cmd = BinaryPrimitives.ReadUInt16LittleEndian(body.AsSpan(0, 2));
                if (cmd == 0x1401)
                {
                    lastWord = body.AsSpan(10).ToArray();
                    await stream.WriteAsync(new byte[] { 0xD0, 0x00, 0x00, 0xFF, 0xFF, 0x03, 0x00, 0x02, 0x00, 0x00, 0x00 });
                }
                else if (cmd == 0x0401)
                {
                    var resp = new byte[11 + lastWord.Length];
                    resp[0] = 0xD0;
                    resp[3] = 0xFF;
                    resp[4] = 0xFF;
                    resp[5] = 0x03;
                    BinaryPrimitives.WriteUInt16LittleEndian(resp.AsSpan(7, 2), (ushort)(2 + lastWord.Length));
                    Buffer.BlockCopy(lastWord, 0, resp, 11, lastWord.Length);
                    await stream.WriteAsync(resp);
                }
            }
        }))
        {
            using var mc = new MelsecMcNet("127.0.0.1", mcServer.Port);
            Assert.True((await mc.ConnectServerAsync()).IsSuccess);
            Assert.True((await mc.WriteAsync("D100", (short)4321)).IsSuccess);
            Assert.Equal((short)4321, (await mc.ReadInt16Async("D100")).Content);
        }

        // 4. Omron FINS/TCP Write (0x01 0x02) & ReadBack (0x01 0x01)
        await using (var finsServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var hs = new byte[20];
            if (!await ReadFullAsync(stream, hs))
            {
                return;
            }

            await stream.WriteAsync(new byte[]
            {
                0x46, 0x49, 0x4E, 0x53, 0x00, 0x00, 0x00, 0x10,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00,
                0x00, 0x00, 0x00, 0x0B, 0x00, 0x00, 0x00, 0x01
            });

            var lastData = new byte[2];
            var tcpHead = new byte[16];
            while (await ReadFullAsync(stream, tcpHead))
            {
                var bodyLen = (int)BinaryPrimitives.ReadUInt32BigEndian(tcpHead.AsSpan(4, 4)) - 8;
                var body = new byte[bodyLen];
                if (!await ReadFullAsync(stream, body))
                {
                    break;
                }

                if (body[10] == 0x01 && body[11] == 0x02)
                {
                    lastData = body.AsSpan(18).ToArray();
                    await stream.WriteAsync(new byte[]
                    {
                        0x46, 0x49, 0x4E, 0x53, 0x00, 0x00, 0x00, 0x16,
                        0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
                        0xC0, 0x00, 0x02, 0x00, 0x0B, 0x00, 0x00, 0x01, 0x00, body[9],
                        0x01, 0x02, 0x00, 0x00
                    });
                }
                else if (body[10] == 0x01 && body[11] == 0x01)
                {
                    byte[] resp =
                    [
                        0x46, 0x49, 0x4E, 0x53, 0x00, 0x00, 0x00, (byte)(22 + lastData.Length),
                        0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00,
                        0xC0, 0x00, 0x02, 0x00, 0x0B, 0x00, 0x00, 0x01, 0x00, body[9],
                        0x01, 0x01, 0x00, 0x00,
                        .. lastData
                    ];
                    await stream.WriteAsync(resp);
                }
            }
        }))
        {
            using var fins = new OmronFinsNet("127.0.0.1", finsServer.Port);
            Assert.True((await fins.ConnectServerAsync()).IsSuccess);
            Assert.True((await fins.WriteAsync("D200", (short)7788)).IsSuccess);
            Assert.Equal((short)7788, (await fins.ReadInt16Async("D200")).Content);
        }

        // 5. Panasonic Mewtocol Write (#WDD) & ReadBack (#RDD)
        await using (var mewtocolServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var lastHex = "0000";
            var buf = new List<byte>();
            var b = new byte[1];
            while (await stream.ReadAsync(b) > 0)
            {
                if (b[0] == '\r')
                {
                    var cmd = Encoding.ASCII.GetString(buf.ToArray());
                    buf.Clear();
                    if (cmd.Contains("#WDD", StringComparison.Ordinal))
                    {
                        var dataStart = cmd.IndexOf("#WDD", StringComparison.Ordinal) + 15;
                        lastHex = cmd.Substring(dataStart, 4);
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(PanasonicHelper.AppendBccAndCr("%01$WD")));
                    }
                    else if (cmd.Contains("#RDD", StringComparison.Ordinal))
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(PanasonicHelper.AppendBccAndCr($"%01$RD{lastHex}")));
                    }
                }
                else
                {
                    buf.Add(b[0]);
                }
            }
        }))
        {
            using var panasonic = new PanasonicMewtocolOverTcp("127.0.0.1", mewtocolServer.Port, 1);
            Assert.True((await panasonic.ConnectServerAsync()).IsSuccess);
            Assert.True((await panasonic.WriteAsync("DT10", (short)5566)).IsSuccess);
            Assert.Equal((short)5566, (await panasonic.ReadInt16Async("DT10")).Content);
        }

        // 6. Beckhoff ADS Write (0x0003) & ReadBack (0x0002)
        await using (var adsServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var lastBytes = new byte[2];
            var tcpHead = new byte[6];
            while (await ReadFullAsync(stream, tcpHead))
            {
                var amsLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tcpHead.AsSpan(2, 4));
                var ams = new byte[amsLen];
                if (!await ReadFullAsync(stream, ams))
                {
                    break;
                }

                var cmdId = BinaryPrimitives.ReadUInt16LittleEndian(ams.AsSpan(16, 2));
                var invokeId = BinaryPrimitives.ReadUInt32LittleEndian(ams.AsSpan(28, 4));
                byte[] payload;
                if (cmdId == 0x0003)
                {
                    lastBytes = ams.AsSpan(44).ToArray();
                    payload = new byte[4]; // result = 0
                }
                else
                {
                    payload = new byte[8 + lastBytes.Length];
                    BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), (uint)lastBytes.Length);
                    Buffer.BlockCopy(lastBytes, 0, payload, 8, lastBytes.Length);
                }

                var respFrame = new byte[6 + 32 + payload.Length];
                BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(2, 4), (uint)(32 + payload.Length));
                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(22, 2), cmdId);
                BinaryPrimitives.WriteUInt16LittleEndian(respFrame.AsSpan(24, 2), 0x0005);
                BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(26, 4), (uint)payload.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(respFrame.AsSpan(34, 4), invokeId);
                Buffer.BlockCopy(payload, 0, respFrame, 38, payload.Length);
                await stream.WriteAsync(respFrame);
            }
        }))
        {
            using var ads = new BeckhoffAdsNet("127.0.0.1", adsServer.Port);
            Assert.True((await ads.ConnectServerAsync()).IsSuccess);
            Assert.True((await ads.WriteAsync("MW10", (short)3141)).IsSuccess);
            Assert.Equal((short)3141, (await ads.ReadInt16Async("MW10")).Content);
        }

        // 7. Allen-Bradley EtherNet/IP CIP Write (0x4D -> 0xCD) & ReadBack (0x4C -> 0xCC)
        await using (var cipServer = await LoopbackPlcServer.StartAsync(async stream =>
        {
            var lastVal = new byte[2];
            var hdr = new byte[24];
            while (await ReadFullAsync(stream, hdr))
            {
                var cmd = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(0, 2));
                var len = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(2, 2));
                var body = len > 0 ? new byte[len] : [];
                if (len > 0 && !await ReadFullAsync(stream, body))
                {
                    break;
                }

                if (cmd == 0x0065)
                {
                    var regResp = new byte[28];
                    BinaryPrimitives.WriteUInt16LittleEndian(regResp.AsSpan(0, 2), 0x0065);
                    BinaryPrimitives.WriteUInt16LittleEndian(regResp.AsSpan(2, 2), 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(regResp.AsSpan(4, 4), 0x12345678);
                    Buffer.BlockCopy(body, 0, regResp, 24, 4);
                    await stream.WriteAsync(regResp);
                }
                else if (cmd == 0x006F)
                {
                    var innerService = body[26];
                    if (innerService == 0x4D)
                    {
                        var pathWords = body[27];
                        var dataOffset = 28 + (pathWords * 2) + 4;
                        lastVal = body.AsSpan(dataOffset, 2).ToArray();
                        byte[] cipReply = [0xCD, 0x00, 0x00, 0x00];
                        var resp = BuildCipRRResponse(0x12345678, cipReply);
                        await stream.WriteAsync(resp);
                    }
                    else if (innerService == 0x4C)
                    {
                        byte[] cipReply = [0xCC, 0x00, 0x00, 0x00, 0xC3, 0x00, .. lastVal];
                        var resp = BuildCipRRResponse(0x12345678, cipReply);
                        await stream.WriteAsync(resp);
                    }
                }
            }
        }))
        {
            using var ab = new AllenBradleyNet("127.0.0.1", cipServer.Port);
            Assert.True((await ab.ConnectServerAsync()).IsSuccess);
            Assert.True((await ab.WriteAsync("Program:Main.SetSpeed", (short)2880)).IsSuccess);
            Assert.Equal((short)2880, (await ab.ReadInt16Async("Program:Main.SetSpeed")).Content);
        }
    }

    private static byte[] BuildCipRRResponse(uint sessionHandle, byte[] cipReply)
    {
        var bodyLen = 16 + cipReply.Length;
        var frame = new byte[24 + bodyLen];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), 0x006F);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)bodyLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), sessionHandle);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 6, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 12, 2), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 14, 2), (ushort)cipReply.Length);
        Buffer.BlockCopy(cipReply, 0, frame, 24 + 16, cipReply.Length);
        return frame;
    }

    private static async Task<bool> ReadFullAsync(NetworkStream stream, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read));
            if (n <= 0)
            {
                return false;
            }

            read += n;
        }

        return true;
    }

    private sealed class WriteTestHarness
    {
        public static readonly Guid DefaultGroupId = Guid.NewGuid();
        public static readonly Guid DefaultDeviceId = Guid.NewGuid();

        public required IDeviceWriteService WriteService { get; init; }
        public required FakeDeviceCommandExecutor Executor { get; init; }
        public required TelemetryBuffer TelemetryBuffer { get; init; }
        public required InMemoryAuditLogStore AuditStore { get; init; }

        public static Task<WriteTestHarness> CreateAsync(
            TagDefinition tag,
            int maxRequestsPerSecond = 20,
            bool groupWriteBackEnabled = true)
        {
            var group = new MqttGroup
            {
                Id = DefaultGroupId,
                Name = "TestGroup",
                Line = "LINE1",
                Topic = "edge/test/telemetry",
                Enabled = true,
                WriteBackEnabled = groupWriteBackEnabled,
                WriteCommandTopic = "edge/${line}/${deviceId}/cmd",
                WriteAckTopic = "edge/${line}/${deviceId}/ack"
            };
            var device = new DeviceDefinition
            {
                Id = DefaultDeviceId,
                GroupId = DefaultGroupId,
                Name = "PLC_01",
                MqttDeviceId = "PLC_01",
                Protocol = ProtocolKind.ModbusTcp,
                Host = "127.0.0.1",
                Port = 502,
                Enabled = true
            };
            var mqtt = new MqttOptions
            {
                Enabled = true,
                WriteBackEnabled = true,
                WriteMaxRequestsPerSecondPerDevice = maxRequestsPerSecond
            };
            var snapshot = new GatewaySnapshot([group], [device], [tag], mqtt);
            var store = new InMemorySnapshotStore(snapshot);
            var telemetry = new TelemetryBuffer();
            var executor = new FakeDeviceCommandExecutor(telemetry);
            var audit = new InMemoryAuditLogStore();
            var service = new DeviceWriteService(store, executor, audit, NullLogger<DeviceWriteService>.Instance);

            return Task.FromResult(new WriteTestHarness
            {
                WriteService = service,
                Executor = executor,
                TelemetryBuffer = telemetry,
                AuditStore = audit
            });
        }
    }

    private sealed class FakeDeviceCommandExecutor(ITelemetryBuffer telemetryBuffer) : IDeviceCommandExecutor
    {
        private int _writeCount;
        public int WriteCount => Volatile.Read(ref _writeCount);

        public async ValueTask<DriverTagWriteExecution> ExecuteTagWriteAsync(
            MqttGroup group,
            DeviceDefinition device,
            TagDefinition tag,
            object rawValue,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _writeCount);
            var readBackEngineering = AcquisitionWorker.Transform(rawValue, tag);
            await telemetryBuffer.WriteAsync(new TagSample(
                group.Id,
                group.Topic,
                device.Id,
                device.Name,
                tag.Id,
                tag.Name,
                readBackEngineering,
                rawValue,
                DataQuality.Good,
                DateTimeOffset.UtcNow), cancellationToken);
            return new DriverTagWriteExecution(rawValue, readBackEngineering);
        }
    }

    private sealed class SpyReusableDriverFactory(object initialRawValue) : IDeviceDriverFactory
    {
        private object _currentRawValue = initialRawValue;
        private int _createCount;
        private int _totalReads;
        private int _totalWrites;

        public int CreateCount => Volatile.Read(ref _createCount);
        public int TotalReads => Volatile.Read(ref _totalReads);
        public int TotalWrites => Volatile.Read(ref _totalWrites);

        public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null)
        {
            Interlocked.Increment(ref _createCount);
            return new SpyDriver(device.Protocol, tags ?? [], this);
        }

        private sealed class SpyDriver(
            ProtocolKind protocol,
            IReadOnlyList<TagDefinition> tags,
            SpyReusableDriverFactory owner) : IDeviceDriver
        {
            public ProtocolKind Protocol => protocol;

            public ValueTask ConnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

            public ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._totalReads);
                return ValueTask.FromResult<object?>(Volatile.Read(ref owner._currentRawValue));
            }

            public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._totalReads);
                var current = Volatile.Read(ref owner._currentRawValue);
                var dict = tags.ToDictionary(t => t.Id, _ => DeviceTagReadResult.Success(current));
                return ValueTask.FromResult<IReadOnlyDictionary<Guid, DeviceTagReadResult>>(dict);
            }

            public ValueTask WriteAsync(TagDefinition tag, object value, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner._totalWrites);
                Volatile.Write(ref owner._currentRawValue, value);
                return ValueTask.CompletedTask;
            }

            public ValueTask DisconnectAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InMemorySnapshotStore(GatewaySnapshot snapshot) : IGatewayConfigurationStore
    {
        public ValueTask InitializeAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask<GatewaySnapshot> GetSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
        public ValueTask<GatewaySnapshot> GetDraftSnapshotAsync(CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
        public ValueTask<IReadOnlyList<ConfigurationRevision>> GetRevisionsAsync(int take, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyList<ConfigurationRevision>>([]);
        public ValueTask<GatewaySnapshot> GetRevisionSnapshotAsync(long version, CancellationToken cancellationToken) => ValueTask.FromResult(snapshot);
        public ValueTask<PublishResult> PublishDraftAsync(string reason, CancellationToken cancellationToken) => ValueTask.FromResult(new PublishResult(1, DateTimeOffset.UtcNow));
        public ValueTask<PublishResult> RollbackAsync(long version, long expectedActiveVersion, string reason, CancellationToken cancellationToken) => ValueTask.FromResult(new PublishResult(1, DateTimeOffset.UtcNow));
        public ValueTask DeleteRevisionAsync(long version, long expectedActiveVersion, string reason, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask UpsertGroupAsync(MqttGroup group, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteGroupAsync(Guid groupId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask UpsertDeviceAsync(DeviceDefinition device, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask DeleteDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReplaceTagsAsync(Guid deviceId, IReadOnlyCollection<TagDefinition> tags, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask SaveMqttOptionsAsync(MqttOptions options, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask ReplaceDraftAsync(GatewaySnapshot next, CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }

    private sealed class InMemoryAuditLogStore : IAuditLogStore
    {
        private readonly ConcurrentQueue<AuditEvent> _events = new();
        public bool TryWrite(AuditEvent auditEvent)
        {
            _events.Enqueue(auditEvent);
            return true;
        }

        public ValueTask<IReadOnlyList<AuditEvent>> GetRecentAsync(int take, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<AuditEvent>>(_events.Take(take).ToArray());
    }

    private sealed class AlwaysAllowResourceGuard : IResourceGuard
    {
        public ResourceSnapshot Current => new(
            ResourceProtectionLevel.Normal, 5, 100, 50, 20, 10_000, 20, DateTimeOffset.UtcNow, null);
        public bool AllowAcquisition => true;
        public bool AllowPersistentEnqueue => true;
    }

    private sealed class LoopbackPlcServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _acceptLoop;

        private LoopbackPlcServer(TcpListener listener, Func<NetworkStream, Task> handler)
        {
            _listener = listener;
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _acceptLoop = Task.Run(async () =>
            {
                try
                {
                    using var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    await using var stream = client.GetStream();
                    await handler(stream);
                }
                catch
                {
                }
            });
        }

        public int Port { get; }

        public static Task<LoopbackPlcServer> StartAsync(Func<NetworkStream, Task> handler)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            return Task.FromResult(new LoopbackPlcServer(listener, handler));
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            _listener.Stop();
            try
            {
                await _acceptLoop;
            }
            catch
            {
            }

            _cts.Dispose();
        }
    }
}
