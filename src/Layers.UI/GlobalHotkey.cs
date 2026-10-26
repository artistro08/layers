using System.Diagnostics;
using System.Runtime.InteropServices;
using Layers.Core.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Layers.UI;

/// <summary>
/// The one global shortcut, registered with <c>RegisterHotKey</c> on the tray icon's hidden window.
/// </summary>
/// <remarks>
/// <c>WM_HOTKEY</c> comes back through <see cref="TrayIcon.HotkeyPressed"/> with <see cref="Id"/>. A combination
/// Windows or another app holds cannot be taken: <see cref="Apply"/> returns <see langword="false"/>, <see cref="IsTaken"/>
/// stays set, and the Settings page warns. <c>MOD_NOREPEAT</c> keeps a held combination from firing over and over.
/// Only the Win32 error code is logged, never the keys. Call on the UI thread. Adapted from MagicBuds.
/// </remarks>
/// <param name="hwnd">The tray icon's window.</param>
public sealed class GlobalHotkey(nint hwnd) : IDisposable
{
    /// <summary>The hotkey ID handed to <c>RegisterHotKey</c> and seen in <c>WM_HOTKEY</c>.</summary>
    public const int Id = 1;

    // A Spare ID For Checking Whether A Combination Is Free
    private const int ProbeId = 0x7FFF;

    private readonly HWND _hwnd = new(hwnd);
    private bool _registered;

    /// <summary>Gets a value indicating whether the last <see cref="Apply"/> was refused because the combination is held elsewhere.</summary>
    public bool IsTaken { get; private set; }

    /// <summary>
    /// Registers a hotkey, letting go of the previous one first.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> just unregisters.
    /// </remarks>
    /// <param name="hotkey">The hotkey, or <see langword="null"/> for none.</param>
    /// <returns><see langword="true"/> when nothing was asked for or Windows accepted the registration.</returns>
    public bool Apply(Hotkey? hotkey)
    {
        Unregister();
        IsTaken = false;
        if (hotkey is not { } key)
        {
            return true;
        }

        if (PInvoke.RegisterHotKey(_hwnd, Id, Modifiers(key), (uint)key.Key))
        {
            _registered = true;
            return true;
        }

        // Taken: The Settings Page Shows The Warning; The Log Gets The Error Code, Never The Keys
        IsTaken = true;
        Trace.TraceWarning("hotkey registration refused: Win32 error {0}", Marshal.GetLastPInvokeError());
        return false;
    }

    /// <summary>
    /// Tells whether Windows would let the app register a combination right now.
    /// </summary>
    /// <remarks>
    /// Probes with a throwaway ID, so the current registration is untouched.
    /// </remarks>
    /// <param name="hotkey">The combination.</param>
    /// <returns><see langword="true"/> when free.</returns>
    public bool IsFree(Hotkey hotkey)
    {
        if (!PInvoke.RegisterHotKey(_hwnd, ProbeId, Modifiers(hotkey), (uint)hotkey.Key))
        {
            return false;
        }

        PInvoke.UnregisterHotKey(_hwnd, ProbeId);
        return true;
    }

    /// <inheritdoc/>
    public void Dispose() => Unregister();

    private void Unregister()
    {
        if (_registered)
        {
            PInvoke.UnregisterHotKey(_hwnd, Id);
            _registered = false;
        }
    }

    private static HOT_KEY_MODIFIERS Modifiers(Hotkey hotkey) => (HOT_KEY_MODIFIERS)(uint)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
}
