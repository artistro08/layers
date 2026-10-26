using System.Collections.Frozen;
using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// Lookups over the generated usage name tables.
/// </summary>
/// <remarks>
/// The tables live in <c>UsageNames.Tables.cs</c>. The lookup order mirrors the web tool's
/// <c>readable_usage_name</c> and <c>readable_target_usage_name</c> with the tool's default mouse labels.
/// </remarks>
public static partial class UsageNames
{
    private const uint PageMask       = 0xFFFF_0000;
    private const uint ButtonPage     = 0x0009_0000;
    private const uint MidiPage       = 0xFFF7_0000;

    private static string[]? s_allNames;

    // Consumer Page Codes The Web Tool Leaves As Hex, Named From The USB HID Usage Tables (Consumer Page 0x0C) In Plain
    // Words: "AC Back" Is The Browser Back Key, "AL Calculator" Opens The Calculator, And So On
    private static readonly FrozenDictionary<uint, string> MediaNames = new KeyValuePair<uint, string>[]
    {
        new(0x000C0030, "Power"),
        new(0x000C0032, "Sleep"),
        new(0x000C0040, "Menu"),
        new(0x000C006F, "Brightness up"),
        new(0x000C0070, "Brightness down"),
        new(0x000C00B0, "Play"),
        new(0x000C00B1, "Pause"),
        new(0x000C00B3, "Fast forward"),
        new(0x000C00B4, "Rewind"),
        new(0x000C00B8, "Eject"),
        new(0x000C00CF, "Voice command"),
        new(0x000C018A, "Mail"),
        new(0x000C0192, "Calculator"),
        new(0x000C0194, "File Explorer"),
        new(0x000C0196, "Web browser"),
        new(0x000C019E, "Lock screen"),
        new(0x000C0201, "New"),
        new(0x000C0202, "Open"),
        new(0x000C0203, "Close"),
        new(0x000C0207, "Save"),
        new(0x000C0208, "Print"),
        new(0x000C021A, "Undo"),
        new(0x000C021B, "Copy"),
        new(0x000C021C, "Cut"),
        new(0x000C021D, "Paste"),
        new(0x000C021F, "Find"),
        new(0x000C0221, "Search"),
        new(0x000C0223, "Browser home"),
        new(0x000C0224, "Browser back"),
        new(0x000C0225, "Browser forward"),
        new(0x000C0226, "Stop loading"),
        new(0x000C0227, "Refresh"),
        new(0x000C022A, "Bookmarks"),
        new(0x000C022D, "Zoom in"),
        new(0x000C022E, "Zoom out"),
        new(0x000C022F, "Reset zoom"),
        new(0x000C0279, "Redo"),
        new(0x000C029F, "Task view"),
    }.ToFrozenDictionary();

    /// <summary>
    /// Gets every named usage once, for the key picker.
    /// </summary>
    /// <remarks>
    /// The mouse labels, the input tables, then the default output table (layers, macros, and the rest), without
    /// "Nothing". Built on first use: the tables live in the generated half of the class, whose initializers may run
    /// after this one.
    /// </remarks>
    public static IReadOnlyList<string> AllNames => s_allNames ??=
        Source0.Values.Concat(Source.Values).Concat(SourceExtra.Values).Concat(MediaNames.Values).Concat(Descriptor0.Values)
            .Where(name => name != "Nothing")
            .Distinct()
            .ToArray();

    /// <summary>
    /// Names an input usage.
    /// </summary>
    /// <remarks>
    /// Mouse labels first, then the main and extra input tables, then plain names for media codes the web tool leaves as
    /// hex, then the Button and MIDI page rules, then hex.
    /// </remarks>
    /// <param name="usage">The input usage.</param>
    /// <returns>The name the web tool shows.</returns>
    public static string Input(uint usage)
    {
        if (Source0.TryGetValue(usage, out var name) || Source.TryGetValue(usage, out name) || SourceExtra.TryGetValue(usage, out name) || MediaNames.TryGetValue(usage, out name))
        {
            return name;
        }

        return (usage & PageMask) switch
        {
            ButtonPage => $"Button {usage & 0xFFFF}",
            MidiPage   => Midi(usage),
            _          => Hex(usage),
        };
    }

    /// <summary>
    /// Names an output usage.
    /// </summary>
    /// <remarks>
    /// The descriptor's table carries the layer, macro, register, and analog names too. Descriptor 1 is the same as
    /// 0, and an unknown descriptor falls back to 0. A usage missing there falls back to the input rules.
    /// </remarks>
    /// <param name="usage">The output usage.</param>
    /// <param name="descriptorNumber">The device's output descriptor number from GET_CONFIG.</param>
    /// <returns>The name the web tool shows.</returns>
    public static string Output(uint usage, byte descriptorNumber)
    {
        var table = descriptorNumber switch
        {
            2 => Descriptor2,
            3 => Descriptor3,
            4 => Descriptor4,
            5 => Descriptor5,
            _ => Descriptor0,
        };

        return table.TryGetValue(usage, out var name) ? name : Input(usage);
    }

    /// <summary>
    /// Formats a usage the way the web tool does when it has no name.
    /// </summary>
    /// <remarks>
    /// Eight uppercase hex digits with a <c>0x</c> prefix.
    /// </remarks>
    /// <param name="usage">The usage.</param>
    /// <returns>The hex text.</returns>
    public static string Hex(uint usage) => string.Create(CultureInfo.InvariantCulture, $"0x{usage:X8}");

    // MIDI Usages Encode The Status Nibble And Channel In Byte 1 And The Note Or Controller In Byte 0
    private static string Midi(uint usage)
    {
        var status  = (usage >> 8) & 0xF0;
        var channel = (usage >> 8) & 0x0F;
        var prefix  = $"MIDI CH{channel}";
        var data    = usage & 0xFF;

        return status switch
        {
            0x90 => $"{prefix} note {data}",
            0xA0 => $"{prefix} PKP {data}",
            0xB0 => $"{prefix} CC{data}",
            0xC0 => $"{prefix} program",
            0xD0 => $"{prefix} CP",
            0xE0 => $"{prefix} PB",
            _    => Hex(usage),
        };
    }
}
