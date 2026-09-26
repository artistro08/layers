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

        // HUD Heading, Explanation, Show HUD Row, Demo, Then The Slider Row
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
        Assert.AreEqual(4, IndexOf(slider), "slider row isn't last");

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
