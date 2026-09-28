using System.ComponentModel;
using Layers.Core.ViewModels;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Foundation;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// The Settings window.
/// </summary>
/// <remarks>
/// Stock controls on a Mica backdrop, under a stock <see cref="TitleBar"/> that carries the app icon, the pane
/// toggle, and our own Minimize and Close caption buttons. Windows can't show Minimize without a greyed Maximize, so
/// the system caption buttons are removed and these two stand in for them: stock buttons restyled to match the
/// Windows 11 ones, registered with Windows as the window's minimize and close areas, with hover and press driven by
/// Windows' pointer events. A stock left <see cref="NavigationView"/> with a narrow pane switches between General
/// (every setting) and About (author, version, and license links, at the bottom of the pane), and General is selected
/// on open. The pages are stock <see cref="Page"/>s in a <see cref="Frame"/>, switched with the stock drill-in
/// transition, and share the window's one view model through the navigation parameter. The window is just tall
/// enough for the General page without a scrollbar. Only one window exists at a time: <see cref="Open"/> activates it
/// if it's already open. Closing destroys it to free memory. The app keeps running. Every change saves immediately
/// through the view model.
/// </remarks>
public sealed partial class SettingsWindow : Window
{
    // The 160 DIP Pane Plus A 600 DIP Content Column, Just Tall Enough For The Whole General Page Without A Scrollbar
    // Measured: The 48 DIP Title Bar Plus The Page's 545 DIPs, Plus The Window's 1 Pixel Top Border And Rounding
    internal const int LogicalWidth  = 760;
    internal const int LogicalHeight = 595;

    private readonly NavigationViewItem _generalItem = NavItem("General", new FontIcon { Glyph = char.ConvertFromUtf32(0xE713) });
    private readonly NavigationViewItem _aboutItem   = NavItem("About", new FontIcon { Glyph = char.ConvertFromUtf32(0xE946) });
    private readonly InputNonClientPointerSource _pointer;
    private readonly GeneralPage _general;
    private XamlRoot? _xamlRoot;
    private bool _closed;

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
        _general         = (GeneralPage)ContentFrame.Content;

        // Fixed Size, With Our Own Minimize And Close In Place Of The System Caption Buttons
        // Windows Draws Maximize (Greyed) Whenever Minimize Is Allowed, So The System Ones Go, Keeping The Border
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = true;
        AppWindow.SetPresenter(presenter);

        // Mica And Stock Title Bar
        SystemBackdrop             = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // App Icon On The Window And In The Title Bar
        var icon = Path.Combine(AppContext.BaseDirectory, "app.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            AppTitleBar.IconSource = new ImageIconSource { ImageSource = new BitmapImage(new Uri(icon)) };
        }

        // Caption Buttons Registered With Windows Wherever They Land, So It Treats Them As Real Minimize And Close
        CaptionButtons.SizeChanged += CaptionButtons_SizeChanged;
        SizeChanged                += Window_SizeChanged;
        Root.Loaded                += Root_Loaded;

        // Hover And Press Shown From Windows' Pointer Events, Since Caption Button Input Never Reaches XAML
        // Released Goes Back To Rest, Since Windows Then Minimizes Or Closes The Window
        _pointer                  = InputNonClientPointerSource.GetForWindowId(AppWindow.Id);
        _pointer.PointerEntered  += Pointer_Entered;
        _pointer.PointerExited   += Pointer_Exited;
        _pointer.PointerPressed  += Pointer_Pressed;
        _pointer.PointerReleased += Pointer_Released;

        // Caption Glyphs Dim While The Window Is Inactive, Like The Stock Ones
        Activated += Window_Activated;

        // Taller Only While The Startup Note Shows, So The General Page Never Scrolls
        model.PropertyChanged += Model_PropertyChanged;
        _general.Loaded       += General_Loaded;

        // Nothing Runs After Closing, Since The Window's Input Source And XAML Are Gone
        Closed += Window_Closed;
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
    /// A new window is moved onto the monitor under the cursor first, so Windows rescales it for that monitor, then
    /// sized from its own DPI and centered on that monitor's work area.
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

        // Create, Then Move Onto The Monitor Under The Cursor, Centered At Its Current Size
        // Moving First Lets Windows Rescale It For That Monitor Now, Instead Of Scaling Our Size A Second Time
        var window = new SettingsWindow(createModel());
        PInvoke.GetCursorPos(out var cursor);
        var work = DisplayArea.GetFromPoint(new PointInt32(cursor.X, cursor.Y), DisplayAreaFallback.Nearest).WorkArea;
        window.AppWindow.Move(Centered(work, window.AppWindow.Size));

        // Then Sized From The Window's Own DPI, Rounded Up So The Page Never Needs A Scrollbar, And Re-Centered
        // The Frame Is Measured, Since ResizeClient Counts A Title Bar This Window Doesn't Draw
        var scale  = PInvoke.GetDpiForWindow(new HWND(WindowNative.GetWindowHandle(window))) / 96.0;
        var frameW = window.AppWindow.Size.Width - window.AppWindow.ClientSize.Width;
        var frameH = window.AppWindow.Size.Height - window.AppWindow.ClientSize.Height;
        var width  = (int)Math.Ceiling(LogicalWidth * scale) + frameW;
        var height = (int)Math.Ceiling(LogicalHeight * scale) + frameH;
        window.AppWindow.Resize(new SizeInt32(width, height));
        window.AppWindow.Move(Centered(work, window.AppWindow.Size));

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

    // Centered On The Work Area, With The Title Bar Never Above Its Top
    private static PointInt32 Centered(RectInt32 work, SizeInt32 size) =>
        new(
            work.X + ((work.Width - size.Width) / 2),
            Math.Max(work.Y, work.Y + ((work.Height - size.Height) / 2)));

    // =========================================================================
    // CAPTION BUTTONS
    // =========================================================================

    // Tells Windows Where Minimize And Close Are, In Physical Client Pixels
    private void RegisterCaptionButtons()
    {
        // Nothing To Measure Before The First Layout, Or After Closing
        if (_closed || !CloseButton.IsLoaded || Root.XamlRoot is null)
        {
            return;
        }

        _pointer.SetRegionRects(NonClientRegionKind.Minimize, new[] { PixelBounds(MinimizeButton) });
        _pointer.SetRegionRects(NonClientRegionKind.Close, new[] { PixelBounds(CloseButton) });
    }

    private RectInt32 PixelBounds(FrameworkElement element)
    {
        var scale  = Root.XamlRoot.RasterizationScale;
        var bounds = element.TransformToVisual(null)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return new RectInt32(
            (int)Math.Round(bounds.X * scale),
            (int)Math.Round(bounds.Y * scale),
            (int)Math.Round(bounds.Width * scale),
            (int)Math.Round(bounds.Height * scale));
    }

    private void ShowCaptionState(NonClientRegionKind region, string state)
    {
        // Nothing To Show After Closing
        if (_closed || !CloseButton.IsLoaded)
        {
            return;
        }

        Button? button = region switch
        {
            NonClientRegionKind.Minimize => MinimizeButton,
            NonClientRegionKind.Close    => CloseButton,
            _                            => null,
        };

        if (button is not null)
        {
            VisualStateManager.GoToState(button, state, true);
        }
    }

    // The Stock Disabled Text Color While Inactive, The Button's Own Otherwise
    // Read From The Panel's ThemeResource, Since An Application Lookup Returns The Launch Theme's Brush
    // Back To Rest On Deactivation Too, Since A Minimize Takes The Window Away With No Pointer Exit
    // Activation Leaves The State Alone, So A Press That Activated The Window Still Shows
    private void ResetCaptionButtons(bool inactive)
    {
        // Nothing To Reset After Closing
        if (_closed || !CloseButton.IsLoaded)
        {
            return;
        }

        foreach (var button in new[] { MinimizeButton, CloseButton })
        {
            if (inactive)
            {
                VisualStateManager.GoToState(button, "Normal", false);
                button.Foreground = CaptionButtons.BorderBrush;
            }
            else
            {
                button.ClearValue(Control.ForegroundProperty);
            }
        }
    }

    private void CaptionButtons_SizeChanged(object sender, SizeChangedEventArgs e) => RegisterCaptionButtons();

    private void Window_SizeChanged(object sender, WindowSizeChangedEventArgs args) => RegisterCaptionButtons();

    private void XamlRoot_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => RegisterCaptionButtons();

    // Loaded Can Fire More Than Once, So The XamlRoot Is Watched Only Once
    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_xamlRoot is null && Root.XamlRoot is { } root)
        {
            _xamlRoot          = root;
            _xamlRoot.Changed += XamlRoot_Changed;
        }

        RegisterCaptionButtons();
    }

    private void Pointer_Entered(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        ShowCaptionState(args.RegionKind, "PointerOver");

    private void Pointer_Exited(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        ShowCaptionState(args.RegionKind, "Normal");

    private void Pointer_Pressed(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        ShowCaptionState(args.RegionKind, "Pressed");

    private void Pointer_Released(InputNonClientPointerSource sender, NonClientPointerEventArgs args) =>
        ShowCaptionState(args.RegionKind, "Normal");

    private void Window_Activated(object sender, WindowActivatedEventArgs args) =>
        ResetCaptionButtons(args.WindowActivationState == WindowActivationState.Deactivated);

    // =========================================================================
    // STARTUP NOTE HEIGHT
    // =========================================================================

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Refit Once The Note's New Visibility Has Been Laid Out
        if (e.PropertyName == nameof(SettingsViewModel.HasStartupNote))
        {
            DispatcherQueue.TryEnqueue(FitStartupNote);
        }
    }

    private void General_Loaded(object sender, RoutedEventArgs e) => FitStartupNote();

    // The Logical Height, Or Tall Enough For The General Page While Its Startup Note Shows, Kept Centered
    private void FitStartupNote()
    {
        // Nothing To Fit After Closing, Or While The General Page Isn't Shown (Its Loaded Refits It)
        if (_closed || !_general.IsLoaded || Root.XamlRoot is null)
        {
            return;
        }

        // Needed Client Height: The Page's Top Plus Its Content, Plus The 1 Pixel Top Border Above The XAML
        var scale  = PInvoke.GetDpiForWindow(new HWND(WindowNative.GetWindowHandle(this))) / 96.0;
        var height = (int)Math.Ceiling(LogicalHeight * scale);
        if (Model.HasStartupNote)
        {
            height = Math.Max(height, (int)Math.Ceiling(_general.NeededHeight(Root) * scale) + 1);
        }

        // Already That Tall
        var client = AppWindow.ClientSize;
        if (client.Height == height)
        {
            return;
        }

        // Grow Or Shrink About The Current Center, Never Above The Work Area
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        var size = new SizeInt32(AppWindow.Size.Width, AppWindow.Size.Height + height - client.Height);
        var top  = AppWindow.Position.Y - ((size.Height - AppWindow.Size.Height) / 2);
        AppWindow.MoveAndResize(new RectInt32(AppWindow.Position.X, Math.Max(work.Y, top), size.Width, size.Height));
    }

    // =========================================================================
    // CLOSING
    // =========================================================================

    // Every Handler Comes Off, Since Windows And XAML Can Still Raise Events While The Window Tears Down
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        _closed = true;
        Current = null;

        CaptionButtons.SizeChanged -= CaptionButtons_SizeChanged;
        SizeChanged                -= Window_SizeChanged;
        Root.Loaded                -= Root_Loaded;
        Activated                  -= Window_Activated;
        Model.PropertyChanged      -= Model_PropertyChanged;
        _general.Loaded            -= General_Loaded;

        _pointer.PointerEntered  -= Pointer_Entered;
        _pointer.PointerExited   -= Pointer_Exited;
        _pointer.PointerPressed  -= Pointer_Pressed;
        _pointer.PointerReleased -= Pointer_Released;

        if (_xamlRoot is not null)
        {
            _xamlRoot.Changed -= XamlRoot_Changed;
        }
    }

    // Keyboard Presses Land Here; Pointer Clicks Are Handled By Windows As Real Caption Buttons
    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        ((OverlappedPresenter)AppWindow.Presenter).Minimize();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
