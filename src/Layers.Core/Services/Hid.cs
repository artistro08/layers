namespace Layers.Core.Services;

/// <summary>
/// One open HID top-level collection.
/// </summary>
/// <remarks>
/// The HID Remapper exposes config (feature reports) and monitor (input reports) as two collections, which Windows
/// shows as two devices. <c>WinRtHidChannel</c> is the real implementation. Tests use a firmware fake.
/// </remarks>
public interface IHidChannel : IDisposable
{
    /// <summary>Raised on a background thread for each input report, including the report ID byte.</summary>
    event EventHandler<byte[]>? InputReport;

    /// <summary>Sends a feature report.</summary>
    /// <remarks>The first byte is the report ID.</remarks>
    /// <param name="report">The report.</param>
    /// <param name="cancellationToken">Cancels the send.</param>
    /// <returns>A task that completes when sent.</returns>
    Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken);

    /// <summary>Reads a feature report.</summary>
    /// <remarks>The result includes the report ID byte. It may be short if the device hasn't answered yet.</remarks>
    /// <param name="reportId">The report ID.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The report bytes.</returns>
    Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken);
}

/// <summary>
/// The config and monitor channels of one physical HID Remapper.
/// </summary>
/// <remarks>
/// Disposing the pair closes both channels.
/// </remarks>
/// <param name="config">The config collection (usage 0x20).</param>
/// <param name="monitor">The monitor collection (usage 0x21).</param>
public sealed class HidDevicePair(IHidChannel config, IHidChannel monitor) : IDisposable
{
    /// <summary>Gets the config channel.</summary>
    public IHidChannel Config { get; } = config;

    /// <summary>Gets the monitor channel.</summary>
    public IHidChannel Monitor { get; } = monitor;

    /// <inheritdoc/>
    public void Dispose()
    {
        Config.Dispose();
        Monitor.Dispose();
    }
}

/// <summary>
/// Finds HID Remapper devices and opens them.
/// </summary>
/// <remarks>
/// <see cref="Arrived"/> and <see cref="Departed"/> carry the config collection's device ID.
/// </remarks>
public interface IHidDeviceSource
{
    /// <summary>Raised when a config collection appears, including ones present at <see cref="Start"/>.</summary>
    event EventHandler<string>? Arrived;

    /// <summary>Raised when a config collection disappears.</summary>
    event EventHandler<string>? Departed;

    /// <summary>Starts watching.</summary>
    /// <remarks>Arrivals for devices already plugged in are raised after this call.</remarks>
    void Start();

    /// <summary>Stops watching.</summary>
    /// <remarks>Safe to call more than once.</remarks>
#pragma warning disable CA1716 // Stop pairs with Start; the app is C# only, so the VB keyword clash doesn't matter
    void Stop();
#pragma warning restore CA1716

    /// <summary>Opens both collections of a device.</summary>
    /// <remarks>The monitor collection is matched to the config collection's physical device.</remarks>
    /// <param name="deviceId">The config collection's device ID.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open pair.</returns>
    Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// The device didn't answer as the protocol requires.
/// </summary>
/// <remarks>
/// Ends the session. <c>DeviceService</c> shows Disconnected and retries after 2 seconds.
/// </remarks>
public sealed class DeviceProtocolException : Exception
{
    /// <summary>Creates the exception.</summary>
    public DeviceProtocolException()
    {
    }

    /// <summary>Creates the exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public DeviceProtocolException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with a message and cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public DeviceProtocolException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
