using Opc.Ua;
using Opc.Ua.Client;
using System.Text;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class HslDeviceDriverFactory(
    ITagReadPlanCompiler planCompiler,
    INetworkConfigurationService? networkConfiguration = null) : IDeviceDriverFactory
{
    public IDeviceDriver Create(DeviceDefinition device, IReadOnlyList<TagDefinition>? tags = null)
    {
        var plan = planCompiler.Compile(device, tags ?? []);
        return device.Protocol switch
    {
        ProtocolKind.SiemensS7 => CreateHsl(device, plan, CreateSiemens(device)),
        ProtocolKind.ModbusTcp => CreateHsl(device, plan, new ModbusTcpNet(device.Host, device.Port, (byte)device.Station)),
        ProtocolKind.MitsubishiMc => CreateMitsubishiDriver(device, plan),
        ProtocolKind.PanasonicMewtocol => CreateHsl(device, plan, new PanasonicMewtocolOverTcp(device.Host, device.Port, (byte)device.Station)),
        ProtocolKind.OmronFins => CreateHsl(device, plan, CreateOmronFins(device)),
        ProtocolKind.OmronCip => CreateHsl(device, plan, new OmronConnectedCipNet(device.Host, device.Port)
        {
            ConnectionTimeoutMultiplier = (byte)device.OmronCipConnectionTimeoutMultiplier
        }),
        ProtocolKind.AllenBradleyEtherNetIp => CreateHsl(device, plan, CreateAllenBradley(device)),
        ProtocolKind.BeckhoffAds => CreateHsl(device, plan, CreateBeckhoff(device)),
        ProtocolKind.KeyenceMc => CreateHsl(device, plan, new KeyenceMcNet(device.Host, device.Port)),
        ProtocolKind.InovanceTcp => CreateHsl(device, plan, new InovanceTcpNet(
            MapInovanceSeries(device.InovanceSeries), device.Host, device.Port, (byte)device.Station)),
        ProtocolKind.DeltaTcp => CreateHsl(device, plan, CreateDelta(device)),
        ProtocolKind.OpcUa => new OpcUaDeviceDriver(device, plan),
        _ => throw new NotSupportedException($"不支持协议 {device.Protocol}。")
    };
    }

    private HslDeviceDriver CreateHsl(DeviceDefinition device, DeviceReadPlan plan, DeviceTcpNet client)
    {
        client.ConnectTimeOut = device.ConnectTimeoutMs;
        client.ReceiveTimeOut = device.ReceiveTimeoutMs;
        var localIpv4 = networkConfiguration?.ResolveLocalIpv4(NetworkUsage.PlcAcquisition);
        if (localIpv4 is not null)
        {
            client.LocalBinding = new IPEndPoint(IPAddress.Parse(localIpv4), 0);
        }

        return new HslDeviceDriver(device.Protocol, new HslTcpClientSession(client), plan);
    }

    private HslDeviceDriver CreateMitsubishiDriver(DeviceDefinition device, DeviceReadPlan plan)
    {
        if (device.MitsubishiFormat is MitsubishiMcFormat.Binary1E or MitsubishiMcFormat.Ascii1E &&
            device.MitsubishiTransport != MitsubishiMcTransport.Tcp)
        {
            throw new NotSupportedException("三菱A兼容1E帧仅支持TCP传输。");
        }

        return device.MitsubishiTransport switch
        {
            MitsubishiMcTransport.Tcp => CreateHsl(device, plan, CreateMitsubishiTcp(device)),
            MitsubishiMcTransport.Udp => CreateHslUdp(device, plan, CreateMitsubishiUdp(device)),
            _ => throw new NotSupportedException($"不支持三菱MC传输方式 {device.MitsubishiTransport}。")
        };
    }

    private HslDeviceDriver CreateHslUdp(DeviceDefinition device, DeviceReadPlan plan, DeviceTcpNet client)
    {
        client.ReceiveTimeOut = device.ReceiveTimeoutMs;
        var localIpv4 = networkConfiguration?.ResolveLocalIpv4(NetworkUsage.PlcAcquisition);
        if (localIpv4 is not null)
        {
            client.LocalBinding = new IPEndPoint(IPAddress.Parse(localIpv4), 0);
        }

        return new HslDeviceDriver(device.Protocol, new HslTcpClientSession(client, true), plan);
    }

    private static SiemensS7Net CreateSiemens(DeviceDefinition device)
    {
        var profile = SiemensConnectionProfile.Resolve(device);
        var client = new SiemensS7Net(MapSiemensSeries(device.SiemensSeries), device.Host)
        {
            Port = profile.Port,
            Rack = profile.Rack,
            Slot = profile.Slot,
            ConnectionType = profile.ConnectionType
        };
        if (device.SiemensLocalTsap > 0)
        {
            client.LocalTSAP = (ushort)device.SiemensLocalTsap;
        }

        if (device.SiemensDestinationTsap > 0)
        {
            client.DestTSAP = (ushort)device.SiemensDestinationTsap;
        }

        return client;
    }

    private static DeviceTcpNet CreateMitsubishiTcp(DeviceDefinition device) => device.MitsubishiFormat switch
    {
        MitsubishiMcFormat.Binary3E => ConfigureMitsubishi(new MelsecMcNet(device.Host, device.Port), device),
        MitsubishiMcFormat.Ascii3E => ConfigureMitsubishi(new MelsecMcAsciiNet(device.Host, device.Port), device),
        MitsubishiMcFormat.Binary1E => ConfigureMitsubishiA1E(new MelsecA1ENet(device.Host, device.Port), device),
        MitsubishiMcFormat.Ascii1E => ConfigureMitsubishiA1E(new MelsecA1EAsciiNet(device.Host, device.Port), device),
        _ => throw new NotSupportedException($"不支持三菱MC格式 {device.MitsubishiFormat}。")
    };

    private static DeviceTcpNet CreateMitsubishiUdp(DeviceDefinition device) => device.MitsubishiFormat switch
    {
        MitsubishiMcFormat.Binary3E => ConfigureMitsubishi(new MelsecMcUdp(device.Host, device.Port), device),
        MitsubishiMcFormat.Ascii3E => ConfigureMitsubishi(new MelsecMcAsciiUdp(device.Host, device.Port), device),
        MitsubishiMcFormat.Binary1E or MitsubishiMcFormat.Ascii1E =>
            throw new NotSupportedException("三菱A兼容1E帧仅支持TCP传输。"),
        _ => throw new NotSupportedException($"不支持三菱MC格式 {device.MitsubishiFormat}。")
    };

    private static T ConfigureMitsubishi<T>(T client, DeviceDefinition device)
        where T : IReadWriteMc
    {
        client.NetworkNumber = (byte)device.NetworkNumber;
        client.PLCNumber = (byte)device.PlcNumber;
        client.NetworkStationNumber = (byte)device.NetworkStationNumber;
        client.TargetIOStation = (ushort)device.TargetIoStation;
        return client;
    }

    private static MelsecA1ENet ConfigureMitsubishiA1E(MelsecA1ENet client, DeviceDefinition device)
    {
        client.PLCNumber = (byte)device.PlcNumber;
        return client;
    }

    private static MelsecA1EAsciiNet ConfigureMitsubishiA1E(MelsecA1EAsciiNet client, DeviceDefinition device)
    {
        client.PLCNumber = (byte)device.PlcNumber;
        return client;
    }

    private static OmronFinsNet CreateOmronFins(DeviceDefinition device)
    {
        var client = new OmronFinsNet(device.Host, device.Port)
        {
            DNA = (byte)device.FinsDestinationNetwork,
            DA2 = (byte)device.FinsDestinationUnit,
            SNA = (byte)device.FinsSourceNetwork,
            SA2 = (byte)device.FinsSourceUnit,
            ReadSplits = (ushort)device.FinsReadSplits
        };
        if (device.FinsDestinationNode > 0)
        {
            client.DA1 = (byte)device.FinsDestinationNode;
        }

        if (device.FinsSourceNode > 0)
        {
            client.SA1 = (byte)device.FinsSourceNode;
        }

        return client;
    }

    private static AllenBradleyNet CreateAllenBradley(DeviceDefinition device)
    {
        var client = new AllenBradleyNet(device.Host, device.Port)
        {
            Slot = (byte)device.Slot,
            ReadArrayUseSegment = device.CipReadArrayUseSegment
        };
        if (!string.IsNullOrWhiteSpace(device.CipRoutePath))
        {
            client.MessageRouter = new MessageRouter(device.CipRoutePath);
        }

        return client;
    }

    private static BeckhoffAdsNet CreateBeckhoff(DeviceDefinition device)
    {
        var client = new BeckhoffAdsNet(device.Host, device.Port)
        {
            UseAutoAmsNetID = device.BeckhoffUseAutoAmsNetId,
            UseTagCache = device.BeckhoffUseTagCache
        };
        if (!string.IsNullOrWhiteSpace(device.BeckhoffTargetAmsNetId))
        {
            client.SetTargetAMSNetId(device.BeckhoffTargetAmsNetId);
        }

        if (!string.IsNullOrWhiteSpace(device.BeckhoffSenderAmsNetId))
        {
            client.SetSenderAMSNetId(device.BeckhoffSenderAmsNetId);
        }

        return client;
    }

    private static SiemensPLCS MapSiemensSeries(SiemensPlcSeries series) => series switch
    {
        SiemensPlcSeries.S1200 => SiemensPLCS.S1200,
        SiemensPlcSeries.S1500 => SiemensPLCS.S1500,
        SiemensPlcSeries.S300 => SiemensPLCS.S300,
        SiemensPlcSeries.S400 => SiemensPLCS.S400,
        SiemensPlcSeries.S200Smart => SiemensPLCS.S200Smart,
        SiemensPlcSeries.S200 => SiemensPLCS.S200,
        _ => throw new NotSupportedException($"不支持西门子系列 {series}。")
    };

    private static InovanceSeries MapInovanceSeries(InovancePlcSeries series) => series switch
    {
        InovancePlcSeries.AM => InovanceSeries.AM,
        InovancePlcSeries.H3U => InovanceSeries.H3U,
        InovancePlcSeries.H5U => InovanceSeries.H5U,
        InovancePlcSeries.Easy => InovanceSeries.Easy,
        _ => throw new NotSupportedException($"不支持汇川系列 {series}。")
    };

    private static DeltaTcpNet CreateDelta(DeviceDefinition device) => new(device.Host, device.Port, (byte)device.Station)
    {
        Series = device.DeltaSeries == DeltaPlcSeries.AS ? DeltaSeries.AS : DeltaSeries.Dvp
    };
}

internal interface IHslClientSession : IAsyncDisposable
{
    IReadWriteNet Client { get; }
    bool IsDatagram { get; }
    ValueTask ConnectAsync(CancellationToken cancellationToken);
    ValueTask DisconnectAsync(CancellationToken cancellationToken);
}

internal sealed class HslTcpClientSession(DeviceTcpNet client, bool isDatagram = false) : IHslClientSession
{
    public IReadWriteNet Client => client;
    public bool IsDatagram => isDatagram;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        var result = await client.ConnectServerAsync().WaitAsync(cancellationToken);
        if (!result.IsSuccess)
        {
            throw new IOException(result.Message);
        }
    }

    public ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        client.ConnectClose();
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        client.ConnectClose();
        return ValueTask.CompletedTask;
    }
}

public sealed record SiemensConnectionParameters(
    int Port,
    byte Rack,
    byte Slot,
    byte ConnectionType,
    string Recommendation);

public static class SiemensConnectionProfile
{
    public static SiemensConnectionParameters Resolve(DeviceDefinition device)
    {
        var (rack, slot, recommendation) = device.SiemensAutoRackSlot
            ? ResolveAutomatic(device.SiemensSeries)
            : ((byte)device.Rack, (byte)device.Slot, "使用手动机架/槽位");
        return new SiemensConnectionParameters(
            device.Port,
            rack,
            slot,
            (byte)device.SiemensConnectionType,
            recommendation);
    }

    private static (byte Rack, byte Slot, string Recommendation) ResolveAutomatic(SiemensPlcSeries series) => series switch
    {
        SiemensPlcSeries.S1200 or SiemensPlcSeries.S1500 => (0, 1, "S7-1200/1500 推荐 Rack 0 / Slot 1"),
        SiemensPlcSeries.S300 => (0, 2, "S7-300 推荐 Rack 0 / Slot 2"),
        SiemensPlcSeries.S400 => (0, 3, "S7-400 推荐 Rack 0 / Slot 3"),
        SiemensPlcSeries.S200 or SiemensPlcSeries.S200Smart => (0, 0, "S7-200 系列使用 TSAP 建链"),
        _ => throw new NotSupportedException($"不支持西门子系列 {series}。")
    };
}

public sealed class HslDeviceDriver : IDeviceDriver
{
    private readonly IHslClientSession _session;
    private readonly IReadWriteNet _client;
    private readonly DeviceReadPlan _readPlan;
    private readonly Dictionary<Guid, string>? _siemensAddresses;
    private readonly SemaphoreSlim _readGate = new(1, 1);

    internal HslDeviceDriver(ProtocolKind protocol, IHslClientSession session, DeviceReadPlan readPlan)
    {
        Protocol = protocol;
        _session = session;
        _client = session.Client;
        _readPlan = readPlan;
        _siemensAddresses = BuildSiemensAddresses(protocol, readPlan);
    }

    public ProtocolKind Protocol { get; }
    public bool IsDatagramTransport => _session.IsDatagram;
    internal Type ClientType => _client.GetType();

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            await _session.ConnectAsync(cancellationToken);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            return await ReadCoreAsync(tag, cancellationToken);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask WriteAsync(TagDefinition tag, object value, CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            await WriteCoreAsync(tag, value, cancellationToken);
        }
        finally
        {
            _readGate.Release();
        }
    }

    private async ValueTask WriteCoreAsync(TagDefinition tag, object value, CancellationToken cancellationToken)
    {
        var protocolAddress = GetProtocolAddress(tag);
        _client.ByteTransform.DataFormat = ResolveDataFormat(tag.ByteOrder);
        OperateResult result = tag.DataType switch
        {
            TagDataType.Bool => await _client.WriteBoolAsync(protocolAddress, Convert.ToBoolean(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.Byte => await _client.WriteAsync(protocolAddress, [Convert.ToByte(value, System.Globalization.CultureInfo.InvariantCulture)]).WaitAsync(cancellationToken),
            TagDataType.Int16 => await _client.WriteAsync(protocolAddress, Convert.ToInt16(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.UInt16 or TagDataType.Bcd => await _client.WriteAsync(protocolAddress, Convert.ToUInt16(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.Int32 => await _client.WriteAsync(protocolAddress, Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.UInt32 => await _client.WriteAsync(protocolAddress, Convert.ToUInt32(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.Int64 => await _client.WriteAsync(protocolAddress, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.UInt64 => await _client.WriteAsync(protocolAddress, Convert.ToUInt64(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.Float32 => await _client.WriteAsync(protocolAddress, Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.Float64 => await _client.WriteAsync(protocolAddress, Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture)).WaitAsync(cancellationToken),
            TagDataType.String => await _client.WriteAsync(protocolAddress, Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty, tag.StringLength).WaitAsync(cancellationToken),
            _ => throw new NotSupportedException($"不支持写入数据类型 {tag.DataType}。")
        };
        EnsureSuccess(result);
    }

    private async ValueTask<object?> ReadCoreAsync(TagDefinition tag, CancellationToken cancellationToken)
    {
        var protocolAddress = GetProtocolAddress(tag);
        if (Protocol == ProtocolKind.SiemensS7)
        {
            return await ReadSiemensAsync(tag, protocolAddress, cancellationToken);
        }

        if (Protocol == ProtocolKind.OmronFins && tag.DataType is not TagDataType.Bool and not TagDataType.String)
        {
            return await ReadOmronFinsAsync(tag, protocolAddress, cancellationToken);
        }

        _client.ByteTransform.DataFormat = ResolveDataFormat(tag.ByteOrder);
        return tag.DataType switch
        {
            TagDataType.Bool => Content(await _client.ReadBoolAsync(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.Byte => Content(await _client.ReadAsync(protocolAddress, 1).WaitAsync(cancellationToken))[0],
            TagDataType.Int16 => Content(await _client.ReadInt16Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.UInt16 => Content(await _client.ReadUInt16Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.Int32 => Content(await _client.ReadInt32Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.UInt32 => Content(await _client.ReadUInt32Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.Int64 => Content(await _client.ReadInt64Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.UInt64 => Content(await _client.ReadUInt64Async(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.Float32 => Content(await _client.ReadFloatAsync(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.Float64 => Content(await _client.ReadDoubleAsync(protocolAddress).WaitAsync(cancellationToken)),
            TagDataType.String => Content(await _client.ReadStringAsync(protocolAddress, (ushort)tag.StringLength).WaitAsync(cancellationToken)),
            TagDataType.Bcd => Content(await _client.ReadUInt16Async(protocolAddress).WaitAsync(cancellationToken)),
            _ => throw new NotSupportedException($"不支持数据类型 {tag.DataType}。")
        };
    }

    private async ValueTask<object?> ReadOmronFinsAsync(
        TagDefinition tag,
        string address,
        CancellationToken cancellationToken)
    {
        var expectedBytes = TagReadPlanCompiler.GetByteLength(tag);
        var wordLength = checked((ushort)((expectedBytes + 1) / 2));
        _client.ByteTransform.DataFormat = ResolveDataFormat(tag.ByteOrder);
        var content = Content(await _client.ReadAsync(address, wordLength).WaitAsync(cancellationToken));
        if (content.Length < expectedBytes)
        {
            throw new IOException(
                $"欧姆龙 FINS 点位 {tag.Name}({tag.Address}) 返回长度不足，期望至少 {expectedBytes} 字节，实际 {content.Length} 字节。" +
                "请检查 PLC 的 FINS/TCP 响应、节点路由和测试模拟器兼容性。");
        }

        return Decode(content, 0, tag);
    }

    private async ValueTask<object?> ReadSiemensAsync(
        TagDefinition tag,
        string address,
        CancellationToken cancellationToken)
    {
        if (tag.DataType == TagDataType.Bool)
        {
            return Content(await _client.ReadBoolAsync(address).WaitAsync(cancellationToken));
        }

        if (tag.DataType == TagDataType.String)
        {
            return Content(await _client.ReadStringAsync(address, (ushort)tag.StringLength).WaitAsync(cancellationToken));
        }

        var length = TagReadPlanCompiler.GetByteLength(tag);
        var content = Content(await _client.ReadAsync(address, length).WaitAsync(cancellationToken));
        if (content.Length < length)
        {
            throw new IOException($"西门子点位 {tag.Name}({tag.Address}) 返回长度不足，期望 {length}，实际 {content.Length}。");
        }

        return Decode(content, 0, tag);
    }

    public ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken) =>
        ReadManyCoreAsync(cancellationToken);

    private async ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyCoreAsync(CancellationToken cancellationToken)
    {
        var values = new Dictionary<Guid, DeviceTagReadResult>(_readPlan.SourceTagCount);
        foreach (var batch in _readPlan.Batches)
        {
            if (batch.UseNativeMultiRead)
            {
                var fallbackToSingle = false;
                await _readGate.WaitAsync(cancellationToken);
                try
                {
                    var addresses = batch.Tags.Select(GetProtocolAddress).ToArray();
                    var lengths = batch.Tags.Select(TagReadPlanCompiler.GetByteLength).ToArray();
                    var result = await ReadNativeBatchAsync(addresses, lengths, cancellationToken);
                    var content = Content(result);
                    var expectedLength = lengths.Sum(length => length);
                    if (content.Length < expectedLength)
                    {
                        throw new IOException($"PLC批量读取返回长度不足，期望 {expectedLength}，实际 {content.Length}。");
                    }

                    var offset = 0;
                    for (var index = 0; index < batch.Tags.Count; index++)
                    {
                        var tag = batch.Tags[index];
                        values[tag.Id] = DeviceTagReadResult.Success(Decode(content, offset, tag));
                        offset += lengths[index];
                    }
                }
                catch (Exception exception) when (DeviceReadFailureClassifier.IsPointSpecific(exception))
                {
                    fallbackToSingle = true;
                }
                finally
                {
                    _readGate.Release();
                }

                if (fallbackToSingle)
                {
                    foreach (var tag in batch.Tags)
                    {
                        values[tag.Id] = await ReadTagSafelyAsync(tag, cancellationToken);
                    }
                }
            }
            else
            {
                foreach (var tag in batch.Tags)
                {
                    values[tag.Id] = await ReadTagSafelyAsync(tag, cancellationToken);
                }
            }
        }

        return values;
    }

    private async ValueTask<DeviceTagReadResult> ReadTagSafelyAsync(
        TagDefinition tag,
        CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            return DeviceTagReadResult.Success(await ReadCoreAsync(tag, cancellationToken));
        }
        catch (Exception exception) when (DeviceReadFailureClassifier.IsPointSpecific(exception))
        {
            return DeviceTagReadResult.ConfigurationFailure(exception.Message);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        await _readGate.WaitAsync(cancellationToken);
        try
        {
            await _session.DisconnectAsync(cancellationToken);
        }
        finally
        {
            _readGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _readGate.WaitAsync();
        try
        {
            await _session.DisposeAsync();
        }
        finally
        {
            _readGate.Release();
        }
    }

    private static T Content<T>(OperateResult<T> result)
    {
        EnsureSuccess(result);
        return result.Content;
    }

    private static void EnsureSuccess(OperateResult result)
    {
        if (!result.IsSuccess)
        {
            throw new IOException(result.Message);
        }
    }

    private Task<OperateResult<byte[]>> ReadNativeBatchAsync(
        string[] addresses,
        ushort[] lengths,
        CancellationToken cancellationToken)
    {
        var task = _client switch
        {
            SiemensS7Net siemens => siemens.ReadAsync(addresses, lengths),
            OmronConnectedCipNet omron => omron.ReadAsync(addresses, lengths),
            AllenBradleyNet allenBradley => allenBradley.ReadAsync(addresses, lengths),
            BeckhoffAdsNet beckhoff => beckhoff.ReadAsync(addresses, lengths),
            _ => throw new NotSupportedException($"协议 {Protocol} 不支持原生多地址读取。")
        };
        return task.WaitAsync(cancellationToken);
    }

    private object? Decode(byte[] content, int offset, TagDefinition tag)
    {
        var format = ResolveDataFormat(tag.ByteOrder);
        var transform = _client.ByteTransform.CreateByDateFormat(format);
        return tag.DataType switch
        {
            TagDataType.Byte => content[offset],
            TagDataType.Int16 => transform.TransInt16(content, offset),
            TagDataType.UInt16 or TagDataType.Bcd => transform.TransUInt16(content, offset),
            TagDataType.Int32 => transform.TransInt32(content, offset),
            TagDataType.UInt32 => transform.TransUInt32(content, offset),
            TagDataType.Int64 => transform.TransInt64(content, offset),
            TagDataType.UInt64 => transform.TransUInt64(content, offset),
            TagDataType.Float32 => transform.TransSingle(content, offset),
            TagDataType.Float64 => transform.TransDouble(content, offset),
            TagDataType.String => transform.TransString(content, offset, tag.StringLength, Encoding.UTF8),
            _ => throw new NotSupportedException($"点位 {tag.Name} 不支持批量解码类型 {tag.DataType}。")
        };
    }

    internal static DataFormat ResolveDataFormat(string? byteOrder) =>
        Enum.TryParse<DataFormat>(byteOrder, true, out var parsed)
            ? parsed
            : DataFormat.ABCD;

    private string GetProtocolAddress(TagDefinition tag)
    {
        if (Protocol == ProtocolKind.ModbusTcp)
        {
            return ModbusTagAddress.ToHslAddress(tag);
        }

        if (_siemensAddresses is not null && _siemensAddresses.TryGetValue(tag.Id, out var address))
        {
            return address;
        }

        return Protocol == ProtocolKind.SiemensS7
            ? SiemensTagAddress.NormalizeForRawRead(tag.Address)
            : tag.Address;
    }

    private static Dictionary<Guid, string>? BuildSiemensAddresses(ProtocolKind protocol, DeviceReadPlan readPlan)
    {
        if (protocol != ProtocolKind.SiemensS7)
        {
            return null;
        }

        var addresses = new Dictionary<Guid, string>(readPlan.SourceTagCount);
        foreach (var batch in readPlan.Batches)
        {
            foreach (var tag in batch.Tags)
            {
                addresses[tag.Id] = SiemensTagAddress.NormalizeForRawRead(tag.Address);
            }
        }

        return addresses;
    }
}

public static class SiemensTagAddress
{
    public static string NormalizeForRawRead(string address)
    {
        var value = address.Trim();
        var dbMarker = value.IndexOf(".DB", StringComparison.OrdinalIgnoreCase);
        if (dbMarker >= 0 && value.Length > dbMarker + 4 && IsWidthCode(value[dbMarker + 3]))
        {
            return string.Concat(value.AsSpan(0, dbMarker + 1), value.AsSpan(dbMarker + 4));
        }

        if (value.Length > 2 && IsMemoryArea(value[0]) && IsWidthCode(value[1]) && char.IsAsciiDigit(value[2]))
        {
            return string.Create(value.Length - 1, value, static (destination, source) =>
            {
                destination[0] = char.ToUpperInvariant(source[0]);
                source.AsSpan(2).CopyTo(destination[1..]);
            });
        }

        return value;
    }

    private static bool IsMemoryArea(char value) => char.ToUpperInvariant(value) is 'V' or 'M' or 'I' or 'Q';

    private static bool IsWidthCode(char value) => char.ToUpperInvariant(value) is 'X' or 'B' or 'W' or 'D';
}

public sealed class OpcUaDeviceDriver(DeviceDefinition device, DeviceReadPlan readPlan) : IDeviceDriver
{
    private ISession? _session;

    public ProtocolKind Protocol => ProtocolKind.OpcUa;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken)
    {
        var endpointUrl = device.Host.StartsWith("opc.tcp://", StringComparison.OrdinalIgnoreCase)
            ? device.Host
            : $"opc.tcp://{device.Host}:{device.Port}";
        var configuration = CreateConfiguration(device);
        await configuration.ValidateAsync(ApplicationType.Client, cancellationToken);
        configuration.CertificateValidator.CertificateValidation += (_, eventArgs) =>
        {
            eventArgs.Accept = device.OpcUaAutoAcceptUntrusted &&
                eventArgs.Error.StatusCode == StatusCodes.BadCertificateUntrusted;
        };

        var sessionFactory = new DefaultSessionFactory(OpcTelemetryContext.Instance);
        var endpointDescription = await CoreClientUtils.SelectEndpointAsync(
            configuration,
            endpointUrl,
            device.OpcUaUseSecurity,
            device.ConnectTimeoutMs,
            sessionFactory.Telemetry,
            cancellationToken);
        var endpoint = new ConfiguredEndpoint(
            null,
            endpointDescription,
            EndpointConfiguration.Create(configuration));
        IUserIdentity identity = string.IsNullOrWhiteSpace(device.OpcUaUsername)
            ? new UserIdentity()
            : new UserIdentity(device.OpcUaUsername, Encoding.UTF8.GetBytes(device.OpcUaPassword ?? string.Empty));
        _session = await sessionFactory.CreateAsync(
            configuration,
            endpoint,
            false,
            device.Name,
            60_000,
            identity,
            [],
            cancellationToken);
    }

    public async ValueTask<object?> ReadAsync(TagDefinition tag, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("OPC UA会话尚未连接。");
        var value = await session.ReadValueAsync(NodeId.Parse(tag.Address), cancellationToken);
        if (StatusCode.IsBad(value.StatusCode))
        {
            throw new ServiceResultException(value.StatusCode);
        }

        return value.Value;
    }

    public async ValueTask WriteAsync(TagDefinition tag, object value, CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("OPC UA会话尚未连接。");
        var nodesToWrite = new WriteValueCollection
        {
            new WriteValue
            {
                NodeId = NodeId.Parse(tag.Address),
                AttributeId = Attributes.Value,
                Value = new DataValue(new Variant(value))
            }
        };
        var response = await session.WriteAsync(null, nodesToWrite, cancellationToken);
        if (response.Results is null || response.Results.Count == 0)
        {
            throw new IOException($"OPC UA点位 {tag.Name}({tag.Address}) 写入未返回状态码。");
        }

        if (StatusCode.IsBad(response.Results[0]))
        {
            throw new ServiceResultException(response.Results[0]);
        }
    }

    public async ValueTask<IReadOnlyDictionary<Guid, DeviceTagReadResult>> ReadManyAsync(CancellationToken cancellationToken)
    {
        var session = _session ?? throw new InvalidOperationException("OPC UA会话尚未连接。");
        var values = new Dictionary<Guid, DeviceTagReadResult>(readPlan.SourceTagCount);
        var validTags = new List<TagDefinition>(readPlan.SourceTagCount);
        var nodesToRead = new ReadValueIdCollection();

        foreach (var batch in readPlan.Batches)
        {
            foreach (var tag in batch.Tags)
            {
                try
                {
                    nodesToRead.Add(new ReadValueId
                    {
                        NodeId = NodeId.Parse(tag.Address),
                        AttributeId = Attributes.Value
                    });
                    validTags.Add(tag);
                }
                catch (Exception exception) when (DeviceReadFailureClassifier.IsPointSpecific(exception))
                {
                    values[tag.Id] = DeviceTagReadResult.ConfigurationFailure(exception.Message);
                }
            }
        }

        if (nodesToRead.Count == 0)
        {
            return values;
        }

        var response = await session.ReadAsync(
            null,
            0,
            TimestampsToReturn.Neither,
            nodesToRead,
            cancellationToken);
        var results = response.Results;
        for (var index = 0; index < validTags.Count; index++)
        {
            var tag = validTags[index];
            if (results is null || index >= results.Count)
            {
                values[tag.Id] = DeviceTagReadResult.ConfigurationFailure("OPC UA批量读取未返回对应节点结果。");
                continue;
            }

            var dataValue = results[index];
            if (StatusCode.IsGood(dataValue.StatusCode))
            {
                values[tag.Id] = DeviceTagReadResult.Success(dataValue.Value);
                continue;
            }

            var statusException = new ServiceResultException(dataValue.StatusCode);
            if (DeviceReadFailureClassifier.IsPointSpecific(statusException))
            {
                values[tag.Id] = DeviceTagReadResult.ConfigurationFailure(statusException.Message);
            }
            else
            {
                throw statusException;
            }
        }

        return values;
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken)
    {
        if (_session is null)
        {
            return;
        }

        await _session.CloseAsync(cancellationToken);
        _session.Dispose();
        _session = null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await DisconnectAsync(CancellationToken.None);
        }
        catch
        {
            _session?.Dispose();
            _session = null;
        }
    }

    private static ApplicationConfiguration CreateConfiguration(DeviceDefinition device) => new()
    {
        ApplicationName = "EdgeNexus Gateway",
        ApplicationUri = $"urn:{Utils.GetHostName()}:EdgeNexusGateway",
        ApplicationType = ApplicationType.Client,
        SecurityConfiguration = new SecurityConfiguration
        {
            ApplicationCertificate = new CertificateIdentifier
            {
                StoreType = "Directory",
                StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "own"),
                SubjectName = "CN=EdgeNexus Gateway"
            },
            TrustedPeerCertificates = new CertificateTrustList
            {
                StoreType = "Directory",
                StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "trusted")
            },
            RejectedCertificateStore = new CertificateTrustList
            {
                StoreType = "Directory",
                StorePath = Path.Combine(AppContext.BaseDirectory, "pki", "rejected")
            },
            AutoAcceptUntrustedCertificates = device.OpcUaAutoAcceptUntrusted,
            AddAppCertToTrustedStore = true
        },
        TransportConfigurations = [],
        TransportQuotas = new TransportQuotas
        {
            OperationTimeout = device.ReceiveTimeoutMs,
            MaxStringLength = 1_048_576,
            MaxByteStringLength = 4_194_304,
            MaxArrayLength = 65_535,
            MaxMessageSize = 4_194_304
        },
        ClientConfiguration = new ClientConfiguration
        {
            DefaultSessionTimeout = 60_000,
            MinSubscriptionLifetime = 10_000
        }
    };

    private sealed class OpcTelemetryContext : ITelemetryContext
    {
        public static readonly OpcTelemetryContext Instance = new();

        public ILoggerFactory LoggerFactory => NullLoggerFactory.Instance;

        public ActivitySource ActivitySource { get; } = new("Snet.Edge.OpcUa");

        public Meter CreateMeter() => new("Snet.Edge.OpcUa");
    }
}

internal static class DeviceReadFailureClassifier
{
    private static readonly string[] PointFailureMarkers =
    [
        "读取的数据越界",
        "非法数据地址",
        "地址错误",
        "地址格式",
        "illegal data address",
        "invalid address",
        "address out of range",
        "unsupported address"
    ];

    public static bool IsPointSpecific(Exception exception)
    {
        if (exception is ArgumentException or FormatException or OverflowException)
        {
            return true;
        }

        if (exception is ServiceResultException serviceException)
        {
            var statusCode = serviceException.StatusCode;
            return statusCode == StatusCodes.BadNodeIdUnknown ||
                statusCode == StatusCodes.BadNodeIdInvalid ||
                statusCode == StatusCodes.BadAttributeIdInvalid ||
                statusCode == StatusCodes.BadIndexRangeInvalid ||
                statusCode == StatusCodes.BadIndexRangeNoData ||
                statusCode == StatusCodes.BadDataEncodingInvalid ||
                statusCode == StatusCodes.BadDataEncodingUnsupported ||
                statusCode == StatusCodes.BadTypeMismatch ||
                statusCode == StatusCodes.BadOutOfRange;
        }

        return exception is IOException &&
            PointFailureMarkers.Any(marker => exception.Message.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
