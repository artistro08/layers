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
/// One key mapping as the firmware reports it.
/// </summary>
/// <remarks>
/// Scaling and hub ports are read off the wire and dropped: the shortcut sheet shows what a key does, not by how much.
/// </remarks>
/// <param name="Source">The input usage (usage page in the high 16 bits).</param>
/// <param name="Target">The output usage. Layer targets live on page 0xFFF1, macro targets on page 0xFFF2.</param>
/// <param name="LayerMask">Bit N set means the mapping is active on layer N.</param>
/// <param name="Sticky">The mapping's sticky flag.</param>
/// <param name="Tap">The mapping's tap flag.</param>
/// <param name="Hold">The mapping's hold flag.</param>
public sealed record Mapping(uint Source, uint Target, byte LayerMask, bool Sticky, bool Tap, bool Hold)
{
    /// <summary>
    /// Gets the 0-based macro slot this mapping fires, or <see langword="null"/> when the target is not a macro.
    /// </summary>
    /// <remarks>
    /// Macro usage 0xFFF20001 is slot 0, which the web tool shows as "Macro 1".
    /// </remarks>
    public int? MacroIndex => (Target & 0xFFFF_0000) == Protocol.MacroUsagePage ? (int)(Target & 0xFFFF) - 1 : null;
}

/// <summary>
/// The fields of a GET_CONFIG reply the app reads.
/// </summary>
/// <remarks>
/// The reply carries more (timeouts, flags, usage counts); only these three drive behavior.
/// </remarks>
/// <param name="Version">The config version.</param>
/// <param name="MappingCount">How many mappings GET_MAPPING can index.</param>
/// <param name="DescriptorNumber">Which output descriptor the device presents, selecting the output name table.</param>
public sealed record ConfigSummary(byte Version, ushort MappingCount, byte DescriptorNumber);

/// <summary>
/// One GET_MACRO reply: up to six usages of a macro.
/// </summary>
/// <remarks>
/// A count below six ends the macro. A zero usage separates steps.
/// </remarks>
/// <param name="Count">How many of the six usage slots are filled, 0 through 6.</param>
/// <param name="Usages">The filled usages, in order.</param>
public sealed record MacroChunk(byte Count, uint[] Usages);

/// <summary>
/// The mappings and macros read from the device.
/// </summary>
/// <remarks>
/// <see cref="Macros"/> always has 32 entries. A macro is a list of steps, and a step is the usages pressed together.
/// Slots no mapping points at are left empty rather than read.
/// </remarks>
/// <param name="DescriptorNumber">The device's output descriptor number, for output names.</param>
/// <param name="Mappings">The mappings in device order.</param>
/// <param name="Macros">The 32 macro slots.</param>
public sealed record DeviceConfig(byte DescriptorNumber, IReadOnlyList<Mapping> Mappings, IReadOnlyList<IReadOnlyList<IReadOnlyList<uint>>> Macros);

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

    /// <summary>GET_MAPPING command. Payload is the mapping index as a u32.</summary>
    public const byte CmdGetMapping = 6;

    /// <summary>GET_MACRO command. Payload is the macro slot then the usage offset, both u32.</summary>
    public const byte CmdGetMacro = 17;

    /// <summary>Number of macro slots on the device.</summary>
    public const int MacroSlots = 32;

    /// <summary>Usages per GET_MACRO reply. A shorter reply ends the macro.</summary>
    public const int MacroItemsPerPacket = 6;

    /// <summary>The most mappings a config read accepts. Replies carry no tag, so a stray reply must not drive thousands of reads.</summary>
    public const int MaxMappings = 2048;

    /// <summary>The most usages a macro read accepts, for the same reason.</summary>
    public const int MaxMacroUsages = 2048;

    /// <summary>Usage page of macro targets: 0xFFF2000N fires macro slot N-1.</summary>
    public const uint MacroUsagePage = 0xFFF2_0000;

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

    /// <summary>Builds GET_MAPPING for one mapping.</summary>
    /// <remarks>Payload is the index as a u32.</remarks>
    /// <param name="index">The mapping index, below the count GET_CONFIG reports.</param>
    /// <returns>The packet.</returns>
    public static byte[] GetMapping(uint index)
    {
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, index);
        return BuildPacket(CmdGetMapping, payload);
    }

    /// <summary>Builds GET_MACRO for six usages of one macro.</summary>
    /// <remarks>Payload is the macro slot then the usage offset, both u32. Read at offsets 0, 6, 12... until a short reply.</remarks>
    /// <param name="macro">The macro slot, 0 through 31.</param>
    /// <param name="offset">The usage offset to start at.</param>
    /// <returns>The packet.</returns>
    public static byte[] GetMacro(uint macro, uint offset)
    {
        Span<byte> payload = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, macro);
        BinaryPrimitives.WriteUInt32LittleEndian(payload[4..], offset);
        return BuildPacket(CmdGetMacro, payload);
    }

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

    /// <summary>Reads the version, mapping count, and descriptor number from a GET_CONFIG reply.</summary>
    /// <remarks>
    /// Reply payload, little-endian: u8 version, u8 flags, u8 unmapped passthrough mask, u32 partial scroll timeout,
    /// u16 mapping count, u32 our usage count, u32 their usage count, u8 interval override, u32 tap-hold threshold,
    /// u8 GPIO debounce, u8 our descriptor number. The payload starts at byte 1.
    /// </remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="summary">The parsed fields.</param>
    /// <returns>
    /// <see langword="true"/> when the CRC is valid, the version is <see cref="ConfigVersion"/>, and the mapping count
    /// is at most <see cref="MaxMappings"/>; a stray reply fails, so the caller reads again.
    /// </returns>
    public static bool TryParseConfigSummary(ReadOnlySpan<byte> packet, out ConfigSummary summary)
    {
        summary = new ConfigSummary(0, 0, 0);
        if (!VerifyCrc(packet))
        {
            return false;
        }

        // A Reply To Someone Else's Command Parses Too: The Version And A Sane Count Keep A Stray One From Driving Reads
        summary = new ConfigSummary(packet[1], BinaryPrimitives.ReadUInt16LittleEndian(packet[8..]), packet[24]);
        return summary.Version == ConfigVersion && summary.MappingCount <= MaxMappings;
    }

    /// <summary>Reads one mapping from a GET_MAPPING reply.</summary>
    /// <remarks>
    /// Reply payload, little-endian: u32 target, u32 source, i32 scaling, u8 layer mask, u8 flags, u8 hub ports.
    /// Flags: bit 0 sticky, bit 1 tap, bit 2 hold.
    /// </remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="mapping">The mapping.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid.</returns>
    public static bool TryParseMapping(ReadOnlySpan<byte> packet, out Mapping mapping)
    {
        mapping = new Mapping(0, 0, 0, false, false, false);
        if (!VerifyCrc(packet))
        {
            return false;
        }

        var flags = packet[14];
        mapping   = new Mapping(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[5..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[1..]),
            packet[13],
            (flags & 0b001) != 0,
            (flags & 0b010) != 0,
            (flags & 0b100) != 0);
        return true;
    }

    /// <summary>Reads one chunk of a macro from a GET_MACRO reply.</summary>
    /// <remarks>Reply payload: u8 count, then six u32 usages of which the first count are valid.</remarks>
    /// <param name="packet">The reply.</param>
    /// <param name="chunk">The chunk.</param>
    /// <returns><see langword="true"/> when the reply's length and CRC are valid and the count is at most six.</returns>
    public static bool TryParseMacroChunk(ReadOnlySpan<byte> packet, out MacroChunk chunk)
    {
        chunk = new MacroChunk(0, []);
        if (!VerifyCrc(packet) || packet[1] > MacroItemsPerPacket)
        {
            return false;
        }

        var usages = new uint[packet[1]];
        for (var i = 0; i < usages.Length; i++)
        {
            usages[i] = BinaryPrimitives.ReadUInt32LittleEndian(packet[(2 + i * 4)..]);
        }

        chunk = new MacroChunk(packet[1], usages);
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
    // MACROS
    // =========================================================================

    /// <summary>Joins GET_MACRO chunks into steps.</summary>
    /// <remarks>
    /// A zero usage ends the current step. A trailing separator leaves no empty step behind, and an empty macro
    /// gives an empty list.
    /// </remarks>
    /// <param name="chunks">The chunks in offset order.</param>
    /// <returns>The steps, each the usages pressed together.</returns>
    public static IReadOnlyList<IReadOnlyList<uint>> AssembleMacro(IEnumerable<MacroChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        var steps = new List<IReadOnlyList<uint>>();
        var step  = new List<uint>();
        foreach (var usage in chunks.SelectMany(chunk => chunk.Usages))
        {
            if (usage != 0)
            {
                step.Add(usage);
                continue;
            }

            // Separator: Close The Step
            if (step.Count > 0)
            {
                steps.Add(step);
                step = [];
            }
        }

        if (step.Count > 0)
        {
            steps.Add(step);
        }

        return steps;
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
