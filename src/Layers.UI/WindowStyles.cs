using System.Runtime.InteropServices;
using Layers.Core.Logic;
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
/// The invisible flyout host setup, re-asserting topmost, the client origin, monitor scale, and the HUD's monitor.
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

    /// <summary>
    /// Gets the work area and scale of the monitor the HUD opens on.
    /// </summary>
    /// <remarks>
    /// Looks up the primary monitor, the one holding the cursor, and the one holding the foreground window (none when
    /// there's no foreground window or it's the desktop or taskbar), then lets <see cref="HudPlacement.ChooseMonitor"/>
    /// pick. Called on every open, so the choice follows the cursor or focus at that moment.
    /// </remarks>
    /// <param name="mode">The "Open HUD on" setting.</param>
    /// <returns>The monitor's work area in physical pixels, and its scale.</returns>
    public static (RectInt32 Work, double Scale) HudMonitor(HudMonitorMode mode)
    {
        // Primary And Cursor Monitors
        PInvoke.GetCursorPos(out var cursor);
        var primary       = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var cursorMonitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);

        // Foreground Monitor, None For No Window Or The Desktop And Taskbar
        var foreground        = PInvoke.GetForegroundWindow();
        var foregroundMonitor = foreground.IsNull || HudPlacement.IsShellWindow(ClassName(foreground))
            ? HMONITOR.Null
            : PInvoke.MonitorFromWindow(foreground, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);

        // Its Work Area And Scale, Or The Primary's If It Vanished In Between
        var monitor = new HMONITOR(HudPlacement.ChooseMonitor(mode, primary, cursorMonitor, foregroundMonitor));
        var info    = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!PInvoke.GetMonitorInfo(monitor, ref info))
        {
            monitor = primary;
            PInvoke.GetMonitorInfo(monitor, ref info);
        }

        var work = info.rcWork;
        return (new RectInt32(work.left, work.top, work.Width, work.Height), Scale(monitor));
    }

    /// <summary>Gets the primary monitor's scale.</summary>
    /// <remarks>Used by the UI tests, which place the HUD on the primary monitor.</remarks>
    /// <returns>For example 1.5 at 144 DPI.</returns>
    public static double PrimaryScale() =>
        Scale(PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY));

    private static double Scale(HMONITOR monitor) =>
        PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _).Succeeded
            ? dpiX / 96.0
            : 1.0;

    private static string ClassName(HWND hwnd)
    {
        Span<char> name = stackalloc char[256];
        var length      = PInvoke.GetClassName(hwnd, name);
        return new string(name[..length]);
    }
}
