using Windows.Win32;

namespace Layers.Core.Services;

/// <summary>
/// What the tray window should do with a message.
/// </summary>
/// <remarks>
/// Returned by <see cref="TrayMessageRouter.Route"/>.
/// </remarks>
public enum TrayAction
{
    /// <summary>Pass to <c>DefWindowProc</c>.</summary>
    None,

    /// <summary>Open the tray menu (right-click).</summary>
    OpenMenu,

    /// <summary>Toggle the shortcut sheet flyout (left-click).</summary>
    OpenFlyout,

    /// <summary><c>WM_HOTKEY</c>: the registered global shortcut was pressed.</summary>
    Hotkey,

    /// <summary>Explorer restarted: add the icon again with <c>NIM_ADD</c>.</summary>
    ReAddIcon,

    /// <summary>Theme or DPI may have changed: redraw the icon.</summary>
    Refresh,

    /// <summary><c>WM_CLOSE</c> from the installer or <c>taskkill</c>: shut down cleanly.</summary>
    Quit,
}

/// <summary>
/// Decides what the hidden tray window does with each message.
/// </summary>
/// <remarks>
/// Kept pure so it's testable without a window. A left button-up on the icon toggles the shortcut sheet and a right
/// button-up opens the menu. <c>WM_HOTKEY</c> is the global shortcut. <c>TaskbarCreated</c> means Explorer restarted. <c>WM_SETTINGCHANGE</c> (including
/// "ImmersiveColorSet" for the taskbar theme) and <c>WM_DPICHANGED</c> redraw the icon. <c>WM_CLOSE</c> quits, so the
/// MSI's <c>CloseApplication</c> can close the app cleanly before replacing files.
/// </remarks>
public static class TrayMessageRouter
{
    /// <summary>The <c>uCallbackMessage</c> registered with <c>Shell_NotifyIconW</c>: <c>WM_APP + 1</c>.</summary>
    public const uint TrayCallbackMessage = PInvoke.WM_APP + 1;

    /// <summary>
    /// Routes one window message.
    /// </summary>
    /// <remarks>
    /// With the default <c>NOTIFYICON_VERSION</c>, the mouse message arrives in the low word of <c>lParam</c>.
    /// </remarks>
    /// <param name="message">The window message.</param>
    /// <param name="lParam">The message's lParam.</param>
    /// <param name="taskbarCreatedMessage">The registered <c>TaskbarCreated</c> message, or 0 if registration failed.</param>
    /// <returns>The action.</returns>
    public static TrayAction Route(uint message, nint lParam, uint taskbarCreatedMessage)
    {
        // Explorer Restarted
        if (taskbarCreatedMessage != 0 && message == taskbarCreatedMessage)
        {
            return TrayAction.ReAddIcon;
        }

        // Icon Clicks
        if (message == TrayCallbackMessage)
        {
            return (uint)(lParam & 0xFFFF) switch
            {
                PInvoke.WM_LBUTTONUP => TrayAction.OpenFlyout,
                PInvoke.WM_RBUTTONUP => TrayAction.OpenMenu,
                _                    => TrayAction.None,
            };
        }

        // Global Shortcut
        if (message == PInvoke.WM_HOTKEY)
        {
            return TrayAction.Hotkey;
        }

        // Clean Shutdown Request
        if (message == PInvoke.WM_CLOSE)
        {
            return TrayAction.Quit;
        }

        // Theme And DPI
        return message is PInvoke.WM_SETTINGCHANGE or PInvoke.WM_DPICHANGED ? TrayAction.Refresh : TrayAction.None;
    }
}
