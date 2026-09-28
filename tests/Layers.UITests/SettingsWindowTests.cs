using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Core.ViewModels;
using Layers.Tests;
using Layers.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media.Animation;

namespace Layers.UITests;

[TestClass]
public sealed class SettingsWindowTests : IDisposable
{
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

    private SettingsWindow Open(StartupState? startup = null) =>
        SettingsWindow.Open(() => new SettingsViewModel(_store, new FakeStartup(startup ?? new StartupState(true, true, null))));

    private static ToggleSwitch Switch(SettingsWindow window, string header) =>
        window.Controls().OfType<ToggleSwitch>().Single(t => AutomationProperties.GetName(t) == header);

    private static T Named<T>(SettingsWindow window, string name) => (T)((FrameworkElement)window.Content).FindName(name);

    private static Page CurrentPage(SettingsWindow window) => (Page)Named<Frame>(window, "ContentFrame").Content;

    private static T InPage<T>(SettingsWindow window, string name) => (T)CurrentPage(window).FindName(name);

    private static Windows.Foundation.Rect Bounds(FrameworkElement element) =>
        element.TransformToVisual(null)
            .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static NavigationViewItem NavItem(SettingsWindow window, string label) =>
        window.Controls().OfType<NavigationViewItem>().Single(item => (string)item.Content == label);

    private static List<Hyperlink> Links(SettingsWindow window) =>
        Descendants(window.Content).OfType<TextBlock>().SelectMany(text => text.Inlines.OfType<Hyperlink>()).ToList();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    /// Opens the layer picker as a user would, through its automation peer, and returns its row checkboxes.
    private static async Task<List<CheckBox>> OpenPicker(SettingsWindow window)
    {
        var picker = InPage<ComboBox>(window, "LayerPicker");
        ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(picker)).Expand();
        await UiHost.Settle();
        return Enumerable.Range(0, picker.Items.Count)
            .Select(i => Descendants(picker.ContainerFromIndex(i)).OfType<CheckBox>().Single())
            .ToList();
    }

    /// Waits out the drill-in transition until the frame shows a page of the given type.
    private static async Task WaitForPage<T>(SettingsWindow window)
        where T : Page
    {
        for (var i = 0; i < 50 && CurrentPage(window) is not T; i++)
        {
            await Task.Delay(20);
        }

        Assert.IsInstanceOfType<T>(CurrentPage(window));
        await UiHost.Settle();
    }

    [TestMethod]
    public Task Opens_WithStoredValues() => UiHost.RunAsync(async () =>
    {
        _store.Save(new HudSettings(false, 0b100, 2300));

        var window = Open();
        await UiHost.Settle();

        Assert.IsFalse(Switch(window, "Show HUD").IsOn);
        Assert.AreEqual(2.3, InPage<Slider>(window, "HoldSlider").Value, 0.001);
        Assert.IsFalse(InPage<ComboBox>(window, "LayerPicker").IsEnabled, "picker stays enabled while the HUD is off");
        Assert.IsFalse(InPage<Button>(window, "PlayButton").IsEnabled, "Play stays enabled while the HUD is off");
        Assert.AreEqual(0.4, InPage<HudPreview>(window, "Preview").Opacity, 0.001, "the demo isn't dimmed when opened with the HUD off");

        // Stored Layers Show Once The HUD Is Back On, Since A Disabled Picker Can't Open
        Switch(window, "Show HUD").IsOn = true;
        await UiHost.Settle();
        var boxes = await OpenPicker(window);
        Assert.HasCount(8, boxes);
        Assert.IsFalse(boxes[2].IsChecked);
        Assert.IsTrue(boxes[1].IsChecked);
        Assert.IsTrue(boxes.All(b => b.IsEnabled));
    });

    [TestMethod]
    public Task Picker_HoldsEightLayers_AndUncheckingSaves() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // Caption And Accessible Name
        var picker = InPage<ComboBox>(window, "LayerPicker");
        Assert.AreEqual("Choose layers...", picker.PlaceholderText);
        Assert.AreEqual(-1, picker.SelectedIndex);
        Assert.AreEqual("Show for these layers", AutomationProperties.GetName(picker));

        // Eight Layers, In Order
        var boxes = await OpenPicker(window);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 8).Select(i => $"Layer {i}").ToList(),
            boxes.Select(b => (string)b.Content).ToList());

        // Unchecking One Saves, And The List Stays Open For More
        ((IToggleProvider)FrameworkElementAutomationPeer.CreatePeerForElement(boxes[3])).Toggle();
        await UiHost.Settle();
        Assert.AreEqual((byte)0b1000, _store.Load().HudSuppressedLayers);
        Assert.IsTrue(picker.IsDropDownOpen, "toggling a layer closed the list");

        // Picking A Row Itself (Enter) Toggles It Too, And Nothing Stays Selected
        picker.SelectedIndex = 5;
        await UiHost.Settle();
        Assert.AreEqual((byte)0b101000, _store.Load().HudSuppressedLayers);
        Assert.IsFalse(boxes[5].IsChecked);

        // Closed Again, It Still Reads "Choose layers..."
        ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(picker)).Collapse();
        await UiHost.Settle();
        Assert.AreEqual(-1, picker.SelectedIndex);
        Assert.IsNull(picker.SelectedItem);
        Assert.AreEqual("Choose layers...", picker.PlaceholderText);
    });

    [TestMethod]
    public Task HudOff_DisablesPicker() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var picker = InPage<ComboBox>(window, "LayerPicker");
        Assert.IsTrue(picker.IsEnabled);

        Switch(window, "Show HUD").IsOn = false;
        await UiHost.Settle();
        Assert.IsFalse(picker.IsEnabled);

        Switch(window, "Show HUD").IsOn = true;
        await UiHost.Settle();
        Assert.IsTrue(picker.IsEnabled);
    });

    [TestMethod]
    public Task MonitorPicker_HoldsThreeChoices_SavesAndFollowsHud() => UiHost.RunAsync(async () =>
    {
        _store.Save(HudSettings.Default with { HudMonitor = HudMonitorMode.Cursor });

        var window = Open();
        await UiHost.Settle();

        // Label, Name, Access Key, And The Stored Choice
        var picker = InPage<ComboBox>(window, "MonitorPicker");
        Assert.AreEqual("Open HUD on", InPage<TextBlock>(window, "MonitorLabel").Text);
        Assert.AreEqual(char.ConvertFromUtf32(0xE78B), InPage<FontIcon>(window, "MonitorIcon").Glyph);
        Assert.IsNull(picker.Header);
        Assert.AreEqual("Open HUD on", AutomationProperties.GetName(picker));
        Assert.AreEqual("O", picker.AccessKey);
        CollectionAssert.AreEqual(
            new List<string> { "Primary monitor", "Monitor with mouse cursor", "Monitor with focused window" },
            picker.Items.Cast<string>().ToList());
        Assert.AreEqual(1, picker.SelectedIndex);

        // Picking One Saves It
        picker.SelectedIndex = 2;
        await UiHost.Settle();
        Assert.AreEqual(HudMonitorMode.FocusedWindow, _store.Load().HudMonitor);
        picker.SelectedIndex = 0;
        await UiHost.Settle();
        Assert.AreEqual(HudMonitorMode.Primary, _store.Load().HudMonitor);

        // Off With The HUD
        Switch(window, "Show HUD").IsOn = false;
        await UiHost.Settle();
        Assert.IsFalse(picker.IsEnabled);
        Switch(window, "Show HUD").IsOn = true;
        await UiHost.Settle();
        Assert.IsTrue(picker.IsEnabled);
    });

    [TestMethod]
    public Task GeneralPage_FitsWithoutScrolling_WithNoSpareRoom() => UiHost.RunAsync(async () =>
    {
        // The Fixed Height Is Measured At 100% Windows Text Size; Larger Text Needs The Scrollbar
        if (new Windows.UI.ViewManagement.UISettings().TextScaleFactor != 1.0)
        {
            Assert.Inconclusive("Windows text size isn't 100%, so the fixed height doesn't apply");
        }

        var window = Open();
        await UiHost.Settle();

        // The Page's Content Below The Title Bar Fits The Window's Height With Nothing To Scroll
        var scroller = Descendants(CurrentPage(window)).OfType<ScrollViewer>().First();
        var top      = scroller.TransformToVisual(null).TransformPoint(default).Y;
        var content  = ((FrameworkElement)scroller.Content).DesiredSize.Height;
        var needs = $"the General page needs {top + content} DIPs";
        Assert.IsLessThanOrEqualTo(SettingsWindow.LogicalHeight, top + content, needs);
        Assert.AreEqual(0, scroller.ScrollableHeight, $"the General page scrolls by {scroller.ScrollableHeight} DIPs");

        // And The Window Is No Taller Than That, Give Or Take Text Rounding At The Monitor's Scale
        var spare = SettingsWindow.LogicalHeight - (top + content);
        var root  = (FrameworkElement)window.Content;
        Assert.IsLessThanOrEqualTo(6, spare, $"the window has {spare} spare DIPs");
        Assert.AreEqual(SettingsWindow.LogicalHeight, root.ActualHeight, 1.5, "the window isn't its logical height");
        Assert.AreEqual(SettingsWindow.LogicalWidth, root.ActualWidth, 1.5, "the window isn't its logical width");
    });

    [TestMethod]
    public Task StartupNote_GrowsWindow_SoNothingScrolls() => UiHost.RunAsync(async () =>
    {
        var window = Open(new StartupState(false, false, "Turned off in Settings › Apps › Startup."));
        await UiHost.Settle();
        await UiHost.Settle();

        // The Note Shows, The Page Still Doesn't Scroll, And The Window Grew To Make Room
        var scroller = Descendants(CurrentPage(window)).OfType<ScrollViewer>().First();
        var root     = (FrameworkElement)window.Content;
        Assert.AreEqual(Visibility.Visible, InPage<TextBlock>(window, "StartupNote").Visibility);
        Assert.AreEqual(0, scroller.ScrollableHeight, $"the General page scrolls by {scroller.ScrollableHeight} DIPs");
        var grown = root.ActualHeight - SettingsWindow.LogicalHeight;
        Assert.IsGreaterThan(10, grown, "the window didn't grow for the note");
    });

    [TestMethod]
    public Task MonitorPicker_AsWideAsItsWidestChoice_OpensOverTheBox() => UiHost.RunAsync(async () =>
    {
        _store.Save(HudSettings.Default with { HudMonitor = HudMonitorMode.FocusedWindow });

        var window = Open();
        await UiHost.Settle();

        // The Widest Choice Shows Whole, With The Box No Wider Than That Needs
        var picker    = InPage<ComboBox>(window, "MonitorPicker");
        var presenter = Descendants(picker).OfType<ContentPresenter>().First(p => p.Name == "ContentPresenter");
        var text      = Descendants(presenter).OfType<TextBlock>().Single();
        Assert.AreEqual("Monitor with focused window", text.Text);
        Assert.IsFalse(text.IsTextTrimmed, "the widest choice is cut off");
        Assert.IsLessThanOrEqualTo(presenter.ActualWidth + 0.5, text.ActualWidth, "the widest choice is cut off");
        text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var roomy = "the box is wider than its widest choice";
        Assert.IsLessThanOrEqualTo(text.DesiredSize.Width + 2, presenter.ActualWidth, roomy);

        // Opened, The Shown Choice's Row Sits Over The Box, As Wide As It
        // Retried, Since Focus Moving Elsewhere (Someone Using The Desktop) Light-Dismisses The List
        FrameworkElement? row = null;
        for (var attempt = 0; attempt < 3 && row is null; attempt++)
        {
            ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(picker)).Expand();
            await UiHost.Settle();
            row = picker.ContainerFromIndex(2) as FrameworkElement;
        }

        Assert.IsNotNull(row, "the list closed before its rows could be measured (focus moved away?)");
        var box      = Bounds(picker);
        var rowBound = Bounds(row);
        ((IExpandCollapseProvider)FrameworkElementAutomationPeer.CreatePeerForElement(picker)).Collapse();
        var where = $"row {rowBound}, box {box}";
        var boxCenter = new Windows.Foundation.Point(box.X + (box.Width / 2), box.Y + (box.Height / 2));
        var rowCenter = new Windows.Foundation.Point(
            rowBound.X + (rowBound.Width / 2),
            rowBound.Y + (rowBound.Height / 2));
        Assert.AreEqual(boxCenter.X, rowCenter.X, 6, $"the list opened beside the box: {where}");
        Assert.AreEqual(boxCenter.Y, rowCenter.Y, 6, $"the list opened off the box: {where}");

        // Navigating Away And Back Twice (The Page Is Cached, So Loaded Fires Again) Doesn't Regrow The Box
        var firstWidth = picker.Width;
        for (var i = 0; i < 2; i++)
        {
            Named<NavigationView>(window, "Nav").SelectedItem = NavItem(window, "About");
            await WaitForPage<AboutPage>(window);
            Named<NavigationView>(window, "Nav").SelectedItem = NavItem(window, "General");
            await WaitForPage<GeneralPage>(window);
        }

        var picker2 = InPage<ComboBox>(window, "MonitorPicker");
        Assert.AreEqual(firstWidth, picker2.Width, 0.001, "the box widened after a round trip through About");
    });

    [TestMethod]
    public Task CaptionButtons_MinimizeAndCloseOnly() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // No System Caption Buttons, So No Maximize; Minimize Still Allowed For The Taskbar And Win+Down
        var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter;
        Assert.IsFalse(presenter.HasTitleBar);
        Assert.IsTrue(presenter.HasBorder);
        Assert.IsTrue(presenter.IsMinimizable);
        Assert.IsFalse(presenter.IsMaximizable);
        Assert.IsFalse(presenter.IsResizable);

        // Ours: Named, Focusable, Stock Glyphs At 10, 48 DIP Squares, Close On The Window's Right Edge
        var minimize = Named<Button>(window, "MinimizeButton");
        var close    = Named<Button>(window, "CloseButton");
        Assert.AreEqual("Minimize", AutomationProperties.GetName(minimize));
        Assert.AreEqual("Close", AutomationProperties.GetName(close));
        foreach (var (button, glyph) in new[] { (minimize, 0xE921), (close, 0xE8BB) })
        {
            Assert.IsTrue(button.IsTabStop);
            Assert.AreEqual(48, button.ActualWidth);
            Assert.AreEqual(48, button.ActualHeight);
            var icon = (FontIcon)button.Content;
            Assert.AreEqual(char.ConvertFromUtf32(glyph), icon.Glyph);
            Assert.AreEqual(10, icon.FontSize);
        }

        var right = close.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(close.ActualWidth, 0));
        Assert.AreEqual(((FrameworkElement)window.Content).ActualWidth, right.X, 1, "Close isn't on the right edge");
        Assert.AreEqual(0, right.Y, 1, "Close isn't at the top");
        Assert.AreEqual(
            close.TransformToVisual(null).TransformPoint(default).X,
            minimize.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(minimize.ActualWidth, 0)).X,
            0.5,
            "Minimize isn't just left of Close");

        // Clicked From The Keyboard: Minimize Minimizes, Close Closes
        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(minimize)).Invoke();
        await UiHost.Settle();
        Assert.AreEqual(Microsoft.UI.Windowing.OverlappedPresenterState.Minimized, presenter.State);
        presenter.Restore();
        await UiHost.Settle();

        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(close)).Invoke();
        await UiHost.Settle();
        Assert.IsNull(SettingsWindow.Current, "Close didn't close the window");
    });

    [TestMethod]
    public Task MovingSlider_SavesHoldAndShowsDemo() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // Hidden At Rest
        var preview = InPage<HudPreview>(window, "Preview");
        Assert.AreEqual(ClockState.Stopped, preview.DemoState);
        Assert.AreEqual(0, preview.RestingOpacity, "the demo HUD shows at rest");

        // Move It As A User Would, Through Its Automation Peer And The Bound Value
        var slider = (IRangeValueProvider)FrameworkElementAutomationPeer.CreatePeerForElement(InPage<Slider>(window, "HoldSlider"));
        slider.SetValue(0.5);
        await Task.Delay(100);

        Assert.AreEqual(500, _store.Load().HudHoldMs);
        Assert.AreEqual(ClockState.Active, preview.DemoState, "changing the duration doesn't show the demo");

        // Holds, Exits, And Stays Hidden Like The Real HUD
        await Task.Delay(HudTiming.Exit + TimeSpan.FromMilliseconds(500 + 400));
        Assert.AreEqual(ClockState.Filling, preview.DemoState, "the demo didn't end exited");
    });

    [TestMethod]
    public Task PlayButton_ShowsDemo() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var preview = InPage<HudPreview>(window, "Preview");
        var play    = InPage<Button>(window, "PlayButton");
        Assert.AreEqual(ClockState.Stopped, preview.DemoState);
        Assert.AreEqual("Play Animation", AutomationProperties.GetName(play));
        Assert.IsTrue(Descendants(play).OfType<TextBlock>().Any(text => text.Text == "Play Animation"));
        Assert.IsTrue(play.IsEnabled);

        // Shows, Then Ends Hidden, And Plays Again From There
        InPage<Slider>(window, "HoldSlider").Value = 0.5;
        await Task.Delay(HudTiming.Exit + TimeSpan.FromMilliseconds(500 + 400));
        Assert.AreEqual(ClockState.Filling, preview.DemoState);

        ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(play)).Invoke();
        await Task.Delay(100);
        Assert.AreEqual(ClockState.Active, preview.DemoState);

        await Task.Delay(HudTiming.Exit + TimeSpan.FromMilliseconds(500 + 400));
        Assert.AreEqual(ClockState.Filling, preview.DemoState);
    });

    [TestMethod]
    public Task HudOff_DisablesDemo_AndOnRestoresIt() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var preview = InPage<HudPreview>(window, "Preview");
        var play    = InPage<Button>(window, "PlayButton");
        var slider  = InPage<Slider>(window, "HoldSlider");
        var invoke  = (IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(play);

        // A Running Demo Stops, Hidden, When Show HUD Turns Off
        invoke.Invoke();
        await Task.Delay(100);
        Assert.AreEqual(ClockState.Active, preview.DemoState);
        Switch(window, "Show HUD").IsOn = false;
        await UiHost.Settle();
        Assert.AreEqual(ClockState.Stopped, preview.DemoState, "the demo kept running with the HUD off");
        Assert.AreEqual(0, preview.RestingOpacity);

        // Off: Dimmed, Play Disabled, And A Slider Change Doesn't Replay
        Assert.IsFalse(preview.IsEnabled);
        Assert.AreEqual(0.4, preview.Opacity, 0.001);
        Assert.IsFalse(play.IsEnabled);
        Assert.IsFalse(slider.IsEnabled);
        slider.Value = 1.2;
        await Task.Delay(100);
        Assert.AreEqual(ClockState.Stopped, preview.DemoState, "a slider change replayed the demo with the HUD off");

        // On Again: Full Opacity, And Play Works
        Switch(window, "Show HUD").IsOn = true;
        await UiHost.Settle();
        Assert.AreEqual(1, preview.Opacity);
        Assert.IsTrue(play.IsEnabled);
        invoke.Invoke();
        await Task.Delay(100);
        Assert.AreEqual(ClockState.Active, preview.DemoState);
    });

    [TestMethod]
    public Task HudSection_OrderTextsAndAccentPlay() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // HUD Heading, Explanation, Show HUD Row, Demo, The Slider Row, Then Open HUD On
        var hudSwitch   = InPage<ToggleSwitch>(window, "HudSwitch");
        var explanation = InPage<TextBlock>(window, "HudExplanation");
        var preview     = InPage<HudPreview>(window, "Preview");
        var slider      = InPage<Slider>(window, "HoldSlider");
        var section     = (Panel)explanation.Parent;
        int IndexOf(FrameworkElement element) =>
            section.Children.IndexOf(element.Parent == section ? element : (UIElement)element.Parent);
        Assert.AreEqual("HUD", ((TextBlock)section.Children[0]).Text, "the HUD heading isn't first");
        Assert.AreEqual(1, IndexOf(explanation), "explanation isn't under the heading");
        Assert.AreEqual(2, IndexOf(hudSwitch), "Show HUD row isn't after the explanation");
        Assert.AreEqual(3, IndexOf(preview), "demo isn't after the Show HUD row");
        Assert.AreEqual(4, IndexOf(slider), "slider row isn't after the demo");
        var monitorRow = IndexOf(InPage<ComboBox>(window, "MonitorPicker"));
        Assert.AreEqual(5, monitorRow, "Open HUD on row isn't after the slider");
        Assert.AreEqual(6, section.Children.Count, "Open HUD on row isn't last");

        // Verbatim Explanation, With Room Below It
        Assert.AreEqual(
            "The HUD (Heads Up Display) is a small pop-up that appears at the bottom of your screen when you switch layers on your HID Remapper.",
            explanation.Text);
        Assert.IsGreaterThanOrEqualTo(12, explanation.Margin.Bottom + ((StackPanel)section).Spacing);

        // Show HUD Beside The Knob, No Header, Same Name And Access Key
        Assert.IsNull(hudSwitch.Header);
        Assert.AreEqual("Show HUD", hudSwitch.OnContent);
        Assert.AreEqual("Show HUD", hudSwitch.OffContent);
        Assert.AreEqual("Show HUD", AutomationProperties.GetName(hudSwitch));
        Assert.AreEqual("H", hudSwitch.AccessKey);

        // Taller Demo Strip, HUD Hidden At Rest
        Assert.AreEqual(150, preview.ActualHeight, 0.5);
        Assert.AreEqual(ClockState.Stopped, preview.DemoState);
        Assert.AreEqual(0, Descendants(preview).OfType<HudFace>().Single().Opacity);

        // Accent Play Button, Icon Centered On The Text's Line
        var play = InPage<Button>(window, "PlayButton");
        Assert.AreSame(Application.Current.Resources["AccentButtonStyle"], play.Style);
        var icon = InPage<FontIcon>(window, "PlayIcon");
        var text = Descendants(play).OfType<TextBlock>().Single(block => block.Text == "Play Animation");
        double MiddleY(FrameworkElement element) =>
            element.TransformToVisual(play).TransformPoint(default).Y + (element.ActualHeight / 2);
        Assert.AreEqual(MiddleY(text), MiddleY(icon), 0.5, "the Play icon isn't centered on the text");
    });

    [TestMethod]
    public Task Slider_HasHeaderTicksAndIcon() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var slider = InPage<Slider>(window, "HoldSlider");
        Assert.AreEqual("HUD Show Duration (In Seconds)", InPage<TextBlock>(window, "HoldHeader").Text);
        Assert.AreEqual("HUD Show Duration (In Seconds)", AutomationProperties.GetName(slider));
        Assert.AreEqual(TickPlacement.BottomRight, slider.TickPlacement);
        Assert.AreEqual(0.5, slider.TickFrequency);
        Assert.AreEqual(0.5, slider.Minimum);
        Assert.AreEqual(5.0, slider.Maximum);
        Assert.AreEqual(0.1, slider.StepFrequency, 0.0001);
        Assert.AreEqual(char.ConvertFromUtf32(0xEC4A), InPage<FontIcon>(window, "HoldIcon").Glyph);
    });

    [TestMethod]
    public Task Preview_ShowsHudOverWallpaperOrFill() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // The Real HUD's Face At Real Size, On A Full-Width Strip
        var preview = InPage<HudPreview>(window, "Preview");
        var face    = Descendants(preview).OfType<HudFace>().Single();
        Assert.AreEqual("Layer 1", face.Text);
        Assert.AreEqual(HudPlacement.BoxDips(face.XamlRoot.RasterizationScale), face.ActualHeight, 0.01, "not the real HUD's whole-pixel height");
        Assert.AreEqual(HudPlacement.BottomGap, face.Margin.Bottom);
        Assert.IsGreaterThanOrEqualTo(127, face.ActualWidth);
        Assert.IsGreaterThan(500, preview.ActualWidth, "the preview isn't full width");

        // No Wallpaper, Or A Missing File, Shows The Fill
        preview.ShowWallpaper(string.Empty);
        Assert.IsFalse(preview.HasWallpaper);
        preview.ShowWallpaper(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg"));
        Assert.IsFalse(preview.HasWallpaper);

        // A File That Can't Load Is Dropped
        var junk = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid()}.jpg");
        await File.WriteAllTextAsync(junk, "not an image");
        try
        {
            preview.ShowWallpaper(junk);
            Assert.IsTrue(preview.HasWallpaper);
            await UiHost.Settle();
            Assert.IsFalse(preview.HasWallpaper, "a broken image wasn't dropped");
        }
        finally
        {
            File.Delete(junk);
        }
    });

    [TestMethod]
    public Task Preview_HidesDemoLabelFromScreenReaders() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // Walk The Window's Automation Tree From Its NavigationView (Frames And Pages Have No Peer); Nothing Screen Readers See May Read The Demo's "Layer 1"
        static IEnumerable<AutomationPeer> Peers(AutomationPeer peer) =>
            peer is null ? [] : new[] { peer }.Concat((peer.GetChildren() ?? []).SelectMany(Peers));

        var nav     = FrameworkElementAutomationPeer.CreatePeerForElement(Descendants(window.Content).OfType<NavigationView>().Single());
        var exposed = Peers(nav).Where(peer => peer.IsControlElement() || peer.IsContentElement()).ToList();

        Assert.IsTrue(exposed.Any(peer => peer.GetName() == "Play Animation"), "the walk never reached the page's controls");
        Assert.IsFalse(exposed.Any(peer => peer.GetName() == "Layer 1"), "a screen reader would read the demo HUD");
    });

    [TestMethod]
    public Task NavigatingAway_StopsDemo() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var preview = InPage<HudPreview>(window, "Preview");
        preview.Replay(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ClockState.Active, preview.DemoState);

        Named<NavigationView>(window, "Nav").SelectedItem = NavItem(window, "About");
        await WaitForPage<AboutPage>(window);

        Assert.AreEqual(ClockState.Stopped, preview.DemoState);
    });

    [TestMethod]
    public Task ClosingWindow_StopsDemo() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        var preview = InPage<HudPreview>(window, "Preview");
        preview.Replay(TimeSpan.FromSeconds(5));
        Assert.AreEqual(ClockState.Active, preview.DemoState);

        window.Close();
        await UiHost.Settle();

        Assert.AreEqual(ClockState.Stopped, preview.DemoState);
    });

    [TestMethod]
    public Task Open_Twice_ReusesWindow() => UiHost.RunAsync(async () =>
    {
        var first = Open();
        await UiHost.Settle();

        var second = Open();

        Assert.AreSame(first, second);
    });

    [TestMethod]
    public Task EveryControl_HasAutomationName() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        // Our Own Controls Carry An Explicit Name, The Picker's Checkboxes Included
        var own = window.Controls().Where(c => c is ToggleSwitch or Slider or ComboBox or NavigationViewItem).ToList();
        own.AddRange(await OpenPicker(window));
        Assert.IsGreaterThanOrEqualTo(14, own.Count);
        foreach (var control in own)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(control)), $"{control.GetType().Name} has no automation name");
        }

        // Every Visible Button, Stock Ones Included, Reads Out A Name
        var buttons = window.Controls().OfType<ButtonBase>().Where(b => b.Visibility == Visibility.Visible && b.ActualWidth > 0).ToList();
        Assert.IsNotEmpty(buttons);
        foreach (var button in buttons)
        {
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(button);
            Assert.IsFalse(string.IsNullOrWhiteSpace(peer.GetName()), $"{button.GetType().Name} {button.Name} has no automation name");
        }

        // Links
        foreach (var link in Links(window))
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(AutomationProperties.GetName(link)), $"link to {link.NavigateUri} has no automation name");
        }
    });

    [TestMethod]
    public Task LockedStartup_DisablesSwitchAndShowsNote() => UiHost.RunAsync(async () =>
    {
        const string note = "Turned off in Settings › Apps › Startup.";

        var window = Open(new StartupState(false, false, note));
        await UiHost.Settle();

        var startupSwitch = Switch(window, "Run on Startup");
        var noteText      = InPage<TextBlock>(window, "StartupNote");
        Assert.IsFalse(startupSwitch.IsEnabled);
        Assert.IsFalse(startupSwitch.IsOn);
        Assert.AreEqual(Visibility.Visible, noteText.Visibility);
        Assert.AreEqual(note, noteText.Text);
    });

    [TestMethod]
    public Task SelectingAbout_ShowsAboutContent() => UiHost.RunAsync(async () =>
    {
        var window = Open();
        await UiHost.Settle();

        Named<NavigationView>(window, "Nav").SelectedItem = NavItem(window, "About");
        await WaitForPage<AboutPage>(window);

        // Only The About Page Shows
        var about = CurrentPage(window);
        Assert.IsFalse(Descendants(Named<Frame>(window, "ContentFrame")).OfType<GeneralPage>().Any(), "General is still shown");

        // Author, Version, And Links
        var texts = Descendants(about).OfType<TextBlock>().Select(text => text.Text).ToList();
        CollectionAssert.Contains(texts, window.Model.Version);
        var links = Links(window).ToDictionary(link => AutomationProperties.GetName(link), link => link.NavigateUri.ToString());
        Assert.AreEqual("https://github.com/artistro08", links["artistro08"]);
        Assert.AreEqual("https://github.com/artistro08/layers", links["GitHub"]);
        Assert.AreEqual("https://github.com/artistro08/layers/blob/main/LICENSE", links["License (MIT)"]);
        Assert.AreEqual("https://github.com/artistro08/layers/blob/main/assets/NOTICE-fluentui.txt", links["Third-party notices"]);

        // And Back
        Named<NavigationView>(window, "Nav").SelectedItem = NavItem(window, "General");
        await WaitForPage<GeneralPage>(window);
        Assert.IsTrue(Switch(window, "Show HUD").IsOn);
    });
}
