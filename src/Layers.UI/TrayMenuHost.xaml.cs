using System.ComponentModel;
using Layers.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// Hosts the stock tray menu.
/// </summary>
/// <remarks>
/// A stock <see cref="MenuFlyout"/> needs a XAML root to open from, so this is an invisible host window (see
/// <see cref="WindowStyles.MakeInvisibleHost"/>). On open it moves to the cursor, takes the foreground (required for
/// light dismiss), and shows the menu. It hides when the menu closes, and the app then closes it, so the menu only
/// takes memory while it's open. Theme, DPI, Esc, outside-click
/// dismissal, keyboard, and screen reader support all come from the stock control.
/// Every item pins <c>MenuFlyoutItemThemePaddingNarrow</c>. Otherwise the stock menu picks its touch-sized padding
/// whenever the host hasn't seen mouse or keyboard input yet, which made the first open after launch oversized.
/// Every icon fills the item's 16x16 icon box (explicit <c>Width</c>/<c>Height</c>), because the box is a
/// <c>Viewbox</c> that would otherwise stretch a <c>PathIcon</c> by its geometry's extent and push it off center.
/// </remarks>
public sealed partial class TrayMenuHost : Window
{
    private readonly HWND _hwnd;

    /// <summary>
    /// Creates the host.
    /// </summary>
    /// <remarks>
    /// The status rows follow <paramref name="model"/>, so an open menu updates live. They're filled in code rather
    /// than with x:Bind, because x:Bind on a window hooks the window's own Activated, which keeps a closed window in
    /// memory for good.
    /// </remarks>
    /// <param name="model">The menu's view model.</param>
    public TrayMenuHost(TrayMenuViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = model;
        InitializeComponent();
        _hwnd = new HWND(WindowNative.GetWindowHandle(this));

        // Invisible Topmost Anchor
        WindowStyles.MakeInvisibleHost(this);

        // Status Rows, Now And On Every Change
        LayerIcon.Data         = Glyphs.Layers(16);
        Model.PropertyChanged += Model_PropertyChanged;
        RefreshRows();

        // Unhooked Again On Close, Or The Closed Window Stays In Memory
        Closed += Window_Closed;
    }

    /// <summary>Raised when the menu has closed, so the app can close this host.</summary>
    public event EventHandler? MenuClosed;

    /// <summary>Raised when Shortcuts is clicked.</summary>
    public event EventHandler? ShortcutsRequested;

    /// <summary>Raised when Settings… is clicked.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised when Quit is clicked.</summary>
    public event EventHandler? QuitRequested;

    /// <summary>Gets the menu's view model.</summary>
    public TrayMenuViewModel Model { get; }

    /// <summary>Gets a value indicating whether the menu is open.</summary>
    public bool IsMenuOpen => Menu.IsOpen;

    internal IReadOnlyList<MenuFlyoutItemBase> MenuItems => Menu.Items.ToList();

    /// <summary>
    /// Maps a status tone to a stock theme brush.
    /// </summary>
    /// <remarks>
    /// Colors the status dot.
    /// </remarks>
    /// <param name="tone">The tone.</param>
    /// <returns>The brush.</returns>
    public static Brush ToneBrush(StatusTone tone) => (Brush)Application.Current.Resources[tone switch
    {
        StatusTone.Success => "SystemFillColorSuccessBrush",
        StatusTone.Caution => "SystemFillColorCautionBrush",
        _                  => "SystemFillColorCriticalBrush",
    }];

    /// <summary>
    /// Opens the menu at the cursor.
    /// </summary>
    /// <remarks>
    /// Called from the tray icon's click handler, which is the moment Windows allows taking the foreground.
    /// </remarks>
    public void ShowAtCursor()
    {
        // Move To The Cursor
        PInvoke.GetCursorPos(out var cursor);
        AppWindow.Move(new PointInt32(cursor.X, cursor.Y));

        // Take The Foreground So Light Dismiss Works
        AppWindow.Show(true);
        Activate();
        PInvoke.SetForegroundWindow(_hwnd);

        // A Menu Asked To Open Before Its New Window Has Loaded Silently Doesn't, So A Fresh Host Waits For Load
        if (Root.IsLoaded)
        {
            OpenMenu();
        }
        else
        {
            // Two Quick Right-Clicks Reach Here Twice Before Load, And The Menu Must Still Open Once
            Root.Loaded -= Root_Loaded;
            Root.Loaded += Root_Loaded;
        }
    }

    // Open Above The Cursor, Flipping If Needed
    private void OpenMenu() => Menu.ShowAt(Root, new FlyoutShowOptions
    {
        Position  = new Point(0, 0),
        Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
    });

    private void Root_Loaded(object sender, RoutedEventArgs e)
    {
        Root.Loaded -= Root_Loaded;
        OpenMenu();
    }

    // Fills The Status, Detail, And Layer Rows From The Model
    private void RefreshRows()
    {
        StatusItem.Text       = Model.StatusLabel;
        StatusDot.Foreground  = ToneBrush(Model.Tone);
        DetailItem.Text       = Model.StatusDetail;
        DetailItem.Visibility = Model.HasStatusDetail ? Visibility.Visible : Visibility.Collapsed;
        LayerItem.Text        = Model.LayerLabel;
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e) => RefreshRows();

    // A Window Still Handling Its Own Or Its Menu's Events Is Never Freed, So Closing Unhooks Them All
    private void Window_Closed(object sender, WindowEventArgs args)
    {
        Closed                -= Window_Closed;
        Root.Loaded           -= Root_Loaded;
        Menu.Closed           -= Menu_Closed;
        ShortcutsItem.Click   -= Shortcuts_Click;
        SettingsItem.Click    -= Settings_Click;
        QuitItem.Click        -= Quit_Click;
        Model.PropertyChanged -= Model_PropertyChanged;
    }

    // Closing The Window Closes An Open Menu Too, And A Closed Window Has No AppWindow
    private void Menu_Closed(object? sender, object e)
    {
        AppWindow?.Hide();
        MenuClosed?.Invoke(this, EventArgs.Empty);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShortcutsRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
