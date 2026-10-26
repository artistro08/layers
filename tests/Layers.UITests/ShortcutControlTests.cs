using Layers.Core.Logic;
using Layers.UI.Controls;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

[TestClass]
public sealed class ShortcutControlTests
{
    [TestMethod]
    public Task Control_ShowsChipsForSetting() => UiHost.RunAsync(async () =>
    {
        var control = new ShortcutControl { HotkeySettings = HotkeySettings.FromHotkey(new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Shift, 'L')) };
        var host    = UiTree.ShowInWindow(control);
        await UiHost.Settle();

        Assert.AreEqual(3, control.FindDescendants<KeyVisual>().Count());
        Assert.IsNull(control.FindDescendant<TextBlock>(t => t.Text == "Assign shortcut" && t.Visibility == Microsoft.UI.Xaml.Visibility.Visible && t.ActualHeight > 0));
        host.Close();
    });

    [TestMethod]
    public Task Control_EmptyShowsAssignPlaceholder() => UiHost.RunAsync(async () =>
    {
        var control = new ShortcutControl { HotkeySettings = new HotkeySettings() };
        var host    = UiTree.ShowInWindow(control);
        await UiHost.Settle();

        Assert.IsNotNull(control.FindDescendant<TextBlock>(t => t.Text == "Assign shortcut"));
        Assert.AreEqual(0, control.FindDescendants<KeyVisual>().Count());
        host.Close();
    });

    [TestMethod]
    public Task Control_ConflictDrawsWarningChips() => UiHost.RunAsync(async () =>
    {
        var control = new ShortcutControl { HotkeySettings = HotkeySettings.FromHotkey(Hotkey.Default), HasConflict = true };
        var host    = UiTree.ShowInWindow(control);
        await UiHost.Settle();

        Assert.IsTrue(control.FindDescendants<KeyVisual>().All(k => k.State == KeyVisualState.Warning));
        host.Close();
    });
}
