using Layers.Core.Logic;
using Layers.Core.Services;
using Microsoft.Win32.SafeHandles;

namespace Layers.Tests;

/// Needs a real HID Remapper on config version 18. Excluded from the default run.
/// One device, one session at a time: two parallel sessions would read each other's replies.
[TestClass]
[DoNotParallelize]
public sealed class HardwareTests
{
    public TestContext TestContext { get; set; } = null!;

    /// Returns the config collection's device ID, or marks the test inconclusive.
    private static async Task<string> RequireDeviceAsync()
    {
        var ids = await WinRtHidDeviceSource.FindConfigDeviceIdsAsync();
        if (ids.Count == 0)
        {
            Assert.Inconclusive("No HID Remapper connected");
        }

        return ids[0];
    }

    /// Starts the service and returns the first state that isn't Disconnected.
    private static async Task<DeviceState> ConnectAsync(DeviceService service)
    {
        var settled           = new TaskCompletionSource<DeviceState>();
        service.StateChanged += (_, state) =>
        {
            if (state.Status != DeviceStatus.Disconnected)
            {
                settled.TrySetResult(state);
            }
        };

        service.Start();
        return await settled.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// Opens the collection the way Chrome's WebHID does: read/write, shared.
    private static SafeFileHandle OpenLikeWebHid(string deviceId) =>
        File.OpenHandle(deviceId, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(30_000)]
    public async Task RealDevice_Connects()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);

        var result = await ConnectAsync(service);

        Assert.AreEqual(DeviceStatus.Connected, result.Status, $"device reported {result.Status}");
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(30_000)]
    public async Task RealDevice_SharesConfigCollectionWithWebConfigTool()
    {
        var deviceId = await RequireDeviceAsync();

        // The Web Config Tool Already Holds The Config Collection
        using var tool          = OpenLikeWebHid(deviceId);
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);

        var result = await ConnectAsync(service);
        Assert.AreEqual(DeviceStatus.Connected, result.Status, $"device reported {result.Status} while the tool held the config collection");

        // And The Tool Can Still Open It While We Hold It; OpenHandle Throws If Not
        using var again = OpenLikeWebHid(deviceId);
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(60_000)]
    public async Task RealDevice_StaysConnectedUnderWebConfigToolTraffic()
    {
        var deviceId            = await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        Assert.AreEqual(DeviceStatus.Connected, (await ConnectAsync(service)).Status);

        var statuses          = new List<DeviceStatus>();
        service.StateChanged += (_, state) =>
        {
            lock (statuses)
            {
                statuses.Add(state.Status);
            }
        };

        // The Tool Polls GET_CONFIG Flat Out For Three Verify Intervals While We Run
        using var tool = new Win32HidChannel(OpenLikeWebHid(deviceId));
        var until      = DateTime.UtcNow + DeviceService.VerifyInterval * 3;
        var polls      = 0;
        while (DateTime.UtcNow < until)
        {
            await tool.SendFeatureAsync(Protocol.GetConfig(), CancellationToken.None);
            await tool.GetFeatureAsync(Protocol.ReportIdConfig, CancellationToken.None);
            polls++;
            await Task.Delay(5);
        }

        TestContext.WriteLine($"{polls} tool polls");
        Assert.AreEqual(DeviceStatus.Connected, service.State.Status);
        lock (statuses)
        {
            Assert.IsTrue(statuses.All(status => status == DeviceStatus.Connected), $"saw {string.Join(", ", statuses)}");
        }
    }

    [TestMethod]
    [TestCategory("Hardware")]
    [Timeout(30_000)]
    public async Task RealDevice_ReadsMappingsWithNames()
    {
        await RequireDeviceAsync();
        await using var service = new DeviceService(new WinRtHidDeviceSource(), TimeProvider.System);
        var read                = new TaskCompletionSource<DeviceConfig>();
        service.ConfigChanged  += (_, config) => read.TrySetResult(config);

        var state = await ConnectAsync(service);
        Assert.AreEqual(DeviceStatus.Connected, state.Status, $"device reported {state.Status}");
        var config = await read.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Every Input Resolves To A Name, And The Sheet Lists What The Device Reports
        var sheet = ShortcutSheet.Build(config);
        foreach (var section in sheet)
        {
            foreach (var key in section.Keys)
            {
                foreach (var row in key.Rows)
                {
                    var output = row.Output switch
                    {
                        MacroOutput macro => $"Macro {macro.Number}: {string.Join(", ", macro.Steps.Select(step => string.Join("+", step)))} ({macro.Steps.Count} steps)",
                        KeysOutput keys   => string.Join("+", keys.Keys),
                        _                 => "?",
                    };
                    TestContext.WriteLine($"{section.Title}: {key.Input} -> {output}{(row.Sticky ? " sticky" : "")}{(row.Tap ? " tap" : "")}{(row.Hold ? " hold" : "")}");
                }
            }
        }

        TestContext.WriteLine($"{config.Mappings.Count} mappings, descriptor {config.DescriptorNumber}");
        Assert.AreEqual(config.Mappings.Count == 0, sheet.Count == 0, "the sheet mirrors the mapping count");

        // Keyboard And Button Inputs Always Have Names; Anything Else Unnamed Is Hex In The Web Tool Too, So It Is Only Reported
        var unnamed = sheet.SelectMany(s => s.Keys).Select(k => k.Input).Where(name => name.StartsWith("0x", StringComparison.Ordinal)).Distinct().ToList();
        TestContext.WriteLine(unnamed.Count == 0 ? "every input named" : $"unnamed, as in the web tool: {string.Join(", ", unnamed)}");
        Assert.IsTrue(unnamed.All(name => !name.StartsWith("0x0007", StringComparison.Ordinal) && !name.StartsWith("0x0009", StringComparison.Ordinal)), "a keyboard or button input had no name");
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
