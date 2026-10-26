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
/// <c>FlyoutShortcut</c> (new in 2.1) is the shortcut sheet hotkey as text; see <see cref="LoadShortcut"/>.
/// A stale <c>SeenLayers</c> value from older builds is ignored. Under MSIX, HKCU writes are virtualized per package,
/// so the MSI and MSIX builds keep separate settings.
/// </remarks>
/// <param name="keyPath">The HKCU-relative key. Tests pass a throwaway key.</param>
public sealed class SettingsStore(string keyPath = SettingsStore.DefaultKeyPath)
{
    /// <summary>The production key path.</summary>
    public const string DefaultKeyPath = @"Software\Layers";

    /// <summary>Gets the web config tool the shortcut sheet's edit button opens until the user picks another.</summary>
    public static Uri DefaultEditorUrl { get; } = new("https://remapper.org/config");

    private const string HudEnabledName          = "HudEnabled";
    private const string HudSuppressedLayersName = "HudSuppressedLayers";
    private const string HudHoldMsName           = "HudHoldMs";
    private const string HudMonitorName          = "HudMonitor";
    private const string FlyoutShortcutName      = "FlyoutShortcut";
    private const string MeaningsKeyName         = "ExpressionMeanings";
    private const string EditorUrlName           = "EditorUrl";
    private const int    ExpressionSlots         = 8;

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

    // =========================================================================
    // SHORTCUT
    // =========================================================================

    /// <summary>
    /// Loads the shortcut sheet hotkey.
    /// </summary>
    /// <remarks>
    /// A missing value is the default, Win+Shift+L. An empty, wrong-typed, or unparsable value means the hotkey is
    /// off, so a corrupt value never throws at startup. Never throws.
    /// </remarks>
    /// <returns>The hotkey, or <see langword="null"/> when off.</returns>
    public Hotkey? LoadShortcut()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            var value     = key?.GetValue(FlyoutShortcutName);
            if (value is null)
            {
                return Hotkey.Default;
            }

            return value is string text && Hotkey.TryParse(text, out var hotkey) ? hotkey : null;
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("shortcut load failed: {0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Loads what the user says each expression means.
    /// </summary>
    /// <remarks>
    /// Stored as REG_SZ values named "1" through "8" under <c>ExpressionMeanings</c>. Anything else there is ignored.
    /// Never throws.
    /// </remarks>
    /// <returns>The meanings by slot.</returns>
    public IReadOnlyDictionary<int, string> LoadMeanings()
    {
        var meanings = new Dictionary<int, string>();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{keyPath}\{MeaningsKeyName}");
            if (key is null)
            {
                return meanings;
            }

            for (var slot = 1; slot <= ExpressionSlots; slot++)
            {
                if (key.GetValue(slot.ToString(System.Globalization.CultureInfo.InvariantCulture)) is string text && !string.IsNullOrWhiteSpace(text))
                {
                    meanings[slot] = text;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("meanings load failed: {0}", ex.Message);
        }

        return meanings;
    }

    /// <summary>
    /// Saves what the user says an expression means.
    /// </summary>
    /// <remarks>
    /// A blank meaning removes the value. Best effort: a failure is traced.
    /// </remarks>
    /// <param name="slot">The expression slot, 1 through 8.</param>
    /// <param name="text">The meaning, or blank to remove it.</param>
    public void SaveMeaning(int slot, string? text)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(slot, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slot, ExpressionSlots);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{keyPath}\{MeaningsKeyName}");
            var name      = slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(text))
            {
                key.DeleteValue(name, throwOnMissingValue: false);
                return;
            }

            key.SetValue(name, text, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("meaning save failed: {0}", ex.Message);
        }
    }

    // =========================================================================
    // EDITOR ADDRESS
    // =========================================================================

    /// <summary>
    /// Reads a web address the way a person types it.
    /// </summary>
    /// <remarks>
    /// A missing scheme means https. Only http and https are accepted, so the edit button can't launch a file or a
    /// script.
    /// </remarks>
    /// <param name="text">The typed address.</param>
    /// <param name="url">The address when this returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> for an http or https address.</returns>
    public static bool TryParseEditorUrl(string? text, out Uri? url)
    {
        url = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var trimmed = text.Trim();
        if (!trimmed.Contains("://", StringComparison.Ordinal) && !trimmed.Contains(':', StringComparison.Ordinal))
        {
            trimmed = $"https://{trimmed}";
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("https" or "http") || parsed.Host.Length == 0)
        {
            return false;
        }

        url = parsed;
        return true;
    }

    /// <summary>
    /// Loads the web config tool's address.
    /// </summary>
    /// <remarks>
    /// A missing or unusable value is <see cref="DefaultEditorUrl"/>. Never throws.
    /// </remarks>
    /// <returns>The address.</returns>
    public Uri LoadEditorUrl()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath);
            return key?.GetValue(EditorUrlName) is string text && TryParseEditorUrl(text, out var url) ? url! : DefaultEditorUrl;
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("editor address load failed: {0}", ex.Message);
            return DefaultEditorUrl;
        }
    }

    /// <summary>
    /// Saves the web config tool's address.
    /// </summary>
    /// <remarks>
    /// Best effort: a failure is traced.
    /// </remarks>
    /// <param name="url">The address.</param>
    public void SaveEditorUrl(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(EditorUrlName, url.AbsoluteUri, RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("editor address save failed: {0}", ex.Message);
        }
    }

    /// <summary>
    /// Saves the shortcut sheet hotkey.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> writes an empty string, which loads as off. Best effort: a failure is traced.
    /// </remarks>
    /// <param name="hotkey">The hotkey, or <see langword="null"/> for off.</param>
    public void SaveShortcut(Hotkey? hotkey)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath);
            key.SetValue(FlyoutShortcutName, hotkey?.ToString() ?? "", RegistryValueKind.String);
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException or ArgumentException)
        {
            Trace.TraceWarning("shortcut save failed: {0}", ex.Message);
        }
    }
}
