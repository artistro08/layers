using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.WindowsAndMessaging;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// Win32 window tweaks WinUI has no stock API for.
/// </summary>
/// <remarks>
/// The invisible flyout host setup, re-asserting topmost, the client origin, and monitor scale.
/// </remarks>
public static class WindowStyles
{
    /// <summary>
    /// Turns a window into an invisible, topmost anchor for a stock flyout.
    /// </summary>
    /// <remarks>
    /// A stock flyout needs a XAML root to open from. The window is borderless, topmost, and hidden from the taskbar
    /// and Alt+Tab. Windows clamps a 1x1 size up to its minimum window size, so it's also made fully transparent with
    /// layered alpha 0. The flyout draws in its own popup window, so it still shows normally.
    /// </remarks>
    /// <param name="window">The host window.</param>
    public static void MakeInvisibleHost(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);

        // Tiny, Borderless, Topmost, Hidden From Switchers
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;
        window.AppWindow.Resize(new SizeInt32(1, 1));

        // Fully Transparent
        var hwnd  = new HWND(WindowNative.GetWindowHandle(window));
        var style = (WINDOW_EX_STYLE)PInvoke.GetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE) | WINDOW_EX_STYLE.WS_EX_LAYERED;
        PInvoke.SetWindowLongPtr(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE, (nint)style);
        PInvoke.SetLayeredWindowAttributes(hwnd, new COLORREF(0), 0, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA);
    }

    /// <summary>Moves a window to the front of the topmost band without activating it.</summary>
    /// <remarks>
    /// <c>WS_EX_TOPMOST</c> only puts a window in the topmost band. Order within the band follows activation, so the
    /// HUD re-asserts this on every show.
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    public static void BringToTopmost(nint hwnd) =>
        PInvoke.SetWindowPos(new HWND(hwnd), HWND.HWND_TOPMOST, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    /// <summary>Gets where a window's client area starts on screen.</summary>
    /// <remarks>
    /// A borderless <see cref="OverlappedPresenter"/> window keeps an invisible frame, so its client area (where its XAML
    /// root starts) is inset from <c>AppWindow.Position</c>. The HUD uses this to place its flyout on an exact screen point.
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    /// <returns>The client area's top-left corner, in physical screen pixels.</returns>
    public static PointInt32 ClientOrigin(nint hwnd)
    {
        var origin = new System.Drawing.Point(0, 0);
        PInvoke.ClientToScreen(new HWND(hwnd), ref origin);
        return new PointInt32(origin.X, origin.Y);
    }

    /// <summary>Gets the primary monitor's scale.</summary>
    /// <remarks>The HUD always sits on the primary monitor.</remarks>
    /// <returns>For example 1.5 at 144 DPI.</returns>
    public static double PrimaryScale() => ScaleAt(new PointInt32(0, 0), primary: true);

    /// <summary>Gets the scale of the monitor containing a point.</summary>
    /// <remarks>Used to size the Settings window on the monitor under the cursor.</remarks>
    /// <param name="point">A screen point.</param>
    /// <returns>The scale.</returns>
    public static double ScaleAt(PointInt32 point) => ScaleAt(point, primary: false);

    private static double ScaleAt(PointInt32 point, bool primary)
    {
        var monitor = PInvoke.MonitorFromPoint(new System.Drawing.Point(point.X, point.Y),
            primary ? MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY : MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded
            ? dpiX / 96.0
            : 1.0;
    }
}
