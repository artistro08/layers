using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class HudTimingTests
{
    [TestMethod]
    public void Hold_Is1700Ms()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(1700), HudTiming.Hold);
    }

    [TestMethod]
    public void Exit_Is150Ms()
    {
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), HudTiming.Exit);
    }
}
