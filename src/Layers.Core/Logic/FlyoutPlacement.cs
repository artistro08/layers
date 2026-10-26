namespace Layers.Core.Logic;

/// <summary>
/// A screen rectangle in physical pixels.
/// </summary>
/// <remarks>
/// Right and bottom are exclusive, as Win32 <c>RECT</c> has them.
/// </remarks>
/// <param name="Left">The left edge.</param>
/// <param name="Top">The top edge.</param>
/// <param name="Right">One past the right edge.</param>
/// <param name="Bottom">One past the bottom edge.</param>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the width in pixels.</summary>
    public int Width => Right - Left;

    /// <summary>Gets the height in pixels.</summary>
    public int Height => Bottom - Top;
}

/// <summary>
/// The screen edge the taskbar sits on.
/// </summary>
/// <remarks>
/// Found by comparing a monitor to its work area; see <see cref="FlyoutPlacement.DetectEdge"/>.
/// </remarks>
public enum TaskbarEdge
{
    /// <summary>The taskbar is at the bottom, or auto-hidden.</summary>
    Bottom,

    /// <summary>The taskbar is at the top.</summary>
    Top,

    /// <summary>The taskbar is on the left.</summary>
    Left,

    /// <summary>The taskbar is on the right.</summary>
    Right,
}

/// <summary>
/// Where the shortcut sheet goes on the monitor that holds the tray icon.
/// </summary>
/// <remarks>
/// Pure math, so it's testable. The flyout is always right-aligned in the work area with a margin. It sits above a
/// bottom taskbar, below a top taskbar, and at the bottom of the work area when the taskbar is on a side. It never
/// leaves the work area: a short or narrow work area shrinks it. Ported from MagicBuds.
/// </remarks>
public static class FlyoutPlacement
{
    /// <summary>The flyout width in device-independent pixels.</summary>
    public const int WidthDip = 480;

    /// <summary>The gap between the flyout and the work area's edges, in device-independent pixels.</summary>
    public const int MarginDip = 12;

    /// <summary>
    /// Finds the taskbar edge.
    /// </summary>
    /// <remarks>
    /// An auto-hidden taskbar takes no work area, so it's treated as bottom.
    /// </remarks>
    /// <param name="monitor">The monitor's full bounds.</param>
    /// <param name="workArea">The monitor's work area.</param>
    /// <returns>The edge.</returns>
    public static TaskbarEdge DetectEdge(PixelRect monitor, PixelRect workArea)
    {
        if (workArea.Bottom < monitor.Bottom)
        {
            return TaskbarEdge.Bottom;
        }

        if (workArea.Top > monitor.Top)
        {
            return TaskbarEdge.Top;
        }

        if (workArea.Left > monitor.Left)
        {
            return TaskbarEdge.Left;
        }

        if (workArea.Right < monitor.Right)
        {
            return TaskbarEdge.Right;
        }

        return TaskbarEdge.Bottom;
    }

    /// <summary>
    /// Works out the flyout's rectangle.
    /// </summary>
    /// <remarks>
    /// The height asked for is a ceiling: the panel inside sizes itself to its content within it.
    /// </remarks>
    /// <param name="monitor">The monitor's full bounds.</param>
    /// <param name="workArea">The monitor's work area.</param>
    /// <param name="scale">The monitor's DPI divided by 96.</param>
    /// <param name="heightDip">The height wanted, in device-independent pixels.</param>
    /// <returns>The rectangle in physical pixels.</returns>
    public static PixelRect Calculate(PixelRect monitor, PixelRect workArea, double scale, double heightDip)
    {
        var edge   = DetectEdge(monitor, workArea);
        var margin = (int)Math.Round(MarginDip * scale);
        var width  = Math.Min((int)Math.Round(WidthDip * scale), workArea.Width - (2 * margin));
        var height = Math.Min((int)Math.Round(heightDip * scale), workArea.Height - (2 * margin));

        var left = workArea.Right - margin - width;
        var top  = edge == TaskbarEdge.Top
            ? workArea.Top + margin
            : workArea.Bottom - margin - height;

        return new PixelRect(left, top, left + width, top + height);
    }
}
