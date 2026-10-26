using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Layers.UI;
using Layers.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace Layers.UITests;

[TestClass]
public sealed class ShortcutSheetViewTests
{
    private static List<IReadOnlyList<IReadOnlyList<uint>>> EmptyMacros() =>
        Enumerable.Repeat<IReadOnlyList<IReadOnlyList<uint>>>([], Protocol.MacroSlots).ToList();

    // Layers 0 And 2
    private static DeviceConfig TwoLayerConfig() => new(0,
    [
        new Mapping(0x00070004, 0x000C00E9, 0b0101, false, false, false),
        new Mapping(0x00070005, 0xFFF10002, 0b0001, true, false, true),
    ], EmptyMacros());

    // One Mapping To Macro 3 With Steps [Left Control, C] [V]
    private static DeviceConfig MacroConfig()
    {
        var macros = EmptyMacros();
        macros[2]  = [[0x000700E0u, 0x00070006u], [0x00070019u]];
        return new DeviceConfig(0, [new Mapping(0x00070004, 0xFFF20003, 1, false, false, false)], macros);
    }

    private static ShortcutSheetViewModel Connected(DeviceConfig config, byte layers = 0b100) =>
        new() { Config = config, State = new DeviceState(DeviceStatus.Connected, new LayerMask(layers)) };

    // The Active Pills That Show: Every Section Carries One, Hidden Unless Its Layer Is Active
    private static List<Border> VisiblePills(ShortcutSheetView view) =>
        view.FindDescendants<Border>(b => b.Tag is "Active" && b.Visibility == Visibility.Visible).ToList();

    [TestMethod]
    public Task Sections_PerLayerWithAKeyBlockEach() => UiHost.RunAsync(async () =>
    {
        var view = new ShortcutSheetView(Connected(TwoLayerConfig()));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        // Layer 0 Has A And B, Layer 2 Has A; Layer 2 Is Active
        Assert.HasCount(2, view.SectionElements);
        Assert.IsNotNull(view.SectionElements[0].FindDescendant<TextBlock>(t => t.Text == "Layer 0"));
        Assert.IsNotNull(view.SectionElements[1].FindDescendant<TextBlock>(t => t.Text == "Layer 2"));
        Assert.HasCount(1, VisiblePills(view));
        Assert.AreEqual(3, view.VisibleRowCount, "one block per key per layer");
        Assert.AreEqual(Visibility.Collapsed, view.Placeholder.Visibility);
        host.Close();
    });

    [TestMethod]
    public Task Block_StacksEverythingTheKeyDoes() => UiHost.RunAsync(async () =>
    {
        // Right Button: Tap Fires Macro 13, Hold Switches To Layer 2, Both On Layer 0
        var config = new DeviceConfig(0,
        [
            new Mapping(0x00090002, 0xFFF2000D, 1, false, true, false),
            new Mapping(0x00090002, 0xFFF10002, 1, false, false, true),
        ], EmptyMacros());
        var view = new ShortcutSheetView(Connected(config, 0b1));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.AreEqual(1, view.VisibleRowCount, "one block for the one key");
        Assert.AreEqual(1, view.FindDescendants<TextBlock>(t => t.Text == "Right click").Count(), "the key shows once");
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Tap"));
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Hold"));
        host.Close();
    });

    [TestMethod]
    public Task OneStepMacro_ShowsTheResultNotTheMacro() => UiHost.RunAsync(async () =>
    {
        // Right Button: Tap Fires Macro 13, Whose One Step Is A Right Click; Macro 2's One Step Is Win+Shift+R
        var macros = EmptyMacros();
        macros[12] = [[0x00090002u]];
        macros[1]  = [[0x000700E3u, 0x000700E1u, 0x00070015u]];
        var config = new DeviceConfig(0,
        [
            new Mapping(0x00090002, 0xFFF2000D, 1, false, true, false),
            new Mapping(0x00090006, 0xFFF20002, 1, false, false, true),
        ], macros);
        var view = new ShortcutSheetView(Connected(config, 0b1));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        // No Macro Chips, No Arrows: Each Tap Or Hold Shows What It Sends
        Assert.IsNull(view.FindDescendant<TextBlock>(t => t.Text.StartsWith("Macro", StringComparison.Ordinal)));
        Assert.AreEqual(0, view.FindDescendants<FontIcon>(f => f.Glyph == "").Count());
        Assert.AreEqual(2, view.FindDescendants<TextBlock>(t => t.Text == "Right click").Count(), "the key, and what its tap sends");
        Assert.AreEqual(2, view.FindDescendants<FontIcon>(f => f.Glyph == "").Count(), "Win + Shift + R");

        // The Macro Is Still Named On Hover, And Still Found By Search
        Assert.IsTrue(view.FindDescendants<FrameworkElement>(e => ToolTipService.GetToolTip(e) is "Macro 13").Any());
        view.SearchBox.Text = "macro 2";
        await UiHost.Settle();
        Assert.AreEqual(1, view.VisibleRowCount);
        host.Close();
    });

    [TestMethod]
    public Task Meaning_ReplacesTheExpressionWhereverItAppears() => UiHost.RunAsync(async () =>
    {
        // Expression 7 Drives Back; The User Says What It Means
        var config = new DeviceConfig(0, [new Mapping(0xFFF30007, 0x00090004, 1, false, false, false)], EmptyMacros());
        var model  = Connected(config, 0b1);
        model.Meanings = new Dictionary<int, string> { [7] = "Tap {Back} or swipe left" };
        var view = new ShortcutSheetView(model);
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.IsNull(view.FindDescendant<KeyVisual>(k => k.Content is "Expression 7"), "the name gives way to the meaning");
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Tap"));
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "or swipe left"));
        Assert.AreEqual(2, view.FindDescendants<TextBlock>(t => t.Text == "Back").Count(), "the key in the meaning, and the output");
        Assert.IsTrue(view.FindDescendants<FrameworkElement>(e => ToolTipService.GetToolTip(e) is "Expression 7").Any());

        // Search Finds It By The User's Words
        view.SearchBox.Text = "swipe";
        await UiHost.Settle();
        Assert.AreEqual(1, view.VisibleRowCount);
        host.Close();
    });

    [TestMethod]
    public Task Rows_WrapFlagsAroundTheOutput() => UiHost.RunAsync(async () =>
    {
        var view = new ShortcutSheetView(Connected(TwoLayerConfig(), 0b1));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Volume up"));
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Sticky · Hold"));
        Assert.IsNull(view.FindDescendant<TextBlock>(t => t.Text.Contains("Tap", StringComparison.Ordinal)));
        Assert.AreEqual(0, view.FindDescendants<FontIcon>(f => f.Glyph == "\uE72A").Count(), "no arrows without a macro");
        host.Close();
    });

    [TestMethod]
    public Task Search_FiltersBlocksAndHidesEmptySections() => UiHost.RunAsync(async () =>
    {
        var view = new ShortcutSheetView(Connected(TwoLayerConfig(), 0b1));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        view.SearchBox.Text = "sticky";
        await UiHost.Settle();

        // Only B Is Sticky, And It Lives On Layer 0, So The Layer 2 Section Hides
        Assert.AreEqual(Visibility.Visible, view.SectionElements[0].Visibility);
        Assert.AreEqual(Visibility.Collapsed, view.SectionElements[1].Visibility);
        Assert.AreEqual(1, view.VisibleRowCount);

        view.SearchBox.Text = "";
        await UiHost.Settle();
        Assert.AreEqual(3, view.VisibleRowCount);
        Assert.AreEqual(Visibility.Visible, view.SectionElements[1].Visibility);
        host.Close();
    });

    [TestMethod]
    public Task MacroRow_ShowsCaptionAndEveryStep() => UiHost.RunAsync(async () =>
    {
        var view = new ShortcutSheetView(Connected(MacroConfig(), 0b1));
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Macro 3"));
        Assert.AreEqual(4, view.FindDescendants<KeyVisual>().Count());   // A, Left Control, C, V
        Assert.AreEqual(1, view.FindDescendants<FontIcon>(f => f.Glyph == "").Count());
        host.Close();
    });

    [TestMethod]
    public Task EmptyMacro_SaysEmpty() => UiHost.RunAsync(async () =>
    {
        var config = new DeviceConfig(0, [new Mapping(0x00070004, 0xFFF20001, 1, false, false, false)], EmptyMacros());
        var view   = new ShortcutSheetView(Connected(config, 0b1));
        var host   = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Macro 1"));
        Assert.IsNotNull(view.FindDescendant<TextBlock>(t => t.Text == "Empty"));
        host.Close();
    });

    [TestMethod]
    public Task Disconnected_ShowsPlaceholderAndFooterWorks() => UiHost.RunAsync(async () =>
    {
        var view = new ShortcutSheetView(new ShortcutSheetViewModel { State = DeviceState.Initial });
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.AreEqual("Plug in your HID Remapper", view.Placeholder.Text);
        Assert.AreEqual(Visibility.Visible, view.Placeholder.Visibility);
        Assert.AreEqual("Disconnected", view.StatusLabel.Text);

        var edit     = false;
        var settings = false;
        view.EditRequested     += (_, _) => edit = true;
        view.SettingsRequested += (_, _) => settings = true;
        UiTree.Invoke(view.EditButton);
        UiTree.Invoke(view.SettingsButton);

        Assert.IsTrue(edit && settings);
        Assert.AreEqual("Edit in browser", AutomationProperties.GetName(view.EditButton));
        Assert.AreEqual("Settings", AutomationProperties.GetName(view.SettingsButton));
        host.Close();
    });

    [TestMethod]
    public Task Connect_WhileOpenSwapsInList() => UiHost.RunAsync(async () =>
    {
        var model = new ShortcutSheetViewModel { State = DeviceState.Initial };
        var view  = new ShortcutSheetView(model);
        var host  = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        model.Config = TwoLayerConfig();
        model.State  = new DeviceState(DeviceStatus.Connected, LayerMask.Base);
        await UiHost.Settle();

        Assert.AreEqual(Visibility.Collapsed, view.Placeholder.Visibility);
        Assert.HasCount(2, view.SectionElements);
        Assert.AreEqual("Connected", view.StatusLabel.Text);
        host.Close();
    });

    [TestMethod]
    public Task LayerSwitch_MovesTheActivePill() => UiHost.RunAsync(async () =>
    {
        var model = Connected(TwoLayerConfig(), 0b1);
        var view  = new ShortcutSheetView(model);
        var host  = UiTree.ShowInWindow(view);
        await UiHost.Settle();
        var sections = view.SectionElements;
        Assert.IsTrue(UiTree.Descendants(sections[0]).Contains(VisiblePills(view).Single()), "layer 0 active");

        model.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b100));
        await UiHost.Settle();

        // The Pill Moved To Layer 2, And The Sections Were Not Rebuilt
        Assert.IsTrue(UiTree.Descendants(sections[1]).Contains(VisiblePills(view).Single()), "layer 2 active");
        CollectionAssert.AreEqual(sections.ToList(), view.SectionElements.ToList(), "a layer switch keeps the built sections");
        host.Close();
    });

    [TestMethod]
    public Task RefreshFailed_ShowsNote() => UiHost.RunAsync(async () =>
    {
        var model = Connected(TwoLayerConfig());
        model.RefreshFailed = true;
        var view = new ShortcutSheetView(model);
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.AreEqual("Couldn't refresh", view.RefreshNote.Text);
        Assert.AreEqual(Visibility.Visible, view.RefreshNote.Visibility);

        model.RefreshFailed = false;
        await UiHost.Settle();
        Assert.AreEqual(Visibility.Collapsed, view.RefreshNote.Visibility);
        host.Close();
    });

    [TestMethod]
    public Task Escape_RaisesClose() => UiHost.RunAsync(async () =>
    {
        var view   = new ShortcutSheetView(Connected(TwoLayerConfig()));
        var closed = false;
        view.CloseRequested += (_, _) => closed = true;
        var host = UiTree.ShowInWindow(view);
        await UiHost.Settle();

        Assert.IsTrue(view.KeyboardAccelerators.Any(a => a.Key == VirtualKey.Escape), "no Escape accelerator");
        view.RequestClose();

        Assert.IsTrue(closed);
        host.Close();
    });
}
