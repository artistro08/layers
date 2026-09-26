using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Layers.Core.Logic;
using Windows.Devices.Enumeration;
using Windows.Devices.HumanInterfaceDevice;
using Windows.Storage;

// Expose String[] To The WinRT ABI For AOT — Layers.Core Is A Plain Library, So CsWinRT's Source Generator
// Runs In OptIn Mode Here (Only WinUI/UWP Projects Get Auto Mode) And Needs This To Pre-Generate The
// IIterable<string> Vtable For DeviceInformation.CreateFromIdAsync/FindAllAsync's additionalProperties Below.
[assembly: WinRT.GeneratedWinRTExposedExternalType(typeof(string[]))]

namespace Layers.Core.Services;

/// <summary>
/// An <see cref="IHidChannel"/> over the stock WinRT <see cref="HidDevice"/>.
/// </summary>
/// <remarks>
/// Report buffers include the report ID as their first byte, matching the protocol layout.
/// </remarks>
/// <param name="device">The open device. Owned and disposed by this channel.</param>
public sealed class WinRtHidChannel(HidDevice device) : IHidChannel
{
    private readonly Lock _gate = new();
    private EventHandler<byte[]>? _inputReport;

    /// <inheritdoc/>
    public event EventHandler<byte[]>? InputReport
    {
        add
        {
            lock (_gate)
            {
                if (_inputReport is null)
                {
                    device.InputReportReceived += OnInputReportReceived;
                }

                _inputReport += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _inputReport -= value;
                if (_inputReport is null)
                {
                    device.InputReportReceived -= OnInputReportReceived;
                }
            }
        }
    }

    /// <inheritdoc/>
    public async Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        var feature  = device.CreateFeatureReport(report[0]);
        feature.Data = report.AsBuffer();
        await device.SendFeatureReportAsync(feature).AsTask(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        var feature = await device.GetFeatureReportAsync(reportId).AsTask(cancellationToken).ConfigureAwait(false);
        return feature.Data.ToArray();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            device.InputReportReceived -= OnInputReportReceived;
            _inputReport = null;
        }

        device.Dispose();
    }

    private void OnInputReportReceived(HidDevice sender, HidInputReportReceivedEventArgs args) =>
        _inputReport?.Invoke(this, args.Report.Data.ToArray());
}

/// <summary>
/// Finds HID Remapper devices with a stock <see cref="DeviceWatcher"/>.
/// </summary>
/// <remarks>
/// Watches the config collection (usage page 0xFF00, usage 0x20). On open, the monitor collection (usage 0x21) is
/// matched by <c>System.Devices.ContainerId</c>, which Windows assigns per physical device, so two attached
/// HID Remappers never cross wires.
/// </remarks>
public sealed class WinRtHidDeviceSource : IHidDeviceSource
{
    private const string ContainerIdProperty = "System.Devices.ContainerId";

    // Plain Array, Not A Collection Expression — See The Assembly-Level GeneratedWinRTExposedExternalType Above
    private static readonly string[] ContainerIdProperties = { ContainerIdProperty };

    private DeviceWatcher? _watcher;

    /// <inheritdoc/>
    public event EventHandler<string>? Arrived;

    /// <inheritdoc/>
    public event EventHandler<string>? Departed;

    /// <summary>
    /// Lists the config collections present now.
    /// </summary>
    /// <remarks>
    /// Used by the hardware tests to skip when no device is attached.
    /// </remarks>
    /// <returns>Device IDs.</returns>
    public static async Task<IReadOnlyList<string>> FindConfigDeviceIdsAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.ConfigUsage));
        return devices.Select(device => device.Id).ToList();
    }

    /// <inheritdoc/>
    public void Start()
    {
        if (_watcher is not null)
        {
            return;
        }

        // DeviceWatcher Needs Added, Removed, And Updated Handlers To Run
        _watcher          = DeviceInformation.CreateWatcher(HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.ConfigUsage));
        _watcher.Added   += (_, info) => Arrived?.Invoke(this, info.Id);
        _watcher.Removed += (_, update) => Departed?.Invoke(this, update.Id);
        _watcher.Updated += (_, _) => { };
        _watcher.Start();
    }

    /// <inheritdoc/>
    public void Stop()
    {
        if (_watcher is null)
        {
            return;
        }

        // Stop, Tolerating The Watcher Changing State Between The Check And The Call
        if (_watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
        {
            try
            {
                _watcher.Stop();
            }
            catch (InvalidOperationException ex)
            {
                Trace.TraceWarning("device watcher stop raced a state change: {0}", ex.Message);
            }
        }

        _watcher = null;
    }

    /// <inheritdoc/>
    public async Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        // Open Config
        var config = await HidDevice.FromIdAsync(deviceId, FileAccessMode.ReadWrite).AsTask(cancellationToken).ConfigureAwait(false)
            ?? throw new DeviceProtocolException("config collection could not be opened");

        try
        {
            // Find The Monitor Collection On The Same Physical Device
            var configInfo = await DeviceInformation.CreateFromIdAsync(deviceId, ContainerIdProperties).AsTask(cancellationToken).ConfigureAwait(false);
            var container  = configInfo.Properties.TryGetValue(ContainerIdProperty, out var value) ? value : null;
            if (container is null)
            {
                throw new DeviceProtocolException("monitor collection not found");
            }

            var monitors   = await DeviceInformation.FindAllAsync(
                HidDevice.GetDeviceSelector(Protocol.ConfigUsagePage, Protocol.MonitorUsage),
                ContainerIdProperties).AsTask(cancellationToken).ConfigureAwait(false);
            var monitorInfo = monitors.FirstOrDefault(info => info.Properties.TryGetValue(ContainerIdProperty, out var id) && Equals(id, container))
                ?? throw new DeviceProtocolException("monitor collection not found");

            // Open Monitor
            var monitor = await HidDevice.FromIdAsync(monitorInfo.Id, FileAccessMode.Read).AsTask(cancellationToken).ConfigureAwait(false)
                ?? throw new DeviceProtocolException("monitor collection could not be opened");

            return new HidDevicePair(new WinRtHidChannel(config), new WinRtHidChannel(monitor));
        }
        catch
        {
            config.Dispose();
            throw;
        }
    }
}
