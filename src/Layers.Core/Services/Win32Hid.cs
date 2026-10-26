using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;

namespace Layers.Core.Services;

/// <summary>
/// An <see cref="IHidChannel"/> for feature reports over a shared Win32 handle.
/// </summary>
/// <remarks>
/// <para>
/// WinRT's <c>HidDevice.FromIdAsync</c> opens a read-write collection with no sharing, which locks the web config tool
/// out while Layers runs and locks Layers out while the tool is connected. This channel works over a handle opened
/// with shared read/write, the way WebHID and hidapi do, so both can hold the config collection at once. Input reports
/// are not supported: the config collection has none.
/// </para>
/// <para>
/// The calls are synchronous. <c>HidD_SetFeature</c> and <c>HidD_GetFeature</c> are blocking control transfers that a
/// cancellation token can't interrupt, so the token is checked before each call only. A wedged device holds a call for
/// the USB control-transfer timeout, a few seconds at most.
/// </para>
/// </remarks>
public sealed partial class Win32HidChannel : IHidChannel
{
    // HIDP_STATUS_SUCCESS Is A Macro In hidpi.h, So The Metadata Has No Constant For It
    private const int HidpStatusSuccess = 0x00110000;

    private readonly SafeFileHandle _handle;
    private readonly int _featureLength;

    /// <summary>
    /// Creates the channel over an open collection handle.
    /// </summary>
    /// <remarks>
    /// Open the handle with <see cref="FileShare.ReadWrite"/> so other programs can hold the collection too. The
    /// channel owns the handle, and reads the feature report length from the collection's descriptor. If that fails
    /// the handle is closed.
    /// </remarks>
    /// <param name="handle">The open collection.</param>
    /// <exception cref="DeviceProtocolException">The descriptor could not be read.</exception>
    public Win32HidChannel(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        _handle = handle;
        try
        {
            _featureLength = FeatureReportLength(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <inheritdoc/>
    /// <remarks>Never raised: the config collection has no input reports.</remarks>
    public event EventHandler<byte[]>? InputReport
    {
        add { }
        remove { }
    }

    /// <inheritdoc/>
    /// <exception cref="Win32Exception">The driver rejected the report.</exception>
    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        // The Driver Wants The Descriptor's Full Report Length, So Short Reports Are Zero-Padded
        var buffer = new byte[Math.Max(report.Length, _featureLength)];
        report.CopyTo(buffer, 0);
        if (!HidD_SetFeature(_handle, buffer, (uint)buffer.Length))
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
        var report = new byte[_featureLength];
        report[0]  = reportId;
        if (!HidD_GetFeature(_handle, report, (uint)report.Length))
        {
            throw new Win32Exception();
        }

        return Task.FromResult(report);
    }

    /// <inheritdoc/>
    public void Dispose() => _handle.Dispose();

    private static int FeatureReportLength(SafeFileHandle handle)
    {
        if (!PInvoke.HidD_GetPreparsedData(handle, out var preparsed))
        {
            throw new DeviceProtocolException("collection descriptor could not be read");
        }

        try
        {
            var status = PInvoke.HidP_GetCaps(preparsed, out var caps);
            if (status.Value != HidpStatusSuccess)
            {
                throw new DeviceProtocolException($"HidP_GetCaps failed with 0x{status.Value:X8}");
            }

            return caps.FeatureReportByteLength;
        }
        finally
        {
            PInvoke.HidD_FreePreparsedData(preparsed);
        }
    }

    // CsWin32 Generates These With SetLastError Off, Which Would Make Win32Exception Report Success; These Keep It
    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_SetFeature(SafeFileHandle device, ReadOnlySpan<byte> report, uint length);

    [LibraryImport("hid.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool HidD_GetFeature(SafeFileHandle device, Span<byte> report, uint length);
}
