using Layers.Core.Logic;
using Layers.Core.Services;
using Microsoft.Win32;

namespace Layers.Tests;

[TestClass]
public sealed class SettingsStoreTests
{
    // =========================================================================
    // SHORTCUT
    // =========================================================================

    [TestMethod]
    public void Shortcut_MissingIsDefault()
    {
        using var temp = new TempRegistryKey();

        Assert.AreEqual(Hotkey.Default, new SettingsStore(temp.Path).LoadShortcut());
    }

    [TestMethod]
    public void Shortcut_EmptyIsDisabled()
    {
        using var temp = new TempRegistryKey();
        temp.Set("FlyoutShortcut", "", RegistryValueKind.String);

        Assert.IsNull(new SettingsStore(temp.Path).LoadShortcut());
    }

    [TestMethod]
    public void Shortcut_GarbageIsDisabled()
    {
        using var temp = new TempRegistryKey();
        temp.Set("FlyoutShortcut", "Ctrl+Ctrl+Q", RegistryValueKind.String);

        Assert.IsNull(new SettingsStore(temp.Path).LoadShortcut());
    }

    [TestMethod]
    public void Shortcut_WrongKindIsDisabled()
    {
        using var temp = new TempRegistryKey();
        temp.Set("FlyoutShortcut", 7, RegistryValueKind.DWord);

        Assert.IsNull(new SettingsStore(temp.Path).LoadShortcut());
    }

    [TestMethod]
    public void Shortcut_RoundTrips()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);

        store.SaveShortcut(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'J'));

        Assert.AreEqual("Ctrl+Alt+J", store.LoadShortcut()!.ToString());
        Assert.AreEqual("Ctrl+Alt+J", temp.Get("FlyoutShortcut"));
    }

    [TestMethod]
    public void Shortcut_SaveNullWritesEmpty()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);

        store.SaveShortcut(null);

        Assert.AreEqual("", temp.Get("FlyoutShortcut"));
        Assert.IsNull(store.LoadShortcut());
    }

    // =========================================================================
    // EXPRESSION MEANINGS
    // =========================================================================

    [TestMethod]
    public void Meanings_MissingAreEmpty()
    {
        using var temp = new TempRegistryKey();

        Assert.IsEmpty(new SettingsStore(temp.Path).LoadMeanings());
    }

    [TestMethod]
    public void Meanings_RoundTripPerSlot()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);

        store.SaveMeaning(7, "Tap {Back} or swipe left");
        store.SaveMeaning(2, "Scroll");

        var meanings = store.LoadMeanings();
        Assert.AreEqual("Tap {Back} or swipe left", meanings[7]);
        Assert.AreEqual("Scroll", meanings[2]);
        Assert.HasCount(2, meanings);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  ")]
    public void Meanings_BlankRemoves(string? text)
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);
        store.SaveMeaning(3, "Something");

        store.SaveMeaning(3, text);

        Assert.IsFalse(store.LoadMeanings().ContainsKey(3));
    }

    [TestMethod]
    public void Meanings_IgnoreStrayValues()
    {
        using var temp = new TempRegistryKey();
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(temp.Path + @"\ExpressionMeanings"))
        {
            key.SetValue("9", "out of range", RegistryValueKind.String);
            key.SetValue("x", "not a slot", RegistryValueKind.String);
            key.SetValue("4", 12, RegistryValueKind.DWord);
            key.SetValue("5", "kept", RegistryValueKind.String);
        }

        var meanings = new SettingsStore(temp.Path).LoadMeanings();

        CollectionAssert.AreEqual(new[] { 5 }, meanings.Keys.ToArray());
    }

    // =========================================================================
    // EDITOR ADDRESS
    // =========================================================================

    [TestMethod]
    public void EditorUrl_MissingIsTheDefault()
    {
        using var temp = new TempRegistryKey();

        Assert.AreEqual(new Uri("https://remapper.org/config"), new SettingsStore(temp.Path).LoadEditorUrl());
    }

    [TestMethod]
    public void EditorUrl_RoundTrips()
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);

        store.SaveEditorUrl(new Uri("http://localhost:8000/"));

        Assert.AreEqual(new Uri("http://localhost:8000/"), store.LoadEditorUrl());
    }

    [TestMethod]
    [DataRow("not a url")]
    [DataRow("file:///C:/x.html")]
    [DataRow("")]
    public void EditorUrl_BadValueIsTheDefault(string value)
    {
        using var temp = new TempRegistryKey();
        temp.Set("EditorUrl", value, RegistryValueKind.String);

        Assert.AreEqual(SettingsStore.DefaultEditorUrl, new SettingsStore(temp.Path).LoadEditorUrl());
    }

    [TestMethod]
    [DataRow("https://remapper.org/config", true)]
    [DataRow("remapper.org/config", true)]
    [DataRow("http://localhost:8000", true)]
    [DataRow("localhost:8080/config", true)]
    [DataRow("192.168.1.5:8080", true)]
    [DataRow("ftp://x", false)]
    [DataRow("javascript:alert(1)", false)]
    [DataRow("  ", false)]
    public void EditorUrl_ParsesWhatAPersonTypes(string text, bool ok)
    {
        Assert.AreEqual(ok, SettingsStore.TryParseEditorUrl(text, out var url));
        if (ok)
        {
            Assert.IsTrue(url!.Scheme is "https" or "http");
        }
    }

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
        var settings   = new HudSettings(false, 0b1010_0101, 2300, HudMonitorMode.FocusedWindow);

        store.Save(settings);

        Assert.AreEqual(settings, store.Load());
        Assert.AreEqual(0, temp.Get("HudEnabled"));
        Assert.AreEqual(0b1010_0101, temp.Get("HudSuppressedLayers"));
        Assert.AreEqual(2300, temp.Get("HudHoldMs"));
        Assert.AreEqual(2, temp.Get("HudMonitor"));
    }

    [TestMethod]
    [DataRow(HudMonitorMode.Primary)]
    [DataRow(HudMonitorMode.Cursor)]
    [DataRow(HudMonitorMode.FocusedWindow)]
    public void HudMonitor_RoundTrips(HudMonitorMode mode)
    {
        using var temp = new TempRegistryKey();
        var store      = new SettingsStore(temp.Path);

        store.Save(HudSettings.Default with { HudMonitor = mode });

        Assert.AreEqual(mode, store.Load().HudMonitor);
        Assert.AreEqual((int)mode, temp.Get("HudMonitor"));
    }

    [TestMethod]
    public void HudMonitor_DefaultsToPrimary()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudEnabled", 1, RegistryValueKind.DWord);

        Assert.AreEqual(HudMonitorMode.Primary, HudSettings.Default.HudMonitor);
        Assert.AreEqual(HudMonitorMode.Primary, new SettingsStore(temp.Path).Load().HudMonitor);
    }

    [TestMethod]
    [DataRow(3)]
    [DataRow(-1)]
    [DataRow(int.MaxValue)]
    public void HudMonitor_OutOfRangeIsPrimary(int stored)
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudMonitor", stored, RegistryValueKind.DWord);

        Assert.AreEqual(HudMonitorMode.Primary, new SettingsStore(temp.Path).Load().HudMonitor);
    }

    [TestMethod]
    public void HudMonitor_WrongTypeIsPrimary()
    {
        using var temp = new TempRegistryKey();
        temp.Set("HudMonitor", "1", RegistryValueKind.String);

        Assert.AreEqual(HudMonitorMode.Primary, new SettingsStore(temp.Path).Load().HudMonitor);
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
