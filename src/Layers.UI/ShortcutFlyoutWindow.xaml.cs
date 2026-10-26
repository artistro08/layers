using System.Numerics;
using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;
using WinRT.Interop;

namespace Layers.UI;

/// <summary>
/// Borderless, always-on-top, see-through window that holds the shortcut sheet's acrylic panel.
/// </summary>
/// <remarks>
/// <para>
/// The window never moves while open: it sits flush on the taskbar edge, as tall as the work area allows and one
/// margin wider than the panel. Only the panel is visible, and it slides in and out inside the window, so the
/// window's own edge at the taskbar cuts it off and it looks like it comes out from behind the taskbar. The window
/// region is clipped to the panel plus its margin, so the empty part lets clicks through.
/// </para>
/// <para>
/// It hides when it loses focus, like Windows' own flyouts. The click on the tray icon that takes focus away arrives
/// right after the hide, so a toggle within 300 ms of hiding is ignored. Closing hides; only <see cref="Shutdown"/>
/// destroys it. Ported from MagicBuds.
/// </para>
/// </remarks>
public sealed partial class ShortcutFlyoutWindow : Window
{
    private const long ReopenGuardMilliseconds = 300;

    // The Window Asks For The Tallest The Work Area Allows; The Panel Inside Is Only As Tall As Its Content
    private const double TallestDip = 10_000;

    // Fluent Motion: https://learn.microsoft.com/windows/apps/design/motion/timing-and-easing
    private static readonly TimeSpan EnterDuration = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ExitDuration  = TimeSpan.FromMilliseconds(167);

    private readonly HWND _hwnd;
    private readonly Visual _slideVisual;
    private readonly Compositor _compositor;
    private long _lastHiddenAt;
    private bool _shuttingDown;
    private bool _isOpen;
    private TaskbarEdge _edge;
    private HWND _returnTo;
    private (int Left, int Top, int Right, int Bottom) _clip;
    private CompositionScopedBatch? _slide;
    private Storyboard? _resize;

    /// <summary>
    /// Creates the window, hidden.
    /// </summary>
    /// <remarks>
    /// The sheet view binds to <paramref name="model"/> and follows it live.
    /// </remarks>
    /// <param name="model">The sheet's view model.</param>
    public ShortcutFlyoutWindow(ShortcutSheetViewModel model)
    {
        InitializeComponent();

        View = new ShortcutSheetView(model);
        View.CloseRequested    += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        View.SettingsRequested += (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty);
        View.EditRequested     += (_, _) => EditRequested?.Invoke(this, EventArgs.Empty);
        ViewHost.Content        = View;

        _hwnd = new HWND(WindowNative.GetWindowHandle(this));

        // Window Chrome
        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable   = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        presenter.SetBorderAndTitleBar(false, false);
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Plain Frameless Popup; The Panel Inside Draws Its Own Rounded Corners And Stroke
        PopupWindowStyle.Apply(_hwnd);

        // Slide Animation Target
        ElementCompositionPreview.SetIsTranslationEnabled(SlideHost, true);
        _slideVisual = ElementCompositionPreview.GetElementVisual(SlideHost);
        _compositor  = _slideVisual.Compositor;

        // Live Resize: The Panel's Height Animates To The Content's, The Region Follows The Panel
        View.SizeChanged         += (_, _) => ResizePanel(animate: _isOpen);
        WindowRoot.LayoutUpdated += (_, _) => ClipToPanel();

        // Closing Hides; Losing Focus Hides
        AppWindow.Closing += (_, e) =>
        {
            if (!_shuttingDown)
            {
                e.Cancel = true;
                HideFlyout();
            }
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && _isOpen)
            {
                HideFlyout();
            }
        };
    }

    /// <summary>Raised when the flyout has been asked to show.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised by Esc.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised by the footer's settings button.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised by the footer's edit button.</summary>
    public event EventHandler? EditRequested;

    /// <summary>Gets a value indicating whether the flyout is open (or still sliding in).</summary>
    public bool IsOpen => _isOpen;

    internal ShortcutSheetView View { get; }

    /// <summary>
    /// Opens the flyout if it isn't already.
    /// </summary>
    /// <remarks>
    /// The tray menu's Shortcuts item uses this.
    /// </remarks>
    /// <param name="iconRect">The tray icon's bounds, or <see langword="null"/> to use the cursor's monitor.</param>
    public void Open(RectInt32? iconRect)
    {
        if (!_isOpen)
        {
            ShowFlyout(iconRect);
        }
    }

    /// <summary>
    /// Opens the flyout, or closes it when open.
    /// </summary>
    /// <remarks>
    /// The tray left-click and the hotkey use this. A toggle right after a hide is the click that caused the hide, and is ignored.
    /// </remarks>
    /// <param name="iconRect">The tray icon's bounds, or <see langword="null"/> to use the cursor's monitor.</param>
    public void Toggle(RectInt32? iconRect)
    {
        if (_isOpen)
        {
            HideFlyout();
            return;
        }

        if (Environment.TickCount64 - _lastHiddenAt < ReopenGuardMilliseconds)
        {
            return;
        }

        ShowFlyout(iconRect);
    }

    /// <summary>
    /// Slides the panel back behind the taskbar, then hides the window.
    /// </summary>
    /// <remarks>
    /// Focus goes back first, so keys don't land in the closing window: to the window that had it before the flyout
    /// opened, or to the taskbar if that window is gone.
    /// </remarks>
    public void HideFlyout()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen       = false;
        _lastHiddenAt = Environment.TickCount64;

        // Back To The Window That Had The Keyboard, If It Still Exists; Else The Taskbar
        if (PInvoke.GetForegroundWindow() == _hwnd)
        {
            var target = !_returnTo.IsNull && PInvoke.IsWindow(_returnTo) ? _returnTo : PInvoke.FindWindow("Shell_TrayWnd", null);
            PInvoke.SetForegroundWindow(target);
        }

        _returnTo = HWND.Null;
        Slide(HiddenOffset(), ExitDuration, new Vector2(1, 0), new Vector2(1, 1));
    }

    /// <summary>
    /// Lets the window really close when the app quits.
    /// </summary>
    /// <remarks>
    /// Before this, a close only hides.
    /// </remarks>
    public void Shutdown()
    {
        _shuttingDown = true;
        _isOpen       = false;
        _slide        = null;
        Close();
    }

    // =========================================================================
    // SHOW
    // =========================================================================

    private void ShowFlyout(RectInt32? iconRect)
    {
        _isOpen = true;

        // Remember Whose Window Is In Front, So A Close Hands The Keyboard Back To It
        var front = PInvoke.GetForegroundWindow();
        _returnTo = front == _hwnd ? _returnTo : front;

        // Still Sliding Out From A Close That Hasn't Finished: Turn Around From Where It Is
        if (!AppWindow.IsVisible)
        {
            PlaceWindow(iconRect);
            _slideVisual.Properties.InsertVector3("Translation", HiddenOffset());
            AppWindow.Show(true);
        }

        Activate();
        PInvoke.SetForegroundWindow(_hwnd);
        Slide(Vector3.Zero, EnterDuration, new Vector2(0, 0), new Vector2(0, 1));
        View.FocusList();
        Opened?.Invoke(this, EventArgs.Empty);
    }

    // Window = The Tallest The Flyout May Get, One Margin Wider Than The Panel, Flush On The Taskbar Edge. The Panel
    // Sits Against The Taskbar Side And Is Only As Tall As Its Content, So It Grows And Shrinks Live.
    private void PlaceWindow(RectInt32? iconRect)
    {
        var (monitor, workArea, scale) = ScreenGeometry.MonitorFor(iconRect);
        var panel  = FlyoutPlacement.Calculate(monitor, workArea, scale, TallestDip);
        var margin = (int)Math.Round(FlyoutPlacement.MarginDip * scale);
        _edge      = FlyoutPlacement.DetectEdge(monitor, workArea);
        SlideHost.VerticalAlignment = _edge == TaskbarEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // ponytail: sized twice because moving to a monitor with another DPI rescales the window once it's shown;
        // a WM_DPICHANGED hook would do it in one pass.
        var bounds = new RectInt32(panel.Left - margin, panel.Top - margin, panel.Width + (2 * margin), panel.Height + (2 * margin));
        AppWindow.MoveAndResize(bounds);
        AppWindow.MoveAndResize(bounds);

        WindowRoot.UpdateLayout();
        ResizePanel(animate: false);
        WindowRoot.UpdateLayout();
        _clip = default;
        ClipToPanel();
    }

    // Panel Height = Content Height Plus Its Border, Capped At What The Window Allows. The View Gets The Cap As Its
    // MaxHeight, So Its Own List Scrolls Instead Of Being Cut.
    private void ResizePanel(bool animate)
    {
        var tallest = WindowRoot.ActualHeight - WindowRoot.Padding.Top - WindowRoot.Padding.Bottom - Panel.BorderThickness.Top - Panel.BorderThickness.Bottom;
        if (tallest > 0)
        {
            View.MaxHeight = tallest;
        }

        var target  = View.ActualHeight + Panel.BorderThickness.Top + Panel.BorderThickness.Bottom;
        var current = Panel.ActualHeight;
        _resize?.Stop();
        _resize = null;
        if (!animate || current <= 0 || Math.Abs(current - target) < 0.5)
        {
            Panel.Height = target;
            return;
        }

        Panel.Height = current;
        var animation = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = true };
        animation.KeyFrames.Add(new SplineDoubleKeyFrame
        {
            KeyTime   = EnterDuration,
            Value     = target,
            KeySpline = new KeySpline { ControlPoint1 = new Windows.Foundation.Point(0, 0), ControlPoint2 = new Windows.Foundation.Point(0, 1) },
        });
        Storyboard.SetTarget(animation, Panel);
        Storyboard.SetTargetProperty(animation, "Height");

        var resize = new Storyboard();
        resize.Children.Add(animation);
        resize.Completed += (_, _) =>
        {
            if (_resize == resize)
            {
                Panel.Height = target;
                resize.Stop();
                _resize = null;
            }
        };
        _resize = resize;
        resize.Begin();
    }

    // Far Enough To Put The Whole Panel Past The Window Edge At The Taskbar, In DIPs
    private Vector3 HiddenOffset()
    {
        var width  = (float)(SlideHost.ActualWidth + FlyoutPlacement.MarginDip);
        var height = (float)(SlideHost.ActualHeight + FlyoutPlacement.MarginDip);
        return _edge switch
        {
            TaskbarEdge.Top   => new Vector3(0, -height, 0),
            TaskbarEdge.Left  => new Vector3(-width, 0, 0),
            TaskbarEdge.Right => new Vector3(width, 0, 0),
            _                 => new Vector3(0, height, 0),
        };
    }

    // Window Region = The Panel Plus Its Margin (Room For The Shadow), Stretched To The Taskbar Edge So The Slide
    // Still Shows. Everything Else Doesn't Draw And Lets Clicks Through.
    private void ClipToPanel()
    {
        if (WindowRoot.XamlRoot is not { } root)
        {
            return;
        }

        var scale  = root.RasterizationScale;
        var top    = SlideHost.ActualOffset.Y - FlyoutPlacement.MarginDip;
        var bottom = SlideHost.ActualOffset.Y + SlideHost.ActualHeight + FlyoutPlacement.MarginDip;
        if (_edge == TaskbarEdge.Top)
        {
            top = 0;
        }
        else
        {
            bottom = WindowRoot.ActualHeight;
        }

        var clip = (0, (int)Math.Floor(top * scale), (int)Math.Ceiling(WindowRoot.ActualWidth * scale), (int)Math.Ceiling(bottom * scale));
        if (clip == _clip)
        {
            return;
        }

        _clip = clip;

        // The Window Owns The Region After This Call
        var region = PInvoke.CreateRectRgn(clip.Item1, clip.Item2, clip.Item3, clip.Item4);
        _ = PInvoke.SetWindowRgn(_hwnd, region, true);
    }

    // Animates The Panel's Translation From Wherever It Is Now; Hides The Window Once A Close Finishes, Unless It Was
    // Reopened In The Meantime
    private void Slide(Vector3 to, TimeSpan duration, Vector2 control1, Vector2 control2)
    {
        var animation = _compositor.CreateVector3KeyFrameAnimation();
        animation.InsertExpressionKeyFrame(0, "this.StartingValue");
        animation.InsertKeyFrame(1, to, _compositor.CreateCubicBezierEasingFunction(control1, control2));
        animation.Duration = duration;

        var slide = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        _slideVisual.StartAnimation("Translation", animation);
        slide.End();
        _slide = slide;

        slide.Completed += (_, _) =>
        {
            // A Batch Finishing After Shutdown Must Not Touch The Closed Window: That Throws Inside A Composition Callback
            if (_slide == slide && !_isOpen && !_shuttingDown)
            {
                AppWindow.Hide();
            }
        };
    }
}
