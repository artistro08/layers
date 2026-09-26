namespace Layers.Core.Logic;

/// <summary>
/// Connection state of the HID Remapper.
/// </summary>
/// <remarks>
/// The numeric values match Layers 1.0.3 for easy cross-reference with the Rust history.
/// </remarks>
public enum DeviceStatus
{
    /// <summary>No device, or the last session failed.</summary>
    Disconnected = 0,

    /// <summary>Connected, but all 8 expression slots belong to the user, so the layer can't be read.</summary>
    NoSlot = 1,

    /// <summary>Connected, and the layer is being read.</summary>
    Connected = 2,

    /// <summary>Connected, but the firmware's config version isn't 18, so nothing is written.</summary>
    VersionMismatch = 3,
}

/// <summary>
/// A snapshot of the device status and active layers.
/// </summary>
/// <remarks>
/// Raised by <c>DeviceService</c> and consumed by the tray icon, the menu, and the HUD rules.
/// </remarks>
/// <param name="Status">The connection status.</param>
/// <param name="Layers">The active layers. Meaningful only when <paramref name="Status"/> is Connected.</param>
public readonly record struct DeviceState(DeviceStatus Status, LayerMask Layers)
{
    /// <summary>
    /// Gets the state before any device is seen.
    /// </summary>
    /// <remarks>
    /// Disconnected at layer 0, matching Layers 1.0.3's startup state.
    /// </remarks>
    public static DeviceState Initial => new(DeviceStatus.Disconnected, LayerMask.Base);
}

/// <summary>
/// User-facing strings for each device status.
/// </summary>
/// <remarks>
/// Copied verbatim from Layers 1.0.3. Don't reword.
/// </remarks>
public static class StatusText
{
    /// <summary>
    /// Gets the tray tooltip.
    /// </summary>
    /// <remarks>
    /// When connected, the tooltip is the layer label itself.
    /// </remarks>
    /// <param name="state">The current device state.</param>
    /// <returns>The tooltip text.</returns>
    public static string Tooltip(DeviceState state) => state.Status switch
    {
        DeviceStatus.Disconnected    => "HID Remapper disconnected",
        DeviceStatus.NoSlot          => "Connected, layer unavailable",
        DeviceStatus.VersionMismatch => "Unsupported firmware version",
        _                            => state.Layers.Label,
    };

    /// <summary>
    /// Gets the status row label in the tray menu.
    /// </summary>
    /// <remarks>
    /// Shorter than the tooltip for VersionMismatch, matching the Rust popup.
    /// </remarks>
    /// <param name="status">The device status.</param>
    /// <returns>The label.</returns>
    public static string Label(DeviceStatus status) => status switch
    {
        DeviceStatus.Connected       => "Connected",
        DeviceStatus.NoSlot          => "Connected, layer unavailable",
        DeviceStatus.VersionMismatch => "Unsupported firmware",
        _                            => "Disconnected",
    };

    /// <summary>
    /// Gets the second-line explanation for degraded statuses.
    /// </summary>
    /// <remarks>
    /// Only NoSlot and VersionMismatch have one.
    /// </remarks>
    /// <param name="status">The device status.</param>
    /// <returns>The detail text, or <see langword="null"/>.</returns>
    public static string? Detail(DeviceStatus status) => status switch
    {
        DeviceStatus.NoSlot          => "All 8 expression slots are in use",
        DeviceStatus.VersionMismatch => "This app supports config version 18",
        _                            => null,
    };
}
