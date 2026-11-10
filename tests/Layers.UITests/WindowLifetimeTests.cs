using Layers.Core.Services;
using Layers.Core.ViewModels;
using Layers.Tests;
using Layers.UI;
using Microsoft.UI.Xaml;

namespace Layers.UITests;

/// <summary>
/// The app makes each window when needed and closes it when done, so a closed window must really be freed.
/// </summary>
/// <remarks>
/// WinUI never frees a window that still handles its own events (Activated, Closed), or its elements' events hooked up
/// in XAML: the native event keeps the handler, and the handler keeps the window. Each window here unhooks them as it
/// closes. Each test opens, closes, and collects one window, then checks that only a weak reference to it is left.
/// </remarks>
[TestClass]
public sealed class WindowLifetimeTests
{
    [TestMethod]
    public Task ShortcutFlyout_IsFreedAfterShutdown() => UiHost.RunAsync(async () =>
    {
        var model = new ShortcutSheetViewModel();
        var freed = await IsFreed(
            () => new ShortcutFlyoutWindow(model),
            window => ((ShortcutFlyoutWindow)window).Shutdown());

        Assert.IsTrue(freed, "the closed flyout is still in memory");
    });

    [TestMethod]
    public Task TrayMenu_IsFreedAfterClose() => UiHost.RunAsync(async () =>
    {
        var model = new TrayMenuViewModel();
        var freed = await IsFreed(() => new TrayMenuHost(model), window => window.Close());

        Assert.IsTrue(freed, "the closed tray menu is still in memory");
    });

    [TestMethod]
    public Task Hud_IsFreedAfterClose() => UiHost.RunAsync(async () =>
    {
        var freed = await IsFreed(() => new HudHost(), window => window.Close());

        Assert.IsTrue(freed, "the closed HUD is still in memory");
    });

    [TestMethod]
    public Task Settings_IsFreedAfterClose() => UiHost.RunAsync(async () =>
    {
        using var key   = new TempRegistryKey();
        var store       = new SettingsStore(key.Path);
        var closes      = 0;
        var freed       = await IsFreed(
            () =>
            {
                // Like App, Which Schedules A Memory Trim When Settings Closes
                var window = SettingsWindow.Open(() => new SettingsViewModel(store, new FakeStartup(new StartupState(true, true, null))));
                window.Closed += (_, _) => closes++;
                return window;
            },
            window => window.Close());

        Assert.IsTrue(freed, "the closed Settings window is still in memory");
    });

    // Opens, Closes, Then Collects Twice (Finalizers Release WinRT Objects In Between)
    private static async Task<bool> IsFreed(Func<Window> create, Action<Window> close)
    {
        var weak = await OpenAndClose(create, close);
        Collect();
        await UiHost.Settle();
        Collect();
        return !weak.IsAlive;
    }

    // Its Own Method, So No Local Keeps The Window Alive
    private static async Task<WeakReference> OpenAndClose(Func<Window> create, Action<Window> close)
    {
        var window = create();
        var weak   = new WeakReference(window);
        window.Activate();
        await UiHost.Settle();
        close(window);
        await UiHost.Settle();
        return weak;
    }

    private static void Collect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }
}
