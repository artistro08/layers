using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class StatusTextTests
{
    [TestMethod]
    [DataRow(DeviceStatus.Disconnected, "HID Remapper disconnected")]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware version")]
    public void Tooltip_ForNonConnectedStatuses(DeviceStatus status, string expected)
    {
        Assert.AreEqual(expected, StatusText.Tooltip(new DeviceState(status, new LayerMask(0b100))));
    }

    [TestMethod]
    public void Tooltip_WhenConnected_IsLayerLabel()
    {
        Assert.AreEqual("Layer 2", StatusText.Tooltip(new DeviceState(DeviceStatus.Connected, new LayerMask(0b100))));
        Assert.AreEqual("Layers 1, 3", StatusText.Tooltip(new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010))));
    }

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected")]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable")]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware")]
    [DataRow(DeviceStatus.Disconnected, "Disconnected")]
    public void Label_PerStatus(DeviceStatus status, string expected)
    {
        Assert.AreEqual(expected, StatusText.Label(status));
    }

    [TestMethod]
    public void Detail_OnlyForDegradedStatuses()
    {
        Assert.AreEqual("All 8 expression slots are in use", StatusText.Detail(DeviceStatus.NoSlot));
        Assert.AreEqual("This app supports config version 18", StatusText.Detail(DeviceStatus.VersionMismatch));
        Assert.IsNull(StatusText.Detail(DeviceStatus.Connected));
        Assert.IsNull(StatusText.Detail(DeviceStatus.Disconnected));
    }

    [TestMethod]
    public void Initial_IsDisconnectedAtLayerZero()
    {
        Assert.AreEqual(new DeviceState(DeviceStatus.Disconnected, LayerMask.Base), DeviceState.Initial);
    }
}
