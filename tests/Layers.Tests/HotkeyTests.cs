using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HotkeyTests
{
    // =========================================================================
    // HOTKEY
    // =========================================================================

    [TestMethod]
    public void Default_IsWinShiftL()
    {
        Assert.AreEqual("Shift+Win+L", Hotkey.Default.ToString());
        Assert.AreEqual(0x4C, Hotkey.Default.Key);
    }

    [TestMethod]
    [DataRow("Ctrl+Alt+J", "Ctrl+Alt+J", 0x4A)]
    [DataRow("win+shift+l", "Shift+Win+L", 0x4C)]
    [DataRow("Control+Windows+F12", "Ctrl+Win+F12", 0x7B)]
    [DataRow("Alt+Space", "Alt+Space", 0x20)]
    [DataRow("Ctrl+Del", "Ctrl+Delete", 0x2E)]
    [DataRow("Win+7", "Win+7", 0x37)]
    public void Parse_AcceptsAnyOrderAndCase(string text, string canonical, int key)
    {
        Assert.IsTrue(Hotkey.TryParse(text, out var hotkey));
        Assert.AreEqual(canonical, hotkey.ToString());
        Assert.AreEqual(key, hotkey.Key);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("J")]
    [DataRow("Shift+J")]
    [DataRow("Ctrl+Ctrl+J")]
    [DataRow("Ctrl+J+K")]
    [DataRow("Ctrl+F25")]
    [DataRow("Ctrl+Tab")]
    [DataRow("Ctrl+")]
    public void Parse_RejectsBadText(string text) => Assert.IsFalse(Hotkey.TryParse(text, out _));

    [TestMethod]
    public void Create_RequiresAnAnchorModifierAndAKnownKey()
    {
        Assert.IsTrue(Hotkey.TryCreate(HotkeyModifiers.Win, 0x41, out _));
        Assert.IsFalse(Hotkey.TryCreate(HotkeyModifiers.Shift, 0x41, out _));
        Assert.IsFalse(Hotkey.TryCreate(HotkeyModifiers.Ctrl, 0x09, out _));
    }

    [TestMethod]
    public void ToString_RoundTrips()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Win, 0x20);

        Assert.IsTrue(Hotkey.TryParse(hotkey.ToString(), out var parsed));
        Assert.AreEqual(hotkey, parsed);
    }

    // =========================================================================
    // HOTKEY SETTINGS
    // =========================================================================

    [TestMethod]
    public void Settings_RoundTripThroughHotkey()
    {
        var settings = HotkeySettings.FromHotkey(Hotkey.Default);

        Assert.IsTrue(settings.Win && settings.Shift && !settings.Ctrl && !settings.Alt);
        Assert.AreEqual(Hotkey.Default, settings.ToHotkey());
        Assert.AreEqual("Win+Shift+L", settings.ToString());
    }

    [TestMethod]
    [DataRow("Win+L")]
    [DataRow("Alt+F4")]
    public void Reserved_AreRefused(string text)
    {
        var parsed = Hotkey.TryParse(text, out var hotkey);

        Assert.IsFalse(parsed && HotkeySettings.FromHotkey(hotkey).IsValid(), $"{text} must be rejected before registration");
    }

    [TestMethod]
    public void Settings_ShiftAloneIsInvalidWithAReason()
    {
        var settings = new HotkeySettings(win: false, ctrl: false, alt: false, shift: true, code: 0x4A);

        Assert.IsFalse(settings.IsValid());
        Assert.IsNull(settings.ToHotkey());
        StringAssert.StartsWith(settings.InvalidReason(), "Invalid shortcut.");
    }

    [TestMethod]
    public void Settings_TabIsAccessibleNotAShortcut()
    {
        Assert.IsTrue(new HotkeySettings(false, false, false, true, 0x09).IsAccessibleShortcut());
        Assert.IsFalse(new HotkeySettings(false, false, false, true, 0x09).IsValid());
    }

    [TestMethod]
    public void Settings_KeysListDrawsWinAndShiftAsGlyphs()
    {
        var keys = new HotkeySettings(win: true, ctrl: true, alt: false, shift: true, code: 0x4C).GetKeysList();

        CollectionAssert.AreEqual(new object[] { 92, "Ctrl", 16, "L" }, keys);
    }

    [TestMethod]
    public void Settings_EmptyAndAltGr()
    {
        Assert.IsTrue(new HotkeySettings().IsEmpty());
        Assert.IsNull(new HotkeySettings().InvalidReason());
        Assert.IsTrue(new HotkeySettings(false, true, true, false, 0x4A).IsAltGrRisk);
        Assert.IsFalse(new HotkeySettings(true, true, true, false, 0x4A).IsAltGrRisk);
    }
}
