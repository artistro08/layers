using Layers.Core.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace Layers.UI;

/// <summary>
/// The Settings window's About page: the app icon and name, author, version, and license links, centered.
/// </summary>
/// <remarks>
/// Shown in the window's <see cref="Frame"/>, which passes the window's one <see cref="SettingsViewModel"/> as the
/// navigation parameter for the version. The page is cached, so it's built once per window.
/// </remarks>
public sealed partial class AboutPage : Page
{
    /// <summary>
    /// Creates the page.
    /// </summary>
    /// <remarks>
    /// The frame creates it on the first navigation to About.
    /// </remarks>
    public AboutPage() => InitializeComponent();

    /// <summary>Gets the view model, set on navigation.</summary>
    public SettingsViewModel? Model { get; private set; }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnNavigatedTo(e);

        // Set Before The Page Loads, Which Is When x:Bind Reads It
        Model ??= (SettingsViewModel)e.Parameter;
    }
}
