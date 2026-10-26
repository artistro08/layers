using System.Buffers.Binary;
using System.IO.Hashing;
using Layers.Core.Logic;

namespace Layers.Tests.Fakes;

/// Builds device replies the way HID Remapper firmware does.
internal static class Replies
{
    public static byte[] Config(byte version, ushort mappingCount = 0, byte descriptorNumber = 0)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = version;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), mappingCount);
        packet[24] = descriptorNumber;
        return Sign(packet);
    }

    // Reply Payload: Target, Source, Scaling (0), Layer Mask, Flags, Hub Ports (0)
    public static byte[] Mapping(uint target, uint source, byte layerMask, byte flags)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(1), target);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), source);
        packet[13] = layerMask;
        packet[14] = flags;
        return Sign(packet);
    }

    // Reply Payload: Count, Then Up To Six Usages
    public static byte[] MacroChunk(uint[] usages)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = (byte)usages.Length;
        for (var i = 0; i < usages.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2 + i * 4), usages[i]);
        }

        return Sign(packet);
    }

    public static byte[] Expression(byte count, ReadOnlySpan<byte> bytes)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = count;
        bytes.CopyTo(packet.AsSpan(2));
        return Sign(packet);
    }

    public static byte[] Monitor(params (uint Usage, int Value)[] items)
    {
        var report = new byte[Protocol.MonitorReportLength];
        report[0]  = Protocol.ReportIdMonitor;
        for (var i = 0; i < items.Length; i++)
        {
            var offset = 1 + i * 9;
            BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(offset), items[i].Usage);
            BinaryPrimitives.WriteInt32LittleEndian(report.AsSpan(offset + 4), items[i].Value);
        }
        return report;
    }

    private static byte[] Sign(byte[] packet)
    {
        var crc = Crc32.HashToUInt32(packet.AsSpan(1, 28));
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(29), crc);
        return packet;
    }
}
