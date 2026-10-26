using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class FlyoutPlacementTests
{
    private static readonly PixelRect Monitor = new(0, 0, 2560, 1440);

    [TestMethod]
    [DataRow(0, 0, 2560, 1392, TaskbarEdge.Bottom)]
    [DataRow(0, 48, 2560, 1440, TaskbarEdge.Top)]
    [DataRow(64, 0, 2560, 1440, TaskbarEdge.Left)]
    [DataRow(0, 0, 2496, 1440, TaskbarEdge.Right)]
    [DataRow(0, 0, 2560, 1440, TaskbarEdge.Bottom)] // Auto-Hide Takes No Work Area
    public void DetectEdge(int l, int t, int r, int b, TaskbarEdge expected) =>
        Assert.AreEqual(expected, FlyoutPlacement.DetectEdge(Monitor, new PixelRect(l, t, r, b)));

    [TestMethod]
    public void Calculate_BottomTaskbarAt150Percent()
    {
        var work = new PixelRect(0, 0, 2560, 1392);

        var rect = FlyoutPlacement.Calculate(Monitor, work, 1.5, 500);

        Assert.AreEqual(720, rect.Width);         // 480 * 1.5
        Assert.AreEqual(750, rect.Height);        // 500 * 1.5
        Assert.AreEqual(2560 - 18, rect.Right);   // 12 * 1.5 Margin
        Assert.AreEqual(1392 - 18, rect.Bottom);
    }

    [TestMethod]
    public void Calculate_TopTaskbarHangsFromTop()
    {
        var rect = FlyoutPlacement.Calculate(Monitor, new PixelRect(0, 48, 2560, 1440), 1.0, 500);

        Assert.AreEqual(60, rect.Top);
        Assert.AreEqual(2560 - 12, rect.Right);
    }

    [TestMethod]
    public void Calculate_SideTaskbarSitsAtTheBottom()
    {
        var rect = FlyoutPlacement.Calculate(Monitor, new PixelRect(64, 0, 2560, 1440), 1.0, 500);

        Assert.AreEqual(1440 - 12, rect.Bottom);
        Assert.AreEqual(2560 - 12, rect.Right);
    }

    [TestMethod]
    public void Calculate_ClampsToShortWorkArea()
    {
        var rect = FlyoutPlacement.Calculate(Monitor, new PixelRect(0, 0, 2560, 300), 1.0, 10_000);

        Assert.AreEqual(300 - 24, rect.Height);
        Assert.AreEqual(12, rect.Top);
    }

    [TestMethod]
    public void Calculate_ClampsToNarrowWorkArea()
    {
        var rect = FlyoutPlacement.Calculate(new PixelRect(0, 0, 300, 1440), new PixelRect(0, 0, 300, 1392), 1.0, 500);

        Assert.AreEqual(300 - 24, rect.Width);
        Assert.AreEqual(12, rect.Left);
    }

    [TestMethod]
    public void Calculate_SecondMonitorOffsetIsKept()
    {
        var monitor = new PixelRect(2560, 0, 4480, 1080);
        var work    = new PixelRect(2560, 0, 4480, 1032);

        var rect = FlyoutPlacement.Calculate(monitor, work, 1.0, 500);

        Assert.AreEqual(4480 - 12, rect.Right);
        Assert.AreEqual(1032 - 12, rect.Bottom);
        Assert.AreEqual(480, rect.Width);
    }
}
