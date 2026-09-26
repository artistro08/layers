using Layers.Core.Logic;
using Windows.Graphics;

namespace Layers.Tests;

[TestClass]
public sealed class HudPlacementTests
{
    [TestMethod]
    [DataRow(1.0, 12)]
    [DataRow(1.25, 15)]
    [DataRow(1.5, 18)]
    [DataRow(2.0, 24)]
    public void Gap_ScaledAndRoundedToWholePixels(double scale, int gap)
    {
        Assert.AreEqual(gap, HudPlacement.GapPixels(scale));

        // The Flyout's Own Gap Stops One Pixel Short Of The Edge
        Assert.AreEqual(gap - 1, HudPlacement.GapDips(scale) * scale, 1e-9);
    }

    [TestMethod]
    [DataRow(1.0, 46)]
    [DataRow(1.25, 58)]
    [DataRow(1.5, 69)]
    [DataRow(2.0, 92)]
    public void Box_ScaledAndRoundedToWholePixels(double scale, int height)
    {
        Assert.AreEqual(height, HudPlacement.BoxPixels(scale));
        Assert.AreEqual(height, HudPlacement.BoxDips(scale) * scale, 1e-9);
    }

    [TestMethod]
    [DataRow(1.0, 982)]
    [DataRow(1.25, 967)]
    [DataRow(1.5, 953)]
    [DataRow(2.0, 924)]
    public void Anchor_BoxAndGapAboveWorkAreaBottom(double scale, int y)
    {
        var anchor = HudPlacement.Anchor(new RectInt32(0, 0, 1920, 1040), scale);

        Assert.AreEqual(new PointInt32(960, y), anchor);
    }

    [TestMethod]
    [DataRow(1.0)]
    [DataRow(1.25)]
    [DataRow(1.5)]
    [DataRow(1.75)]
    [DataRow(2.0)]
    public void Anchor_FlyoutContentEndsOnePixelAboveWorkAreaBottom(double scale)
    {
        var anchor = HudPlacement.Anchor(new RectInt32(0, 0, 1920, 1040), scale);

        // Box Plus The Flyout's Own Gap, Down From The Anchor
        Assert.AreEqual(1040 - 1, anchor.Y + HudPlacement.BoxPixels(scale) + HudPlacement.GapDips(scale) * scale, 1e-9);
    }

    [TestMethod]
    public void Anchor_RespectsNegativeOriginWorkArea()
    {
        // Taskbar At The Top Of A Monitor Left Of And Above The Primary Origin
        var anchor = HudPlacement.Anchor(new RectInt32(-1920, -1032, 1920, 1032), 1.25);

        Assert.AreEqual(new PointInt32(-960, -73), anchor);
    }

    [TestMethod]
    public void HostPosition_BottomLeftOnAnchor()
    {
        Assert.AreEqual(new PointInt32(1720, 1327), HudPlacement.HostPosition(new PointInt32(1720, 1374), 47));
        Assert.AreEqual(new PointInt32(-960, -120), HudPlacement.HostPosition(new PointInt32(-960, -73), 47));
    }

    [TestMethod]
    [DataRow(1.0, 1723, 1330, -3.0, 44.0)]
    [DataRow(1.25, 1723, 1330, -2.4, 35.2)]
    [DataRow(1.5, 1723, 1330, -2.0, 29.333333333333332)]
    [DataRow(1.25, -957, -107, -2.4, 32.8)]
    public void FlyoutPosition_AnchorFromClientOriginInDips(double scale, int originX, int originY, double x, double y)
    {
        // Anchor Above The Primary's Bottom Center, Or Above The Bottom Of A Negative-Origin Monitor
        var anchor = originX < 0 ? new PointInt32(-960, -66) : new PointInt32(1720, 1374);

        var position = HudPlacement.FlyoutPosition(anchor, new PointInt32(originX, originY), scale);

        // Point Stores Floats
        Assert.AreEqual(x, position.X, 1e-4);
        Assert.AreEqual(y, position.Y, 1e-4);
    }
}
