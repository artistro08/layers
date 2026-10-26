namespace Layers.Core.Logic;

/// <summary>
/// What a mapping produces: keys, or a macro.
/// </summary>
/// <remarks>
/// The view renders <see cref="KeysOutput"/> as chips. A one-step <see cref="MacroOutput"/> shows as its keys behind
/// a macro icon; a longer one shows as its steps, an arrow, then a macro chip.
/// </remarks>
public abstract record ShortcutOutput;

/// <summary>
/// An output that is one or more named keys.
/// </summary>
/// <remarks>
/// HID Remapper targets are single usages, so <see cref="Keys"/> has one entry today; the list shape keeps the
/// chip code path shared with macro steps.
/// </remarks>
/// <param name="Keys">The key names.</param>
public sealed record KeysOutput(IReadOnlyList<string> Keys) : ShortcutOutput;

/// <summary>
/// An output that fires a macro.
/// </summary>
/// <remarks>
/// <see cref="Steps"/> is empty for a macro slot with nothing in it.
/// </remarks>
/// <param name="Number">The 1-based macro number, as the web tool labels it.</param>
/// <param name="Steps">The steps, each the key names pressed together.</param>
public sealed record MacroOutput(int Number, IReadOnlyList<IReadOnlyList<string>> Steps) : ShortcutOutput;

/// <summary>
/// One thing a key does on a layer.
/// </summary>
/// <remarks>
/// A key with a tap and a hold mapping has two of these under it.
/// </remarks>
/// <param name="Output">What the key does.</param>
/// <param name="Sticky">The mapping's sticky flag.</param>
/// <param name="Tap">The mapping's tap flag.</param>
/// <param name="Hold">The mapping's hold flag.</param>
public sealed record ShortcutRow(ShortcutOutput Output, bool Sticky, bool Tap, bool Hold);

/// <summary>
/// Everything one input key does on a layer.
/// </summary>
/// <remarks>
/// Drawn as one divided block: the key, then its rows.
/// </remarks>
/// <param name="Input">The input key's name.</param>
/// <param name="Rows">Its mappings on the layer, in device order.</param>
public sealed record KeyGroup(string Input, IReadOnlyList<ShortcutRow> Rows);

/// <summary>
/// The keys mapped on one layer.
/// </summary>
/// <remarks>
/// Only layers with mappings are built.
/// </remarks>
/// <param name="Layer">The layer, 0 through 7.</param>
/// <param name="Keys">Its keys, in the order the device first lists them.</param>
public sealed record LayerSection(int Layer, IReadOnlyList<KeyGroup> Keys)
{
    /// <summary>Gets the section heading, for example "Layer 2".</summary>
    public string Title => $"Layer {Layer}";
}

/// <summary>
/// Turns the device config into the sheet's sections.
/// </summary>
/// <remarks>
/// Pure. One section per layer, layer 0 first. Within a layer each key appears once, with everything it does there
/// (tap, hold, and so on) under it, so a key reads as one block. A mapping on several layers appears under each.
/// </remarks>
public static class ShortcutSheet
{
    private const int  LayerCount   = 8;
    private const uint NothingUsage = 0;

    /// <summary>
    /// Builds the sections.
    /// </summary>
    /// <remarks>
    /// A mapping with an empty mask never fires, and a mapping to Nothing only silences its key (usually so an expression
    /// can read it), so neither appears. A macro target past the 32 slots is shown by name only.
    /// </remarks>
    /// <param name="config">The device config.</param>
    /// <returns>The sections with keys, layer 0 first.</returns>
    public static IReadOnlyList<LayerSection> Build(DeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var sections = new List<LayerSection>();
        for (var layer = 0; layer < LayerCount; layer++)
        {
            var bit  = 1 << layer;
            var keys = config.Mappings
                .Where(mapping => (mapping.LayerMask & bit) != 0 && mapping.Target != NothingUsage)
                .GroupBy(mapping => mapping.Source)
                .Select(group => new KeyGroup(UsageNames.Input(group.Key), group.Select(mapping => ToRow(mapping, config)).ToList()))
                .ToList();
            if (keys.Count > 0)
            {
                sections.Add(new LayerSection(layer, keys));
            }
        }

        return sections;
    }

    private static ShortcutRow ToRow(Mapping mapping, DeviceConfig config)
    {
        // Macro: Its Number And Its Steps
        if (mapping.MacroIndex is int index && index >= 0 && index < config.Macros.Count)
        {
            var steps = config.Macros[index]
                .Select(step => (IReadOnlyList<string>)step.Select(usage => UsageNames.Output(usage, config.DescriptorNumber)).ToList())
                .ToList();
            return new ShortcutRow(new MacroOutput(index + 1, steps), mapping.Sticky, mapping.Tap, mapping.Hold);
        }

        return new ShortcutRow(new KeysOutput([UsageNames.Output(mapping.Target, config.DescriptorNumber)]), mapping.Sticky, mapping.Tap, mapping.Hold);
    }
}
