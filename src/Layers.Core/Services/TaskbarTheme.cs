using System.Diagnostics;
using System.Security;
using Microsoft.Win32;

namespace Layers.Core.Services;

/// <summary>
/// Reads whether the taskbar uses the light theme.
/// </summary>
/// <remarks>
/// The tray icon follows the taskbar theme (<c>SystemUsesLightTheme</c>), not the app theme. There's no WinRT API for
/// the taskbar theme, so this reads the registry. A missing value means a dark taskbar, matching Windows' default.
/// </remarks>
public static class TaskbarTheme
{
    /// <summary>The Windows personalization key.</summary>
    public const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>
    /// Whether the taskbar is light.
    /// </summary>
    /// <remarks>
    /// Read fresh on every tray refresh, which happens on <c>WM_SETTINGCHANGE</c>. A key that can't be read (access
    /// denied, or deleted mid-read) is traced and treated as a dark taskbar, so a tray refresh never crashes on it.
    /// </remarks>
    /// <param name="keyPath">The HKCU-relative key. Tests pass a throwaway key.</param>
    /// <returns><see langword="true"/> for a light taskbar.</returns>
    public static bool IsLight(string keyPath = PersonalizeKeyPath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is SecurityException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("taskbar theme unreadable, assuming dark: {0}", ex.Message);
            return false;
        }
    }
}
