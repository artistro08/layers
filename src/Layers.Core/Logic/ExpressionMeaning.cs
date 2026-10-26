using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// One piece of a meaning: plain text, or a key drawn as a chip.
/// </summary>
/// <remarks>
/// Produced by <see cref="ExpressionMeaning.Parse"/>.
/// </remarks>
/// <param name="Text">The text, or the key's name as the web config tool gives it.</param>
/// <param name="IsKey">Whether this piece is a key.</param>
public readonly record struct MeaningPart(string Text, bool IsKey);

/// <summary>
/// An expression the device's mappings use, and what it drives.
/// </summary>
/// <remarks>
/// Listed in Settings › Shortcuts so the user can say what it means.
/// </remarks>
/// <param name="Slot">The expression slot, 1 through 8.</param>
/// <param name="Drives">The outputs mapped from it, once each, in device order. Empty when it is only fed by a key.</param>
public sealed record ExpressionUse(int Slot, IReadOnlyList<string> Drives);

/// <summary>
/// The user's own description of what an expression does.
/// </summary>
/// <remarks>
/// Expressions are small stack programs (often gesture logic with stored values) that no reader can reliably put into
/// words, so the user writes a meaning once. A meaning is text with key names in braces, such as
/// "Hold {Back} and swipe left", which the sheet draws as text and key chips wherever the expression appears.
/// </remarks>
public static class ExpressionMeaning
{
    private const string Prefix         = "Expression ";
    private const uint   PageMask       = 0xFFFF_0000;
    private const uint   ExpressionPage = 0xFFF3_0000;
    private const int    Slots          = 8;

    /// <summary>
    /// Splits a meaning into text and keys.
    /// </summary>
    /// <remarks>
    /// Text between braces is a key. An unclosed or empty pair of braces stays text. Text pieces are trimmed and blank
    /// ones dropped.
    /// </remarks>
    /// <param name="text">The meaning.</param>
    /// <returns>The pieces, in order. Empty for a blank meaning.</returns>
    public static IReadOnlyList<MeaningPart> Parse(string? text)
    {
        var parts = new List<MeaningPart>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return parts;
        }

        var plain = new System.Text.StringBuilder();
        var i     = 0;
        while (i < text.Length)
        {
            var close = text[i] == '{' ? text.IndexOf('}', i + 1) : -1;
            if (close > i + 1)
            {
                AddText(parts, plain);
                parts.Add(new MeaningPart(text[(i + 1)..close], true));
                i = close + 1;
                continue;
            }

            plain.Append(text[i]);
            i++;
        }

        AddText(parts, plain);
        return parts;
    }

    /// <summary>
    /// Makes the token for a key, as the key picker inserts it.
    /// </summary>
    /// <param name="name">The key's name.</param>
    /// <returns>The name in braces.</returns>
    public static string Token(string name) => $"{{{name}}}";

    /// <summary>
    /// Reads the slot from an expression's name.
    /// </summary>
    /// <param name="name">A usage name, for example "Expression 7".</param>
    /// <returns>The slot, 1 through 8, or <see langword="null"/> for any other name.</returns>
    public static int? Slot(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.StartsWith(Prefix, StringComparison.Ordinal)
            && int.TryParse(name.AsSpan(Prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var slot)
            && slot is >= 1 and <= Slots
            ? slot
            : null;
    }

    /// <summary>
    /// Finds the expressions the mappings use.
    /// </summary>
    /// <remarks>
    /// An expression is used when a mapping on some layer reads it or writes it. Layers' own layer-reading expression is
    /// never mapped, so it never appears.
    /// </remarks>
    /// <param name="config">The device config.</param>
    /// <returns>The used slots, lowest first, each with what it drives.</returns>
    public static IReadOnlyList<ExpressionUse> Uses(DeviceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var live = config.Mappings.Where(mapping => mapping.LayerMask != 0).ToList();
        return Enumerable.Range(1, Slots)
            .Select(slot => (Slot: slot, Usage: ExpressionPage | (uint)slot))
            .Where(e => live.Any(m => m.Source == e.Usage || m.Target == e.Usage))
            .Select(e => new ExpressionUse(e.Slot, live
                .Where(m => m.Source == e.Usage)
                .Select(m => UsageNames.Output(m.Target, config.DescriptorNumber))
                .Distinct()
                .ToList()))
            .ToList();
    }

    private static void AddText(List<MeaningPart> parts, System.Text.StringBuilder plain)
    {
        var text = plain.ToString().Trim();
        plain.Clear();
        if (text.Length > 0)
        {
            parts.Add(new MeaningPart(text, false));
        }
    }
}
