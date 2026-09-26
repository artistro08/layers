using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class LayerMaskTests
{
    [TestMethod]
    public void LayerZero_ShowsNoBadge()
    {
        var mask = new LayerMask(0b1);

        Assert.IsNull(mask.Badge);
        CollectionAssert.AreEqual(new[] { 0 }, mask.Active.ToArray());
        Assert.AreEqual("Layer 0", mask.Label);
    }

    [TestMethod]
    public void EmptyMask_IsTreatedAsLayerZero()
    {
        var mask = new LayerMask(0);

        CollectionAssert.AreEqual(new[] { 0 }, mask.Active.ToArray());
        Assert.IsNull(mask.Badge);
    }

    [TestMethod]
    public void SingleLayer_BadgesThatLayer()
    {
        var mask = new LayerMask(0b1000);

        Assert.AreEqual(3, mask.Badge);
        Assert.AreEqual("Layer 3", mask.Label);
    }

    [TestMethod]
    public void SeveralLayers_BadgeHighestAndListAll()
    {
        var mask = new LayerMask(0b1010);

        CollectionAssert.AreEqual(new[] { 1, 3 }, mask.Active.ToArray());
        Assert.AreEqual(3, mask.Badge);
        Assert.AreEqual("Layers 1, 3", mask.Label);
    }

    [TestMethod]
    public void LayerZeroWithAnother_BadgesHigher()
    {
        var mask = new LayerMask(0b0101);

        CollectionAssert.AreEqual(new[] { 0, 2 }, mask.Active.ToArray());
        Assert.AreEqual(2, mask.Badge);
        Assert.AreEqual("Layers 0, 2", mask.Label);
    }

    [TestMethod]
    public void AllEightBits_AreParsed()
    {
        Assert.AreEqual(7, new LayerMask(0b1000_0000).Badge);
    }

    [TestMethod]
    public void Base_IsLayerZero()
    {
        Assert.AreEqual((byte)1, LayerMask.Base.Bits);
    }
}
