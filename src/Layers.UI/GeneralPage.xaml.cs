using Layers.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.System;

namespace Layers.UI;

/// <summary>
/// The Settings window's General page: start at sign-in and the HUD settings.
/// </summary>
/// <remarks>
/// Shown in the window's <see cref="Frame"/>, which passes the window's one <see cref="SettingsViewModel"/> as the
/// navigation parameter. The page is cached, so going back to it keeps its bindings and scroll position. The HUD
/// section, in order: what the HUD is, the Show HUD switch with a "Choose layers..." combo box of the 8 layer
/// checkboxes at the row's right edge, a live demo of the HUD over the bottom of the user's wallpaper (replayed whenever
/// HUD Show Duration changes, and by its accent Play button), and the HUD Show Duration slider with a speedometer
/// icon and half-second ticks.
/// </remarks>
public sealed partial class GeneralPage : Page
{
    /// <summary>
    /// Creates the page.
    /// </summary>
    /// <remarks>
    /// The frame creates it on the first navigation.
    /// </remarks>
    public GeneralPage()
    {
        InitializeComponent();
        HoldIcon.Glyph = char.ConvertFromUtf32(0xEC4A);
        PlayIcon.Glyph = char.ConvertFromUtf32(0xE768);
    }

    /// <summary>Gets the view model, set on the first navigation.</summary>
    public SettingsViewModel? Model { get; private set; }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnNavigatedTo(e);

        // Only The First Visit Sets Up, Since The Page Is Cached
        if (Model is not null)
        {
            return;
        }

        // Set Before The Page Loads, Which Is When x:Bind Reads It
        Model = (SettingsViewModel)e.Parameter;

        // Layer Picker Rows From A Concrete List, So CsWinRT Generates Its Collection Interfaces
        // An x:Bind To The Array Behind IReadOnlyList Marshals Without Them And ItemsSource Throws E_INVALIDARG
        LayerPicker.ItemsSource = new List<LayerOptionViewModel>(Model.Layers);
    }

    private void HoldSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        // Not For The Stored Value Arriving While The Page Loads
        if (!IsLoaded)
        {
            return;
        }

        // Demo The New Duration
        Preview.Replay(TimeSpan.FromSeconds(e.NewValue));
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e) =>
        Preview.Replay(TimeSpan.FromSeconds(HoldSlider.Value));

    // A Row Picked Instead Of Its Checkbox Hit (Enter) Toggles That Layer
    // Then Drops The Selection, So The Box Keeps Showing "Choose layers..."
    private void LayerPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayerPicker.SelectedItem is not LayerOptionViewModel option)
        {
            return;
        }

        LayerPicker.SelectedIndex = -1;
        Toggle(option);
    }

    // Key Presses In The Open List Skip The ComboBox's Own Handlers, So Listen On The Panel Holding The Rows
    // Removed First, Since It Opens Many Times Over The Same Panel
    private void LayerPicker_DropDownOpened(object sender, object e)
    {
        if (LayerPicker.ContainerFromIndex(0) is not { } row || VisualTreeHelper.GetParent(row) is not UIElement rows)
        {
            return;
        }

        rows.PreviewKeyDown -= LayerRows_PreviewKeyDown;
        rows.PreviewKeyDown += LayerRows_PreviewKeyDown;
    }

    // Space Toggles The Focused Row's Checkbox And Leaves The List Open For The Next One
    private void LayerRows_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Space || e.OriginalSource is not ComboBoxItem { Content: LayerOptionViewModel option })
        {
            return;
        }

        e.Handled = true;
        Toggle(option);
    }

    private static void Toggle(LayerOptionViewModel option)
    {
        if (option.IsEnabled)
        {
            option.IsShown = !option.IsShown;
        }
    }

    private async void StartupSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        // Nothing To Save Before The First Navigation
        if (Model is null)
        {
            return;
        }

        await Model.SetStartAtSignInAsync(StartupSwitch.IsOn);
    }
}
