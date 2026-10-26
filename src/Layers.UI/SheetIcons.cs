using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Layers.UI;

/// <summary>
/// Solid icons for the shortcut sheet's mouse, media, layer, and macro chips, with the label each chip shows.
/// </summary>
/// <remarks>
/// Every icon is drawn in the same 24 x 24 frame, so they all scale to the same size beside the chip's text. Scrolling
/// uses Fluent System Icons' phone vertical scroll icon, turned a quarter for horizontal. Expressions use an asterisk. An icon is
/// one or two layers: a solid one and, for the mouse, a dimmed one for the buttons that aren't pressed. The layer chip
/// uses the app's own layers glyph.
/// </remarks>
public static class SheetIcons
{
    private const string LeftButton  = "M5,8 A7,7 0 0 1 11.5,1.02 V10 H5 Z";
    private const string RightButton = "M12.5,1.02 A7,7 0 0 1 19,8 V10 H12.5 Z";
    private const string MouseBody   = "M5,11 H19 V16 A7,7 0 0 1 12,23 A7,7 0 0 1 5,16 Z";
    private const string Wheel       = "M10.75,3 H13.25 V9 H10.75 Z";
    private const string Speaker     = "M2,9 H6 L11,4.5 V19.5 L6,15 H2 Z";
    private const string NearWave    = "M14,8.6 A4.9,4.9 0 0 1 14,15.4 L13,14.4 A3.5,3.5 0 0 0 13,9.6 Z";
    private const string FarWave     = "M16.8,5.8 A8.8,8.8 0 0 1 16.8,18.2 L15.8,17.2 A7.4,7.4 0 0 0 15.8,6.8 Z";
    // Fluent System Icons' ic_fluent_phone_vertical_scroll_24_filled (MIT): A Phone With Up And Down Arrows
    private const string PhoneScroll = "M15.75 2C16.9926 2 18 3.00736 18 4.25V19.75C18 20.9926 16.9926 22 15.75 22H8.25C7.00736 22 6 20.9926 6 19.75V4.25C6 3.00736 7.00736 2 8.25 2H15.75ZM13.7144 13.475L11.9997 15.2246L10.2824 13.4747C9.99224 13.179 9.51738 13.1746 9.22175 13.4647C8.92611 13.7548 8.92163 14.2297 9.21175 14.5253L11.4647 16.8211C11.7588 17.1208 12.2417 17.1207 12.5356 16.8208L14.7856 14.525C15.0756 14.2291 15.0708 13.7543 14.775 13.4644C14.4791 13.1744 14.0043 13.1792 13.7144 13.475ZM14.7856 9.52083L12.5356 7.22504C12.2684 6.95239 11.8451 6.92747 11.5493 7.15038L11.4647 7.22469L9.21175 9.52048C8.92163 9.81612 8.92611 10.291 9.22175 10.5811C9.49051 10.8448 9.90738 10.8651 10.1989 10.6445L10.2824 10.5711L11.9997 8.82115L13.7144 10.5708C14.0043 10.8666 14.4791 10.8714 14.775 10.5814C15.0439 10.3179 15.0723 9.90147 14.8574 9.60567L14.7856 9.52083L12.5356 7.22504L14.7856 9.52083Z";

    // Three Bars Through The Center, Filled Nonzero So Where They Cross Stays Solid
    internal const string Asterisk = "F1 M10.20,21.00 L13.80,21.00 L13.80,3.00 L10.20,3.00 Z M18.89,18.06 L20.69,14.94 L5.11,5.94 L3.31,9.06 Z M3.31,14.94 L5.11,18.06 L20.69,9.06 L18.89,5.94 Z";

    private const string Cross       = "M14.5,9.9 L15.9,8.5 L18,10.6 L20.1,8.5 L21.5,9.9 L19.4,12 L21.5,14.1 L20.1,15.5 L18,13.4 L15.9,15.5 L14.5,14.1 L16.6,12 Z";

    /// <summary>
    /// Finds the icon and label for a key name, if it has one.
    /// </summary>
    /// <remarks>
    /// Mouse buttons are labeled by the click ("Left click"), media keys keep the web tool's name, and layer and macro
    /// targets keep theirs ("Layer 2", "Macro 8").
    /// </remarks>
    /// <param name="name">The key's name as the web config tool gives it.</param>
    /// <returns>The label and a factory for the icon, or <see langword="null"/> for a keyboard key.</returns>
    public static (string Label, Func<double, FrameworkElement> Icon)? For(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name switch
        {
            "Left button"    => ("Left click", size => Build(size, (MouseBody + " " + LeftButton, false), (RightButton, true))),
            "Right button"   => ("Right click", size => Build(size, (MouseBody + " " + RightButton, false), (LeftButton, true))),
            "Middle button"  => ("Middle click", size => Build(size, (MouseBody + " " + Wheel, false), (LeftButton + " " + RightButton, true))),
            "Volume up"      => (name, size => Build(size, (Speaker + " " + NearWave + " " + FarWave, false))),
            "Volume down"    => (name, size => Build(size, (Speaker + " " + NearWave, false))),
            "Mute"           => (name, size => Build(size, (Speaker + " " + Cross, false))),
            "Play/pause"     => (name, size => Build(size, ("M2,5 L10,12 L2,19 Z M13,5 H16 V19 H13 Z M19,5 H22 V19 H19 Z", false))),
            "Stop"           => (name, size => Build(size, ("M6,6 H18 V18 H6 Z", false))),
            "Next track"     => (name, size => Build(size, ("M4,5 L14,12 L4,19 Z M16,5 H19 V19 H16 Z", false))),
            "Previous track" => (name, size => Build(size, ("M20,5 L10,12 L20,19 Z M5,5 H8 V19 H5 Z", false))),
            "V scroll"       => ("Vertical scroll", size => Turned(Build(size, (PhoneScroll, false)), 0)),
            "H scroll"       => ("Horizontal scroll", size => Turned(Build(size, (PhoneScroll, false)), 90)),
            _ when name.StartsWith("Layer ", StringComparison.Ordinal) => (name, LayerIcon),
            _ when Layers.Core.Logic.ExpressionMeaning.Slot(name) is not null => (name, size => Build(size, (Asterisk, false))),
            _ when name.StartsWith("Macro ", StringComparison.Ordinal) => (name, size => Build(size, ("M2,6 L10,12 L2,18 Z", true), ("M11,5 L21,12 L11,19 Z", false))),
            _ => null,
        };
    }

    // Each Layer A PathIcon In The Shared 24 x 24 Frame, Scaled To The Requested Size
    private static Viewbox Build(double size, params (string Data, bool Dim)[] layers)
    {
        var frame = new Grid { Width = 24, Height = 24 };
        foreach (var (data, dim) in layers)
        {
            frame.Children.Add(new PathIcon
            {
                Width   = 24,
                Height  = 24,
                Opacity = dim ? 0.4 : 1,
                Data    = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), data),
            });
        }

        return new Viewbox { Width = size, Height = size, Child = frame };
    }

    // An Icon Turned About Its Center, For Horizontal Scrolling
    private static Viewbox Turned(Viewbox icon, double angle)
    {
        icon.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        icon.RenderTransform       = new RotateTransform { Angle = angle };
        return icon;
    }

    private static Viewbox LayerIcon(double size) => new()
    {
        Width  = size,
        Height = size,
        Child  = new PathIcon { Width = 24, Height = 24, Data = Glyphs.Layers(24) },
    };
}
