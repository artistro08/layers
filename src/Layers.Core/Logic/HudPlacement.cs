using Windows.Foundation;
using Windows.Graphics;

namespace Layers.Core.Logic;

/// <summary>
/// Where the HUD sits.
/// </summary>
/// <remarks>
/// The HUD flyout's content is the box plus a transparent gap of <see cref="BottomGap"/> logical pixels under it, and
/// it reaches to one pixel short of the bottom of the chosen monitor's work area (see <see cref="ChooseMonitor"/>),
/// horizontally centered. So the box rests above the edge like the Windows 11 virtual-desktop switcher, and the
/// flyout's own bounds clip the exit slide at the edge. The flyout opens below an anchor at its top center, because a
/// point on the edge row itself belongs to any monitor below, and the flyout would then be placed on that monitor.
/// </remarks>
public static class HudPlacement
{
    /// <summary>Distance from the work area's bottom to the box's bottom, in logical pixels.</summary>
    public const double BottomGap = 12;

    /// <summary>The box's height in logical pixels, like the switcher.</summary>
    public const double BoxHeight = 46;

    /// <summary>
    /// Gets the box height in physical pixels.
    /// </summary>
    /// <remarks>
    /// <see cref="BoxHeight"/> scaled and rounded to whole pixels, for example 58 at 125%, as in the switcher.
    /// </remarks>
    /// <param name="scale">DPI scale, for example 1.25 at 120 DPI.</param>
    /// <returns>The box height in physical pixels.</returns>
    public static int BoxPixels(double scale) => (int)Math.Round(BoxHeight * scale);

    /// <summary>
    /// Gets the box height in DIPs.
    /// </summary>
    /// <remarks>
    /// <see cref="BoxPixels"/> back in DIPs, so the box sits exactly between the anchor and the gap.
    /// </remarks>
    /// <param name="scale">DPI scale.</param>
    /// <returns>The box height in DIPs.</returns>
    public static double BoxDips(double scale) => BoxPixels(scale) / scale;

    /// <summary>
    /// Gets the gap under the HUD in physical pixels.
    /// </summary>
    /// <remarks>
    /// <see cref="BottomGap"/> scaled and rounded to whole pixels, for example 15 at 125%, as in the switcher. The exit
    /// slides the HUD through this gap to the edge.
    /// </remarks>
    /// <param name="scale">DPI scale, for example 1.25 at 120 DPI.</param>
    /// <returns>The gap in physical pixels.</returns>
    public static int GapPixels(double scale) => (int)Math.Round(BottomGap * scale);

    /// <summary>
    /// Gets the transparent gap inside the flyout, under the box, in DIPs.
    /// </summary>
    /// <remarks>
    /// <see cref="GapPixels"/> less one pixel, back in DIPs, so it's a whole number of pixels and the flyout ends one
    /// pixel above the edge. A flyout that ends exactly on the edge fails the stock fit check by a rounding error and
    /// flips to open upward from the anchor, which put the box a whole flyout height (73 px at 125%) too high.
    /// </remarks>
    /// <param name="scale">DPI scale.</param>
    /// <returns>The flyout's gap in DIPs.</returns>
    public static double GapDips(double scale) => (GapPixels(scale) - 1) / scale;

    /// <summary>
    /// Gets the anchor point the flyout's top center sits on, in physical pixels.
    /// </summary>
    /// <remarks>
    /// Horizontally centered on the work area, <see cref="BoxPixels"/> plus <see cref="GapPixels"/> above its bottom,
    /// so the box's bottom rests the full gap above the edge and the flyout's content (with its one pixel shorter
    /// <see cref="GapDips"/>) ends one pixel short of the edge.
    /// </remarks>
    /// <param name="work">The chosen monitor's work area.</param>
    /// <param name="scale">DPI scale.</param>
    /// <returns>The anchor point.</returns>
    public static PointInt32 Anchor(RectInt32 work, double scale) =>
        new(work.X + work.Width / 2, work.Y + work.Height - BoxPixels(scale) - GapPixels(scale));

    /// <summary>
    /// Gets where the invisible host window goes.
    /// </summary>
    /// <remarks>
    /// Windows clamps the host up to its minimum size, so its bottom-left sits on the anchor. That keeps it on the
    /// chosen monitor, so the flyout renders at that monitor's scale instead of a monitor below it.
    /// </remarks>
    /// <param name="anchor">The anchor point.</param>
    /// <param name="hostHeight">The host window's height in physical pixels.</param>
    /// <returns>The host's top-left corner.</returns>
    public static PointInt32 HostPosition(PointInt32 anchor, int hostHeight) =>
        new(anchor.X, anchor.Y - hostHeight);

    /// <summary>
    /// Gets the anchor in the host's XAML coordinates, for the flyout's show position.
    /// </summary>
    /// <remarks>
    /// The borderless host's client area (where its XAML root starts) is inset by an invisible frame, so the anchor is
    /// measured from the client origin and converted to DIPs.
    /// </remarks>
    /// <param name="anchor">The anchor point.</param>
    /// <param name="clientOrigin">The host's client-area top-left on screen.</param>
    /// <param name="scale">DPI scale.</param>
    /// <returns>The show position in DIPs, relative to the host's root.</returns>
    public static Point FlyoutPosition(PointInt32 anchor, PointInt32 clientOrigin, double scale) =>
        new((anchor.X - clientOrigin.X) / scale, (anchor.Y - clientOrigin.Y) / scale);

    /// <summary>
    /// Picks the monitor the HUD opens on.
    /// </summary>
    /// <remarks>
    /// The pure half of the "Open HUD on" setting: the app looks up the three monitors, and this picks one. A missing
    /// foreground monitor (<c>0</c>, when there's no foreground window or it's the desktop or taskbar) falls back to
    /// the primary.
    /// </remarks>
    /// <param name="mode">The setting.</param>
    /// <param name="primary">The primary monitor.</param>
    /// <param name="cursor">The monitor holding the mouse cursor.</param>
    /// <param name="foreground">The monitor holding the foreground window, or <c>0</c> for none.</param>
    /// <returns>The chosen monitor handle.</returns>
    public static nint ChooseMonitor(HudMonitorMode mode, nint primary, nint cursor, nint foreground) => mode switch
    {
        HudMonitorMode.Cursor                             => cursor,
        HudMonitorMode.FocusedWindow when foreground != 0 => foreground,
        _                                                 => primary,
    };

    /// <summary>
    /// Whether a window class is the desktop or the taskbar.
    /// </summary>
    /// <remarks>
    /// Clicking the desktop or the taskbar makes it the foreground window, but that isn't a window the user is working
    /// in, so the "Monitor with focused window" setting treats it as no foreground window and uses the primary.
    /// </remarks>
    /// <param name="className">The foreground window's class name.</param>
    /// <returns>
    /// <see langword="true"/> for <c>Progman</c>, <c>WorkerW</c>, <c>Shell_TrayWnd</c>, and the taskbar on other
    /// monitors, <c>Shell_SecondaryTrayWnd</c>.
    /// </returns>
    public static bool IsShellWindow(string className) =>
        className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
}
