using Snet.Edge.Application;
using Snet.Edge.Domain;

namespace Snet.Edge.Infrastructure;

public sealed class TagReadPlanCompiler : ITagReadPlanCompiler
{
    private const int MaxItemsPerBatch = 16;
    private const int MaxBytesPerBatch = 200;

    public DeviceReadPlan Compile(DeviceDefinition device, IReadOnlyList<TagDefinition> tags)
    {
        var batches = new List<TagReadBatch>();
        var pending = new List<TagDefinition>(MaxItemsPerBatch);
        var pendingBytes = 0;
        var supportsNativeMultiRead = device.Protocol is
            ProtocolKind.SiemensS7 or
            ProtocolKind.OmronCip or
            ProtocolKind.AllenBradleyEtherNetIp or
            ProtocolKind.BeckhoffAds;

        foreach (var tag in tags)
        {
            var byteLength = GetByteLength(tag);
            var batchable = supportsNativeMultiRead && tag.DataType != TagDataType.Bool && byteLength <= MaxBytesPerBatch;
            if (!batchable)
            {
                FlushPending(batches, pending, ref pendingBytes);
                batches.Add(new TagReadBatch([tag], false));
                continue;
            }

            if (pending.Count >= MaxItemsPerBatch || pendingBytes + byteLength > MaxBytesPerBatch)
            {
                FlushPending(batches, pending, ref pendingBytes);
            }

            pending.Add(tag);
            pendingBytes += byteLength;
        }

        FlushPending(batches, pending, ref pendingBytes);
        return new DeviceReadPlan(batches, tags.Count);
    }

    public static ushort GetByteLength(TagDefinition tag) => tag.DataType switch
    {
        TagDataType.Bool or TagDataType.Byte => 1,
        TagDataType.Int16 or TagDataType.UInt16 or TagDataType.Bcd => 2,
        TagDataType.Int32 or TagDataType.UInt32 or TagDataType.Float32 => 4,
        TagDataType.Int64 or TagDataType.UInt64 or TagDataType.Float64 => 8,
        TagDataType.String => checked((ushort)Math.Clamp(tag.StringLength, 1, ushort.MaxValue)),
        _ => throw new NotSupportedException($"不支持点位数据类型 {tag.DataType}。")
    };

    private static void FlushPending(
        ICollection<TagReadBatch> batches,
        List<TagDefinition> pending,
        ref int pendingBytes)
    {
        if (pending.Count == 0)
        {
            return;
        }

        batches.Add(new TagReadBatch(pending.ToArray(), pending.Count > 1));
        pending.Clear();
        pendingBytes = 0;
    }
}
