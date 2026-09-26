using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UI;

/// <summary>
/// The HUD box's face: its border, the layers glyph, and the label.
/// </summary>
/// <remarks>
/// Shared by the real HUD (<see cref="HudHost"/>, over its live desktop acrylic) and the Settings preview
/// (<see cref="HudPreview"/>, over in-app acrylic), so the two can't drift apart. The face is at least 127 DIPs wide
/// with 6 DIP corners and the stock flyout border, and its <see cref="Control.Background"/> fills inside the border.
/// The height comes from the host (<c>HudPlacement.BoxHeight</c>).
/// </remarks>
public sealed partial class HudFace : UserControl
{
    /// <summary>
    /// Creates the face.
    /// </summary>
    /// <remarks>
    /// The label starts empty.
    /// </remarks>
    public HudFace() => InitializeComponent();

    /// <summary>Gets or sets the label, for example "Layer 1".</summary>
    /// <remarks>Plain text, set directly on the label.</remarks>
    public string Text
    {
        get => Label.Text;
        set => Label.Text = value;
    }

    /// <summary>
    /// Hides the label from screen readers.
    /// </summary>
    /// <remarks>
    /// For the Settings demo, whose "Layer 1" is decoration. Setting <see cref="AccessibilityView.Raw"/> on the face
    /// itself isn't enough: UI Automation still exposes a raw element's children, so the label is the one to hide.
    /// </remarks>
    internal void HideFromScreenReaders() => AutomationProperties.SetAccessibilityView(Label, AccessibilityView.Raw);
}
