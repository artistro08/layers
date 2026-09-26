using System.Diagnostics;
using System.Security;
using Layers.Core.Logic;
using Microsoft.Win32;

namespace Layers.Core.Services;

/// <summary>
/// Loads and saves HUD settings in the registry.
/// </summary>
/// <remarks>
/// Uses <c>HKCU\Software\Layers</c> with the same DWORD value names as Layers 1.0.3, so existing users keep their settings.
/// Values are validated on load: a missing or wrong-typed value uses the default, the mute mask keeps only its low
/// 8 bits, <c>HudHoldMs</c> (new in 2.0) is clamped to 500..5000, and <c>HudMonitor</c> (new in 2.0) outside 0..2 is
/// Primary.
/// A stale <c>SeenLayers</c> value from older builds is ignored. Under MSIX, HKCU writes are virtualized per package,
/// so the MSI and MSIX builds keep separate settings.
/// </remarks>
/// <param name="keyPath">The HKCU-relative key. Tests pass a throwaway key.</param>
public sealed class SettingsStore(string keyPath = SettingsStore.DefaultKeyPath)
{
    /// <summary>The production key path.</summary>
    public const string DefaultKeyPath = @"Software\Layers";

    private const string HudEnabledName          = "HudEnabled";
    private const string HudSuppressedLayersName = "HudSuppressedLayers";
    private const string HudHoldMsName           = "HudHoldMs";
    private const string HudMonitorName          = "HudMonitor";

    /// <summary>
    /// Loads the settings.
    /// </summary>
    /// <remarks>
    /// Never throws. Unreadable values fall back to <see cref="HudSettings.Default"/>.
    /// </remarks>
    /// <returns>The settings.</returns>
    public HudSettings Load()
    {
        var defaults = HudSettings.Default;

        try
        {
            // Open Or Use Defaults
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            if (key is null)
            {
                return defaults;
            }

            // Read And Validate
            var enabled    = key.GetValue(HudEnabledName) is int e ? e != 0 : defaults.HudEnabled;
            var suppressed = key.GetValue(HudSuppressedLayersName) is int s ? (byte)(s & 0xFF) : defaults.HudSuppressedLayers;
            var hold       = key.GetValue(HudHoldMsName) is int h ? HudSettings.ClampHold(h) : defaults.HudHoldMs;
            var monitor    = key.GetValue(HudMonitorName) is int m && Enum.IsDefined((HudMonitorMode)m) ? (HudMonitorMode)m : defaults.HudMonitor;

            return new HudSettings(enabled, suppressed, hold, monitor);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("settings load failed: {0}", ex.Message);
            return defaults;
        }
    }

    /// <summary>
    /// Saves the settings.
    /// </summary>
    /// <remarks>
    /// Best effort: a failure is traced and never breaks the running app.
    /// </remarks>
    /// <param name="settings">The settings.</param>
    public void Save(HudSettings settings)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(HudEnabledName, settings.HudEnabled ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue(HudSuppressedLayersName, (int)settings.HudSuppressedLayers, RegistryValueKind.DWord);
            key.SetValue(HudHoldMsName, settings.HudHoldMs, RegistryValueKind.DWord);
            key.SetValue(HudMonitorName, (int)settings.HudMonitor, RegistryValueKind.DWord);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("settings save failed: {0}", ex.Message);
        }
    }
}
