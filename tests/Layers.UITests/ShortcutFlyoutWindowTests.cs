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
        Assert.AreEqual(480, ShortcutFlyoutWindow.PanelWidthDip);
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
