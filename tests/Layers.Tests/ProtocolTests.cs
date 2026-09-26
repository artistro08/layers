using System.Buffers.Binary;
using System.IO.Hashing;
using Layers.Core.Logic;
using Layers.Tests.Fakes;

namespace Layers.Tests;

[TestClass]
public sealed class ProtocolTests
{
    private static readonly byte[] Ours = Protocol.ExpressionBytes.ToArray();

    // =========================================================================
    // FRAMING
    // =========================================================================

    [TestMethod]
    public void Packet_HasHeaderAndLength()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdResume, []);

        Assert.AreEqual(Protocol.PacketLength, packet.Length);
        Assert.AreEqual(Protocol.ReportIdConfig, packet[0]);
        Assert.AreEqual(Protocol.ConfigVersion, packet[1]);
        Assert.AreEqual(Protocol.CmdResume, packet[2]);
        Assert.IsTrue(packet[3..29].All(b => b == 0));
    }

    [TestMethod]
    public void Packet_PayloadFollowsCommandByte()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdSetMonitorEnabled, [1]);

        Assert.AreEqual((byte)1, packet[3]);
        Assert.IsTrue(packet[4..29].All(b => b == 0));
    }

    [TestMethod]
    public void Crc_CoversBytesOneThroughTwentyEight()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdResume, []);

        Assert.AreEqual(Crc32.HashToUInt32(packet.AsSpan(1, 28)), BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(29)));
    }

    [TestMethod]
    public void VerifyCrc_AcceptsOwnPackets()
    {
        Assert.IsTrue(Protocol.VerifyCrc(Protocol.BuildPacket(Protocol.CmdGetExpression, [3, 0, 0, 0])));
    }

    [TestMethod]
    public void VerifyCrc_RejectsCorruptedPayload()
    {
        var packet = Protocol.BuildPacket(Protocol.CmdGetExpression, [3, 0, 0, 0]);
        packet[5] ^= 0xFF;

        Assert.IsFalse(Protocol.VerifyCrc(packet));
    }

    [TestMethod]
    public void VerifyCrc_RejectsShortBuffer()
    {
        Assert.IsFalse(Protocol.VerifyCrc(new byte[10]));
    }

    [TestMethod]
    public void OversizedPayload_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Protocol.BuildPacket(Protocol.CmdResume, new byte[27]));
    }

    // =========================================================================
    // COMMANDS
    // =========================================================================

    [TestMethod]
    public void ExpressionBytes_AreLayerStatePushSentinelMonitor()
    {
        CollectionAssert.AreEqual(new byte[] { 20, 1, 0x01, 0x00, 0x00, 0xFF, 44 }, Ours);
    }

    [TestMethod]
    public void Sentinel_InExpressionMatchesFilter()
    {
        Assert.AreEqual(Protocol.SentinelUsage, BinaryPrimitives.ReadUInt32LittleEndian(Ours.AsSpan(2)));
    }

    [TestMethod]
    public void Append_CarriesSlotCountAndBytes()
    {
        var packet = Protocol.AppendExpression(5);

        Assert.AreEqual(Protocol.CmdAppendToExpression, packet[2]);
        Assert.AreEqual((byte)5, packet[3]);
        Assert.AreEqual((byte)3, packet[4]);
        CollectionAssert.AreEqual(Ours, packet[5..12]);
        Assert.IsTrue(packet[12..29].All(b => b == 0));
        Assert.IsTrue(Protocol.VerifyCrc(packet));
    }

    [TestMethod]
    public void GetExpression_CarriesSlotAndZeroOffset()
    {
        var packet = Protocol.GetExpression(6);

        Assert.AreEqual(Protocol.CmdGetExpression, packet[2]);
        Assert.AreEqual(6u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3)));
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7)));
    }

    [TestMethod]
    public void MonitorEnable_DiffersOnlyInPayload()
    {
        Assert.AreEqual((byte)1, Protocol.SetMonitorEnabled(true)[3]);
        Assert.AreEqual((byte)0, Protocol.SetMonitorEnabled(false)[3]);
        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Protocol.SetMonitorEnabled(true)[2]);
    }

    [TestMethod]
    public void Resume_HasEmptyPayload()
    {
        var packet = Protocol.Resume();

        Assert.AreEqual(Protocol.CmdResume, packet[2]);
        Assert.IsTrue(packet[3..29].All(b => b == 0));
    }

    [TestMethod]
    public void GetConfig_HasCommandThree()
    {
        Assert.AreEqual((byte)3, Protocol.GetConfig()[2]);
    }

    // =========================================================================
    // EXPRESSION RESPONSES
    // =========================================================================

    [TestMethod]
    public void ParsesEmptySlot()
    {
        Assert.IsTrue(Protocol.TryParseExpressionResponse(Replies.Expression(0, []), out var slot));
        Assert.AreEqual((byte)0, slot.ElementCount);
    }

    [TestMethod]
    public void ParsesOurExpressionBack()
    {
        Assert.IsTrue(Protocol.TryParseExpressionResponse(Replies.Expression(3, Ours), out var slot));
        Assert.AreEqual((byte)3, slot.ElementCount);
        CollectionAssert.AreEqual(Ours, slot.Bytes[..7]);
        Assert.IsTrue(slot.IsOurs);
    }

    [TestMethod]
    public void ExpressionResponse_BadCrcRejected()
    {
        var reply = Replies.Expression(3, Ours);
        reply[4] ^= 0xFF;

        Assert.IsFalse(Protocol.TryParseExpressionResponse(reply, out _));
    }

    [TestMethod]
    public void ExpressionResponse_ShortRejected()
    {
        Assert.IsFalse(Protocol.TryParseExpressionResponse(new byte[20], out _));
    }

    // =========================================================================
    // SLOT CHOICE
    // =========================================================================

    private static SlotContents Slot(byte count, params byte[] bytes)
    {
        var padded = new byte[27];
        bytes.CopyTo(padded, 0);
        return new SlotContents(count, padded);
    }

    [TestMethod]
    public void ReusesOurSlot_EvenWithEarlierEmpty()
    {
        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Existing, 1), Protocol.ChooseSlot([Slot(0), Slot(3, Ours)]));
    }

    [TestMethod]
    public void TakesFirstEmpty_WhenOursAbsent()
    {
        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Empty, 1), Protocol.ChooseSlot([Slot(5, 20, 20, 20), Slot(0), Slot(0)]));
    }

    [TestMethod]
    public void NoneFree_WhenAllOccupied()
    {
        var slots = Enumerable.Range(0, 8).Select(_ => Slot(2, 20, 44)).ToList();

        Assert.AreEqual(SlotChoiceKind.NoneFree, Protocol.ChooseSlot(slots).Kind);
    }

    [TestMethod]
    public void LongerExpressionStartingLikeOurs_IsNotOurs()
    {
        var bytes = Ours.Append((byte)20).ToArray();

        Assert.AreEqual(new SlotChoice(SlotChoiceKind.Empty, 1), Protocol.ChooseSlot([Slot(4, bytes), Slot(0)]));
    }

    // =========================================================================
    // CONFIG VERSION
    // =========================================================================

    [TestMethod]
    public void ReadsConfigVersion()
    {
        Assert.IsTrue(Protocol.TryParseConfigVersion(Replies.Config(18), out var version));
        Assert.AreEqual((byte)18, version);
    }

    [TestMethod]
    public void ReportsMismatchedVersion()
    {
        Assert.IsTrue(Protocol.TryParseConfigVersion(Replies.Config(19), out var version));
        Assert.AreEqual((byte)19, version);
    }

    [TestMethod]
    public void ConfigResponse_BadCrcRejected()
    {
        var reply = Replies.Config(18);
        reply[1] = 19;

        Assert.IsFalse(Protocol.TryParseConfigVersion(reply, out _));
    }

    // =========================================================================
    // MONITOR REPORTS
    // =========================================================================

    [TestMethod]
    public void ExtractsMaskFromSentinel()
    {
        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, 0b100)), out var mask));
        Assert.AreEqual((byte)0b100, mask.Bits);
    }

    [TestMethod]
    public void FindsSentinelInLastItem()
    {
        var items = Enumerable.Range(0, 6).Select(i => (0x0009_0001u + (uint)i, 1)).Append((Protocol.SentinelUsage, 2)).ToArray();

        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor(items), out var mask));
        Assert.AreEqual((byte)2, mask.Bits);
    }

    [TestMethod]
    public void IgnoresOrdinaryUsages()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor((0x0009_0001u, 1), (0x0001_0030u, -5)), out _));
    }

    [TestMethod]
    public void IgnoresPaddingItems()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor(), out _));
    }

    [TestMethod]
    public void IgnoresWrongReportId()
    {
        var report = Replies.Monitor((Protocol.SentinelUsage, 3));
        report[0] = Protocol.ReportIdConfig;

        Assert.IsFalse(Protocol.TryParseMonitorReport(report, out _));
    }

    [TestMethod]
    public void TryParseMonitorReport_TruncatedIsFalse()
    {
        Assert.IsFalse(Protocol.TryParseMonitorReport([Protocol.ReportIdMonitor, 0, 0], out _));
        Assert.IsFalse(Protocol.TryParseMonitorReport([], out _));
        Assert.IsFalse(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, 3))[..63], out _));
    }

    [TestMethod]
    public void KeepsLowEightBitsOfNegativeValue()
    {
        Assert.IsTrue(Protocol.TryParseMonitorReport(Replies.Monitor((Protocol.SentinelUsage, -1)), out var mask));
        Assert.AreEqual((byte)0xFF, mask.Bits);
    }

    [TestMethod]
    public void LongerReport_IsAccepted()
    {
        var report = Replies.Monitor((Protocol.SentinelUsage, 4)).Concat(new byte[8]).ToArray();

        Assert.IsTrue(Protocol.TryParseMonitorReport(report, out var mask));
        Assert.AreEqual((byte)4, mask.Bits);
    }
}
