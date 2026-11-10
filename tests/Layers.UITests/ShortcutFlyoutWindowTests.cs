using Layers.Core.ViewModels;
using Layers.UI;
using Microsoft.UI.Windowing;

namespace Layers.UITests;

[TestClass]
public sealed class ShortcutFlyoutWindowTests
{
    [TestMethod]
    public Task Open_ThenHide_TogglesIsOpenAndRaisesOpened() => UiHost.RunAsync(async () =>
    {
        var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
        var opened = 0;
        window.Opened += (_, _) => opened++;

        window.Open(null);
        await UiHost.Settle();
        Assert.IsTrue(window.IsOpen);
        Assert.AreEqual(1, opened);

        // Open Again Is A No-Op
        window.Open(null);
        Assert.AreEqual(1, opened);

        window.HideFlyout();
        await UiHost.Settle();
        await UiHost.Settle();
        Assert.IsFalse(window.IsOpen);
        Assert.IsFalse(window.AppWindow.IsVisible);
        window.Shutdown();
    });

    [TestMethod]
    public Task Toggle_IgnoresBounceWithin300ms() => UiHost.RunAsync(async () =>
    {
        var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());

        window.Toggle(null);
        await UiHost.Settle();
        Assert.IsTrue(window.IsOpen);

        // The Hide, Then The Tray Click That Stole Focus Arriving Right After It
        window.Toggle(null);
        window.Toggle(null);
        await UiHost.Settle();
        await UiHost.Settle();

        Assert.IsFalse(window.IsOpen);
        window.Shutdown();
    });

    [TestMethod]
    public Task ToggleTwice_WhileLoading_StillRaisesHidden() => UiHost.RunAsync(async () =>
    {
        // A Quick Double Toggle On A New Window Closes It Before It Loads; The App Only Releases It On Hidden
        var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
        var hidden = 0;
        window.Hidden += (_, _) => hidden++;
        Assert.IsTrue(window.IsLoading, "the window loaded before the toggles, so this test can't hit the loading path");

        window.Toggle(null);
        if (!window.IsLoading)
        {
            window.Shutdown();
            Assert.Inconclusive("moving the window finished loading it, so the second toggle can't land mid-load");
        }

        window.Toggle(null);
        for (var i = 0; i < 50 && hidden == 0; i++)
        {
            await UiHost.Settle();
        }

        Assert.AreEqual(1, hidden, "a flyout closed while loading never raised Hidden");
        Assert.IsFalse(window.IsOpen);
        window.Shutdown();
    });

    [TestMethod]
    public Task Window_IsBorderlessPopupNotInSwitcher() => UiHost.RunAsync(async () =>
    {
        var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());

        window.Open(null);
        await UiHost.Settle();

        Assert.IsFalse(window.AppWindow.IsShownInSwitchers);
        var presenter = (OverlappedPresenter)window.AppWindow.Presenter;
        Assert.IsTrue(presenter.IsAlwaysOnTop);
        Assert.IsFalse(presenter.HasBorder);
        Assert.IsFalse(presenter.HasTitleBar);
        window.Shutdown();
    });

    [TestMethod]
    public Task Hide_ReturnsFocusToTheWindowThatHadIt() => UiHost.RunAsync(async () =>
    {
        // The User's App: A Plain Window In Front Before The Hotkey
        var editor = new Microsoft.UI.Xaml.Window { Content = new Microsoft.UI.Xaml.Controls.TextBox() };
        editor.Activate();
        await UiHost.Settle();
        var editorHwnd = new Windows.Win32.Foundation.HWND(WinRT.Interop.WindowNative.GetWindowHandle(editor));
        TakeForegroundRight();
        Windows.Win32.PInvoke.SetForegroundWindow(editorHwnd);
        await UiHost.Settle();
        if (Windows.Win32.PInvoke.GetForegroundWindow() != editorHwnd)
        {
            editor.Close();
            Assert.Inconclusive("the test host could not take the foreground");
        }

        var window = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
        window.Open(null);
        await UiHost.Settle();

        window.HideFlyout();
        await UiHost.Settle();
        await UiHost.Settle();

        Assert.IsTrue(Windows.Win32.PInvoke.GetForegroundWindow() == editorHwnd, "focus went back to the editor");
        window.Shutdown();
        editor.Close();
    });

    // Windows Only Lets The Process That Got The Last Input Take The Foreground, And Earlier Tests' Windows Leave This
    // One Without It. A Press And Release Of An Unassigned Key (0xE8) Earns It Back Without Typing Anything.
    private static unsafe void TakeForegroundRight()
    {
        var inputs = stackalloc Windows.Win32.UI.Input.KeyboardAndMouse.INPUT[2];
        for (var i = 0; i < 2; i++)
        {
            inputs[i].type             = Windows.Win32.UI.Input.KeyboardAndMouse.INPUT_TYPE.INPUT_KEYBOARD;
            inputs[i].Anonymous.ki.wVk =(Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY)0xE8;
        }

        inputs[1].Anonymous.ki.dwFlags = Windows.Win32.UI.Input.KeyboardAndMouse.KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP;
        _ = Windows.Win32.PInvoke.SendInput(new Span<Windows.Win32.UI.Input.KeyboardAndMouse.INPUT>(inputs, 2), sizeof(Windows.Win32.UI.Input.KeyboardAndMouse.INPUT));
    }

    [TestMethod]
    public Task ViewEvents_AreReRaised() => UiHost.RunAsync(async () =>
    {
        var window   = new ShortcutFlyoutWindow(new ShortcutSheetViewModel());
        var closed   = false;
        var settings = false;
        var edit     = false;
        window.CloseRequested    += (_, _) => closed = true;
        window.SettingsRequested += (_, _) => settings = true;
        window.EditRequested     += (_, _) => edit = true;

        window.Open(null);
        await UiHost.Settle();
        window.View.RequestClose();
        UiTree.Invoke(window.View.SettingsButton);
        UiTree.Invoke(window.View.EditButton);

        Assert.IsTrue(closed && settings && edit);
        window.Shutdown();
    });
}
