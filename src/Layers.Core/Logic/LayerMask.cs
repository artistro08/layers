using System.Globalization;

namespace Layers.Core.Logic;

/// <summary>
/// The set of currently active HID Remapper layers, one bit per layer.
/// </summary>
/// <remarks>
/// Bit <c>n</c> set means layer <c>n</c> is active. The firmware floors an empty mask to layer 0,
/// but a garbled report must not produce an empty display, so an empty mask is read as layer 0 too.
/// All 8 bits are honored even though the rp2040 firmware only uses 4 layers.
/// </remarks>
/// <param name="Bits">The raw mask from the device.</param>
public readonly record struct LayerMask(byte Bits)
{
    /// <summary>
    /// Gets the mask for layer 0 alone.
    /// </summary>
    /// <remarks>
    /// This is the firmware's state after a connect or a RESUME.
    /// </remarks>
    public static LayerMask Base => new(1);

    /// <summary>
    /// Gets the active layer numbers in ascending order.
    /// </summary>
    /// <remarks>
    /// Never empty: an empty mask yields <c>[0]</c>.
    /// </remarks>
    public IReadOnlyList<int> Active
    {
        get
        {
            // Empty Mask Means Layer 0
            if (Bits == 0)
            {
                return [0];
            }

            // Collect Set Bits
            var active = new List<int>(8);
            for (var layer = 0; layer < 8; layer++)
            {
                if ((Bits & (1 << layer)) != 0)
                {
                    active.Add(layer);
                }
            }

            return active;
        }
    }

    /// <summary>
    /// Gets the digit drawn on the tray icon.
    /// </summary>
    /// <remarks>
    /// This is the highest active layer, or <see langword="null"/> when that is layer 0, which draws as the bare glyph.
    /// </remarks>
    public int? Badge
    {
        get
        {
            var highest = Active[^1];
            return highest == 0 ? null : highest;
        }
    }

    /// <summary>
    /// Gets the human-readable label, for example "Layer 2" or "Layers 1, 3".
    /// </summary>
    /// <remarks>
    /// Used by the tooltip, the menu, and the HUD.
    /// </remarks>
    public string Label
    {
        get
        {
            var active = Active;
            var list   = string.Join(", ", active.Select(layer => layer.ToString(CultureInfo.InvariantCulture)));

            return active.Count == 1 ? $"Layer {list}" : $"Layers {list}";
        }
    }
}
