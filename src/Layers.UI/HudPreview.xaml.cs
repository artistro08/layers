using Layers.Core.Logic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.UI;

/// <summary>
/// A live demo of the HUD over the bottom of the user's own desktop wallpaper, for the Settings window.
/// </summary>
/// <remarks>
/// A rounded strip, as wide as the page and 150 DIPs tall, that shows the bottom of the wallpaper Windows reports
/// (<c>SPI_GETDESKWALLPAPER</c>), filled across and anchored to its bottom edge. The real HUD's face
/// (<see cref="HudFace"/>) sits at bottom center at its real size, <see cref="HudPlacement.BottomGap"/> above the edge,
/// showing "Layer 1" over in-app acrylic. <see cref="Replay"/> runs the real sequence: appear instantly, hold, then
/// slide down and fade over <see cref="HudTiming.Exit"/> (cubic ease-in), clipped by the strip's rounded bottom edge.
/// At rest the HUD is hidden, and once the exit ends it stays gone (the storyboard holds its end values), like the real
/// one, so every replay visibly brings it up. The demo stops, leaving the HUD hidden, when the preview unloads (the page
/// navigates away or the window closes). While disabled (Show HUD is off) it's dimmed to the stock disabled look, any
/// demo stops, and replays are ignored. With no wallpaper (a solid color), a missing file, or a
/// file that can't load, the strip shows the stock neutral fill.
/// </remarks>
public sealed partial class HudPreview : UserControl
{
    // The Page's Width At 200%, So The Wallpaper Stays Sharp Without Decoding A Full-Size Image
    private const int DecodeWidth = 1200;

    // Dimmed Like The Stock Disabled Controls While Show HUD Is Off
    private const double DisabledOpacity = 0.4;

    private readonly Storyboard _demo = new();
    private readonly DoubleAnimationUsingKeyFrames _slide;
    private readonly DoubleAnimationUsingKeyFrames _fade;

    /// <summary>
    /// Creates the preview.
    /// </summary>
    /// <remarks>
    /// Reads the wallpaper path once. It doesn't follow a wallpaper change while Settings is open.
    /// </remarks>
    public HudPreview()
    {
        InitializeComponent();

        // Same Box Height, Gap, And Label As The Real HUD
        Face.Height = HudPlacement.BoxHeight;
        Face.Margin = new Thickness(0, 0, 0, HudPlacement.BottomGap);
        Face.Text   = new LayerMask(0b10).Label;

        // Decoration Only, So Screen Readers Don't Read "Layer 1"
        Face.HideFromScreenReaders();

        // Appear, Hold, Then Slide Down Through The Gap Past The Edge While Fading, Like The Real HUD
        _slide = Sequence(FaceShift, "Y", 0, HudPlacement.BoxHeight + HudPlacement.BottomGap);
        _fade  = Sequence(Face, "Opacity", 1, 0);
        _demo.Children.Add(_slide);
        _demo.Children.Add(_fade);

        // No Demo Left Running Off Screen
        Unloaded += (_, _) => _demo.Stop();

        // Disabled (Show HUD Off): Dim, And Stop Any Demo, Leaving The HUD Hidden
        // Also On Load, Since A Stored "Off" Arrives Before The Preview Is In The Tree, Without IsEnabledChanged
        IsEnabledChanged += (_, _) => ApplyEnabled();
        Loaded           += (_, _) => ApplyEnabled();

        // An Image That Fails To Load Draws Nothing, So Drop It And Show The Fill
        Wallpaper.ImageFailed += (_, _) => Wallpaper.ImageSource = null;
        ShowWallpaper(WallpaperPath());
    }

    /// <summary>
    /// Replays the HUD's appear, hold, and exit.
    /// </summary>
    /// <remarks>
    /// Called when HUD Show Duration changes. A replay mid-sequence restarts it cleanly: the HUD snaps back in place at
    /// full opacity, then holds for the new duration. Does nothing while the preview is disabled (Show HUD is off).
    /// </remarks>
    /// <param name="hold">How long the HUD holds before its exit.</param>
    public void Replay(TimeSpan hold)
    {
        // No Demo While Show HUD Is Off
        if (!IsEnabled)
        {
            return;
        }

        // Restart With The New Hold
        _demo.Stop();
        foreach (var animation in new[] { _slide, _fade })
        {
            animation.KeyFrames[1].KeyTime = KeyTime.FromTimeSpan(hold);
            animation.KeyFrames[2].KeyTime = KeyTime.FromTimeSpan(hold + HudTiming.Exit);
        }

        _demo.Begin();
    }

    /// <summary>Gets the demo's state: stopped at rest, active while showing or exiting, filling once exited.</summary>
    /// <remarks>For the UI tests.</remarks>
    internal ClockState DemoState => _demo.GetCurrentState();

    /// <summary>
    /// Shows a wallpaper file, or the neutral fill when there's none.
    /// </summary>
    /// <remarks>
    /// Used on creation and by the UI tests for the fallback cases.
    /// </remarks>
    /// <param name="path">The image file, or empty for none.</param>
    internal void ShowWallpaper(string path)
    {
        // No Wallpaper, Or The File Is Gone
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            Wallpaper.ImageSource = null;
            return;
        }

        Wallpaper.ImageSource = new BitmapImage { DecodePixelWidth = DecodeWidth, UriSource = new Uri(path) };
    }

    /// <summary>Gets the HUD's resting opacity (0, hidden) outside any animation.</summary>
    /// <remarks>For the UI tests.</remarks>
    internal double RestingOpacity => Face.Opacity;

    /// <summary>Gets whether a wallpaper image is set.</summary>
    /// <remarks>For the UI tests.</remarks>
    internal bool HasWallpaper => Wallpaper.ImageSource is not null;

    private void ApplyEnabled()
    {
        // Dim While Disabled
        Opacity = IsEnabled ? 1 : DisabledOpacity;

        // Nothing To Stop While Enabled
        if (IsEnabled)
        {
            return;
        }

        _demo.Stop();
    }

    private static DoubleAnimationUsingKeyFrames Sequence(DependencyObject target, string property, double shown, double gone)
    {
        // Shown From The Start (So A Replay After An Exit Appears At Once), Held, Then Eased Out (Times Set On Replay)
        // Plain Animations With A BeginTime Leave The Previous Run's Held End Value On Screen Until They Start
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = shown });
        animation.KeyFrames.Add(new DiscreteDoubleKeyFrame { Value = shown });
        animation.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            Value          = gone,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        });
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, property);
        return animation;
    }

    private static unsafe string WallpaperPath()
    {
        // Empty When The Desktop Is A Solid Color
        const int MaxPath = 260;
        var buffer        = stackalloc char[MaxPath];
        return PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETDESKWALLPAPER, MaxPath, buffer, 0)
            ? new string(buffer)
            : string.Empty;
    }
}
