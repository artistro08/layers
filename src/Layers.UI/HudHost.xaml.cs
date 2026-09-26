using System.Diagnostics;
using Layers.Core.Logic;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// Hosts the layer-change HUD, a stock <see cref="Flyout"/>.
/// </summary>
/// <remarks>
/// Like <see cref="TrayMenuHost"/>, this is an invisible host window (see <see cref="WindowStyles.MakeInvisibleHost"/>)
/// that a stock flyout opens from. The flyout's content is the box plus a transparent gap, reaching to one pixel
/// short of the bottom of the work area of the monitor the user's "Open HUD on" setting picks (see
/// <see cref="HudPlacement"/> and <see cref="WindowStyles.HudMonitor"/>). The host moves to just above the flyout's
/// top anchor on every open, so it sits on that monitor and the flyout renders at that monitor's scale. The
/// flyout is <see cref="FlyoutShowMode.Transient"/> and the host is shown without activation, so the foreground app
/// keeps focus. Open and close transitions are off, so it appears instantly. The box draws live desktop acrylic through
/// a <c>SystemBackdropElement</c> in its own bounds, so the blur moves, fades, and clips with it. It holds for the
/// user's HUD Show Duration (1.7 s by default) after the last change, then the <c>Exit</c> storyboard slides the box
/// down through the gap while fading it over 150 ms (cubic ease-in), clipped by the flyout's bounds at the edge, and
/// the flyout hides when that finishes.
/// </remarks>
public sealed partial class HudHost : Window
{
    private readonly nint _hwnd;
    private readonly DispatcherQueueTimer _hold;
    private readonly HudLifecycle _lifecycle = new();
    private HudMonitorMode _monitor;
    private bool _windowClosed;

    /// <summary>
    /// Creates the hidden HUD host.
    /// </summary>
    /// <remarks>
    /// Created once at startup and reused for every change.
    /// </remarks>
    public HudHost()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        // Invisible Topmost Anchor
        WindowStyles.MakeInvisibleHost(this);

        // Plain Flyout Background Where Desktop Acrylic Isn't Supported
        if (!DesktopAcrylicController.IsSupported())
        {
            Face.Background = (Brush)Application.Current.Resources["AcrylicBackgroundFillColorDefaultBrush"];
        }

        // Exit Duration, Defined Once
        foreach (var animation in Exit.Children)
        {
            animation.Duration = HudTiming.Exit;
        }

        // Hold Timer, Stopped With The Window, Which Also Ends All HUD Work
        _hold             = DispatcherQueue.CreateTimer();
        _hold.IsRepeating = false;
        _hold.Tick       += (_, _) => Hold_Tick();
        Closed           += (_, _) =>
        {
            _windowClosed = true;
            _hold.Stop();
        };
    }

    /// <summary>Gets the shown label.</summary>
    /// <remarks>
    /// Used by the UI tests, which check the label updates while the HUD is open.
    /// </remarks>
    public string Text => Face.Text;

    /// <summary>
    /// Shows the HUD for a layer state.
    /// </summary>
    /// <remarks>
    /// Opens the flyout at the anchor, or just updates the label if it's already open, and restarts the hold. A change
    /// during the exit stops it, which snaps the box back in place at full opacity. A change while the flyout is
    /// closing reopens it once it has closed. The hold and monitor setting are passed on every show, so a new HUD
    /// Show Duration or "Open HUD on" applies on the next change. The monitor is picked when the flyout opens, so an
    /// open HUD stays where it is.
    /// </remarks>
    /// <param name="layers">The new layers.</param>
    /// <param name="hold">How long the HUD stays up after this change.</param>
    /// <param name="monitor">Which monitor to open on.</param>
    public void Show(LayerMask layers, TimeSpan hold, HudMonitorMode monitor = HudMonitorMode.Primary)
    {
        // Cancel Any Exit And Snap Back
        Exit.Stop();

        // Content And This Show's Hold, Set Before Any Open Starts The Timer
        Face.Text      = layers.Label;
        _hold.Interval = hold;
        _monitor       = monitor;

        // Open Unless Already Up Or Closing
        // A Flyout That Closed On Its Own Counts As Closing, One That Never Opened Resets
        _lifecycle.Lost(Hud.IsOpen);
        if (_lifecycle.Show())
        {
            Open();
        }

        // Restart The Hold
        _hold.Stop();
        _hold.Start();
    }

    private void Open()
    {
        // Nothing To Do After Quit, Or If A Show Already Reopened It Before This Queued Open Ran
        if (_windowClosed || Hud.IsOpen)
        {
            return;
        }

        // Host Sits Just Above The Anchor On The Chosen Monitor
        // So Its Clamped Size Can't Reach A Monitor Below With Another Scale
        // Moved Twice, Since Crossing Into Another Scale Resizes It And Its Bottom Must Still Land On The Anchor
        var (work, scale) = WindowStyles.HudMonitor(_monitor);
        var anchor        = HudPlacement.Anchor(work, scale);
        AppWindow.Move(HudPlacement.HostPosition(anchor, AppWindow.Size.Height));
        AppWindow.Move(HudPlacement.HostPosition(anchor, AppWindow.Size.Height));
        AppWindow.Show(false);
        WindowStyles.BringToTopmost(_hwnd);

        // Mixed-DPI Guard: The DIP Math Below Assumes The Moves Left The Host At The Monitor's Scale
        // Only Traced, Not Used: RasterizationScale Can Lag A Move Until XAML Handles The DPI Change, So The Monitor's
        // Scale Stays The Source Of Truth, And A Mismatch Here May Be That Lag Rather Than A Wrong Placement
        if (Root.XamlRoot is { } root && Math.Abs(root.RasterizationScale - scale) > 0.001)
        {
            Trace.TraceWarning("HUD host renders at {0} on a {1} monitor", root.RasterizationScale, scale);
        }

        // Whole-Pixel Box And Transparent Gap To One Pixel Above The Edge, Which The Exit Slides Through
        Box.Height    = HudPlacement.BoxDips(scale);
        Frame.Padding = new Thickness(0, 0, 0, HudPlacement.GapDips(scale));
        ExitSlide.To  = Box.Height + Frame.Padding.Bottom;

        // Opens Below A Point On The Chosen Monitor, Since A Point On The Edge Row Belongs To A Monitor Below
        // Transient Must Be In The Options, Since Their Default (Auto) Overrides The Flyout's ShowMode And Takes Focus
        Hud.ShowAt(Root, new FlyoutShowOptions
        {
            Position  = HudPlacement.FlyoutPosition(anchor, WindowStyles.ClientOrigin(_hwnd), scale),
            Placement = FlyoutPlacementMode.Bottom,
            ShowMode  = FlyoutShowMode.Transient,
        });

        // Restart The Hold, So A Reopened HUD Always Hides After Its Duration
        _hold.Stop();
        _hold.Start();
    }

    private void Hold_Tick()
    {
        // The Open Failed Silently, So Reset Instead Of Animating Unloaded Content
        if (_lifecycle.Lost(Hud.IsOpen))
        {
            AppWindow.Hide();
            return;
        }

        // Closing (Requested Or On Its Own), So Closed Handles It
        if (!Hud.IsOpen)
        {
            return;
        }

        // Slide Out
        Exit.Begin();
    }

    private void Exit_Completed(object sender, object e)
    {
        // Nothing To Hide After Quit
        // Ignore A Completion Queued Before A Show Stopped The Exit, Or It Would Hide The HUD That Show Brought Back.
        // Relies On The Default FillBehavior (HoldEnd): A Finished Exit Reads Filling, Only Stop() Makes It Stopped
        if (_windowClosed || Exit.GetCurrentState() == ClockState.Stopped)
        {
            return;
        }

        // Close Once, Unless It Already Closed On Its Own
        if (_lifecycle.Hide())
        {
            Hud.Hide();
        }
    }

    private void Hud_Opened(object sender, object e) => _lifecycle.Opened();

    private void Hud_Closed(object sender, object e)
    {
        // Closing The Window Closes The Flyout Too, And A Closed Window Has No AppWindow To Hide Or Reopen In
        if (_windowClosed)
        {
            return;
        }

        // A Show Arrived While Closing, So Reopen Once The Close Has Finished
        if (_lifecycle.Closed() && DispatcherQueue.TryEnqueue(Open))
        {
            return;
        }

        AppWindow.Hide();
    }
}
