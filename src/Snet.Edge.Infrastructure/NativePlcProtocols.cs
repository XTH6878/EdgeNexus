using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

internal enum DataFormat
{
    ABCD,
    BADC,
    CDAB,
    DCBA
}

internal enum SiemensPLCS
{
    S1200 = 1,
    S300 = 2,
    S400 = 3,
    S1500 = 4,
    S200Smart = 5,
    S200 = 6
}

internal enum InovanceSeries
{
    AM,
    H3U,
    H5U,
    Easy
}

internal enum DeltaSeries
{
    Dvp,
    AS
}

internal class OperateResult
{
    public bool IsSuccess { get; set; }
    public string Message { get; set; } = "Success";
    public int ErrorCode { get; set; }

    public static OperateResult CreateSuccessResult() => new() { IsSuccess = true };

    public static OperateResult CreateFailedResult(string message, int errorCode = -1) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode };

    public static OperateResult<T> CreateSuccessResult<T>(T content) =>
        new() { IsSuccess = true, Content = content };

    public static OperateResult<T> CreateFailedResult<T>(string message, int errorCode = -1) =>
        new() { IsSuccess = false, Message = message, ErrorCode = errorCode };

    public static OperateResult<T> CreateFailedResult<T>(OperateResult result) =>
        new() { IsSuccess = false, Message = result.Message, ErrorCode = result.ErrorCode };
}

internal sealed class OperateResult<T> : OperateResult
{
    public T Content { get; set; } = default!;
}

internal sealed class PlcByteTransform(DataFormat dataFormat = DataFormat.ABCD)
{
    public DataFormat DataFormat { get; set; } = dataFormat;

    public PlcByteTransform CreateByDateFormat(DataFormat format) => new(format);

    public short TransInt16(byte[] buffer, int index)
    {
        var span = Order2(buffer.AsSpan(index, 2));
        return BinaryPrimitives.ReadInt16BigEndian(span);
    }

    public ushort TransUInt16(byte[] buffer, int index)
    {
        var span = Order2(buffer.AsSpan(index, 2));
        return BinaryPrimitives.ReadUInt16BigEndian(span);
    }

    public int TransInt32(byte[] buffer, int index)
    {
        var span = Order4(buffer.AsSpan(index, 4));
        return BinaryPrimitives.ReadInt32BigEndian(span);
    }

    public uint TransUInt32(byte[] buffer, int index)
    {
        var span = Order4(buffer.AsSpan(index, 4));
        return BinaryPrimitives.ReadUInt32BigEndian(span);
    }

    public long TransInt64(byte[] buffer, int index)
    {
        var span = Order8(buffer.AsSpan(index, 8));
        return BinaryPrimitives.ReadInt64BigEndian(span);
    }

    public ulong TransUInt64(byte[] buffer, int index)
    {
        var span = Order8(buffer.AsSpan(index, 8));
        return BinaryPrimitives.ReadUInt64BigEndian(span);
    }

    public float TransSingle(byte[] buffer, int index)
    {
        var span = Order4(buffer.AsSpan(index, 4));
        return BinaryPrimitives.ReadSingleBigEndian(span);
    }

    public double TransDouble(byte[] buffer, int index)
    {
        var span = Order8(buffer.AsSpan(index, 8));
        return BinaryPrimitives.ReadDoubleBigEndian(span);
    }

    public string TransString(byte[] buffer, int index, int length, Encoding encoding)
    {
        var available = Math.Clamp(length, 0, Math.Max(0, buffer.Length - index));
        return encoding.GetString(buffer, index, available).TrimEnd('\0');
    }

    public byte[] TransByte(ulong value)
    {
        Span<byte> bigEndian = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bigEndian, value);
        return Order8(bigEndian);
    }

    private byte[] Order2(ReadOnlySpan<byte> source) => DataFormat switch
    {
        DataFormat.BADC or DataFormat.DCBA => [source[1], source[0]],
        _ => [source[0], source[1]]
    };

    private byte[] Order4(ReadOnlySpan<byte> source) => DataFormat switch
    {
        DataFormat.ABCD => [source[0], source[1], source[2], source[3]],
        DataFormat.BADC => [source[1], source[0], source[3], source[2]],
        DataFormat.CDAB => [source[2], source[3], source[0], source[1]],
        DataFormat.DCBA => [source[3], source[2], source[1], source[0]],
        _ => [source[0], source[1], source[2], source[3]]
    };

    private byte[] Order8(ReadOnlySpan<byte> source) => DataFormat switch
    {
        DataFormat.ABCD => [source[0], source[1], source[2], source[3], source[4], source[5], source[6], source[7]],
        DataFormat.BADC => [source[1], source[0], source[3], source[2], source[5], source[4], source[7], source[6]],
        DataFormat.CDAB => [source[6], source[7], source[4], source[5], source[2], source[3], source[0], source[1]],
        DataFormat.DCBA => [source[7], source[6], source[5], source[4], source[3], source[2], source[1], source[0]],
        _ => [source[0], source[1], source[2], source[3], source[4], source[5], source[6], source[7]]
    };
}

internal interface IReadWriteNet
{
    PlcByteTransform ByteTransform { get; set; }
    Task<OperateResult<byte[]>> ReadAsync(string address, ushort length);
    Task<OperateResult<bool>> ReadBoolAsync(string address);
    Task<OperateResult<short>> ReadInt16Async(string address);
    Task<OperateResult<ushort>> ReadUInt16Async(string address);
    Task<OperateResult<int>> ReadInt32Async(string address);
    Task<OperateResult<uint>> ReadUInt32Async(string address);
    Task<OperateResult<long>> ReadInt64Async(string address);
    Task<OperateResult<ulong>> ReadUInt64Async(string address);
    Task<OperateResult<float>> ReadFloatAsync(string address);
    Task<OperateResult<double>> ReadDoubleAsync(string address);
    Task<OperateResult<string>> ReadStringAsync(string address, ushort length);
}

internal interface IReadWriteMc : IReadWriteNet
{
    byte NetworkNumber { get; set; }
    byte PLCNumber { get; set; }
    byte NetworkStationNumber { get; set; }
    ushort TargetIOStation { get; set; }
}

internal abstract class DeviceTcpNet : IReadWriteNet, IDisposable
{
    protected Socket? Socket;
    private int _disposed;

    protected DeviceTcpNet(string ipAddress = "127.0.0.1", int port = 502)
    {
        IpAddress = ipAddress;
        Port = port;
    }

    public string IpAddress { get; set; }
    public int Port { get; set; }
    public int ConnectTimeOut { get; set; } = 3_000;
    public int ReceiveTimeOut { get; set; } = 3_000;
    public IPEndPoint? LocalBinding { get; set; }
    public PlcByteTransform ByteTransform { get; set; } = new();
    protected virtual bool IsUdpTransport => false;
    protected virtual ushort WordByteSize => 2;

    public virtual async Task<OperateResult> ConnectServerAsync()
    {
        ConnectClose();
        using var cts = new CancellationTokenSource(Math.Clamp(ConnectTimeOut, 100, 60_000));
        try
        {
            var socket = IsUdpTransport
                ? new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                : new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

            if (LocalBinding is not null)
            {
                socket.Bind(LocalBinding);
            }

            await socket.ConnectAsync(IpAddress, Port, cts.Token);
            Socket = socket;
            var handshake = await InitializeOnConnectAsync(cts.Token);
            if (!handshake.IsSuccess)
            {
                ConnectClose();
                return handshake;
            }

            return OperateResult.CreateSuccessResult();
        }
        catch (Exception exception)
        {
            ConnectClose();
            return OperateResult.CreateFailedResult(exception.Message);
        }
    }

    protected virtual Task<OperateResult> InitializeOnConnectAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OperateResult.CreateSuccessResult());

    public virtual OperateResult ConnectClose()
    {
        var socket = Interlocked.Exchange(ref Socket, null);
        if (socket is not null)
        {
            try
            {
                socket.Close();
                socket.Dispose();
            }
            catch
            {
            }
        }

        return OperateResult.CreateSuccessResult();
    }

    public abstract Task<OperateResult<byte[]>> ReadAsync(string address, ushort length);

    public virtual async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var read = await ReadAsync(address, 1);
        if (!read.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(read);
        }

        return read.Content.Length > 0
            ? OperateResult.CreateSuccessResult(read.Content[0] != 0)
            : OperateResult.CreateFailedResult<bool>("读取位数据返回长度为0。");
    }

    public virtual async Task<OperateResult<short>> ReadInt16Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(2)), bytes => ByteTransform.TransInt16(bytes, 0));

    public virtual async Task<OperateResult<ushort>> ReadUInt16Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(2)), bytes => ByteTransform.TransUInt16(bytes, 0));

    public virtual async Task<OperateResult<int>> ReadInt32Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(4)), bytes => ByteTransform.TransInt32(bytes, 0));

    public virtual async Task<OperateResult<uint>> ReadUInt32Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(4)), bytes => ByteTransform.TransUInt32(bytes, 0));

    public virtual async Task<OperateResult<long>> ReadInt64Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(8)), bytes => ByteTransform.TransInt64(bytes, 0));

    public virtual async Task<OperateResult<ulong>> ReadUInt64Async(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(8)), bytes => ByteTransform.TransUInt64(bytes, 0));

    public virtual async Task<OperateResult<float>> ReadFloatAsync(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(4)), bytes => ByteTransform.TransSingle(bytes, 0));

    public virtual async Task<OperateResult<double>> ReadDoubleAsync(string address) =>
        TransformValue(await ReadAsync(address, GetRegisterCount(8)), bytes => ByteTransform.TransDouble(bytes, 0));

    public virtual async Task<OperateResult<string>> ReadStringAsync(string address, ushort length) =>
        TransformValue(
            await ReadAsync(address, GetRegisterCount(Math.Max((int)length, 1))),
            bytes => ByteTransform.TransString(bytes, 0, length, Encoding.UTF8));

    protected ushort GetRegisterCount(int byteCount) =>
        WordByteSize <= 1 ? (ushort)byteCount : (ushort)((byteCount + WordByteSize - 1) / WordByteSize);

    private static OperateResult<T> TransformValue<T>(OperateResult<byte[]> read, Func<byte[], T> decoder)
    {
        if (!read.IsSuccess)
        {
            return OperateResult.CreateFailedResult<T>(read);
        }

        try
        {
            return OperateResult.CreateSuccessResult(decoder(read.Content));
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<T>(exception.Message);
        }
    }

    protected async ValueTask SendAllAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var socket = Socket ?? throw new IOException("PLC通信连接未建立。");
        var sent = 0;
        while (sent < payload.Length)
        {
            var count = await socket.SendAsync(payload[sent..], SocketFlags.None, cancellationToken);
            if (count <= 0)
            {
                throw new IOException("PLC连接在发送时关闭。");
            }

            sent += count;
        }
    }

    protected async ValueTask<byte[]> ReceiveExactAsync(int length, CancellationToken cancellationToken)
    {
        var socket = Socket ?? throw new IOException("PLC通信连接未建立。");
        var buffer = new byte[length];
        var received = 0;
        while (received < length)
        {
            var count = await socket.ReceiveAsync(buffer.AsMemory(received, length - received), SocketFlags.None, cancellationToken);
            if (count <= 0)
            {
                throw new IOException("PLC连接已对端关闭。");
            }

            received += count;
        }

        return buffer;
    }

    protected async ValueTask<byte[]> ReceiveDatagramAsync(int maxBytes, CancellationToken cancellationToken)
    {
        var socket = Socket ?? throw new IOException("PLC UDP会话未建立。");
        var buffer = new byte[maxBytes];
        var count = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, cancellationToken);
        if (count <= 0)
        {
            throw new IOException("PLC UDP响应为空。");
        }

        return buffer[..count];
    }

    protected CancellationTokenSource CreateReceiveTimeoutTokenSource() =>
        new(Math.Clamp(ReceiveTimeOut, 100, 60_000));

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            ConnectClose();
        }
    }
}

// ============================================================================
// 1. Siemens S7 Native Protocol Client & Address Parser
// ============================================================================

internal sealed record S7AddressData
{
    public byte DataCode { get; init; }
    public ushort DbBlock { get; init; }
    public int AddressStart { get; init; }
    public ushort Length { get; init; }
    public bool IsBit { get; init; }

    public static OperateResult<S7AddressData> ParseFrom(string address, ushort length = 1)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<S7AddressData>("西门子地址不能为空。");
        }

        var normalized = SiemensTagAddress.NormalizeForRawRead(address).Trim().ToUpperInvariant();
        try
        {
            if (normalized.StartsWith("DB", StringComparison.Ordinal))
            {
                var dotIndex = normalized.IndexOf('.');
                if (dotIndex <= 2 || !ushort.TryParse(normalized.AsSpan(2, dotIndex - 2), out var dbBlock))
                {
                    return OperateResult.CreateFailedResult<S7AddressData>($"非法西门子DB地址格式: {address}");
                }

                var remainder = normalized[(dotIndex + 1)..];
                if (remainder.StartsWith("DBX", StringComparison.Ordinal) ||
                    remainder.StartsWith("DBB", StringComparison.Ordinal) ||
                    remainder.StartsWith("DBW", StringComparison.Ordinal) ||
                    remainder.StartsWith("DBD", StringComparison.Ordinal))
                {
                    remainder = remainder[3..];
                }

                return ParseBitOrByteOffset(0x84, dbBlock, remainder, length);
            }

            if (normalized.StartsWith("SM", StringComparison.Ordinal))
            {
                return ParseAreaWithOptionalWidth(0x05, 0, normalized[2..], length);
            }

            if (normalized.StartsWith("PI", StringComparison.Ordinal) || normalized.StartsWith("PE", StringComparison.Ordinal) ||
                normalized.StartsWith("PQ", StringComparison.Ordinal))
            {
                return ParseAreaWithOptionalWidth(0x80, 0, normalized[2..], length);
            }

            var prefix = normalized[0];
            var rest = normalized[1..];
            return prefix switch
            {
                'M' => ParseAreaWithOptionalWidth(0x83, 0, rest, length),
                'I' or 'E' => ParseAreaWithOptionalWidth(0x81, 0, rest, length),
                'Q' or 'A' => ParseAreaWithOptionalWidth(0x82, 0, rest, length),
                'V' => ParseAreaWithOptionalWidth(0x84, 1, rest, length),
                'T' => ParseTimerCounter(0x1D, rest, length),
                'C' or 'Z' => ParseTimerCounter(0x1C, rest, length),
                _ => OperateResult.CreateFailedResult<S7AddressData>($"不支持的西门子地址区域: {address}")
            };
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<S7AddressData>($"西门子地址格式错误 ({address}): {exception.Message}");
        }
    }

    private static OperateResult<S7AddressData> ParseAreaWithOptionalWidth(
        byte dataCode,
        ushort dbBlock,
        string rest,
        ushort length)
    {
        if (rest.Length > 1 && rest[0] is 'X' or 'B' or 'W' or 'D' && char.IsAsciiDigit(rest[1]))
        {
            rest = rest[1..];
        }

        return ParseBitOrByteOffset(dataCode, dbBlock, rest, length);
    }

    private static OperateResult<S7AddressData> ParseBitOrByteOffset(
        byte dataCode,
        ushort dbBlock,
        string offsetText,
        ushort length)
    {
        var bitDot = offsetText.IndexOf('.');
        if (bitDot >= 0)
        {
            if (!int.TryParse(offsetText.AsSpan(0, bitDot), NumberStyles.None, CultureInfo.InvariantCulture, out var byteOffset) ||
                !int.TryParse(offsetText.AsSpan(bitDot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bitOffset) ||
                bitOffset is < 0 or > 7)
            {
                return OperateResult.CreateFailedResult<S7AddressData>($"非法西门子位地址偏移: {offsetText}");
            }

            return OperateResult.CreateSuccessResult(new S7AddressData
            {
                DataCode = dataCode,
                DbBlock = dbBlock,
                AddressStart = (byteOffset * 8) + bitOffset,
                Length = 1,
                IsBit = true
            });
        }

        if (!int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return OperateResult.CreateFailedResult<S7AddressData>($"非法西门子字节地址偏移: {offsetText}");
        }

        return OperateResult.CreateSuccessResult(new S7AddressData
        {
            DataCode = dataCode,
            DbBlock = dbBlock,
            AddressStart = offset * 8,
            Length = Math.Max(length, (ushort)1),
            IsBit = false
        });
    }

    private static OperateResult<S7AddressData> ParseTimerCounter(byte dataCode, string offsetText, ushort length)
    {
        if (!int.TryParse(offsetText, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
        {
            return OperateResult.CreateFailedResult<S7AddressData>($"非法西门子定时器/计数器地址: {offsetText}");
        }

        return OperateResult.CreateSuccessResult(new S7AddressData
        {
            DataCode = dataCode,
            DbBlock = 0,
            AddressStart = index,
            Length = Math.Max(length, (ushort)2),
            IsBit = false
        });
    }
}

internal sealed class SiemensS7Net(SiemensPLCS series, string ipAddress = "127.0.0.1") : DeviceTcpNet(ipAddress, 102)
{
    private int _pduSequence;

    public SiemensPLCS CurrentPlc { get; } = series;
    public byte Rack { get; set; }
    public byte Slot { get; set; }
    public byte ConnectionType { get; set; } = 1;
    public ushort LocalTSAP { get; set; }
    public ushort DestTSAP { get; set; }
    protected override ushort WordByteSize => 1;

    protected override async Task<OperateResult> InitializeOnConnectAsync(CancellationToken cancellationToken)
    {
        var localTsap = LocalTSAP > 0
            ? LocalTSAP
            : CurrentPlc switch
            {
                SiemensPLCS.S200 => (ushort)0x4D57,
                SiemensPLCS.S200Smart => (ushort)0x1000,
                _ => (ushort)0x0100
            };

        var destTsap = DestTSAP > 0
            ? DestTSAP
            : CurrentPlc switch
            {
                SiemensPLCS.S200 => (ushort)0x4D57,
                SiemensPLCS.S200Smart => (ushort)0x0301,
                _ => (ushort)((Math.Clamp((int)ConnectionType, 1, 3) << 8) | ((Rack & 0x07) << 5) | (Slot & 0x1F))
            };

        byte[] cotpCr =
        [
            0x03, 0x00, 0x00, 0x16,
            0x11, 0xE0, 0x00, 0x00, 0x00, 0x01, 0x00,
            0xC0, 0x01, 0x0A,
            0xC1, 0x02, (byte)(localTsap >> 8), (byte)(localTsap & 0xFF),
            0xC2, 0x02, (byte)(destTsap >> 8), (byte)(destTsap & 0xFF)
        ];

        await SendAllAsync(cotpCr, cancellationToken);
        var cotpPacket = await ReadTpktPacketAsync(cancellationToken);
        if (cotpPacket.Length < 6 || (cotpPacket[5] & 0xF0) != 0xD0)
        {
            return OperateResult.CreateFailedResult("西门子 ISO-COTP 握手被拒绝。");
        }

        byte[] s7Setup =
        [
            0x03, 0x00, 0x00, 0x19,
            0x02, 0xF0, 0x80,
            0x32, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00, 0x00,
            0xF0, 0x00, 0x00, 0x01, 0x00, 0x01, 0x03, 0xC0
        ];

        await SendAllAsync(s7Setup, cancellationToken);
        var setupResp = await ReadTpktPacketAsync(cancellationToken);
        if (setupResp.Length < 19 || setupResp[7] != 0x32 || setupResp[8] != 0x03)
        {
            return OperateResult.CreateFailedResult("西门子 S7 通信参数协商失败。");
        }

        return OperateResult.CreateSuccessResult();
    }

    public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) =>
        ReadAsync([address], [length]);

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var parsed = S7AddressData.ParseFrom(address, 1);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(parsed.Message);
        }

        var response = await ExecuteReadAsync([parsed.Content with { IsBit = true, Length = 1 }]);
        if (!response.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(response);
        }

        return OperateResult.CreateSuccessResult(response.Content.Length > 0 && response.Content[0] != 0);
    }

    public async Task<OperateResult<byte[]>> ReadAsync(string[] addresses, ushort[] lengths)
    {
        if (addresses.Length == 0 || addresses.Length != lengths.Length)
        {
            return OperateResult.CreateFailedResult<byte[]>("西门子批量读取地址与长度数组不匹配。");
        }

        var items = new S7AddressData[addresses.Length];
        for (var index = 0; index < addresses.Length; index++)
        {
            var parsed = S7AddressData.ParseFrom(addresses[index], lengths[index]);
            if (!parsed.IsSuccess)
            {
                return OperateResult.CreateFailedResult<byte[]>(parsed.Message);
            }

            items[index] = parsed.Content;
        }

        return await ExecuteReadAsync(items);
    }

    private async Task<OperateResult<byte[]>> ExecuteReadAsync(IReadOnlyList<S7AddressData> items)
    {
        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            var paramLength = 2 + (items.Count * 12);
            var totalLength = 4 + 3 + 10 + paramLength;
            var frame = new byte[totalLength];
            frame[0] = 0x03;
            frame[1] = 0x00;
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(2, 2), (ushort)totalLength);
            frame[4] = 0x02;
            frame[5] = 0xF0;
            frame[6] = 0x80;
            frame[7] = 0x32;
            frame[8] = 0x01;
            var sequence = (ushort)(Interlocked.Increment(ref _pduSequence) & 0xFFFF);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(11, 2), sequence);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(13, 2), (ushort)paramLength);
            BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(15, 2), 0);
            frame[17] = 0x04;
            frame[18] = (byte)items.Count;

            var offset = 19;
            foreach (var item in items)
            {
                frame[offset] = 0x12;
                frame[offset + 1] = 0x0A;
                frame[offset + 2] = 0x10;
                frame[offset + 3] = item.IsBit ? (byte)0x01 : (byte)0x02;
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 4, 2), item.Length);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(offset + 6, 2), item.DbBlock);
                frame[offset + 8] = item.DataCode;
                frame[offset + 9] = (byte)((item.AddressStart >> 16) & 0xFF);
                frame[offset + 10] = (byte)((item.AddressStart >> 8) & 0xFF);
                frame[offset + 11] = (byte)(item.AddressStart & 0xFF);
                offset += 12;
            }

            await SendAllAsync(frame, timeout.Token);
            var packet = await ReadTpktPacketAsync(timeout.Token);
            if (packet.Length < 21 || packet[7] != 0x32 || packet[8] != 0x03)
            {
                return OperateResult.CreateFailedResult<byte[]>("西门子 S7 读取响应报文头无效。");
            }

            var errorClass = packet[17];
            var errorCode = packet[18];
            if (errorClass != 0 || errorCode != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"西门子 S7 协议错误: Class=0x{errorClass:X2}, Code=0x{errorCode:X2}");
            }

            var respParamLen = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(13, 2));
            var dataCursor = 19 + respParamLen;
            var totalBytes = items.Sum(item => (int)item.Length);
            var output = new byte[totalBytes];
            var outOffset = 0;

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (dataCursor + 4 > packet.Length)
                {
                    return OperateResult.CreateFailedResult<byte[]>("西门子 S7 响应数据区被截断。");
                }

                var returnCode = packet[dataCursor];
                var transportSize = packet[dataCursor + 1];
                var rawLen = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(dataCursor + 2, 2));
                var byteLen = transportSize is 0x04 or 0x05 or 0x07 ? (rawLen + 7) / 8 : rawLen;
                dataCursor += 4;

                if (returnCode != 0xFF)
                {
                    var marker = returnCode is 0x05 or 0x0A ? "非法数据地址" : "读取的数据越界";
                    return OperateResult.CreateFailedResult<byte[]>($"西门子点位返回码 0x{returnCode:X2} ({marker})");
                }

                if (dataCursor + byteLen > packet.Length)
                {
                    return OperateResult.CreateFailedResult<byte[]>("西门子 S7 数据项长度不足。");
                }

                var copyLen = Math.Min((int)item.Length, byteLen);
                Buffer.BlockCopy(packet, dataCursor, output, outOffset, copyLen);
                outOffset += item.Length;
                dataCursor += byteLen;
                if (byteLen % 2 == 1 && index < items.Count - 1)
                {
                    dataCursor++;
                }
            }

            return OperateResult.CreateSuccessResult(output);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }

    private async ValueTask<byte[]> ReadTpktPacketAsync(CancellationToken cancellationToken)
    {
        var header = await ReceiveExactAsync(4, cancellationToken);
        if (header[0] != 0x03)
        {
            throw new IOException($"无效 TPKT 版本号: 0x{header[0]:X2}");
        }

        var packetLength = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2, 2));
        if (packetLength < 4)
        {
            throw new IOException($"无效 TPKT 报文长度: {packetLength}");
        }

        var body = await ReceiveExactAsync(packetLength - 4, cancellationToken);
        var full = new byte[packetLength];
        Buffer.BlockCopy(header, 0, full, 0, 4);
        Buffer.BlockCopy(body, 0, full, 4, body.Length);
        return full;
    }
}

// ============================================================================
// 2. Modbus TCP, Inovance TCP, and Delta TCP Native Clients
// ============================================================================

internal class ModbusTcpNet : DeviceTcpNet
{
    private int _transactionId;

    public ModbusTcpNet(string ipAddress = "127.0.0.1", int port = 502, byte station = 1)
        : base(ipAddress, port)
    {
        Station = station;
    }

    public byte Station { get; set; }

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var parsed = ParseModbusAddress(address, defaultFunctionCode: 1);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(parsed.Message);
        }

        var (functionCode, startAddress, station) = parsed.Content;
        if (functionCode is 3 or 4)
        {
            var regRead = await ExecuteModbusReadAsync(functionCode, startAddress, 1, station);
            if (!regRead.IsSuccess)
            {
                return OperateResult.CreateFailedResult<bool>(regRead);
            }

            return OperateResult.CreateSuccessResult(regRead.Content.Length >= 2 && (regRead.Content[0] != 0 || regRead.Content[1] != 0));
        }

        var coilRead = await ExecuteModbusReadAsync(functionCode, startAddress, 1, station);
        if (!coilRead.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(coilRead);
        }

        return OperateResult.CreateSuccessResult(coilRead.Content.Length > 0 && (coilRead.Content[0] & 0x01) != 0);
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var parsed = ParseModbusAddress(address, defaultFunctionCode: 3);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(parsed.Message);
        }

        var (functionCode, startAddress, station) = parsed.Content;
        return await ExecuteModbusReadAsync(functionCode, startAddress, Math.Max(length, (ushort)1), station);
    }

    protected virtual OperateResult<(byte FunctionCode, ushort Address, byte Station)> ParseModbusAddress(
        string address,
        byte defaultFunctionCode)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<(byte, ushort, byte)>("Modbus地址格式错误：地址为空。");
        }

        var functionCode = defaultFunctionCode;
        var station = Station;
        var raw = address.Trim();

        foreach (var part in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("x=", StringComparison.OrdinalIgnoreCase))
            {
                if (!byte.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out functionCode) ||
                    functionCode is < 1 or > 4)
                {
                    return OperateResult.CreateFailedResult<(byte, ushort, byte)>($"非法数据地址功能码: {part}");
                }
            }
            else if (part.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
            {
                if (!byte.TryParse(part.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out station))
                {
                    return OperateResult.CreateFailedResult<(byte, ushort, byte)>($"非法Modbus站号: {part}");
                }
            }
            else
            {
                raw = part;
            }
        }

        if (!ushort.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return OperateResult.CreateFailedResult<(byte, ushort, byte)>($"非法数据地址: {address}");
        }

        return OperateResult.CreateSuccessResult((functionCode, offset, station));
    }

    private async Task<OperateResult<byte[]>> ExecuteModbusReadAsync(
        byte functionCode,
        ushort startAddress,
        ushort count,
        byte station)
    {
        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            var txId = (ushort)(Interlocked.Increment(ref _transactionId) & 0xFFFF);
            var request = new byte[12];
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0, 2), txId);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2, 2), 0);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), 6);
            request[6] = station;
            request[7] = functionCode;
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8, 2), startAddress);
            BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10, 2), count);

            await SendAllAsync(request, timeout.Token);
            var mbap = await ReceiveExactAsync(7, timeout.Token);
            var bodyLen = BinaryPrimitives.ReadUInt16BigEndian(mbap.AsSpan(4, 2));
            if (bodyLen < 2 || bodyLen > 260)
            {
                return OperateResult.CreateFailedResult<byte[]>($"Modbus MBAP响应长度异常: {bodyLen}");
            }

            var pdu = await ReceiveExactAsync(bodyLen - 1, timeout.Token);
            var respFc = pdu[0];
            if (respFc == (byte)(functionCode | 0x80))
            {
                var exCode = pdu.Length > 1 ? pdu[1] : (byte)0;
                var reason = exCode switch
                {
                    0x01 => "非法功能码 (illegal data address)",
                    0x02 => "非法数据地址 (illegal data address)",
                    0x03 => "读取的数据越界 (address out of range)",
                    _ => $"Modbus异常码 0x{exCode:X2}"
                };
                return OperateResult.CreateFailedResult<byte[]>(reason, exCode);
            }

            if (respFc != functionCode || pdu.Length < 2)
            {
                return OperateResult.CreateFailedResult<byte[]>($"Modbus响应功能码不匹配: 0x{respFc:X2}");
            }

            var byteCount = pdu[1];
            if (pdu.Length < 2 + byteCount)
            {
                return OperateResult.CreateFailedResult<byte[]>("Modbus响应载荷长度不足。");
            }

            return OperateResult.CreateSuccessResult(pdu.AsSpan(2, byteCount).ToArray());
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }
}

internal sealed class InovanceTcpNet : ModbusTcpNet
{
    public InovanceTcpNet()
    {
    }

    public InovanceTcpNet(InovanceSeries series, string ipAddress = "127.0.0.1", int port = 502, byte station = 1)
        : base(ipAddress, port, station)
    {
        Series = series;
    }

    public InovanceSeries Series { get; set; } = InovanceSeries.AM;

    public OperateResult<string> TranslateToModbusAddress(string address, byte modbusCode)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<string>("汇川PLC地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        try
        {
            return Series switch
            {
                InovanceSeries.AM => TranslateAmAddress(text, modbusCode),
                InovanceSeries.H3U or InovanceSeries.H5U or InovanceSeries.Easy => TranslateHSeriesAddress(text, Series, modbusCode),
                _ => OperateResult.CreateFailedResult<string>($"不支持的汇川系列: {Series}")
            };
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<string>($"汇川地址格式错误 ({address}): {exception.Message}");
        }
    }

    protected override OperateResult<(byte FunctionCode, ushort Address, byte Station)> ParseModbusAddress(
        string address,
        byte defaultFunctionCode)
    {
        var translated = TranslateToModbusAddress(address, defaultFunctionCode);
        if (!translated.IsSuccess)
        {
            return OperateResult.CreateFailedResult<(byte, ushort, byte)>(translated.Message);
        }

        return base.ParseModbusAddress(translated.Content, defaultFunctionCode);
    }

    private static OperateResult<string> TranslateAmAddress(string text, byte modbusCode)
    {
        if (text.StartsWith("SD", StringComparison.Ordinal) || text.StartsWith("MW", StringComparison.Ordinal))
        {
            if (ushort.TryParse(text.AsSpan(2), out var wordAddr))
            {
                return OperateResult.CreateSuccessResult($"x={modbusCode};{wordAddr}");
            }
        }
        else if (text.StartsWith("MD", StringComparison.Ordinal))
        {
            if (ushort.TryParse(text.AsSpan(2), out var dwordAddr))
            {
                return OperateResult.CreateSuccessResult($"x={modbusCode};{dwordAddr * 2}");
            }
        }
        else if (text.StartsWith("SM", StringComparison.Ordinal))
        {
            if (TryParseBitOrWordIndex(text[2..], out var smAddr))
            {
                return OperateResult.CreateSuccessResult($"x={modbusCode};{smAddr}");
            }
        }
        else if (text.StartsWith("QX", StringComparison.Ordinal) || text.StartsWith("IX", StringComparison.Ordinal) ||
                 text.StartsWith("MX", StringComparison.Ordinal))
        {
            if (TryParseBitOrWordIndex(text[2..], out var bitAddr))
            {
                return OperateResult.CreateSuccessResult($"x={modbusCode};{bitAddr}");
            }
        }
        else if (text[0] is 'Q' or 'I' or 'M')
        {
            if (TryParseBitOrWordIndex(text[1..], out var bitAddr))
            {
                return OperateResult.CreateSuccessResult($"x={modbusCode};{bitAddr}");
            }
        }

        return OperateResult.CreateFailedResult<string>($"非法汇川AM系列地址: {text}");
    }

    private static OperateResult<string> TranslateHSeriesAddress(string text, InovanceSeries series, byte modbusCode)
    {
        if (text.StartsWith("SD", StringComparison.Ordinal) && ushort.TryParse(text.AsSpan(2), out var sd))
        {
            return OperateResult.CreateSuccessResult($"x=3;{0x2400 + sd}");
        }

        if (text.StartsWith("SM", StringComparison.Ordinal) && ushort.TryParse(text.AsSpan(2), out var sm))
        {
            return OperateResult.CreateSuccessResult($"x=1;{0x2400 + sm}");
        }

        var prefix = text[0];
        var numberPart = text[1..];
        switch (prefix)
        {
            case 'D' when ushort.TryParse(numberPart, out var d):
                return OperateResult.CreateSuccessResult($"x=3;{d}");
            case 'R' when ushort.TryParse(numberPart, out var r):
                return OperateResult.CreateSuccessResult($"x=3;{0x3000 + r}");
            case 'M' when ushort.TryParse(numberPart, out var m):
                var mOffset = series == InovanceSeries.H3U && m >= 8000 ? 0x1F40 + (m - 8000) : m;
                return OperateResult.CreateSuccessResult($"x=1;{mOffset}");
            case 'S' when ushort.TryParse(numberPart, out var s):
                return OperateResult.CreateSuccessResult($"x=1;{0xE000 + s}");
            case 'T' when ushort.TryParse(numberPart, out var t):
                return OperateResult.CreateSuccessResult($"x={modbusCode};{(modbusCode == 1 ? 0xF000 + t : 0xF000 + t)}");
            case 'C' when ushort.TryParse(numberPart, out var c):
                return OperateResult.CreateSuccessResult($"x={modbusCode};{(modbusCode == 1 ? 0xF400 + c : 0xF400 + c)}");
            case 'X' when TryParseOctal(numberPart, out var x):
                return OperateResult.CreateSuccessResult($"x=1;{0xF800 + x}");
            case 'Y' when TryParseOctal(numberPart, out var y):
                return OperateResult.CreateSuccessResult($"x=1;{0xFC00 + y}");
            default:
                return OperateResult.CreateFailedResult<string>($"非法汇川{series}系列地址: {text}");
        }
    }

    private static bool TryParseBitOrWordIndex(string text, out ushort result)
    {
        result = 0;
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            if (!ushort.TryParse(text.AsSpan(0, dot), out var byteIndex) ||
                !ushort.TryParse(text.AsSpan(dot + 1), out var bitIndex) ||
                bitIndex > 15)
            {
                return false;
            }

            result = (ushort)((byteIndex * 8) + bitIndex);
            return true;
        }

        return ushort.TryParse(text, out result);
    }

    private static bool TryParseOctal(string text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var acc = 0;
        foreach (var ch in text)
        {
            if (ch is < '0' or > '7')
            {
                return false;
            }

            acc = (acc * 8) + (ch - '0');
        }

        value = (ushort)acc;
        return true;
    }
}

internal sealed class DeltaTcpNet : ModbusTcpNet
{
    public DeltaTcpNet()
    {
    }

    public DeltaTcpNet(string ipAddress, int port = 502, byte station = 1)
        : base(ipAddress, port, station)
    {
    }

    public DeltaSeries Series { get; set; } = DeltaSeries.Dvp;

    public OperateResult<string> TranslateToModbusAddress(string address, byte modbusCode)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<string>("台达PLC地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        if (text.Length < 2)
        {
            return OperateResult.CreateFailedResult<string>($"非法台达PLC地址: {address}");
        }

        var prefix = text[0];
        var numberPart = text[1..];
        if (Series == DeltaSeries.AS)
        {
            return prefix switch
            {
                'D' when ushort.TryParse(numberPart, out var d) => OperateResult.CreateSuccessResult($"x=3;{d}"),
                'M' when ushort.TryParse(numberPart, out var m) => OperateResult.CreateSuccessResult($"x=1;{m}"),
                'X' when TryParseDeltaBitOrOctal(numberPart, out var x) => OperateResult.CreateSuccessResult($"x=2;{0x6000 + x}"),
                'Y' when TryParseDeltaBitOrOctal(numberPart, out var y) => OperateResult.CreateSuccessResult($"x=1;{0xA000 + y}"),
                'S' when ushort.TryParse(numberPart, out var s) => OperateResult.CreateSuccessResult($"x=1;{0x5000 + s}"),
                'T' when ushort.TryParse(numberPart, out var t) => OperateResult.CreateSuccessResult($"x={modbusCode};{0xC000 + t}"),
                'C' when ushort.TryParse(numberPart, out var c) => OperateResult.CreateSuccessResult($"x={modbusCode};{0xE000 + c}"),
                _ => OperateResult.CreateFailedResult<string>($"非法台达AS系列地址: {address}")
            };
        }

        return prefix switch
        {
            'D' when ushort.TryParse(numberPart, out var d) =>
                OperateResult.CreateSuccessResult($"x=3;{(d >= 4096 ? 0x9000 + (d - 4096) : 0x1000 + d)}"),
            'M' when ushort.TryParse(numberPart, out var m) =>
                OperateResult.CreateSuccessResult($"x=1;{(m >= 1536 ? 0xB000 + (m - 1536) : 0x0800 + m)}"),
            'X' when TryParseOctalDvp(numberPart, out var x) =>
                OperateResult.CreateSuccessResult($"x=2;{0x0400 + x}"),
            'Y' when TryParseOctalDvp(numberPart, out var y) =>
                OperateResult.CreateSuccessResult($"x=1;{0x0500 + y}"),
            'S' when ushort.TryParse(numberPart, out var s) =>
                OperateResult.CreateSuccessResult($"x=1;{s}"),
            'T' when ushort.TryParse(numberPart, out var t) =>
                OperateResult.CreateSuccessResult($"x={modbusCode};{0x0600 + t}"),
            'C' when ushort.TryParse(numberPart, out var c) =>
                OperateResult.CreateSuccessResult($"x={modbusCode};{0x0E00 + c}"),
            _ => OperateResult.CreateFailedResult<string>($"非法台达DVP系列地址: {address}")
        };
    }

    protected override OperateResult<(byte FunctionCode, ushort Address, byte Station)> ParseModbusAddress(
        string address,
        byte defaultFunctionCode)
    {
        var translated = TranslateToModbusAddress(address, defaultFunctionCode);
        if (!translated.IsSuccess)
        {
            return OperateResult.CreateFailedResult<(byte, ushort, byte)>(translated.Message);
        }

        return base.ParseModbusAddress(translated.Content, defaultFunctionCode);
    }

    private static bool TryParseOctalDvp(string text, out ushort value)
    {
        value = 0;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var acc = 0;
        foreach (var ch in text)
        {
            if (ch is < '0' or > '7')
            {
                return false;
            }

            acc = (acc * 8) + (ch - '0');
        }

        value = (ushort)acc;
        return true;
    }

    private static bool TryParseDeltaBitOrOctal(string text, out ushort value)
    {
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            value = 0;
            if (!ushort.TryParse(text.AsSpan(0, dot), out var word) ||
                !ushort.TryParse(text.AsSpan(dot + 1), out var bit) ||
                bit > 15)
            {
                return false;
            }

            value = (ushort)((word * 16) + bit);
            return true;
        }

        return ushort.TryParse(text, out value);
    }
}

// ============================================================================
// 3. Mitsubishi MC (3E/1E Binary/ASCII TCP/UDP) & Keyence KV MC Native Clients
// ============================================================================

internal sealed record McAddressData(
    string DeviceType,
    byte BinaryCode,
    ushort A1ECode,
    int AddressStart,
    ushort Length,
    bool IsBit,
    bool IsHexAddress);

internal class MelsecMcNet : DeviceTcpNet, IReadWriteMc
{
    public MelsecMcNet(string ipAddress = "127.0.0.1", int port = 5000)
        : base(ipAddress, port)
    {
    }

    public byte NetworkNumber { get; set; }
    public byte PLCNumber { get; set; } = 0xFF;
    public byte NetworkStationNumber { get; set; }
    public ushort TargetIOStation { get; set; } = 0x03FF;
    protected virtual bool IsAsciiProtocol => false;

    public virtual OperateResult<McAddressData> McAnalysisAddress(string address, ushort length, bool isBit)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<McAddressData>("三菱MC地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        string prefix;
        string numberPart;
        if (text.Length >= 2 && text[..2] is "ZR" or "SD" or "SM" or "TN" or "TS" or "CN" or "CS" or "SW" or "SB" or "DX" or "DY")
        {
            prefix = text[..2];
            numberPart = text[2..];
        }
        else
        {
            prefix = text[..1];
            numberPart = text[1..];
        }

        (byte binCode, ushort a1eCode, bool bitDevice, int radix) = prefix switch
        {
            "D" => ((byte)0xA8, (ushort)0x4420, false, 10),
            "W" => ((byte)0xB4, (ushort)0x5720, false, 16),
            "R" => ((byte)0xAF, (ushort)0x5220, false, 10),
            "ZR" => ((byte)0xB0, (ushort)0x5A52, false, 10),
            "SD" => ((byte)0xA9, (ushort)0x5344, false, 10),
            "SW" => ((byte)0xB5, (ushort)0x5357, false, 16),
            "TN" => ((byte)0xC2, (ushort)0x544E, false, 10),
            "CN" => ((byte)0xC5, (ushort)0x434E, false, 10),
            "M" => ((byte)0x90, (ushort)0x4D20, true, 10),
            "SM" => ((byte)0x91, (ushort)0x534D, true, 10),
            "L" => ((byte)0x92, (ushort)0x4C20, true, 10),
            "F" => ((byte)0x93, (ushort)0x4620, true, 10),
            "V" => ((byte)0x94, (ushort)0x5620, true, 10),
            "S" => ((byte)0x98, (ushort)0x5320, true, 10),
            "X" => ((byte)0x9C, (ushort)0x5820, true, 16),
            "Y" => ((byte)0x9D, (ushort)0x5920, true, 16),
            "B" => ((byte)0xA0, (ushort)0x4220, true, 16),
            "SB" => ((byte)0xA1, (ushort)0x5342, true, 16),
            "TS" => ((byte)0xC1, (ushort)0x5453, true, 10),
            "CS" => ((byte)0xC4, (ushort)0x4353, true, 10),
            _ => ((byte)0, (ushort)0, false, 0)
        };

        if (binCode == 0 || string.IsNullOrWhiteSpace(numberPart))
        {
            return OperateResult.CreateFailedResult<McAddressData>($"非法三菱MC地址: {address}");
        }

        try
        {
            var parsedAddr = Convert.ToInt32(numberPart, radix);
            if (parsedAddr < 0)
            {
                return OperateResult.CreateFailedResult<McAddressData>($"非法三菱MC负地址: {address}");
            }

            return OperateResult.CreateSuccessResult(new McAddressData(
                prefix.PadRight(2, '*'),
                binCode,
                a1eCode,
                parsedAddr,
                Math.Max(length, (ushort)1),
                isBit || bitDevice,
                radix == 16));
        }
        catch
        {
            return OperateResult.CreateFailedResult<McAddressData>($"无法解析三菱MC地址偏移: {address}");
        }
    }

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var analysis = McAnalysisAddress(address, 1, isBit: true);
        if (!analysis.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(analysis.Message);
        }

        var read = await ExecuteMc3EReadAsync(analysis.Content with { IsBit = true, Length = 1 });
        if (!read.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(read);
        }

        return OperateResult.CreateSuccessResult(read.Content.Length > 0 && read.Content[0] != 0);
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var analysis = McAnalysisAddress(address, length, isBit: false);
        if (!analysis.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(analysis.Message);
        }

        return await ExecuteMc3EReadAsync(analysis.Content);
    }

    private async Task<OperateResult<byte[]>> ExecuteMc3EReadAsync(McAddressData addr)
    {
        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            if (IsAsciiProtocol)
            {
                var addrText = addr.IsHexAddress
                    ? addr.AddressStart.ToString("X6", CultureInfo.InvariantCulture)
                    : addr.AddressStart.ToString("D6", CultureInfo.InvariantCulture);
                var subcmd = addr.IsBit ? "0001" : "0000";
                var body = $"00100401{subcmd}{addr.DeviceType}{addrText}{addr.Length:X4}";
                var frameStr = $"5000{NetworkNumber:X2}{PLCNumber:X2}{TargetIOStation:X4}{NetworkStationNumber:X2}{body.Length:X4}{body}";
                var reqBytes = Encoding.ASCII.GetBytes(frameStr);
                await SendAllAsync(reqBytes, timeout.Token);

                byte[] respBytes;
                if (IsUdpTransport)
                {
                    respBytes = await ReceiveDatagramAsync(4096, timeout.Token);
                }
                else
                {
                    var head = await ReceiveExactAsync(22, timeout.Token);
                    var dataLenHex = Encoding.ASCII.GetString(head, 14, 4);
                    var dataLen = Convert.ToInt32(dataLenHex, 16);
                    var remain = dataLen > 4 ? await ReceiveExactAsync(dataLen - 4, timeout.Token) : [];
                    respBytes = [.. head, .. remain];
                }

                if (respBytes.Length < 22)
                {
                    return OperateResult.CreateFailedResult<byte[]>("三菱MC ASCII 3E响应长度不足。");
                }

                var endCode = Convert.ToUInt16(Encoding.ASCII.GetString(respBytes, 18, 4), 16);
                if (endCode != 0)
                {
                    return OperateResult.CreateFailedResult<byte[]>($"三菱MC错误码 0x{endCode:X4} (非法数据地址)", endCode);
                }

                var payloadAscii = Encoding.ASCII.GetString(respBytes, 22, respBytes.Length - 22);
                if (addr.IsBit)
                {
                    return OperateResult.CreateSuccessResult(payloadAscii.Select(ch => ch == '1' ? (byte)1 : (byte)0).ToArray());
                }

                var wordBytes = new byte[(payloadAscii.Length / 4) * 2];
                for (var i = 0; i + 4 <= payloadAscii.Length; i += 4)
                {
                    var word = Convert.ToUInt16(payloadAscii.Substring(i, 4), 16);
                    BinaryPrimitives.WriteUInt16BigEndian(wordBytes.AsSpan((i / 4) * 2, 2), word);
                }

                return OperateResult.CreateSuccessResult(wordBytes);
            }
            else
            {
                var req = new byte[21];
                req[0] = 0x50;
                req[1] = 0x00;
                req[2] = NetworkNumber;
                req[3] = PLCNumber;
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(4, 2), TargetIOStation);
                req[6] = NetworkStationNumber;
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(7, 2), 12);
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(9, 2), 0x0010);
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(11, 2), 0x0401);
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(13, 2), addr.IsBit ? (ushort)1 : (ushort)0);
                req[15] = (byte)(addr.AddressStart & 0xFF);
                req[16] = (byte)((addr.AddressStart >> 8) & 0xFF);
                req[17] = (byte)((addr.AddressStart >> 16) & 0xFF);
                req[18] = addr.BinaryCode;
                BinaryPrimitives.WriteUInt16LittleEndian(req.AsSpan(19, 2), addr.Length);

                await SendAllAsync(req, timeout.Token);

                byte[] resp;
                if (IsUdpTransport)
                {
                    resp = await ReceiveDatagramAsync(4096, timeout.Token);
                }
                else
                {
                    var head = await ReceiveExactAsync(11, timeout.Token);
                    var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(head.AsSpan(7, 2));
                    var remain = dataLen > 2 ? await ReceiveExactAsync(dataLen - 2, timeout.Token) : [];
                    resp = [.. head, .. remain];
                }

                if (resp.Length < 11)
                {
                    return OperateResult.CreateFailedResult<byte[]>("三菱MC Binary 3E响应长度不足。");
                }

                var endCode = BinaryPrimitives.ReadUInt16LittleEndian(resp.AsSpan(9, 2));
                if (endCode != 0)
                {
                    return OperateResult.CreateFailedResult<byte[]>($"三菱MC错误码 0x{endCode:X4} (非法数据地址)", endCode);
                }

                var rawPayload = resp.AsSpan(11).ToArray();
                if (addr.IsBit)
                {
                    var bitResult = new byte[addr.Length];
                    for (var i = 0; i < addr.Length && (i / 2) < rawPayload.Length; i++)
                    {
                        var b = rawPayload[i / 2];
                        bitResult[i] = (i % 2 == 0 ? (b >> 4) & 0x01 : b & 0x01) != 0 ? (byte)1 : (byte)0;
                    }

                    return OperateResult.CreateSuccessResult(bitResult);
                }

                return OperateResult.CreateSuccessResult(rawPayload);
            }
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }
}

internal sealed class MelsecMcAsciiNet(string ipAddress = "127.0.0.1", int port = 5000) : MelsecMcNet(ipAddress, port)
{
    protected override bool IsAsciiProtocol => true;
}

internal sealed class MelsecMcUdp(string ipAddress = "127.0.0.1", int port = 5000) : MelsecMcNet(ipAddress, port)
{
    protected override bool IsUdpTransport => true;
}

internal sealed class MelsecMcAsciiUdp(string ipAddress = "127.0.0.1", int port = 5000) : MelsecMcNet(ipAddress, port)
{
    protected override bool IsAsciiProtocol => true;
    protected override bool IsUdpTransport => true;
}

internal sealed class MelsecA1ENet(string ipAddress = "127.0.0.1", int port = 5000) : DeviceTcpNet(ipAddress, port)
{
    public byte PLCNumber { get; set; } = 0xFF;

    public static OperateResult<byte[]> BuildReadCommand(string address, ushort length, bool isBit, byte plcNumber)
    {
        var parser = new MelsecMcNet();
        var analysis = parser.McAnalysisAddress(address, length, isBit);
        if (!analysis.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(analysis.Message);
        }

        var addr = analysis.Content;
        var frame = new byte[12];
        frame[0] = (byte)(isBit || addr.IsBit ? 0x00 : 0x01);
        frame[1] = plcNumber;
        frame[2] = 0x0A;
        frame[3] = 0x00;
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4, 4), addr.AddressStart);
        frame[8] = (byte)(addr.A1ECode & 0xFF);
        frame[9] = (byte)((addr.A1ECode >> 8) & 0xFF);
        frame[10] = (byte)(addr.Length % 256);
        frame[11] = 0x00;
        return OperateResult.CreateSuccessResult(frame);
    }

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var cmd = BuildReadCommand(address, 1, isBit: true, PLCNumber);
        if (!cmd.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(cmd.Message);
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            await SendAllAsync(cmd.Content, timeout.Token);
            var head = await ReceiveExactAsync(2, timeout.Token);
            if (head[1] != 0)
            {
                return OperateResult.CreateFailedResult<bool>($"三菱A-1E读取错误码: 0x{head[1]:X2}");
            }

            var data = await ReceiveExactAsync(1, timeout.Token);
            return OperateResult.CreateSuccessResult(((data[0] >> 4) & 0x01) != 0);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<bool>(exception.Message);
        }
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var cmd = BuildReadCommand(address, length, isBit: false, PLCNumber);
        if (!cmd.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(cmd.Message);
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            await SendAllAsync(cmd.Content, timeout.Token);
            var head = await ReceiveExactAsync(2, timeout.Token);
            if (head[1] != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"三菱A-1E读取错误码: 0x{head[1]:X2} (非法数据地址)");
            }

            var data = await ReceiveExactAsync(Math.Max((int)length, 1) * 2, timeout.Token);
            return OperateResult.CreateSuccessResult(data);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }
}

internal sealed class MelsecA1EAsciiNet(string ipAddress = "127.0.0.1", int port = 5000) : DeviceTcpNet(ipAddress, port)
{
    public byte PLCNumber { get; set; } = 0xFF;

    public static OperateResult<byte[]> BuildReadCommand(string address, ushort length, bool isBit, byte plcNumber)
    {
        var parser = new MelsecMcNet();
        var analysis = parser.McAnalysisAddress(address, length, isBit);
        if (!analysis.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(analysis.Message);
        }

        var addr = analysis.Content;
        var subheader = isBit || addr.IsBit ? "00" : "01";
        var devCode = $"{(char)((addr.A1ECode >> 8) & 0xFF)}{(char)(addr.A1ECode & 0xFF)}";
        var addrStr = addr.IsHexAddress
            ? addr.AddressStart.ToString("X8", CultureInfo.InvariantCulture)
            : addr.AddressStart.ToString("D8", CultureInfo.InvariantCulture);
        var points = (addr.Length % 256).ToString("X2", CultureInfo.InvariantCulture);
        var ascii = $"{subheader}{plcNumber:X2}000A{devCode}{addrStr}{points}00";
        return OperateResult.CreateSuccessResult(Encoding.ASCII.GetBytes(ascii));
    }

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var cmd = BuildReadCommand(address, 1, isBit: true, PLCNumber);
        if (!cmd.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(cmd.Message);
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            await SendAllAsync(cmd.Content, timeout.Token);
            var head = await ReceiveExactAsync(4, timeout.Token);
            var endCode = Convert.ToByte(Encoding.ASCII.GetString(head, 2, 2), 16);
            if (endCode != 0)
            {
                return OperateResult.CreateFailedResult<bool>($"三菱A-1E ASCII读取错误码: 0x{endCode:X2}");
            }

            var body = await ReceiveExactAsync(2, timeout.Token);
            return OperateResult.CreateSuccessResult(body[0] == (byte)'1');
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<bool>(exception.Message);
        }
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var cmd = BuildReadCommand(address, length, isBit: false, PLCNumber);
        if (!cmd.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(cmd.Message);
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            await SendAllAsync(cmd.Content, timeout.Token);
            var head = await ReceiveExactAsync(4, timeout.Token);
            var endCode = Convert.ToByte(Encoding.ASCII.GetString(head, 2, 2), 16);
            if (endCode != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"三菱A-1E ASCII读取错误码: 0x{endCode:X2} (非法数据地址)");
            }

            var wordCount = Math.Max((int)length, 1);
            var asciiData = Encoding.ASCII.GetString(await ReceiveExactAsync(wordCount * 4, timeout.Token));
            var bytes = new byte[wordCount * 2];
            for (var i = 0; i < wordCount; i++)
            {
                var word = Convert.ToUInt16(asciiData.Substring(i * 4, 4), 16);
                BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(i * 2, 2), word);
            }

            return OperateResult.CreateSuccessResult(bytes);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }
}

internal sealed class KeyenceMcNet(string ipAddress = "127.0.0.1", int port = 5000) : MelsecMcNet(ipAddress, port)
{
    public override OperateResult<McAddressData> McAnalysisAddress(string address, ushort length, bool isBit)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<McAddressData>("基恩士MC地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        if (text.StartsWith("DM", StringComparison.Ordinal))
        {
            return base.McAnalysisAddress($"D{text[2..]}", length, isBit: false);
        }

        if (text.StartsWith("CM", StringComparison.Ordinal))
        {
            return base.McAnalysisAddress($"SD{text[2..]}", length, isBit: false);
        }

        if (text.StartsWith("MR", StringComparison.Ordinal) && TryParseKeyenceRelay(text[2..], out var mrIndex))
        {
            return base.McAnalysisAddress($"M{mrIndex}", length, isBit: true);
        }

        if (text.StartsWith("LR", StringComparison.Ordinal) && TryParseKeyenceRelay(text[2..], out var lrIndex))
        {
            return base.McAnalysisAddress($"L{lrIndex}", length, isBit: true);
        }

        if (text.StartsWith("CR", StringComparison.Ordinal) && TryParseKeyenceRelay(text[2..], out var crIndex))
        {
            return base.McAnalysisAddress($"SM{crIndex}", length, isBit: true);
        }

        if (text.StartsWith("R", StringComparison.Ordinal) && char.IsAsciiDigit(text.ElementAtOrDefault(1)) &&
            TryParseKeyenceRelay(text[1..], out var rIndex))
        {
            return base.McAnalysisAddress($"X{rIndex:X}", length, isBit: true);
        }

        return base.McAnalysisAddress(text, length, isBit);
    }

    private static bool TryParseKeyenceRelay(string digits, out int linearBitIndex)
    {
        linearBitIndex = 0;
        if (!int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var raw) || raw < 0)
        {
            return false;
        }

        var channel = raw / 100;
        var bit = raw % 100;
        if (bit > 15)
        {
            return false;
        }

        linearBitIndex = (channel * 16) + bit;
        return true;
    }
}

// ============================================================================
// 4. Panasonic Mewtocol Over TCP Native Client & Address Parser
// ============================================================================

internal static class PanasonicHelper
{
    public static OperateResult<(string AreaType, int Address, bool IsBit)> AnalysisAddress(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<(string, int, bool)>("松下Mewtocol地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        if (text.StartsWith("DT", StringComparison.Ordinal) ||
            text.StartsWith("LD", StringComparison.Ordinal) ||
            text.StartsWith("FL", StringComparison.Ordinal))
        {
            var area = text[..2];
            return int.TryParse(text.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var wordOffset) && wordOffset >= 0
                ? OperateResult.CreateSuccessResult((area, wordOffset, false))
                : OperateResult.CreateFailedResult<(string, int, bool)>($"非法松下字寄存器地址: {address}");
        }

        var prefix = text[0];
        if (prefix is 'R' or 'X' or 'Y' or 'L' or 'T' or 'C' &&
            TryParsePanasonicContact(text[1..], out var contactOffset))
        {
            return OperateResult.CreateSuccessResult((prefix.ToString(), contactOffset, true));
        }

        return OperateResult.CreateFailedResult<(string, int, bool)>($"不支持的松下Mewtocol地址: {address}");
    }

    private static bool TryParsePanasonicContact(string digits, out int index)
    {
        index = 0;
        if (string.IsNullOrWhiteSpace(digits))
        {
            return false;
        }

        if (digits.Length == 1 && int.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var singleBit))
        {
            index = singleBit;
            return true;
        }

        var wordPart = digits[..^1];
        var bitChar = digits[^1];
        if (!int.TryParse(wordPart, NumberStyles.None, CultureInfo.InvariantCulture, out var word) ||
            !int.TryParse(bitChar.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bit))
        {
            return false;
        }

        index = (word * 16) + bit;
        return true;
    }

    public static string AppendBccAndCr(string commandWithoutBcc)
    {
        byte xor = 0;
        foreach (var ch in commandWithoutBcc)
        {
            xor ^= (byte)ch;
        }

        return $"{commandWithoutBcc}{xor:X2}\r";
    }
}

internal sealed class PanasonicMewtocolOverTcp(
    string ipAddress = "127.0.0.1",
    int port = 9094,
    byte station = 0xEE) : DeviceTcpNet(ipAddress, port)
{
    public byte Station { get; set; } = station == 0 ? (byte)0xEE : station;

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var parsed = PanasonicHelper.AnalysisAddress(address);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(parsed.Message);
        }

        var (area, index, _) = parsed.Content;
        var word = index / 16;
        var bit = index % 16;
        var cmdCore = $"%{Station:X2}#RCP1{area}{word:D3}{bit:X1}";
        var response = await ExecuteMewtocolAsync(PanasonicHelper.AppendBccAndCr(cmdCore));
        if (!response.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(response);
        }

        var body = response.Content;
        var rcIdx = body.IndexOf("$RC", StringComparison.Ordinal);
        if (rcIdx < 0 || rcIdx + 3 >= body.Length)
        {
            return OperateResult.CreateFailedResult<bool>($"松下Mewtocol位响应格式异常: {body}");
        }

        return OperateResult.CreateSuccessResult(body[rcIdx + 3] == '1');
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var parsed = PanasonicHelper.AnalysisAddress(address);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(parsed.Message);
        }

        var (area, startAddr, isBit) = parsed.Content;
        var wordCount = Math.Max((int)length, 1);
        var endAddr = startAddr + wordCount - 1;
        var cmdCore = isBit
            ? $"%{Station:X2}#RCC{area}{startAddr / 16:D4}{endAddr / 16:D4}"
            : $"%{Station:X2}#RDD{area[0]}{startAddr:D5}{endAddr:D5}";

        var response = await ExecuteMewtocolAsync(PanasonicHelper.AppendBccAndCr(cmdCore));
        if (!response.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(response);
        }

        var body = response.Content;
        var markerIndex = body.IndexOf("$R", StringComparison.Ordinal);
        if (markerIndex < 0 || body.Length < markerIndex + 3 + (wordCount * 4) + 2)
        {
            return OperateResult.CreateFailedResult<byte[]>($"松下Mewtocol字响应长度不足: {body}");
        }

        var dataHex = body.Substring(markerIndex + 3, wordCount * 4);
        var bytes = new byte[wordCount * 2];
        for (var i = 0; i < wordCount; i++)
        {
            var lo = Convert.ToByte(dataHex.Substring(i * 4, 2), 16);
            var hi = Convert.ToByte(dataHex.Substring((i * 4) + 2, 2), 16);
            bytes[i * 2] = hi;
            bytes[(i * 2) + 1] = lo;
        }

        return OperateResult.CreateSuccessResult(bytes);
    }

    private async Task<OperateResult<string>> ExecuteMewtocolAsync(string frame)
    {
        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            await SendAllAsync(Encoding.ASCII.GetBytes(frame), timeout.Token);
            var socket = Socket ?? throw new IOException("松下Mewtocol连接未建立。");
            var buffer = new List<byte>(64);
            var single = new byte[1];
            while (buffer.Count < 4096)
            {
                var read = await socket.ReceiveAsync(single, SocketFlags.None, timeout.Token);
                if (read <= 0)
                {
                    throw new IOException("松下Mewtocol连接被对端关闭。");
                }

                if (single[0] == '\r')
                {
                    break;
                }

                buffer.Add(single[0]);
            }

            var text = Encoding.ASCII.GetString(buffer.ToArray());
            var errIdx = text.IndexOf('!');
            if (errIdx >= 0)
            {
                return OperateResult.CreateFailedResult<string>($"松下Mewtocol返回错误帧: {text} (非法数据地址)");
            }

            return OperateResult.CreateSuccessResult(text);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<string>(exception.Message);
        }
    }
}

// ============================================================================
// 5. Omron FINS/TCP Native Client & Address Parser
// ============================================================================

internal sealed record OmronFinsAddress
{
    public byte BitCode { get; init; }
    public byte WordCode { get; init; }
    public ushort WordAddress { get; init; }
    public byte BitOffset { get; init; }
    public bool IsBit { get; init; }

    public static OperateResult<OmronFinsAddress> ParseFrom(string address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<OmronFinsAddress>("欧姆龙FINS地址不能为空。");
        }

        var text = address.Trim().ToUpperInvariant();
        string area;
        string rest;
        if (text.StartsWith("CIO", StringComparison.Ordinal))
        {
            area = "CIO";
            rest = text[3..];
        }
        else if (text.Length >= 2 && text[..2] is "DM" or "WR" or "HR" or "AR" or "EM")
        {
            area = text[..2];
            rest = text[2..];
        }
        else
        {
            area = text[..1];
            rest = text[1..];
        }

        (byte bitCode, byte wordCode) = area switch
        {
            "D" or "DM" => ((byte)0x02, (byte)0x82),
            "C" or "CIO" => ((byte)0x30, (byte)0xB0),
            "W" or "WR" => ((byte)0x31, (byte)0xB1),
            "H" or "HR" => ((byte)0x32, (byte)0xB2),
            "A" or "AR" => ((byte)0x33, (byte)0xB3),
            "E" or "EM" => ((byte)0x20, (byte)0xA0),
            _ => ((byte)0, (byte)0)
        };

        if (wordCode == 0 || string.IsNullOrWhiteSpace(rest))
        {
            return OperateResult.CreateFailedResult<OmronFinsAddress>($"非法欧姆龙FINS地址区域: {address}");
        }

        var dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            if (!ushort.TryParse(rest.AsSpan(0, dot), NumberStyles.None, CultureInfo.InvariantCulture, out var wordAddr) ||
                !byte.TryParse(rest.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bitAddr) ||
                bitAddr > 15)
            {
                return OperateResult.CreateFailedResult<OmronFinsAddress>($"非法欧姆龙FINS位地址: {address}");
            }

            return OperateResult.CreateSuccessResult(new OmronFinsAddress
            {
                BitCode = bitCode,
                WordCode = wordCode,
                WordAddress = wordAddr,
                BitOffset = bitAddr,
                IsBit = true
            });
        }

        if (!ushort.TryParse(rest, NumberStyles.None, CultureInfo.InvariantCulture, out var wordOnly))
        {
            return OperateResult.CreateFailedResult<OmronFinsAddress>($"非法欧姆龙FINS字地址: {address}");
        }

        return OperateResult.CreateSuccessResult(new OmronFinsAddress
        {
            BitCode = bitCode,
            WordCode = wordCode,
            WordAddress = wordOnly,
            BitOffset = 0,
            IsBit = false
        });
    }
}

internal class OmronFinsNet(string ipAddress = "127.0.0.1", int port = 9600) : DeviceTcpNet(ipAddress, port)
{
    private int _sid;

    public byte ICF { get; set; } = 0x80;
    public byte RSV { get; set; }
    public byte GCT { get; set; } = 0x02;
    public byte DNA { get; set; }
    public byte DA1 { get; set; } = 0x01;
    public byte DA2 { get; set; }
    public byte SNA { get; set; }
    public byte SA1 { get; set; } = 0x0B;
    public byte SA2 { get; set; }
    public ushort ReadSplits { get; set; } = 500;

    protected override async Task<OperateResult> InitializeOnConnectAsync(CancellationToken cancellationToken)
    {
        byte[] handshake =
        [
            0x46, 0x49, 0x4E, 0x53,
            0x00, 0x00, 0x00, 0x0C,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, SA1
        ];

        await SendAllAsync(handshake, cancellationToken);
        var resp = await ReceiveExactAsync(24, cancellationToken);
        if (resp[0] != 0x46 || resp[1] != 0x49 || resp[2] != 0x4E || resp[3] != 0x53)
        {
            return OperateResult.CreateFailedResult("欧姆龙 FINS/TCP 握手标识头不匹配。");
        }

        var errorCode = BinaryPrimitives.ReadUInt32BigEndian(resp.AsSpan(12, 4));
        if (errorCode != 0)
        {
            return OperateResult.CreateFailedResult($"欧姆龙 FINS/TCP 节点分配失败，错误码: 0x{errorCode:X8}");
        }

        if (SA1 == 0)
        {
            SA1 = resp[19];
        }

        if (DA1 == 0)
        {
            DA1 = resp[23];
        }

        return OperateResult.CreateSuccessResult();
    }

    public override async Task<OperateResult<bool>> ReadBoolAsync(string address)
    {
        var parsed = OmronFinsAddress.ParseFrom(address);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(parsed.Message);
        }

        var addr = parsed.Content;
        var read = await ExecuteFinsReadAsync(addr with { IsBit = true }, 1);
        if (!read.IsSuccess)
        {
            return OperateResult.CreateFailedResult<bool>(read);
        }

        return OperateResult.CreateSuccessResult(read.Content.Length > 0 && read.Content[0] != 0);
    }

    public override async Task<OperateResult<byte[]>> ReadAsync(string address, ushort length)
    {
        var parsed = OmronFinsAddress.ParseFrom(address);
        if (!parsed.IsSuccess)
        {
            return OperateResult.CreateFailedResult<byte[]>(parsed.Message);
        }

        var addr = parsed.Content;
        var totalWords = Math.Max((int)length, 1);
        var splitLimit = Math.Max((int)ReadSplits, 1);
        if (totalWords <= splitLimit || addr.IsBit)
        {
            return await ExecuteFinsReadAsync(addr, (ushort)totalWords);
        }

        var combined = new List<byte>(totalWords * 2);
        var currentWord = addr.WordAddress;
        var remaining = totalWords;
        while (remaining > 0)
        {
            var chunk = (ushort)Math.Min(remaining, splitLimit);
            var part = await ExecuteFinsReadAsync(addr with { WordAddress = currentWord }, chunk);
            if (!part.IsSuccess)
            {
                return part;
            }

            combined.AddRange(part.Content);
            currentWord = (ushort)(currentWord + chunk);
            remaining -= chunk;
        }

        return OperateResult.CreateSuccessResult(combined.ToArray());
    }

    private async Task<OperateResult<byte[]>> ExecuteFinsReadAsync(OmronFinsAddress addr, ushort count)
    {
        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            var sid = (byte)(Interlocked.Increment(ref _sid) & 0xFF);
            byte[] frame =
            [
                0x46, 0x49, 0x4E, 0x53,
                0x00, 0x00, 0x00, 0x1A,
                0x00, 0x00, 0x00, 0x02,
                0x00, 0x00, 0x00, 0x00,
                ICF, RSV, GCT, DNA, DA1, DA2, SNA, SA1, SA2, sid,
                0x01, 0x01,
                addr.IsBit ? addr.BitCode : addr.WordCode,
                (byte)(addr.WordAddress >> 8),
                (byte)(addr.WordAddress & 0xFF),
                addr.IsBit ? addr.BitOffset : (byte)0x00,
                (byte)(count >> 8),
                (byte)(count & 0xFF)
            ];

            await SendAllAsync(frame, timeout.Token);
            var tcpHeader = await ReceiveExactAsync(16, timeout.Token);
            if (tcpHeader[0] != 0x46 || tcpHeader[1] != 0x49 || tcpHeader[2] != 0x4E || tcpHeader[3] != 0x53)
            {
                return OperateResult.CreateFailedResult<byte[]>("欧姆龙 FINS/TCP 响应标识不匹配。");
            }

            var payloadLen = (int)BinaryPrimitives.ReadUInt32BigEndian(tcpHeader.AsSpan(4, 4)) - 8;
            if (payloadLen < 14 || payloadLen > 65536)
            {
                return OperateResult.CreateFailedResult<byte[]>($"欧姆龙 FINS/TCP 响应长度异常: {payloadLen}");
            }

            var body = await ReceiveExactAsync(payloadLen, timeout.Token);
            var mainErr = body[12];
            var subErr = body[13];
            if ((mainErr & 0x7F) != 0 || subErr != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"欧姆龙 FINS 错误码: 0x{mainErr:X2}{subErr:X2} (非法数据地址)");
            }

            return OperateResult.CreateSuccessResult(body.AsSpan(14).ToArray());
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }
}

// ============================================================================
// 6. Allen-Bradley EtherNet/IP & Omron CIP Native Clients
// ============================================================================

internal sealed class MessageRouter
{
    public MessageRouter(string routePath)
    {
        RoutePath = routePath;
        var parts = routePath.Split(['.', ',', '/', ' '], StringSplitOptions.RemoveEmptyEntries);
        Port = parts.Length > 0 && byte.TryParse(parts[0], out var p) ? p : (byte)1;
        Slot = parts.Length > 1 && byte.TryParse(parts[1], out var s) ? s : (byte)0;
    }

    public string RoutePath { get; }
    public byte Port { get; }
    public byte Slot { get; }
}

internal class AllenBradleyNet(string ipAddress = "127.0.0.1", int port = 44818) : DeviceTcpNet(ipAddress, port)
{
    private uint _sessionHandle;
    protected override ushort WordByteSize => 1;

    public byte Slot { get; set; }
    public bool ReadArrayUseSegment { get; set; } = true;
    public MessageRouter? MessageRouter { get; set; }

    protected override async Task<OperateResult> InitializeOnConnectAsync(CancellationToken cancellationToken)
    {
        var registerFrame = new byte[28];
        BinaryPrimitives.WriteUInt16LittleEndian(registerFrame.AsSpan(0, 2), 0x0065);
        BinaryPrimitives.WriteUInt16LittleEndian(registerFrame.AsSpan(2, 2), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(registerFrame.AsSpan(24, 2), 1);

        await SendAllAsync(registerFrame, cancellationToken);
        var resp = await ReceiveExactAsync(28, cancellationToken);
        var cmd = BinaryPrimitives.ReadUInt16LittleEndian(resp.AsSpan(0, 2));
        var status = BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(8, 4));
        if (cmd != 0x0065 || status != 0)
        {
            return OperateResult.CreateFailedResult($"EtherNet/IP RegisterSession 失败，状态码: 0x{status:X8}");
        }

        _sessionHandle = BinaryPrimitives.ReadUInt32LittleEndian(resp.AsSpan(4, 4));
        return OperateResult.CreateSuccessResult();
    }

    public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) =>
        ReadAsync([address], [length]);

    public async Task<OperateResult<byte[]>> ReadAsync(string[] addresses, ushort[] lengths)
    {
        if (addresses.Length == 0 || addresses.Length != lengths.Length)
        {
            return OperateResult.CreateFailedResult<byte[]>("CIP批量读取地址与长度不匹配。");
        }

        foreach (var address in addresses)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                return OperateResult.CreateFailedResult<byte[]>("CIP标签地址不能为空 (非法数据地址)。");
            }
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            var cipService = addresses.Length == 1
                ? BuildSingleReadService(addresses[0], 1)
                : BuildMultiReadService(addresses);
            var routeSlot = MessageRouter?.Slot ?? Slot;
            var routePort = MessageRouter?.Port ?? 1;
            var unconnectedSend = WrapUnconnectedSend(cipService, routePort, routeSlot);
            var eipPacket = WrapSendRRData(_sessionHandle, unconnectedSend);

            await SendAllAsync(eipPacket, timeout.Token);
            var header = await ReceiveExactAsync(24, timeout.Token);
            var status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            var dataLen = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2, 2));
            if (status != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"EtherNet/IP 封装层错误: 0x{status:X8}");
            }

            var body = dataLen > 0 ? await ReceiveExactAsync(dataLen, timeout.Token) : [];
            return ParseCipReadResponse(body, addresses, lengths);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }

    private static byte[] BuildSingleReadService(string tagAddress, ushort elements)
    {
        var epPath = BuildAnsiSymbolicPath(tagAddress);
        var service = new byte[2 + epPath.Length + 2];
        service[0] = 0x4C;
        service[1] = (byte)(epPath.Length / 2);
        Buffer.BlockCopy(epPath, 0, service, 2, epPath.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(service.AsSpan(2 + epPath.Length, 2), elements);
        return service;
    }

    private static byte[] BuildMultiReadService(IReadOnlyList<string> addresses)
    {
        var subServices = addresses.Select(addr => BuildSingleReadService(addr, 1)).ToArray();
        var count = subServices.Length;
        var headerLen = 2 + (count * 2);
        var totalSubLen = subServices.Sum(s => s.Length);
        var payload = new byte[4 + headerLen + totalSubLen];
        payload[0] = 0x0A;
        payload[1] = 0x02;
        payload[2] = 0x20;
        payload[3] = 0x02;
        payload[4] = 0x24;
        payload[5] = 0x01;
        // Wait: 0x0A request has 4-byte path (02 20 02 24 01 -> 6 bytes total header), let's allocate 6 + headerLen + totalSubLen
        payload = new byte[6 + headerLen + totalSubLen];
        payload[0] = 0x0A;
        payload[1] = 0x02;
        payload[2] = 0x20;
        payload[3] = 0x02;
        payload[4] = 0x24;
        payload[5] = 0x01;
        BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(6, 2), (ushort)count);

        var currentRelOffset = headerLen;
        var writePos = 6 + headerLen;
        for (var i = 0; i < count; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8 + (i * 2), 2), (ushort)currentRelOffset);
            var sub = subServices[i];
            Buffer.BlockCopy(sub, 0, payload, writePos, sub.Length);
            writePos += sub.Length;
            currentRelOffset += sub.Length;
        }

        return payload;
    }

    private static byte[] BuildAnsiSymbolicPath(string tagAddress)
    {
        var bytes = new List<byte>(32);
        foreach (var segment in tagAddress.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bracketIdx = segment.IndexOf('[');
            var symbolName = bracketIdx >= 0 ? segment[..bracketIdx] : segment;
            var nameBytes = Encoding.ASCII.GetBytes(symbolName);
            bytes.Add(0x91);
            bytes.Add((byte)nameBytes.Length);
            bytes.AddRange(nameBytes);
            if (nameBytes.Length % 2 == 1)
            {
                bytes.Add(0x00);
            }

            if (bracketIdx >= 0)
            {
                var closeIdx = segment.IndexOf(']', bracketIdx);
                if (closeIdx > bracketIdx + 1 &&
                    uint.TryParse(segment.AsSpan(bracketIdx + 1, closeIdx - bracketIdx - 1), out var elemIdx))
                {
                    if (elemIdx <= 255)
                    {
                        bytes.Add(0x28);
                        bytes.Add((byte)elemIdx);
                    }
                    else
                    {
                        bytes.Add(0x29);
                        bytes.Add(0x00);
                        bytes.Add((byte)(elemIdx & 0xFF));
                        bytes.Add((byte)((elemIdx >> 8) & 0xFF));
                    }
                }
            }
        }

        return bytes.ToArray();
    }

    private static byte[] WrapUnconnectedSend(byte[] cipService, byte routePort, byte routeSlot)
    {
        var pad = cipService.Length % 2;
        var packet = new byte[10 + cipService.Length + pad + 4];
        packet[0] = 0x52;
        packet[1] = 0x02;
        packet[2] = 0x20;
        packet[3] = 0x06;
        packet[4] = 0x24;
        packet[5] = 0x01;
        packet[6] = 0x0A;
        packet[7] = 0xF0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8, 2), (ushort)cipService.Length);
        Buffer.BlockCopy(cipService, 0, packet, 10, cipService.Length);
        var routePos = 10 + cipService.Length + pad;
        packet[routePos] = 0x01;
        packet[routePos + 1] = 0x00;
        packet[routePos + 2] = routePort;
        packet[routePos + 3] = routeSlot;
        return packet;
    }

    private static byte[] WrapSendRRData(uint sessionHandle, byte[] cipPayload)
    {
        var bodyLen = 16 + cipPayload.Length;
        var frame = new byte[24 + bodyLen];
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(0, 2), 0x006F);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(2, 2), (ushort)bodyLen);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4, 4), sessionHandle);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 6, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 12, 2), 0x00B2);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24 + 14, 2), (ushort)cipPayload.Length);
        Buffer.BlockCopy(cipPayload, 0, frame, 24 + 16, cipPayload.Length);
        return frame;
    }

    private static OperateResult<byte[]> ParseCipReadResponse(
        byte[] body,
        IReadOnlyList<string> addresses,
        IReadOnlyList<ushort> lengths)
    {
        if (body.Length < 20)
        {
            return OperateResult.CreateFailedResult<byte[]>("CIP响应数据长度不足。");
        }

        var cipSpan = body.AsSpan(16);
        var serviceReply = cipSpan[0];
        var generalStatus = cipSpan[2];
        if (generalStatus != 0)
        {
            return OperateResult.CreateFailedResult<byte[]>(
                $"CIP读取失败 (Status=0x{generalStatus:X2}, 非法数据地址)");
        }

        if (serviceReply == 0xCC && addresses.Count == 1)
        {
            var dataSpan = cipSpan[6..];
            var targetLen = (int)lengths[0];
            var output = new byte[targetLen];
            Buffer.BlockCopy(dataSpan.ToArray(), 0, output, 0, Math.Min(targetLen, dataSpan.Length));
            return OperateResult.CreateSuccessResult(output);
        }

        if (serviceReply == 0x8A)
        {
            var replyStart = 4;
            var count = BinaryPrimitives.ReadUInt16LittleEndian(cipSpan.Slice(replyStart, 2));
            var totalBytes = lengths.Sum(l => (int)l);
            var output = new byte[totalBytes];
            var outCursor = 0;
            for (var i = 0; i < Math.Min(count, lengths.Count); i++)
            {
                var relOffset = BinaryPrimitives.ReadUInt16LittleEndian(cipSpan.Slice(replyStart + 2 + (i * 2), 2));
                var itemSpan = cipSpan[(replyStart + relOffset)..];
                if (itemSpan.Length < 6 || itemSpan[2] != 0)
                {
                    return OperateResult.CreateFailedResult<byte[]>(
                        $"CIP批量子项 {addresses[i]} 读取失败 (Status=0x{(itemSpan.Length > 2 ? itemSpan[2] : 0xFF):X2}, 非法数据地址)");
                }

                var valueSpan = itemSpan[6..];
                var itemLen = (int)lengths[i];
                var copyLen = Math.Min(itemLen, valueSpan.Length);
                valueSpan[..copyLen].CopyTo(output.AsSpan(outCursor, copyLen));
                outCursor += itemLen;
            }

            return OperateResult.CreateSuccessResult(output);
        }

        return OperateResult.CreateFailedResult<byte[]>($"未知的CIP响应服务码: 0x{serviceReply:X2}");
    }
}

internal sealed class OmronConnectedCipNet(string ipAddress = "127.0.0.1", int port = 44818)
    : AllenBradleyNet(ipAddress, port)
{
    public byte ConnectionTimeoutMultiplier { get; set; } = 2;
}

// ============================================================================
// 7. Beckhoff ADS Native Client
// ============================================================================

internal sealed class BeckhoffAdsNet(string ipAddress = "127.0.0.1", int port = 851) : DeviceTcpNet(ipAddress, port)
{
    private readonly ConcurrentDictionary<string, uint> _symbolHandles = new(StringComparer.Ordinal);
    private int _invokeId;
    private byte[] _targetAmsNetId = [127, 0, 0, 1, 1, 1];
    private ushort _targetAmsPort = 851;
    private byte[] _senderAmsNetId = [127, 0, 0, 1, 1, 2];
    private ushort _senderAmsPort = 32905;
    protected override ushort WordByteSize => 1;

    public bool UseAutoAmsNetID { get; set; } = true;
    public bool UseTagCache { get; set; } = true;

    public void SetTargetAMSNetId(string amsNetId)
    {
        var (netId, amsPort) = ParseAmsNetId(amsNetId, _targetAmsPort);
        _targetAmsNetId = netId;
        _targetAmsPort = amsPort;
    }

    public void SetSenderAMSNetId(string amsNetId)
    {
        var (netId, amsPort) = ParseAmsNetId(amsNetId, _senderAmsPort);
        _senderAmsNetId = netId;
        _senderAmsPort = amsPort;
    }

    protected override Task<OperateResult> InitializeOnConnectAsync(CancellationToken cancellationToken)
    {
        _symbolHandles.Clear();
        if (UseAutoAmsNetID && Socket?.RemoteEndPoint is IPEndPoint remote && Socket.LocalEndPoint is IPEndPoint local)
        {
            var rBytes = remote.Address.MapToIPv4().GetAddressBytes();
            var lBytes = local.Address.MapToIPv4().GetAddressBytes();
            _targetAmsNetId = [rBytes[0], rBytes[1], rBytes[2], rBytes[3], 1, 1];
            _senderAmsNetId = [lBytes[0], lBytes[1], lBytes[2], lBytes[3], 1, 1];
            _targetAmsPort = Port is 48898 ? (ushort)851 : (ushort)Math.Clamp(Port, 1, 65535);
        }

        return Task.FromResult(OperateResult.CreateSuccessResult());
    }

    public override Task<OperateResult<byte[]>> ReadAsync(string address, ushort length) =>
        ReadAsync([address], [length]);

    public async Task<OperateResult<byte[]>> ReadAsync(string[] addresses, ushort[] lengths)
    {
        if (addresses.Length == 0 || addresses.Length != lengths.Length)
        {
            return OperateResult.CreateFailedResult<byte[]>("倍福ADS批量读取地址与长度不匹配。");
        }

        using var timeout = CreateReceiveTimeoutTokenSource();
        try
        {
            var targets = new (uint IndexGroup, uint IndexOffset, ushort Length)[addresses.Length];
            for (var i = 0; i < addresses.Length; i++)
            {
                var resolved = await ResolveAdsAddressAsync(addresses[i], lengths[i], timeout.Token);
                if (!resolved.IsSuccess)
                {
                    return OperateResult.CreateFailedResult<byte[]>(resolved.Message);
                }

                targets[i] = resolved.Content;
            }

            if (targets.Length == 1)
            {
                var single = targets[0];
                return await ExecuteAdsReadAsync(single.IndexGroup, single.IndexOffset, single.Length, timeout.Token);
            }

            return await ExecuteAdsSumReadAsync(targets, timeout.Token);
        }
        catch (Exception exception)
        {
            return OperateResult.CreateFailedResult<byte[]>(exception.Message);
        }
    }

    private async Task<OperateResult<(uint IndexGroup, uint IndexOffset, ushort Length)>> ResolveAdsAddressAsync(
        string address,
        ushort length,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return OperateResult.CreateFailedResult<(uint, uint, ushort)>("倍福ADS地址不能为空 (非法数据地址)。");
        }

        var text = address.Trim();
        if (text.StartsWith("s=", StringComparison.OrdinalIgnoreCase))
        {
            var symbolName = text[2..].Trim();
            if (string.IsNullOrEmpty(symbolName))
            {
                return OperateResult.CreateFailedResult<(uint, uint, ushort)>("倍福ADS符号变量名不能为空 (非法数据地址)。");
            }

            if (UseTagCache && _symbolHandles.TryGetValue(symbolName, out var cachedHandle))
            {
                return OperateResult.CreateSuccessResult(((uint)0xF005, cachedHandle, Math.Max(length, (ushort)1)));
            }

            var nameBytes = Encoding.ASCII.GetBytes(symbolName + "\0");
            var handleResp = await ExecuteAdsReadWriteAsync(0xF003, 0, 4, nameBytes, cancellationToken);
            if (!handleResp.IsSuccess || handleResp.Content.Length < 4)
            {
                return OperateResult.CreateFailedResult<(uint, uint, ushort)>(
                    $"倍福ADS符号句柄解析失败 ({symbolName}): {handleResp.Message} (非法数据地址)");
            }

            var handle = BinaryPrimitives.ReadUInt32LittleEndian(handleResp.Content.AsSpan(0, 4));
            if (UseTagCache)
            {
                _symbolHandles[symbolName] = handle;
            }

            return OperateResult.CreateSuccessResult(((uint)0xF005, handle, Math.Max(length, (ushort)1)));
        }

        if (text.StartsWith("IG=", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[3..].Split(';', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 &&
                TryParseHexOrDec(parts[0], out var ig) &&
                TryParseHexOrDec(parts[1], out var io))
            {
                return OperateResult.CreateSuccessResult((ig, io, Math.Max(length, (ushort)1)));
            }

            return OperateResult.CreateFailedResult<(uint, uint, ushort)>($"非法倍福ADS IndexGroup地址: {address}");
        }

        var upper = text.ToUpperInvariant();
        var area = upper[0];
        var rest = upper[1..];
        if (rest.Length > 1 && rest[0] is 'X' or 'B' or 'W' or 'D' && char.IsAsciiDigit(rest[1]))
        {
            rest = rest[1..];
        }

        var dot = rest.IndexOf('.');
        if (dot >= 0)
        {
            if (uint.TryParse(rest.AsSpan(0, dot), out var byteOff) &&
                uint.TryParse(rest.AsSpan(dot + 1), out var bitOff) &&
                bitOff <= 7)
            {
                var bitGroup = area switch
                {
                    'M' => 0x4021u,
                    'I' => 0xF021u,
                    'Q' => 0xF031u,
                    _ => 0u
                };
                if (bitGroup != 0)
                {
                    return OperateResult.CreateSuccessResult((bitGroup, (byteOff * 8) + bitOff, (ushort)1));
                }
            }
        }
        else if (uint.TryParse(rest, out var byteOffset))
        {
            var byteGroup = area switch
            {
                'M' => 0x4020u,
                'I' => 0xF020u,
                'Q' => 0xF030u,
                _ => 0u
            };
            if (byteGroup != 0)
            {
                return OperateResult.CreateSuccessResult((byteGroup, byteOffset, Math.Max(length, (ushort)1)));
            }
        }

        return OperateResult.CreateFailedResult<(uint, uint, ushort)>($"非法倍福ADS地址格式: {address}");
    }

    private async Task<OperateResult<byte[]>> ExecuteAdsReadAsync(
        uint indexGroup,
        uint indexOffset,
        ushort readLength,
        CancellationToken cancellationToken)
    {
        var reqData = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(0, 4), indexGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(4, 4), indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(8, 4), readLength);

        var resp = await SendAdsCommandAsync(0x0002, reqData, cancellationToken);
        if (!resp.IsSuccess)
        {
            return resp;
        }

        if (resp.Content.Length < 8)
        {
            return OperateResult.CreateFailedResult<byte[]>("倍福ADS Read响应长度不足。");
        }

        var adsResult = BinaryPrimitives.ReadUInt32LittleEndian(resp.Content.AsSpan(0, 4));
        var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.Content.AsSpan(4, 4));
        if (adsResult != 0)
        {
            return OperateResult.CreateFailedResult<byte[]>($"倍福ADS读取错误码 0x{adsResult:X4} (非法数据地址)");
        }

        return OperateResult.CreateSuccessResult(resp.Content.AsSpan(8, Math.Min(payloadLen, resp.Content.Length - 8)).ToArray());
    }

    private async Task<OperateResult<byte[]>> ExecuteAdsReadWriteAsync(
        uint indexGroup,
        uint indexOffset,
        uint readLength,
        byte[] writeData,
        CancellationToken cancellationToken)
    {
        var reqData = new byte[16 + writeData.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(0, 4), indexGroup);
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(4, 4), indexOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(8, 4), readLength);
        BinaryPrimitives.WriteUInt32LittleEndian(reqData.AsSpan(12, 4), (uint)writeData.Length);
        Buffer.BlockCopy(writeData, 0, reqData, 16, writeData.Length);

        var resp = await SendAdsCommandAsync(0x0009, reqData, cancellationToken);
        if (!resp.IsSuccess)
        {
            return resp;
        }

        if (resp.Content.Length < 8)
        {
            return OperateResult.CreateFailedResult<byte[]>("倍福ADS ReadWrite响应长度不足。");
        }

        var adsResult = BinaryPrimitives.ReadUInt32LittleEndian(resp.Content.AsSpan(0, 4));
        var payloadLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(resp.Content.AsSpan(4, 4));
        if (adsResult != 0)
        {
            return OperateResult.CreateFailedResult<byte[]>($"倍福ADS ReadWrite错误码 0x{adsResult:X4} (非法数据地址)");
        }

        return OperateResult.CreateSuccessResult(resp.Content.AsSpan(8, Math.Min(payloadLen, resp.Content.Length - 8)).ToArray());
    }

    private async Task<OperateResult<byte[]>> ExecuteAdsSumReadAsync(
        IReadOnlyList<(uint IndexGroup, uint IndexOffset, ushort Length)> items,
        CancellationToken cancellationToken)
    {
        var writeData = new byte[items.Count * 12];
        var totalDataBytes = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            BinaryPrimitives.WriteUInt32LittleEndian(writeData.AsSpan(i * 12, 4), item.IndexGroup);
            BinaryPrimitives.WriteUInt32LittleEndian(writeData.AsSpan((i * 12) + 4, 4), item.IndexOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(writeData.AsSpan((i * 12) + 8, 4), item.Length);
            totalDataBytes += item.Length;
        }

        var expectedReadBytes = (uint)((items.Count * 4) + totalDataBytes);
        var sumResp = await ExecuteAdsReadWriteAsync(0xF080, (uint)items.Count, expectedReadBytes, writeData, cancellationToken);
        if (!sumResp.IsSuccess)
        {
            return sumResp;
        }

        if (sumResp.Content.Length < expectedReadBytes)
        {
            return OperateResult.CreateFailedResult<byte[]>("倍福ADS SumRead响应载荷不足。");
        }

        for (var i = 0; i < items.Count; i++)
        {
            var itemErr = BinaryPrimitives.ReadUInt32LittleEndian(sumResp.Content.AsSpan(i * 4, 4));
            if (itemErr != 0)
            {
                return OperateResult.CreateFailedResult<byte[]>($"倍福ADS批量子项错误码 0x{itemErr:X4} (非法数据地址)");
            }
        }

        return OperateResult.CreateSuccessResult(sumResp.Content.AsSpan(items.Count * 4, totalDataBytes).ToArray());
    }

    private async Task<OperateResult<byte[]>> SendAdsCommandAsync(
        ushort commandId,
        byte[] commandData,
        CancellationToken cancellationToken)
    {
        var amsLen = 32 + commandData.Length;
        var frame = new byte[6 + amsLen];
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(2, 4), (uint)amsLen);
        Buffer.BlockCopy(_targetAmsNetId, 0, frame, 6, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(12, 2), _targetAmsPort);
        Buffer.BlockCopy(_senderAmsNetId, 0, frame, 14, 6);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(20, 2), _senderAmsPort);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(22, 2), commandId);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(24, 2), 0x0004);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(26, 4), (uint)commandData.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(30, 4), 0);
        var invokeId = (uint)Interlocked.Increment(ref _invokeId);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(34, 4), invokeId);
        Buffer.BlockCopy(commandData, 0, frame, 38, commandData.Length);

        await SendAllAsync(frame, cancellationToken);
        var tcpHeader = await ReceiveExactAsync(6, cancellationToken);
        var packetLen = (int)BinaryPrimitives.ReadUInt32LittleEndian(tcpHeader.AsSpan(2, 4));
        if (packetLen < 32 || packetLen > 1_048_576)
        {
            return OperateResult.CreateFailedResult<byte[]>($"倍福ADS响应包长度异常: {packetLen}");
        }

        var amsPacket = await ReceiveExactAsync(packetLen, cancellationToken);
        var headerError = BinaryPrimitives.ReadUInt32LittleEndian(amsPacket.AsSpan(24, 4));
        if (headerError != 0)
        {
            return OperateResult.CreateFailedResult<byte[]>($"倍福ADS头部错误码: 0x{headerError:X4}");
        }

        return OperateResult.CreateSuccessResult(amsPacket.AsSpan(32).ToArray());
    }

    private static (byte[] NetId, ushort Port) ParseAmsNetId(string text, ushort defaultPort)
    {
        var colon = text.IndexOf(':');
        var netIdPart = colon >= 0 ? text[..colon] : text;
        var port = colon >= 0 && ushort.TryParse(text.AsSpan(colon + 1), out var p) ? p : defaultPort;
        var segments = netIdPart.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 6)
        {
            return ([127, 0, 0, 1, 1, 1], port);
        }

        var bytes = new byte[6];
        for (var i = 0; i < 6; i++)
        {
            bytes[i] = byte.TryParse(segments[i], out var b) ? b : (byte)1;
        }

        return (bytes, port);
    }

    private static bool TryParseHexOrDec(string text, out uint value)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }
}
