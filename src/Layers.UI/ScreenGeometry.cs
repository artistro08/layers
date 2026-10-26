using Layers.Core.Logic;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Layers.UI;

/// <summary>
/// Reads the monitor under the tray icon, or the cursor, for the flyout's placement.
/// </summary>
/// <remarks>
/// The math itself is <see cref="FlyoutPlacement"/>, kept pure. Ported from MagicBuds.
/// </remarks>
public static class ScreenGeometry
{
    /// <summary>
    /// Finds the monitor that holds the tray icon, or the cursor when the icon's position is unknown.
    /// </summary>
    /// <remarks>
    /// Physical pixels. The scale is the monitor's effective DPI over 96, or 1 when Windows won't say.
    /// </remarks>
    /// <param name="iconRect">The tray icon's bounds, or <see langword="null"/> to use the cursor.</param>
    /// <returns>The monitor's bounds, its work area, and its scale.</returns>
    public static (PixelRect Monitor, PixelRect WorkArea, double Scale) MonitorFor(RectInt32? iconRect)
    {
        HMONITOR monitor;
        if (iconRect is { } icon)
        {
            var rect = new RECT(icon.X, icon.Y, icon.X + icon.Width, icon.Y + icon.Height);
            monitor  = PInvoke.MonitorFromRect(in rect, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }
        else
        {
            PInvoke.GetCursorPos(out var point);
            monitor = PInvoke.MonitorFromPoint(point, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        }

        var info = new MONITORINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        PInvoke.GetMonitorInfo(monitor, ref info);
        var scale = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpi, out _).Succeeded ? dpi / 96.0 : 1.0;

        return (ToPixelRect(info.rcMonitor), ToPixelRect(info.rcWork), scale);
    }

    private static PixelRect ToPixelRect(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);
}
