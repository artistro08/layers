using System.Buffers.Binary;
using System.IO.Hashing;
using Layers.Core.Logic;

namespace Layers.Tests.Fakes;

/// Builds device replies the way HID Remapper firmware does.
internal static class Replies
{
    public static byte[] Config(byte version)
    {
        var packet = new byte[Protocol.PacketLength];
        packet[0]  = Protocol.ReportIdConfig;
        packet[1]  = version;
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
