using Layers.Core.Logic;
using Layers.Core.ViewModels;

namespace Layers.Tests;

[TestClass]
public sealed class TrayMenuViewModelTests
{
    [TestMethod]
    public void Initial_IsDisconnected()
    {
        var model = new TrayMenuViewModel();

        Assert.AreEqual("Disconnected", model.StatusLabel);
        Assert.AreEqual(StatusTone.Critical, model.Tone);
        Assert.IsFalse(model.HasStatusDetail);
        Assert.AreEqual("Layer 0", model.LayerLabel);
    }

    [TestMethod]
    [DataRow(DeviceStatus.Connected, "Connected", StatusTone.Success, false)]
    [DataRow(DeviceStatus.NoSlot, "Connected, layer unavailable", StatusTone.Caution, true)]
    [DataRow(DeviceStatus.VersionMismatch, "Unsupported firmware", StatusTone.Caution, true)]
    [DataRow(DeviceStatus.Disconnected, "Disconnected", StatusTone.Critical, false)]
    public void Status_MapsLabelToneAndDetail(DeviceStatus status, string label, StatusTone tone, bool hasDetail)
    {
        var model = new TrayMenuViewModel { State = new DeviceState(status, LayerMask.Base) };

        Assert.AreEqual(label, model.StatusLabel);
        Assert.AreEqual(tone, model.Tone);
        Assert.AreEqual(hasDetail, model.HasStatusDetail);
        Assert.AreEqual(StatusText.Detail(status), model.StatusDetail);
    }

    [TestMethod]
    public void LayerLabel_FollowsMask()
    {
        var model = new TrayMenuViewModel { State = new DeviceState(DeviceStatus.Connected, new LayerMask(0b1010)) };

        Assert.AreEqual("Layers 1, 3", model.LayerLabel);
    }

    [TestMethod]
    public void StateChange_RaisesEveryDerivedProperty()
    {
        var model   = new TrayMenuViewModel();
        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        model.State = new DeviceState(DeviceStatus.NoSlot, new LayerMask(0b10));

        string[] expected = [nameof(model.State), nameof(model.StatusLabel), nameof(model.StatusDetail), nameof(model.HasStatusDetail), nameof(model.LayerLabel), nameof(model.Tone)];
        CollectionAssert.AreEquivalent(expected, changed);
    }

    [TestMethod]
    public void SameState_RaisesNothing()
    {
        var model   = new TrayMenuViewModel();
        var changed = 0;
        model.PropertyChanged += (_, _) => changed++;

        model.State = DeviceState.Initial;

        Assert.AreEqual(0, changed);
    }
}
