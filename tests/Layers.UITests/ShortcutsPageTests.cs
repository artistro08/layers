using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;
using Layers.Tests;
using Layers.UI;
using Layers.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

[TestClass]
public sealed class ShortcutsPageTests : IDisposable
{
    private static readonly int[] UsedSlots     = [1, 7];
    private static readonly string[] VolumeKeys = ["Volume up", "Volume down"];

    private TempRegistryKey _key = null!;
    private SettingsStore _store = null!;

    [TestInitialize]
    public void Setup()
    {
        _key   = new TempRegistryKey();
        _store = new SettingsStore(_key.Path);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await UiHost.RunAsync(() =>
        {
            SettingsWindow.Current?.Close();
            return Task.CompletedTask;
        });
    }

    public void Dispose() => _key.Dispose();

    private SettingsWindow Open(IReadOnlyList<ExpressionUse>? uses = null) =>
        SettingsWindow.Open(() => new SettingsViewModel(_store, new FakeStartup(new StartupState(true, true, null))) { ExpressionUses = uses ?? [] });

    private static T Named<T>(SettingsWindow window, string name) => (T)((FrameworkElement)window.Content).FindName(name);

    private static async Task<ShortcutsPage> GoToShortcuts(SettingsWindow window)
    {
        var nav  = Named<NavigationView>(window, "Nav");
        var item = ((FrameworkElement)window.Content).FindDescendants<NavigationViewItem>().Single(i => (string)i.Content == "Shortcuts");
        nav.SelectedItem = item;
        for (var i = 0; i < 20 && Named<Frame>(window, "ContentFrame").Content is not ShortcutsPage; i++)
        {
            await UiHost.Settle();
        }

        await UiHost.Settle();
        return (ShortcutsPage)Named<Frame>(window, "ContentFrame").Content;
    }

    [TestMethod]
    public Task ShortcutsPage_ShowsPickerWithSavedShortcut() => UiHost.RunAsync(async () =>
    {
        _store.SaveShortcut(new Hotkey(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 'J'));
        var window = Open();
        await UiHost.Settle();

        var page = await GoToShortcuts(window);

        Assert.AreEqual("Ctrl+Alt+J", page.Picker.HotkeySettings!.ToHotkey()!.ToString());
        Assert.AreEqual(3, page.Picker.FindDescendants<KeyVisual>().Count());
        Assert.IsFalse(page.Note.IsOpen);
    });

    [TestMethod]
    public Task ShortcutsPage_DefaultIsWinShiftL() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var page = await GoToShortcuts(window);

        Assert.AreEqual(Hotkey.Default, page.Picker.HotkeySettings!.ToHotkey());
    });

    [TestMethod]
    public Task ShortcutsPage_TakenShortcutShowsWarning() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();
        window.Model.ShortcutTaken = true;

        var page = await GoToShortcuts(window);

        Assert.IsTrue(page.Picker.HasConflict);
        Assert.IsTrue(page.Note.IsOpen);
        Assert.AreEqual(Visibility.Visible, page.Note.Visibility);
    });

    [TestMethod]
    public Task ShortcutsPage_NoteShowsAndClears() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();
        var page = await GoToShortcuts(window);

        window.Model.ShortcutNote = "That shortcut is taken, so the previous one was kept.";
        await UiHost.Settle();
        Assert.IsTrue(page.Note.IsOpen);
        StringAssert.Contains(page.Note.Message, "previous one was kept");

        window.Model.ShortcutNote = null;
        await UiHost.Settle();
        Assert.IsFalse(page.Note.IsOpen);
    });

    [TestMethod]
    public Task EditorUrl_EditsSaveAndResetRestores() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();
        var page = await GoToShortcuts(window);

        Assert.AreEqual("https://remapper.org/config", page.EditorUrlBox.Text);

        page.EditorUrlBox.Text = "not a url";
        await UiHost.Settle();
        Assert.AreEqual(Visibility.Visible, page.EditorUrlError.Visibility);

        page.EditorUrlBox.Text = "remapper.org/beta";
        await UiHost.Settle();
        Assert.AreEqual(Visibility.Collapsed, page.EditorUrlError.Visibility);
        Assert.AreEqual(new Uri("https://remapper.org/beta"), _store.LoadEditorUrl());

        UiTree.Invoke(page.EditorUrlReset);
        await UiHost.Settle();
        Assert.AreEqual("https://remapper.org/config", page.EditorUrlBox.Text);
        Assert.AreEqual(SettingsStore.DefaultEditorUrl, _store.LoadEditorUrl());
    });

    [TestMethod]
    public Task Expressions_OneCardPerUsedExpression() => UiHost.RunAsync(async () =>
    {
        var window = Open([new ExpressionUse(1, ["Forward"]), new ExpressionUse(7, ["Macro 14"])]);
        await UiHost.Settle();

        var page = await GoToShortcuts(window);

        CollectionAssert.AreEqual(UsedSlots, page.ExpressionCards.Select(c => c.Slot).ToArray());
        Assert.IsNotNull(page.FindDescendant<TextBlock>(t => t.Text == "Expression 7"));
        Assert.IsNotNull(page.FindDescendant<TextBlock>(t => t.Text == "Drives Forward"));
        Assert.AreEqual(Visibility.Collapsed, page.ExpressionsNote.Visibility);
    });

    [TestMethod]
    public Task Expressions_NoneShowsTheNote() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var page = await GoToShortcuts(window);

        Assert.IsEmpty(page.ExpressionCards);
        Assert.AreEqual(Visibility.Visible, page.ExpressionsNote.Visibility);
    });

    [TestMethod]
    public Task Expressions_TypingSavesAndPreviews() => UiHost.RunAsync(async () =>
    {
        var window = Open([new ExpressionUse(7, ["Macro 14"])]);
        await UiHost.Settle();
        var page = await GoToShortcuts(window);
        var card = page.ExpressionCards.Single();

        card.Box.Text = "Tap {Back} or swipe left";
        await UiHost.Settle();

        // The Save Waits For A Pause In Typing
        await UiHost.Settle();
        await UiHost.Settle();
        Assert.AreEqual("Tap {Back} or swipe left", _store.LoadMeanings()[7]);
        Assert.IsNotNull(card.Preview.FindDescendant<KeyVisual>(k => k.Content is "Back"), "the preview draws the key as a chip");
    });

    [TestMethod]
    public Task Expressions_PickedKeyIsInsertedAtTheCaret() => UiHost.RunAsync(async () =>
    {
        var window = Open([new ExpressionUse(7, ["Macro 14"])]);
        await UiHost.Settle();
        var page = await GoToShortcuts(window);
        var card = page.ExpressionCards.Single();
        card.Box.Text           = "Hold  and swipe";
        card.Box.SelectionStart = 5;

        ShortcutsPage.InsertKey(card, "Back");
        await UiHost.Settle();

        Assert.AreEqual("Hold {Back} and swipe", card.Box.Text);
        Assert.AreEqual("Hold {Back} and swipe", _store.LoadMeanings()[7]);
    });

    [TestMethod]
    public Task Expressions_PickerListsChipsAndFilters() => UiHost.RunAsync(async () =>
    {
        var window = Open([new ExpressionUse(7, [])]);
        await UiHost.Settle();
        var page = await GoToShortcuts(window);

        var all      = ShortcutsPage.PickerItems(string.Empty);
        var filtered = ShortcutsPage.PickerItems("volume");

        Assert.IsTrue(all.Count > 50);
        CollectionAssert.IsSubsetOf(VolumeKeys, filtered.ToList());
        Assert.IsTrue(filtered.All(name => name.Contains("olume", StringComparison.OrdinalIgnoreCase)));
    });
}
