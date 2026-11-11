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
using Windows.Graphics;

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
    private const nuint RetryTimerId = 1;

    // Fixed Identity For The MSIX Build, So Windows Keeps The Icon's "Show On Taskbar" Choice Across Updates
    // Its Install Path Has The Version In It, And Without A GUID Windows Treats Each Version As A New Icon, Hidden In
    // The Overflow. The MSI Build Keeps One Path, And Dev Builds Run From Others, So Those Keep The Plain Window + ID
    // (Windows Ties A GUID To The Path That First Used It). Never Change It: That Strands Every User's Choice Again
    private static readonly Guid PackagedGuid = new("B3080742-07FC-49F2-B481-3F246990020F");

    private static TrayIcon? s_current;

    private readonly HWND _hwnd;
    private readonly Guid? _guid;
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
    /// <param name="packaged">Whether this is the MSIX build, which picks the icon's fixed identity.</param>
    /// <exception cref="InvalidOperationException">A tray icon already exists.</exception>
    /// <exception cref="Win32Exception">The window couldn't be created.</exception>
    public TrayIcon(bool packaged)
    {
        if (s_current is not null)
        {
            throw new InvalidOperationException("Only one tray icon may exist.");
        }

        s_current = this;
        _guid     = packaged ? PackagedGuid : null;

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

        // A Copy That Crashed Or Was Killed Leaves Its Icon Under The Same GUID, And NIM_ADD Fails Until It's Gone
        if (_guid is not null)
        {
            var stale = Identity();
            PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in stale);
        }

        Apply(NOTIFY_ICON_MESSAGE.NIM_ADD);
    }

    /// <summary>Raised when the icon is right-clicked.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when the icon is left-clicked.</summary>
    public event EventHandler? FlyoutRequested;

    /// <summary>Raised when a hotkey registered on this window fires, with the hotkey ID.</summary>
    public event EventHandler<int>? HotkeyPressed;

    /// <summary>Raised when the hidden window gets <c>WM_CLOSE</c>.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Gets the hidden window handle.</summary>
    public nint Handle => _hwnd;

    /// <summary>
    /// Finds where the icon sits on screen.
    /// </summary>
    /// <remarks>
    /// Physical pixels. <see langword="null"/> when the shell cannot say, for example while the icon is in the
    /// overflow menu that is not open.
    /// </remarks>
    /// <returns>The icon's rectangle, or <see langword="null"/>.</returns>
    public RectInt32? GetIconRect()
    {
        var id = new NOTIFYICONIDENTIFIER { cbSize = (uint)sizeof(NOTIFYICONIDENTIFIER), hWnd = _hwnd, uID = IconId, guidItem = _guid ?? Guid.Empty };
        if (PInvoke.Shell_NotifyIconGetRect(in id, out var rect).Failed)
        {
            return null;
        }

        return new RectInt32(rect.left, rect.top, rect.right - rect.left, rect.bottom - rect.top);
    }

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
        var data = Identity();
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
            var data = Identity();
            data.uFlags          |= NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
            data.uCallbackMessage = TrayMessageRouter.TrayCallbackMessage;
            data.hIcon            = (HICON)icon.DangerousGetHandle();
            var tip = StatusText.Tooltip(_state);
            tip.AsSpan(0, Math.Min(tip.Length, 127)).CopyTo(data.szTip.AsSpan());

            // Add Or Modify, Falling Back To Add If The Shell Lost It
            var ok = PInvoke.Shell_NotifyIcon(operation, in data);
            if (!ok && operation == NOTIFY_ICON_MESSAGE.NIM_MODIFY)
            {
                ok = PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in data);
            }

            _added = ok || _added;

            // The Shell Can Refuse Or Time Out While It's Still Starting At Sign-In, And Nothing Else Would Try
            // Again, So Retry Every 2 Seconds Until It Takes
            if (ok)
            {
                PInvoke.KillTimer(_hwnd, RetryTimerId);
            }
            else
            {
                Trace.TraceWarning("tray icon {0} failed, retrying", operation);
                PInvoke.SetTimer(_hwnd, RetryTimerId, 2000, null);
            }

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

    // The Window And ID, Plus The GUID When There Is One (Then The Shell Goes By The GUID)
    private NOTIFYICONDATAW Identity() => new()
    {
        cbSize   = (uint)sizeof(NOTIFYICONDATAW),
        hWnd     = _hwnd,
        uID      = IconId,
        guidItem = _guid ?? Guid.Empty,
        uFlags   = _guid is null ? 0 : NOTIFY_ICON_DATA_FLAGS.NIF_GUID,
    };

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
                    case TrayAction.OpenFlyout:
                        self.FlyoutRequested?.Invoke(self, EventArgs.Empty);
                        return new LRESULT(0);
                    case TrayAction.Hotkey:
                        self.HotkeyPressed?.Invoke(self, (int)wParam.Value);
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
