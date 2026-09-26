namespace Layers.Core.Logic;

/// <summary>
/// The user's HUD preferences.
/// </summary>
/// <remarks>
/// Persisted by <c>SettingsStore</c> as <c>HudEnabled</c>, <c>HudSuppressedLayers</c>, <c>HudHoldMs</c>, and
/// <c>HudMonitor</c>.
/// </remarks>
/// <param name="HudEnabled">Master switch.</param>
/// <param name="HudSuppressedLayers">Bit <c>n</c> set means layer <c>n</c> is muted.</param>
/// <param name="HudHoldMs">How long the HUD stays up after the last change, in milliseconds.</param>
/// <param name="HudMonitor">Which monitor the HUD opens on. Primary unless given.</param>
public readonly record struct HudSettings(
    bool HudEnabled,
    byte HudSuppressedLayers,
    int HudHoldMs,
    HudMonitorMode HudMonitor = HudMonitorMode.Primary)
{
    /// <summary>The shortest allowed hold, in milliseconds.</summary>
    public const int MinHoldMs = 500;

    /// <summary>The longest allowed hold, in milliseconds.</summary>
    public const int MaxHoldMs = 5000;

    /// <summary>
    /// Gets the defaults: enabled, nothing muted, the standard hold.
    /// </summary>
    /// <remarks>
    /// Used when a registry value is missing or unreadable. The hold comes from <see cref="HudTiming.Hold"/>.
    /// </remarks>
    public static HudSettings Default => new(true, 0, (int)HudTiming.Hold.TotalMilliseconds);

    /// <summary>
    /// Gets the hold as a time span.
    /// </summary>
    /// <remarks>
    /// What the HUD's hold timer uses.
    /// </remarks>
    public TimeSpan Hold => TimeSpan.FromMilliseconds(HudHoldMs);

    /// <summary>
    /// Limits a hold to the allowed range.
    /// </summary>
    /// <remarks>
    /// Used on load and when the Settings slider changes it.
    /// </remarks>
    /// <param name="ms">The hold in milliseconds.</param>
    /// <returns>The hold, between <see cref="MinHoldMs"/> and <see cref="MaxHoldMs"/>.</returns>
    public static int ClampHold(int ms) => Math.Clamp(ms, MinHoldMs, MaxHoldMs);
}

/// <summary>
/// Which monitor the HUD opens on, the "Open HUD on" setting.
/// </summary>
/// <remarks>
/// Stored as the <c>HudMonitor</c> DWORD, so the values are fixed. Evaluated on every show.
/// </remarks>
public enum HudMonitorMode
{
    /// <summary>The primary monitor, the default.</summary>
    Primary = 0,

    /// <summary>The monitor holding the mouse cursor.</summary>
    Cursor = 1,

    /// <summary>The monitor holding the foreground window, or the primary when there's none.</summary>
    FocusedWindow = 2,
}

/// <summary>
/// Decides when the HUD appears.
/// </summary>
/// <remarks>
/// Ported from <c>settings.rs</c> and <c>main.rs</c> in Layers 1.0.3.
/// </remarks>
public static class HudRules
{
    /// <summary>
    /// Whether a HUD may show for this layer state.
    /// </summary>
    /// <remarks>
    /// Every active layer must be allowed, not just the highest. Holding a muted layer 1 while layer 5 is active
    /// gives "1, 5", and muting layer 1 has to mean silence there too.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="layers">The layer state.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public static bool Allowed(HudSettings settings, LayerMask layers) =>
        settings.HudEnabled && (layers.Bits & settings.HudSuppressedLayers) == 0;

    /// <summary>
    /// Whether a HUD may show for a move between two layer states.
    /// </summary>
    /// <remarks>
    /// Both ends must be allowed, so a muted layer is silent in both directions: releasing it doesn't announce
    /// the layer you land back on.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="from">The previous layers.</param>
    /// <param name="to">The new layers.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public static bool AllowedTransition(HudSettings settings, LayerMask from, LayerMask to) =>
        Allowed(settings, from) && Allowed(settings, to);

    /// <summary>
    /// Whether the HUD fires for this device state change.
    /// </summary>
    /// <remarks>
    /// The mask must change, the device must be Connected before and after (so connect and reconnect never fire it),
    /// and the transition must be allowed.
    /// </remarks>
    /// <param name="settings">The HUD settings.</param>
    /// <param name="previous">The state before the change.</param>
    /// <param name="current">The state after the change.</param>
    /// <returns><see langword="true"/> when the HUD should show.</returns>
    public static bool ShouldShow(HudSettings settings, DeviceState previous, DeviceState current) =>
        previous.Status == DeviceStatus.Connected
        && current.Status == DeviceStatus.Connected
        && previous.Layers != current.Layers
        && AllowedTransition(settings, previous.Layers, current.Layers);
}
