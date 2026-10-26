using Layers.Core.Services;

namespace Layers.Tests;

[TestClass]
public sealed class WindhawkTests
{
    [TestMethod]
    public void NoWindhawk_IsFalse() => Assert.IsFalse(Windhawk.HasTrayOnEveryTaskbar(@"Software\LayersTests\NoSuchKey"));

    [TestMethod]
    public void EnabledMod_IsTrue()
    {
        using var temp = new TempRegistryKey();
        temp.Set("unused", 0, Microsoft.Win32.RegistryValueKind.DWord);
        using (var mods = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(temp.Path + @"\local@taskbar-multi-tray"))
        {
            mods.SetValue("Disabled", 0, Microsoft.Win32.RegistryValueKind.DWord);
        }

        Assert.IsTrue(Windhawk.HasTrayOnEveryTaskbar(temp.Path, Microsoft.Win32.Registry.CurrentUser));
    }

    [TestMethod]
    public void DisabledMod_IsFalse()
    {
        using var temp = new TempRegistryKey();
        using (var mods = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(temp.Path + @"\taskbar-multi-tray"))
        {
            mods.SetValue("Disabled", 1, Microsoft.Win32.RegistryValueKind.DWord);
        }

        Assert.IsFalse(Windhawk.HasTrayOnEveryTaskbar(temp.Path, Microsoft.Win32.Registry.CurrentUser));
    }
}
