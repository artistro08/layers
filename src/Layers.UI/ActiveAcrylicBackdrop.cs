using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Layers.UI;

/// <summary>
/// Desktop acrylic that stays live on a window that's never active.
/// </summary>
/// <remarks>
/// The stock <see cref="DesktopAcrylicBackdrop"/> shows its solid fallback color whenever its window isn't the active
/// one, and the HUD never takes focus, so it would always look like a flat tint. This is the stock
/// <see cref="DesktopAcrylicController"/> with a configuration that always reports input as active, so the HUD gets the
/// same translucent blur as the Windows virtual-desktop switcher.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001",
    Justification = "XAML owns the backdrop's lifetime; the controller is disposed when its target disconnects.")]
public sealed partial class ActiveAcrylicBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    /// <inheritdoc/>
    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        ArgumentNullException.ThrowIfNull(connectedTarget);
        base.OnTargetConnected(connectedTarget, xamlRoot);

        // No Acrylic Here (HudHost Paints A Plain Background Instead)
        if (!DesktopAcrylicController.IsSupported())
        {
            return;
        }

        // Live Acrylic, Following The App Theme (Releasing Any Controller A Missed Disconnect Left Behind)
        _controller?.Dispose();
        _controller = new DesktopAcrylicController();
        _controller.SetSystemBackdropConfiguration(new SystemBackdropConfiguration
        {
            IsInputActive = true,
            Theme         = (xamlRoot?.Content as FrameworkElement)?.ActualTheme == ElementTheme.Light
                ? SystemBackdropTheme.Light
                : SystemBackdropTheme.Dark,
        });
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    /// <inheritdoc/>
    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        ArgumentNullException.ThrowIfNull(disconnectedTarget);
        base.OnTargetDisconnected(disconnectedTarget);

        // Release The Controller
        _controller?.RemoveSystemBackdropTarget(disconnectedTarget);
        _controller?.Dispose();
        _controller = null;

        // Close The Target Here, On The UI Thread
        // XAML drops each disconnected target without closing it, so its last release comes from the GC finalizer
        // thread, where closing its UI-thread content site fails fast (RPC_E_WRONG_THREAD in Microsoft.UI.Input.dll)
        (disconnectedTarget as IDisposable)?.Dispose();
    }
}
