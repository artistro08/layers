using System.ComponentModel;
using Layers.Core.Logic;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Layers.Core.Services;

/// <summary>
/// An <see cref="IHidChannel"/> for feature reports over a shared Win32 handle.
/// </summary>
/// <remarks>
/// WinRT's <c>HidDevice.FromIdAsync</c> opens a read-write collection with no sharing, which locks the web config tool
/// out while Layers runs and locks Layers out while the tool is connected. This opens the collection with shared
/// read/write, the way WebHID and hidapi do, so both can hold the config collection at once. Input reports are not
/// supported: the config collection has none.
/// </remarks>
public sealed class Win32HidChannel : IHidChannel
{
    private readonly SafeFileHandle _handle;

    private Win32HidChannel(SafeFileHandle handle) => _handle = handle;

    /// <inheritdoc/>
    /// <remarks>Never raised: the config collection has no input reports.</remarks>
    public event EventHandler<byte[]>? InputReport
    {
        add { }
        remove { }
    }

    /// <summary>
    /// Opens a collection with shared read/write access.
    /// </summary>
    /// <remarks>
    /// The WinRT device ID of a HID collection is its Win32 device path, so it opens directly.
    /// </remarks>
    /// <param name="deviceId">The collection's device ID.</param>
    /// <returns>The open channel.</returns>
    /// <exception cref="IOException">The collection is gone or held exclusively by another process.</exception>
    public static Win32HidChannel Open(string deviceId) =>
        new(File.OpenHandle(deviceId, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite));

    /// <inheritdoc/>
    /// <exception cref="Win32Exception">The driver rejected the report.</exception>
    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        if (!PInvoke.HidD_SetFeature(_handle, report))
        {
            throw new Win32Exception();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <exception cref="Win32Exception">The driver rejected the read.</exception>
    public Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // The First Byte Selects The Report; The Driver Fills The Rest
        var report = new byte[Protocol.PacketLength];
        report[0]  = reportId;
        if (!PInvoke.HidD_GetFeature(_handle, report))
        {
            throw new Win32Exception();
        }

        return Task.FromResult(report);
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();
}
