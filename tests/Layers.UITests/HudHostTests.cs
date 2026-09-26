using Layers.Core.Logic;
using Layers.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Win32;
using WinRT.Interop;

namespace Layers.UITests;

[TestClass]
public sealed class HudHostTests
{
    private static readonly TimeSpan LongHold  = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ShortHold = TimeSpan.FromMilliseconds(300);

    // Slack For Dispatcher Timers And The Flyout's Async Close
    private const int Margin = 400;

    private static IReadOnlyList<Popup> OpenPopups(HudHost hud) =>
        VisualTreeHelper.GetOpenPopupsForXamlRoot(hud.Content.XamlRoot);

    private static bool IsOpen(HudHost hud) => OpenPopups(hud).Count > 0;

    /// Creates the host and waits for its XAML root, which only exists once the dispatcher has run.
    /// In the app the host is created at startup, long before the first show.
    private static async Task<HudHost> NewHud()
    {
        var hud = new HudHost();
        for (var i = 0; i < 100 && hud.Content.XamlRoot is null; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsNotNull(hud.Content.XamlRoot, "host never got a XAML root");
        return hud;
    }

    [TestMethod]
    public Task Show_OpensFlyoutWithLabel() => UiHost.RunAsync(async () =>
    {
        var hud = await NewHud();

        hud.Show(new LayerMask(0b1010), LongHold);
        await UiHost.Settle();

        Assert.IsTrue(IsOpen(hud));
        Assert.AreEqual("Layers 1, 3", hud.Text);
        hud.Close();
    });

    [TestMethod]
    public Task Show_DoesNotTakeFocus() => UiHost.RunAsync(async () =>
    {
        var hud    = await NewHud();
        var before = PInvoke.GetForegroundWindow();

        hud.Show(new LayerMask(0b10), LongHold);
        await UiHost.Settle();

        Assert.IsTrue(IsOpen(hud));
        Assert.AreEqual(before, PInvoke.GetForegroundWindow());
        hud.Close();
    });

    [TestMethod]
    public Task Show_SitsBottomCenterOfPrimary() => UiHost.RunAsync(async () =>
    {
        var hud = await NewHud();

        hud.Show(new LayerMask(0b100), LongHold);
        await UiHost.Settle();

        // Flyout Content Bounds On Screen, From The Host's Client Origin
        var content = (FrameworkElement)OpenPopups(hud).Single().Child;
        var scale   = WindowStyles.PrimaryScale();
        var origin  = WindowStyles.ClientOrigin(WindowNative.GetWindowHandle(hud));
        var bounds  = content.TransformToVisual(null).TransformBounds(new Rect(0, 0, content.ActualWidth, content.ActualHeight));
        var left    = origin.X + bounds.Left * scale;
        var right   = origin.X + bounds.Right * scale;
        var top     = origin.Y + bounds.Top * scale;
        var bottom  = origin.Y + bounds.Bottom * scale;

        // Centered, Top On The Anchor, Ending One Pixel Above The Work Area's Bottom
        var work   = DisplayArea.Primary.WorkArea;
        var anchor = HudPlacement.Anchor(work, scale);
        Assert.AreEqual(work.X + work.Width / 2.0, (left + right) / 2, 1, "not centered");
        Assert.AreEqual(anchor.Y, top, 1, "top not on the anchor");
        Assert.AreEqual(work.Y + work.Height - 1, bottom, 1, "bottom not one pixel above the edge");
        Assert.AreEqual(HudPlacement.GapPixels(scale), work.Y + work.Height - (top + HudPlacement.BoxPixels(scale)), 1, "gap under the box");
        hud.Close();
    });

    [TestMethod]
    public Task Show_ClosesAfterHoldAndExit() => UiHost.RunAsync(async () =>
    {
        var hud = await NewHud();

        hud.Show(new LayerMask(0b100), ShortHold);
        await Task.Delay(100);
        Assert.IsTrue(IsOpen(hud), "closed before the hold ended");

        await Task.Delay(ShortHold + HudTiming.Exit + TimeSpan.FromMilliseconds(Margin));
        Assert.IsFalse(IsOpen(hud), "still open after hold + exit");
        Assert.IsFalse(hud.AppWindow.IsVisible, "host still shown");
        hud.Close();
    });

    [TestMethod]
    public Task Show_DuringExitKeepsOpenAndUpdates() => UiHost.RunAsync(async () =>
    {
        var hud = await NewHud();

        // Second Show Lands Inside The 150 ms Exit
        hud.Show(new LayerMask(0b100), ShortHold);
        await Task.Delay(ShortHold + HudTiming.Exit / 3);
        hud.Show(new LayerMask(0b1000), ShortHold);
        await Task.Delay(ShortHold / 2);

        Assert.IsTrue(IsOpen(hud), "second show should keep the HUD up");
        Assert.AreEqual("Layer 3", hud.Text);

        // And It Still Hides After The New Hold
        await Task.Delay(ShortHold + HudTiming.Exit + TimeSpan.FromMilliseconds(Margin));
        Assert.IsFalse(IsOpen(hud), "never closed after the second hold");
        hud.Close();
    });

    [TestMethod]
    public Task Close_WithPendingHoldDoesNotThrow() => UiHost.RunAsync(async () =>
    {
        var hud = await NewHud();

        hud.Show(new LayerMask(0b100), ShortHold);
        await Task.Delay(50);
        hud.Close();

        // Past When The Hold Would Have Fired
        await Task.Delay(ShortHold + HudTiming.Exit + TimeSpan.FromMilliseconds(Margin));
    });
}
