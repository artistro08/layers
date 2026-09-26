using Layers.Core.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Win32;

namespace Layers.UI;

/// <summary>
/// The Settings window.
/// </summary>
/// <remarks>
/// Stock controls on a Mica backdrop, under a stock <see cref="TitleBar"/> that carries the app icon and the pane
/// toggle, with tall caption buttons to match. A stock left <see cref="NavigationView"/> with a narrow pane switches
/// between General (every setting) and About (author, version, and license links, at the bottom of the pane), and
/// General is selected on open. The pages are stock <see cref="Page"/>s in a <see cref="Frame"/>, switched with the
/// stock drill-in transition, and share the window's one view model through the navigation parameter. Only one window exists at a time: <see cref="Open"/> activates it if it's already
/// open. Closing destroys it to free memory. The app keeps running. Every change saves immediately through the view
/// model.
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    // The 160 DIP Pane Plus A 600 DIP Content Column
    private const int LogicalWidth  = 760;
    private const int LogicalHeight = 600;

    private readonly NavigationViewItem _generalItem = NavItem("General", new FontIcon { Glyph = char.ConvertFromUtf32(0xE713) });
    private readonly NavigationViewItem _aboutItem   = NavItem("About", new FontIcon { Glyph = char.ConvertFromUtf32(0xE946) });

    private SettingsWindow(SettingsViewModel model)
    {
        Model = model;
        InitializeComponent();

        // Pane Items From Concrete Lists
        // CsWinRT's AOT Mode Can't Cast The Native MenuItems Vector That XAML Items Are Added To
        Nav.MenuItemsSource       = new List<object> { _generalItem };
        Nav.FooterMenuItemsSource = new List<object> { _aboutItem };

        // General Selected And Shown On Open, Without A Transition
        ContentFrame.Navigate(typeof(GeneralPage), model, new SuppressNavigationTransitionInfo());
        Nav.SelectedItem = _generalItem;

        // Mica And Stock Title Bar, With Caption Buttons As Tall As It
        SystemBackdrop                           = new MicaBackdrop();
        ExtendsContentIntoTitleBar               = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(AppTitleBar);

        // Fixed Size
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        AppWindow.SetPresenter(presenter);

        // App Icon On The Window And In The Title Bar
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            AppTitleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(icon)) };
        }

        Closed += (_, _) => Current = null;
    }

    /// <summary>Gets the open Settings window, if any.</summary>
    /// <remarks>Hides <see cref="Window.Current"/>, which is always null in a desktop app.</remarks>
    public static new SettingsWindow? Current { get; private set; }

    /// <summary>Gets the view model.</summary>
    public SettingsViewModel Model { get; }

    /// <summary>
    /// Opens Settings, or brings the open window to the front.
    /// </summary>
    /// <remarks>
    /// A new window is centered on the monitor under the cursor and sized for that monitor's scale.
    /// </remarks>
    /// <param name="createModel">Creates the view model for a new window.</param>
    /// <returns>The window.</returns>
    public static SettingsWindow Open(Func<SettingsViewModel> createModel)
    {
        ArgumentNullException.ThrowIfNull(createModel);

        // Reuse The Open Window
        if (Current is not null)
        {
            Current.Activate();
            return Current;
        }

        // Create, Size, And Center Under The Cursor
        var window = new SettingsWindow(createModel());
        PInvoke.GetCursorPos(out var cursor);
        var point  = new PointInt32(cursor.X, cursor.Y);
        var scale  = WindowStyles.ScaleAt(point);
        var width  = (int)Math.Round(LogicalWidth * scale);
        var height = (int)Math.Round(LogicalHeight * scale);
        var work   = DisplayArea.GetFromPoint(point, DisplayAreaFallback.Nearest).WorkArea;
        window.AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        Current = window;
        window.Activate();
        _ = window.Model.LoadStartupAsync();
        return window;
    }

    internal IEnumerable<Control> Controls() => Descendants(Root).OfType<Control>();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static NavigationViewItem NavItem(string label, IconElement icon)
    {
        var item = new NavigationViewItem { Content = label, Icon = icon };
        AutomationProperties.SetName(item, label);
        return item;
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        // Already Showing It (The Selection On Open)
        var about = args.SelectedItem == (object)_aboutItem;
        if (about == (ContentFrame.Content is AboutPage))
        {
            return;
        }

        // Drill In: The Old Page Zooms In And Fades Out, The New One Zooms Down To Size And Fades In
        ContentFrame.Navigate(about ? typeof(AboutPage) : typeof(GeneralPage), Model, new DrillInNavigationTransitionInfo());
    }
}
