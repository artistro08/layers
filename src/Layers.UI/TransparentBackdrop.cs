using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls;

namespace Layers.UI;

/// <summary>
/// Makes the whole window see-through, so only the elements drawn on it show.
/// </summary>
/// <remarks>
/// A window's backdrop slot takes any composition brush; a fully transparent one leaves the window clear. The flyout
/// draws its own acrylic panel with <c>SystemBackdropElement</c>. DWM is told to blend the window with what's behind
/// it (blur-behind with a region that covers nothing turns on alpha without any blur), the 1 px DWM border is turned
/// off, and the background erases to black (see-through here) instead of white, which flashed for a frame. Same
/// technique as WinUIEx's TransparentTintBackdrop. Ported from MagicBuds.
/// </remarks>
public sealed unsafe partial class TransparentBackdrop : SystemBackdrop
{
    // DWMWA_COLOR_NONE: No Border Color At All
    private const uint ColorNone = 0xFFFFFFFE;

    private Windows.UI.Composition.Compositor? _compositor;

    /// <inheritdoc/>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(connectedTarget);
        ArgumentNullException.ThrowIfNull(xamlRoot);
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // The Backdrop Slot Uses The System Compositor, Which Needs A System Dispatcher Queue
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().EnsureSystemDispatcherQueue();
        _compositor                 ??= new Windows.UI.Composition.Compositor();
        connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));

        // Blend With What's Behind Instead Of Filling Black
        var hwnd    = new HWND(Microsoft.UI.Win32Interop.GetWindowFromWindowId(xamlRoot.ContentIslandEnvironment.AppWindowId));
        var margins = new MARGINS();
        PInvoke.DwmExtendFrameIntoClientArea(hwnd, in margins);
        var nowhere    = PInvoke.CreateRectRgn(-2, -2, -1, -1);
        var blurBehind = new DWM_BLURBEHIND
        {
            dwFlags  = PInvoke.DWM_BB_ENABLE | PInvoke.DWM_BB_BLURREGION,
            fEnable  = true,
            hRgnBlur = nowhere,
        };
        PInvoke.DwmEnableBlurBehindWindow(hwnd, in blurBehind);
        PInvoke.DeleteObject(nowhere);

        // No 1 px Outline, And Erase To Black
        var noBorder = ColorNone;
        PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_BORDER_COLOR, &noBorder, sizeof(uint));
        PInvoke.SetWindowSubclass(hwnd, &EraseToBlack, 0, 0);
    }

    /// <inheritdoc/>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ArgumentNullException.ThrowIfNull(disconnectedTarget);

        var brush = disconnectedTarget.SystemBackdrop;
        disconnectedTarget.SystemBackdrop = null;
        brush?.Dispose();

        base.OnTargetDisconnected(disconnectedTarget);

        // Close The Target Here, On The UI Thread
        // XAML drops each disconnected target without closing it, so its last release comes from the GC finalizer
        // thread, where closing its UI-thread content site fails fast (RPC_E_WRONG_THREAD in Microsoft.UI.Input.dll)
        (disconnectedTarget as IDisposable)?.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT EraseToBlack(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        if (message != PInvoke.WM_ERASEBKGND || !PInvoke.GetClientRect(hwnd, out var rect))
        {
            return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
        }

        _ = PInvoke.FillRect(new HDC((nint)wParam.Value), &rect, (HBRUSH)PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.BLACK_BRUSH).Value);
        return new LRESULT(1);
    }
}
