using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HudRulesTests
{
    private static LayerMask L(byte bits) => new(bits);

    private static DeviceState Connected(byte bits) => new(DeviceStatus.Connected, L(bits));

    [TestMethod]
    public void Default_IsEnabledNothingSuppressed()
    {
        Assert.AreEqual(new HudSettings(true, 0, 1700), HudSettings.Default);
    }

    [TestMethod]
    public void Default_AllowsEveryLayer()
    {
        for (var layer = 0; layer < 8; layer++)
        {
            Assert.IsTrue(HudRules.Allowed(HudSettings.Default, L((byte)(1 << layer))));
        }
    }

    [TestMethod]
    public void Disabled_AllowsNothing()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(false, 0, 1700), L(0b10)));
    }

    [TestMethod]
    public void SuppressedLayer_IsSilent()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b10, 1700), L(0b10)));
    }

    [TestMethod]
    public void UnsuppressedLayer_StillShows()
    {
        Assert.IsTrue(HudRules.Allowed(new HudSettings(true, 0b10, 1700), L(0b100)));
    }

    [TestMethod]
    public void StackedWithSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b10, 1700), L(0b10_0010)));
    }

    [TestMethod]
    public void SuppressingLayerZero_SilencesBase()
    {
        Assert.IsFalse(HudRules.Allowed(new HudSettings(true, 0b1, 1700), L(0b1)));
    }

    [TestMethod]
    public void Transition_IntoSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(true, 0b10, 1700), L(0b1), L(0b10)));
    }

    [TestMethod]
    public void Transition_OutOfSuppressed_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(true, 0b10, 1700), L(0b10), L(0b1)));
    }

    [TestMethod]
    public void Transition_BetweenAllowed_Shows()
    {
        Assert.IsTrue(HudRules.AllowedTransition(new HudSettings(true, 0b10, 1700), L(0b1), L(0b100)));
    }

    [TestMethod]
    public void Transition_WhenDisabled_IsSilent()
    {
        Assert.IsFalse(HudRules.AllowedTransition(new HudSettings(false, 0, 1700), L(0b1), L(0b100)));
    }

    [TestMethod]
    public void ShouldShow_OnConnectedLayerChange()
    {
        Assert.IsTrue(HudRules.ShouldShow(HudSettings.Default, Connected(0b1), Connected(0b100)));
    }

    [TestMethod]
    public void ShouldShow_NotWhenMaskUnchanged()
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, Connected(0b100), Connected(0b100)));
    }

    [TestMethod]
    [DataRow(DeviceStatus.Disconnected)]
    [DataRow(DeviceStatus.NoSlot)]
    [DataRow(DeviceStatus.VersionMismatch)]
    public void ShouldShow_NeverOnConnectOrReconnect(DeviceStatus before)
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, new DeviceState(before, L(0b100)), Connected(0b1)));
    }

    [TestMethod]
    public void ShouldShow_NotOnDisconnect()
    {
        Assert.IsFalse(HudRules.ShouldShow(HudSettings.Default, Connected(0b100), new DeviceState(DeviceStatus.Disconnected, L(0b1))));
    }

    [TestMethod]
    public void ShouldShow_RespectsSuppressionBothWays()
    {
        var settings = new HudSettings(true, 0b10, 1700);

        Assert.IsFalse(HudRules.ShouldShow(settings, Connected(0b1), Connected(0b10)));
        Assert.IsFalse(HudRules.ShouldShow(settings, Connected(0b10), Connected(0b1)));
    }
}
