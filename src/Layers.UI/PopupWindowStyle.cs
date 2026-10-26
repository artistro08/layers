using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.UI;

/// <summary>
/// Turns a WinUI window into a plain, frameless popup.
/// </summary>
/// <remarks>
/// The presenter's "no border" (<c>SetBorderAndTitleBar(false, false)</c>) still leaves a 3 px dialog frame
/// (<c>WS_DLGFRAME</c>) that draws as a white outline, and WinUI puts it back whenever the window shows. A subclass
/// strips the frame bits from every style change after WinUI's own handler has run. Corners are squared, because
/// whatever the window shows draws its own. Ported from MagicBuds.
/// </remarks>
public static unsafe class PopupWindowStyle
{
    private const uint FrameBits = (uint)(WINDOW_STYLE.WS_BORDER | WINDOW_STYLE.WS_DLGFRAME | WINDOW_STYLE.WS_THICKFRAME);

    /// <summary>
    /// Applies the popup style.
    /// </summary>
    /// <remarks>
    /// Call once, after the presenter is set.
    /// </remarks>
    /// <param name="hwnd">The window.</param>
    public static void Apply(nint hwnd)
    {
        var handle = new HWND(hwnd);
        PInvoke.SetWindowSubclass(handle, &StripFrame, 0, 0);
        var style = unchecked((int)(uint)(WINDOW_STYLE.WS_POPUP | WINDOW_STYLE.WS_CLIPSIBLINGS | WINDOW_STYLE.WS_SYSMENU));
        PInvoke.SetWindowLongPtr(handle, WINDOW_LONG_PTR_INDEX.GWL_STYLE, style);
        PInvoke.SetWindowPos(handle, HWND.Null, 0, 0, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_FRAMECHANGED | SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

        // Square Corners: The Panel Inside Draws Its Own
        var corner = (int)DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_DONOTROUND;
        PInvoke.DwmSetWindowAttribute(handle, DWMWINDOWATTRIBUTE.DWMWA_WINDOW_CORNER_PREFERENCE, &corner, sizeof(int));
    }

    // WinUI's Own Handler Adds The Frame Back, So It Runs First And The Frame Comes Off After
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT StripFrame(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        var result = PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
        if (message == PInvoke.WM_STYLECHANGING && (int)wParam.Value == (int)WINDOW_LONG_PTR_INDEX.GWL_STYLE)
        {
            var style = (STYLESTRUCT*)lParam.Value;
            style->styleNew &= ~FrameBits;
        }

        return result;
    }
}
