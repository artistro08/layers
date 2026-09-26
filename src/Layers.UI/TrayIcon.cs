using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Layers.Core.Logic;
using Layers.Core.Services;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.UI;

/// <summary>
/// The notification-area icon and the hidden window that receives its messages.
/// </summary>
/// <remarks>
/// <para>
/// WinUI 3 has no stock tray icon, so this uses <c>Shell_NotifyIconW</c> directly. The hidden window
/// (<c>LayersMessageWindow</c>, <c>WS_EX_TOOLWINDOW</c>, never shown) is a normal top-level window, not
/// <c>HWND_MESSAGE</c>, because message-only windows get neither DPI nor broadcast messages. It's created on the UI
/// thread, whose WinUI message loop dispatches its messages.
/// </para>
/// <para>
/// <c>Shell_NotifyIconW</c> can re-enter the window procedure through a cross-process SendMessage, so refreshes are
/// guarded against nesting. No exception may escape the window procedure: that would abort the process.
/// </para>
/// </remarks>
public sealed unsafe class TrayIcon : IDisposable
{
    private const string WindowClass = "LayersMessageWindow";
    private const uint IconId        = 1;

    private static TrayIcon? s_current;

    private readonly HWND _hwnd;
    private readonly uint _taskbarCreated;
    private DestroyIconSafeHandle? _icon;
    private DeviceState _state = DeviceState.Initial;
    private bool _added;
    private bool _refreshing;

    /// <summary>
    /// Creates the hidden window and adds the icon.
    /// </summary>
    /// <remarks>
    /// Must run on the UI thread. Only one instance may exist.
    /// </remarks>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon()
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        s_current = this;

        // Register The Hidden Window Class
        var instance = PInvoke.GetModuleHandle((string?)null);
        fixed (char* className = WindowClass)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize        = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc   = &WindowProc,
                hInstance     = (HINSTANCE)instance.DangerousGetHandle(),
                lpszClassName = className,
            };
            PInvoke.RegisterClassEx(in windowClass);

            // Create It
            _hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, WindowClass, "Layers", WINDOW_STYLE.WS_OVERLAPPED,
                0, 0, 0, 0, HWND.Null, null, instance, null);
        }

        if (_hwnd.IsNull)
        {
            s_current = null;
            throw new Win32Exception();
        }

        // Explorer Restart Notification
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");

        Apply(NOTIFY_ICON_MESSAGE.NIM_ADD);
    }

    /// <summary>Raised when the icon is left- or right-clicked.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when the hidden window gets <c>WM_CLOSE</c>.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Gets the hidden window handle.</summary>
    public nint Handle => _hwnd;

    /// <summary>
    /// Redraws the icon and tooltip for a new device state.
    /// </summary>
    /// <remarks>
    /// Called on the UI thread whenever <c>DeviceService</c> reports a change.
    /// </remarks>
    /// <param name="state">The device state.</param>
    public void Update(DeviceState state)
    {
        // Ignore Late Updates After Dispose, So A Dead Window Never Re-Adds The Icon
        if (s_current != this)
        {
            return;
        }

        _state = state;
        Apply(_added ? NOTIFY_ICON_MESSAGE.NIM_MODIFY : NOTIFY_ICON_MESSAGE.NIM_ADD);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (s_current != this)
        {
            return;
        }

        // Block Re-Entrant Refreshes For Good, So NIM_DELETE's Cross-Process SendMessage Can't Re-Add The Icon
        _refreshing = true;

        // Remove The Icon
        var data = new NOTIFYICONDATAW { cbSize = (uint)sizeof(NOTIFYICONDATAW), hWnd = _hwnd, uID = IconId };
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in data);

        // Release Resources
        PInvoke.DestroyWindow(_hwnd);
        _icon?.Dispose();
        _icon     = null;
        s_current = null;
    }

    // =========================================================================
    // ICON
    // =========================================================================

    private void Apply(NOTIFY_ICON_MESSAGE operation)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            // Render For The Taskbar's DPI And Theme
            var size = IconMath.IconSize(TaskbarDpi());
            var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(_state, TaskbarTheme.IsLight(), size), size);

            // Describe The Icon
            var data = new NOTIFYICONDATAW
            {
                cbSize           = (uint)sizeof(NOTIFYICONDATAW),
                hWnd             = _hwnd,
                uID              = IconId,
                uFlags           = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP,
                uCallbackMessage = TrayMessageRouter.TrayCallbackMessage,
                hIcon            = (HICON)icon.DangerousGetHandle(),
            };
            var tip = StatusText.Tooltip(_state);
            tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(data.szTip.AsSpan());

            // Add Or Modify, Falling Back To Add If The Shell Lost It
            var ok = PInvoke.Shell_NotifyIcon(operation, in data);
            if (!ok && operation == NOTIFY_ICON_MESSAGE.NIM_MODIFY)
            {
                ok = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
            }

            _added = ok || _added;

            // Swap Icons
            _icon?.Dispose();
            _icon = icon;
        }
        catch (Win32Exception ex)
        {
            Trace.TraceWarning("tray refresh failed: {0}", ex.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private uint TaskbarDpi()
    {
        // The Taskbar's Own DPI, Then Ours, Then 96
        var taskbar = PInvoke.FindWindow("Shell_TrayWnd", null);
        var dpi     = taskbar.IsNull ? 0u : PInvoke.GetDpiForWindow(taskbar);
        if (dpi == 0)
        {
            dpi = PInvoke.GetDpiForWindow(_hwnd);
        }

        return dpi == 0 ? 96u : dpi;
    }

    // =========================================================================
    // WINDOW PROCEDURE
    // =========================================================================

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WindowProc(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            var self = s_current;
            if (self is not null && hwnd == self._hwnd)
            {
                switch (TrayMessageRouter.Route(message, lParam.Value, self._taskbarCreated))
                {
                    case TrayAction.OpenMenu:
                        self.MenuRequested?.Invoke(self, EventArgs.Empty);
                        return new LRESULT(0);
                    case TrayAction.ReAddIcon:
                        self._added = false;
                        self.Apply(NOTIFY_ICON_MESSAGE.NIM_ADD);
                        return new LRESULT(0);
                    case TrayAction.Quit:
                        self.QuitRequested?.Invoke(self, EventArgs.Empty);
                        return new LRESULT(0);
                    case TrayAction.Refresh:
                        self.Apply(NOTIFY_ICON_MESSAGE.NIM_MODIFY);
                        break;
                    case TrayAction.None:
                    default:
                        break;
                }
            }
        }
#pragma warning disable CA1031 // An exception escaping an unmanaged callback aborts the process
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceError("tray window procedure failed: {0}", ex);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
