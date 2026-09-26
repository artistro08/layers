using Layers.Core.Logic;
using Layers.Core.Services;
using Microsoft.Win32;

namespace Layers.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    [TestMethod]
    public void DefaultKeyPath_MatchesRust()
    {
        // MSTEST0032: both sides resolve to the same compile-time literal, which is the point of this test
#pragma warning disable MSTEST0032
        Assert.AreEqual(@"Software\Layers", SettingsStore.DefaultKeyPath);
#pragma warning restore MSTEST0032
    }

    [TestMethod]
    public void Load_MissingKeyGivesDefaults()
    {
        using var temp = new TempRegistryKey();

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void SaveThenLoad_RoundTrips()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);
        var settings   = new HudSettings(false, 0b1010_0101, 2300);

        store.Save(settings);

        Assert.AreEqual(settings, store.Load());
        Assert.AreEqual(0, temp.Get("HudEnabled"));
        Assert.AreEqual(0b1010_0101, temp.Get("HudSuppressedLayers"));
        Assert.AreEqual(2300, temp.Get("HudHoldMs"));
    }

    [TestMethod]
    [DataRow(100, 500)]
    [DataRow(-5, 500)]
    [DataRow(99_999, 5000)]
    [DataRow(500, 500)]
    [DataRow(5000, 5000)]
    public void Load_ClampsHold(int stored, int expected)
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudHoldMs", stored, RegistryValueKind.DWord);

        Assert.AreEqual(expected, new SettingsStore(temp.Path).Load().HudHoldMs);
    }

    [TestMethod]
    public void Load_MissingHoldIsDefault()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudEnabled", 0, RegistryValueKind.DWord);

        Assert.AreEqual(1700, new SettingsStore(temp.Path).Load().HudHoldMs);
    }

    [TestMethod]
    public void Load_MasksToEightBits()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudSuppressedLayers", 0x1_0102, RegistryValueKind.DWord);

        Assert.AreEqual((byte)0x02, new SettingsStore(temp.Path).Load().HudSuppressedLayers);
    }

    [TestMethod]
    public void Load_WrongTypeFallsBackToDefault()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudEnabled", "0", RegistryValueKind.String);
        temp.Set("HudSuppressedLayers", new byte[] { 1 }, RegistryValueKind.Binary);
        temp.Set("HudHoldMs", "2000", RegistryValueKind.String);

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void Load_IgnoresStaleSeenLayers()
    {
        using var temp = new TempRegistryKey();
        temp.Set("SeenLayers", 0xFF, RegistryValueKind.DWord);
        temp.Set("HudEnabled", 1, RegistryValueKind.DWord);

        Assert.AreEqual(HudSettings.Default, new SettingsStore(temp.Path).Load());
    }

    [TestMethod]
    public void Save_FailureDoesNotThrow()
    {
        // A key name over 255 characters can't be created; CreateSubKey throws ArgumentException.
        // Rooted under Software\LayersTests\ (never Software\Layers) so this can never touch real settings,
        // even if the invalid-name check somehow had a side effect before throwing.
        var store = new SettingsStore(@"Software\LayersTests\" + new string('a', 300));

        store.Save(HudSettings.Default);
    }
}
