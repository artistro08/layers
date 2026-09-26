using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Layers.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Layers.UITests;

[TestClass]
public sealed class TrayMenuTests
{
    private static List<string> VisibleTexts(TrayMenuHost host) => host.MenuItems
        .Where(item => item.Visibility == Visibility.Visible)
        .Select(item => item is MenuFlyoutItem text ? text.Text : "---")
        .ToList();

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected", null)]
    [DataRow(DeviceStatus.Disconnected, "Disconnected", null)]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable", "All 8 expression slots are in use")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware", "This app supports config version 18")]
    public Task Items_MatchStatus(DeviceStatus status, string label, string? detail) => UiHost.RunAsync(async () =>
    {
        var model = new TrayMenuViewModel { State = new DeviceState(status, LayerMask.Base) };
        var host  = new TrayMenuHost(model);

        host.RefreshBindings();
        await UiHost.Settle();

        // Status, Optional Detail, Layer, Then The Fixed Rows
        var expected = new List<string> { label };
        if (detail is not null)
        {
            expected.Add(detail);
        }

        expected.AddRange(["Layer 0", "---", "Settings…", "---", "Quit"]);
        CollectionAssert.AreEqual(expected, VisibleTexts(host));
        host.Close();
    });

    [TestMethod]
    public Task Items_UpdateLive() => UiHost.RunAsync(async () =>
    {
        var model = new TrayMenuViewModel();
        var host  = new TrayMenuHost(model);
        host.RefreshBindings();

        model.State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010));
        await UiHost.Settle();

        CollectionAssert.Contains(VisibleTexts(host), "Layers 1, 3");
        CollectionAssert.Contains(VisibleTexts(host), "Connected");
        host.Close();
    });
}
