using System.Security;
using Microsoft.Win32;

namespace Layers.Core.Services;

/// <summary>
/// What Windhawk (a Windows customization tool) has changed that the app needs to know about.
/// </summary>
/// <remarks>
/// Windhawk lists its installed mods as keys under <c>HKLM\SOFTWARE\Windhawk\Engine\Mods</c>, named by mod ID, each
/// with a <c>Disabled</c> value. Read on every call, so turning a mod on or off applies without restarting the app.
/// Ported from MagicBuds.
/// </remarks>
/// <seealso href="https://windhawk.net/mods/taskbar-multi-tray"/>
public static class Windhawk
{
    private const string ModsKey = @"SOFTWARE\Windhawk\Engine\Mods";

    // The Taskbar Multi Tray Mod, Or A Fork Of It ("local@taskbar-multi-tray-fork")
    private const string MultiTrayModId = "taskbar-multi-tray";

    /// <summary>
    /// Tells whether a turned-on Taskbar Multi Tray mod puts the tray, and so the app's icon, on every taskbar.
    /// </summary>
    /// <remarks>
    /// The icon's position then only says where one of them is, so the flyout follows the cursor's monitor instead.
    /// Never throws: an unreadable key counts as no mod.
    /// </remarks>
    /// <param name="modsKeyPath">The mods key. Tests pass a throwaway key.</param>
    /// <param name="hive">The hive the key lives under. Tests pass <c>CurrentUser</c>.</param>
    /// <returns><see langword="true"/> when the mod is installed and not disabled.</returns>
    public static bool HasTrayOnEveryTaskbar(string modsKeyPath = ModsKey, RegistryKey? hive = null)
    {
        try
        {
            using var mods = (hive ?? Registry.LocalMachine).OpenSubKey(modsKeyPath);
            if (mods is null)
            {
                return false;
            }

            foreach (var id in mods.GetSubKeyNames().Where(id => id.Contains(MultiTrayModId, StringComparison.OrdinalIgnoreCase)))
            {
                using var mod = mods.OpenSubKey(id);
                if (mod?.GetValue("Disabled") is not 1)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            // Can't Read It: Behave As Without The Mod
        }

        return false;
    }
}
