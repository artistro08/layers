using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class IconMathTests
{
    private static AlphaBuffer Filled(int w, int h, byte value)
    {
        var buffer = new AlphaBuffer(w, h);
        Array.Fill(buffer.Pixels, value);
        return buffer;
    }

    // =========================================================================
    // SIZE
    // =========================================================================

    [TestMethod]
    [DataRow(96u, 16)]
    [DataRow(120u, 20)]
    [DataRow(144u, 24)]
    [DataRow(192u, 32)]
    [DataRow(168u, 28)]
    [DataRow(108u, 20)]
    public void IconSize_RoundsUpToMultipleOfFour(uint dpi, int expected)
    {
        Assert.AreEqual(expected, IconMath.IconSize(dpi));
    }

    // =========================================================================
    // DOWNSAMPLE
    // =========================================================================

    [TestMethod]
    public void Downsample_AveragesBlocks()
    {
        var src = new AlphaBuffer(2, 2);
        src.Pixels[0] = 255;
        src.Pixels[1] = 255;

        var result = IconMath.Downsample(src, 2);

        Assert.AreEqual(1, result.Width);
        Assert.AreEqual((byte)127, result.Pixels[0]);
    }

    [TestMethod]
    public void Downsample_FullStaysFull()
    {
        Assert.IsTrue(IconMath.Downsample(Filled(8, 8, 255), 4).Pixels.All(p => p == 255));
    }

    [TestMethod]
    public void Downsample_EmptyStaysEmpty()
    {
        Assert.IsTrue(IconMath.Downsample(new AlphaBuffer(8, 8), 4).Pixels.All(p => p == 0));
    }

    [TestMethod]
    public void Downsample_KeepsBlocksSeparate()
    {
        var src = new AlphaBuffer(4, 2);
        src[2, 0] = 255;
        src[3, 0] = 255;
        src[2, 1] = 255;
        src[3, 1] = 255;

        var result = IconMath.Downsample(src, 2);

        CollectionAssert.AreEqual(new byte[] { 0, 255 }, result.Pixels);
    }

    [TestMethod]
    public void Downsample_FactorOneIsIdentity()
    {
        var src = new AlphaBuffer(3, 3);
        src[1, 1] = 42;

        CollectionAssert.AreEqual(src.Pixels, IconMath.Downsample(src, 1).Pixels);
    }

    [TestMethod]
    public void Downsample_RejectsNonDividingFactor()
    {
        Assert.ThrowsExactly<ArgumentException>(() => IconMath.Downsample(new AlphaBuffer(5, 4), 2));
    }

    [TestMethod]
    public void Downsample_RejectsZeroFactor()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => IconMath.Downsample(new AlphaBuffer(4, 4), 0));
    }

    // =========================================================================
    // PREMULTIPLY
    // =========================================================================

    [TestMethod]
    public void Premultiply_FullCoverageKeepsColor()
    {
        var bgra = IconMath.ToPremultipliedBgra(Filled(1, 1, 255), 0x10, 0x20, 0x30);

        CollectionAssert.AreEqual(new byte[] { 0x30, 0x20, 0x10, 255 }, bgra);
    }

    [TestMethod]
    public void Premultiply_ZeroCoverageIsTransparentBlack()
    {
        CollectionAssert.AreEqual(new byte[] { 0, 0, 0, 0 }, IconMath.ToPremultipliedBgra(new AlphaBuffer(1, 1), 255, 255, 255));
    }

    [TestMethod]
    public void Premultiply_HalfCoverageHalvesColor()
    {
        var bgra = IconMath.ToPremultipliedBgra(Filled(1, 1, 128), 255, 255, 255);

        CollectionAssert.AreEqual(new byte[] { 128, 128, 128, 128 }, bgra);
    }

    // =========================================================================
    // CENTER INK
    // =========================================================================

    [TestMethod]
    public void CenterInk_MovesLowInkUp()
    {
        var src = new AlphaBuffer(8, 8);
        src[3, 6] = 255;
        src[4, 7] = 255;

        var bounds = IconMath.CenterInk(src).InkBounds()!.Value;

        Assert.AreEqual(3, bounds.MinY);
        Assert.AreEqual(4, bounds.MaxY);
    }

    [TestMethod]
    public void CenterInk_MovesLeftInkRight()
    {
        var src = new AlphaBuffer(8, 8);
        src[0, 3] = 255;
        src[1, 4] = 255;

        var bounds = IconMath.CenterInk(src).InkBounds()!.Value;

        Assert.AreEqual(3, bounds.MinX);
        Assert.AreEqual(4, bounds.MaxX);
    }

    [TestMethod]
    public void CenterInk_EmptyUnchanged()
    {
        var src = new AlphaBuffer(8, 8);

        CollectionAssert.AreEqual(src.Pixels, IconMath.CenterInk(src).Pixels);
    }

    [TestMethod]
    public void CenterInk_AlreadyCenteredUnchanged()
    {
        var src = new AlphaBuffer(8, 8);
        src[3, 3] = 255;
        src[4, 4] = 255;

        CollectionAssert.AreEqual(src.Pixels, IconMath.CenterInk(src).Pixels);
    }

    [TestMethod]
    public void CenterInk_PreservesCoverageValues()
    {
        var src = new AlphaBuffer(8, 8);
        src[0, 0] = 17;

        Assert.IsTrue(IconMath.CenterInk(src).Pixels.Contains((byte)17));
    }
}
