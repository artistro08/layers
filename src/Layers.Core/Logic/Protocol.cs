using System.Buffers.Binary;
using System.IO.Hashing;

namespace Layers.Core.Logic;

/// <summary>
/// Parses a packet into a value without throwing.
/// </summary>
/// <remarks>
/// Every device reply is untrusted, so parsers report failure rather than throw.
/// </remarks>
/// <typeparam name="T">The parsed value type.</typeparam>
/// <param name="packet">The raw bytes.</param>
/// <param name="value">The parsed value when this returns <see langword="true"/>.</param>
/// <returns><see langword="true"/> when the packet is well formed.</returns>
public delegate bool TryParse<T>(ReadOnlySpan<byte> packet, out T value);

/// <summary>
/// One expression slot as the firmware reports it.
/// </summary>
/// <remarks>
/// Holds an element count and the first 27 encoded element bytes.
/// </remarks>
/// <param name="ElementCount">Number of expression elements in the slot. Zero means the slot is empty.</param>
/// <param name="Bytes">The 27 element bytes from the reply.</param>
public sealed record SlotContents(byte ElementCount, byte[] Bytes)
{
    /// <summary>
    /// Gets a value indicating whether this slot holds our injected expression.
    /// </summary>
    /// <remarks>
    /// Checked periodically, because loading a config from the web tool sends <c>CLEAR_EXPRESSIONS</c>,
    /// which wipes ours along with everything else.
    /// </remarks>
    public bool IsOurs =>
        ElementCount == Protocol.ExpressionElementCount
        && Bytes.Length >= Protocol.ExpressionBytes.Length
        && Bytes.AsSpan(0, Protocol.ExpressionBytes.Length).SequenceEqual(Protocol.ExpressionBytes);
}

/// <summary>
/// Which slot to use for our expression.
/// </summary>
/// <remarks>
/// Existing means ours is already installed, Empty means a free slot to append into,
/// and NoneFree means all 8 slots belong to the user.
/// </remarks>
public enum SlotChoiceKind
{
    /// <summary>Our expression is already in <see cref="SlotChoice.Slot"/>.</summary>
    Existing,

    /// <summary><see cref="SlotChoice.Slot"/> is empty and free to append into.</summary>
    Empty,

    /// <summary>Every slot is in use by the user.</summary>
    NoneFree,
}

/// <summary>
/// The result of <see cref="Protocol.ChooseSlot"/>.
/// </summary>
/// <remarks>
/// <see cref="Slot"/> is meaningless when <see cref="Kind"/> is NoneFree.
/// </remarks>
/// <param name="Kind">What kind of slot was found.</param>
/// <param name="Slot">The slot index, 0 through 7.</param>
public readonly record struct SlotChoice(SlotChoiceKind Kind, byte Slot);

/// <summary>
/// HID Remapper config wire format for config version 18.
/// </summary>
/// <remarks>
/// Pure data, no I/O. Ported from <c>protocol.rs</c> in Layers 1.0.3. Every opcode, report ID, and command byte
/// is tied to config version 18. The app never sends PERSIST_CONFIG, CLEAR_EXPRESSIONS, or SUSPEND,
/// so it only ever touches device RAM.
/// </remarks>
public static class Protocol
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>Feature report ID for config packets.</summary>
    public const byte ReportIdConfig = 100;

    /// <summary>Input report ID for monitor reports.</summary>
    public const byte ReportIdMonitor = 101;

    /// <summary>The only firmware config version this app writes to.</summary>
    public const byte ConfigVersion = 18;

    /// <summary>Config packets are always 33 bytes: report ID, version, command, 26 payload bytes, then a CRC32.</summary>
    public const int PacketLength = 33;

    /// <summary>Maximum payload size.</summary>
    public const int PayloadLength = 26;

    /// <summary>Vendor-defined usage page of both HID Remapper collections.</summary>
    public const ushort ConfigUsagePage = 0xFF00;

    /// <summary>Usage of the config collection (feature reports).</summary>
    public const ushort ConfigUsage = 0x0020;

    /// <summary>Usage of the monitor collection (input reports). Windows exposes it as a separate device.</summary>
    public const ushort MonitorUsage = 0x0021;

    /// <summary>Number of expression slots on the device.</summary>
    public const int ExpressionSlots = 8;

    /// <summary>Vendor-defined usage our expression reports the layer mask under. Can't collide with a real input usage.</summary>
    public const uint SentinelUsage = 0xFF00_0001;

    /// <summary>Report ID plus 7 packed 9-byte items.</summary>
    public const int MonitorReportLength = 1 + MonitorItems * MonitorItemLength;

    /// <summary>GET_CONFIG command.</summary>
    public const byte CmdGetConfig = 3;

    /// <summary>RESUME command. Required after appending, or the expression never evaluates.</summary>
    public const byte CmdResume = 11;

    /// <summary>APPEND_TO_EXPRESSION command.</summary>
    public const byte CmdAppendToExpression = 20;

    /// <summary>GET_EXPRESSION command.</summary>
    public const byte CmdGetExpression = 21;

    /// <summary>SET_MONITOR_ENABLED command.</summary>
    public const byte CmdSetMonitorEnabled = 22;

    internal const byte ExpressionElementCount = 3;

    private const int PayloadStart      = 3;
    private const int CrcStart          = 29;
    private const int MonitorItems      = 7;
    private const int MonitorItemLength = 9;

    /// <summary>
    /// Gets the encoded expression <c>layer_state 0xFF000001 monitor</c>.
    /// </summary>
    /// <remarks>
    /// Three elements, seven bytes. <c>layer_state</c> and <c>push_usage</c> each push one value,
    /// and <c>monitor</c> consumes two, so the firmware's validator accepts the balanced stack.
    /// </remarks>
    public static ReadOnlySpan<byte> ExpressionBytes => [20, 1, 0x01, 0x00, 0x00, 0xFF, 44];

    // =========================================================================
    // FRAMING
    // =========================================================================

    /// <summary>
    /// Builds a signed 33-byte config packet.
    /// </summary>
    /// <remarks>
    /// The CRC32 covers bytes 1 through 28 (everything between the report ID and the CRC field)
    /// and is written little-endian at byte 29.
    /// </remarks>
    /// <param name="command">The command byte.</param>
    /// <param name="payload">Up to 26 payload bytes.</param>
    /// <returns>The packet.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The payload is longer than 26 bytes. Call sites use fixed sizes, so this is a bug, not bad input.</exception>
    public static byte[] BuildPacket(byte command, ReadOnlySpan<byte> payload)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(payload.Length, PayloadLength);

        // Header And Payload
        var packet = new byte[PacketLength];
        packet[0]  = ReportIdConfig;
        packet[1]  = ConfigVersion;
        packet[2]  = command;
        payload.CopyTo(packet.AsSpan(PayloadStart));

        // Sign
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(CrcStart), CrcOf(packet));

        return packet;
    }

    /// <summary>
    /// Checks a packet's length and CRC.
    /// </summary>
    /// <remarks>
    /// The firmware's replies don't echo the command byte, so the CRC is the integrity check.
    /// </remarks>
    /// <param name="packet">The packet.</param>
    /// <returns><see langword="true"/> when the packet is at least 33 bytes and its CRC matches.</returns>
    public static bool VerifyCrc(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < PacketLength)
        {
            return false;
        }

        return BinaryPrimitives.ReadUInt32LittleEndian(packet[CrcStart..PacketLength]) == CrcOf(packet);
    }

    private static uint CrcOf(ReadOnlySpan<byte> packet) => Crc32.HashToUInt32(packet[1..CrcStart]);

    // =========================================================================
    // COMMANDS
    // =========================================================================

    /// <summary>Builds GET_CONFIG.</summary>
    /// <remarks>The reply carries the config version in byte 1.</remarks>
    /// <returns>The packet.</returns>
    public static byte[] GetConfig() => BuildPacket(CmdGetConfig, []);

    /// <summary>Builds GET_EXPRESSION for one slot.</summary>
    /// <remarks>Payload is the slot as a u32, then the element offset (always 0) as a u32.</remarks>
    /// <param name="slot">The slot, 0 through 7.</param>
    /// <returns>The packet.</returns>
    public static byte[] GetExpression(byte slot)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, slot);
        return BuildPacket(CmdGetExpression, payload);
    }

    /// <summary>Builds APPEND_TO_EXPRESSION with our expression.</summary>
    /// <remarks>Payload is the slot, the element count (3), then the 7 expression bytes.</remarks>
    /// <param name="slot">The empty slot to append into.</param>
    /// <returns>The packet.</returns>
    public static byte[] AppendExpression(byte slot)
    {
        Span<byte> payload = stackalloc byte[2 + 7];
        payload[0]         = slot;
        payload[1]         = ExpressionElementCount;
        ExpressionBytes.CopyTo(payload[2..]);
        return BuildPacket(CmdAppendToExpression, payload);
    }

    /// <summary>Builds RESUME.</summary>
    /// <remarks>Required after appending: only RESUME marks the expression valid. It also resets the firmware's layer state to layer 0.</remarks>
    /// <returns>The packet.</returns>
    public static byte[] Resume() => BuildPacket(CmdResume, []);

    /// <summary>Builds SET_MONITOR_ENABLED.</summary>
    /// <remarks>Monitor mode streams input usages, including our sentinel, as input reports on the monitor collection.</remarks>
    /// <param name="enabled">Whether to enable Monitor mode.</param>
    /// <returns>The packet.</returns>
    public static byte[] SetMonitorEnabled(bool enabled) => BuildPacket(CmdSetMonitorEnabled, [enabled ? (byte)1 : (byte)0]);

    // =========================================================================
    // PARSING
    // =========================================================================

    /// <summary>Reads the config version from a GET_CONFIG reply.</summary>
    /// <remarks>A mismatch is surfaced, never guessed at: a wrong guess would write an expression the firmware reads as something else.</remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="version">The version.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid.</returns>
    public static bool TryParseConfigVersion(ReadOnlySpan<byte> packet, out byte version)
    {
        version = 0;
        if (!VerifyCrc(packet))
        {
            return false;
        }

        version = packet[1];
        return true;
    }

    /// <summary>Reads one slot from a GET_EXPRESSION reply.</summary>
    /// <remarks>Layout: report ID, element count, 27 element bytes, CRC.</remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="slot">The slot contents.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid.</returns>
    public static bool TryParseExpressionResponse(ReadOnlySpan<byte> packet, out SlotContents slot)
    {
        slot = new SlotContents(0, new byte[27]);
        if (!VerifyCrc(packet))
        {
            return false;
        }

        slot = new SlotContents(packet[1], packet[2..CrcStart].ToArray());
        return true;
    }

    /// <summary>Extracts the layer mask from a monitor report, if it carries our sentinel.</summary>
    /// <remarks>
    /// The firmware emits an item only when a value changes, so every sentinel item is a real layer switch.
    /// <c>layer_state</c> bypasses the ×1000 fixed-point convention, so the value is the raw mask in its low 8 bits.
    /// </remarks>
    /// <param name="report">The input report, including the report ID byte.</param>
    /// <param name="mask">The layer mask.</param>
    /// <returns><see langword="true"/> when the report is well formed and carries the sentinel.</returns>
    public static bool TryParseMonitorReport(ReadOnlySpan<byte> report, out LayerMask mask)
    {
        mask = default;
        if (report.Length < MonitorReportLength || report[0] != ReportIdMonitor)
        {
            return false;
        }

        // Scan The Seven Items
        for (var item = 0; item < MonitorItems; item++)
        {
            var offset = 1 + item * MonitorItemLength;
            var usage  = BinaryPrimitives.ReadUInt32LittleEndian(report[offset..]);
            if (usage != SentinelUsage)
            {
                continue;
            }

            var value = BinaryPrimitives.ReadInt32LittleEndian(report[(offset + 4)..]);
            mask      = new LayerMask(unchecked((byte)value));
            return true;
        }

        return false;
    }

    // =========================================================================
    // SLOT CHOICE
    // =========================================================================

    /// <summary>Decides which slot to use.</summary>
    /// <remarks>
    /// Reusing our own slot matters: without it, every app restart would use up another slot,
    /// and 8 restarts would fill the device.
    /// </remarks>
    /// <param name="slots">The slots in index order.</param>
    /// <returns>The choice.</returns>
    public static SlotChoice ChooseSlot(IReadOnlyList<SlotContents> slots)
    {
        ArgumentNullException.ThrowIfNull(slots);

        // Ours Already Installed
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].IsOurs)
            {
                return new SlotChoice(SlotChoiceKind.Existing, (byte)i);
            }
        }

        // First Empty Slot
        for (var i = 0; i < slots.Count; i++)
        {
            if (slots[i].ElementCount == 0)
            {
                return new SlotChoice(SlotChoiceKind.Empty, (byte)i);
            }
        }

        return new SlotChoice(SlotChoiceKind.NoneFree, 0);
    }
}
