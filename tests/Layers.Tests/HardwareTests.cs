using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Tests;

/// Needs a real HID Remapper on config version 18. Excluded from the default run.
[TestClass]
public sealed class HardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    private static async Task RequireDeviceAsync()
    {
        if ((await WinRtHidDeviceSource.FindConfigDeviceIdsAsync()).Count == 0)
        {
            Assert.Inconclusive("No HID Remapper connected");
        }
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(30_000)]
    public async Task RealDevice_Connects()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        var settled             = new TaskCompletionSource<DeviceState>();
        service.StateChanged   += (_, state) =>
        {
            if (state.Status != DeviceStatus.Disconnected)
            {
                settled.TrySetResult(state);
            }
        };

        service.Start();
        var result = await settled.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.AreEqual(DeviceStatus.Connected, result.Status, $"device reported {result.Status}");
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [TestCategory("Manual")]
    [Timeout(60_000)]
    public async Task RealDevice_ReportsLayerChange()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        var changed             = new TaskCompletionSource<LayerMask>();
        service.StateChanged   += (_, state) =>
        {
            if (state.Status == DeviceStatus.Connected && state.Layers != LayerMask.Base)
            {
                changed.TrySetResult(state.Layers);
            }
        };

        service.Start();
        TestContext.WriteLine("Switch to a non-zero layer on the device within 45 seconds.");
        var layers = await changed.Task.WaitAsync(TimeSpan.FromSeconds(45));

        TestContext.WriteLine($"Saw {layers.Label}");
    }
}
