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
/// light dismiss), and shows the menu. It hides again when the menu closes. Theme, DPI, Esc, outside-click
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
    /// The menu binds to <paramref name="model"/>, so an open menu updates live.
    /// </remarks>
    /// <param name="model">The menu's view model.</param>
    public TrayMenuHost(TrayMenuViewModel model)
    {
        Model = model;
        InitializeComponent();
        _hwnd = new HWND(WindowNative.GetWindowHandle(this));

        // Invisible Topmost Anchor
        WindowStyles.MakeInvisibleHost(this);
    }

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
    /// Used by x:Bind for the status dot.
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

        // Open Above The Cursor, Flipping If Needed
        Menu.ShowAt(Root, new FlyoutShowOptions
        {
            Position  = new Point(0, 0),
            Placement = FlyoutPlacementMode.TopEdgeAlignedLeft,
        });
    }

    internal void RefreshBindings() => Bindings.Update();

    // Closing The Window Closes An Open Menu Too, And A Closed Window Has No AppWindow
    private void Menu_Closed(object sender, object e) => AppWindow?.Hide();

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void Quit_Click(object sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);
}
