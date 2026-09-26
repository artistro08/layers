using Layers.Core.Logic;
using Layers.Core.Services;
using Windows.Win32;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.Tests;

[TestClass]
public sealed class TrayIconRendererTests
{
    private static readonly DeviceState Base = new(DeviceStatus.Connected, LayerMask.Base);

    private static DeviceState Layer(int n) => new(DeviceStatus.Connected, new LayerMask((byte)(1 << n)));

    private static AlphaBuffer AlphaOf(byte[] bgra, int size)
    {
        var alpha = new AlphaBuffer(size, size);
        for (var i = 0; i < alpha.Pixels.Length; i++)
        {
            alpha.Pixels[i] = bgra[i * 4 + 3];
        }
        return alpha;
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void RenderBgra_HasFourBytesPerPixel(int size)
    {
        Assert.AreEqual(size * size * 4, TrayIconRenderer.RenderBgra(Base, false, size).Length);
    }

    [TestMethod]
    public void LayerZero_DrawsGlyphNotDigit()
    {
        var glyph = TrayIconRenderer.RenderBgra(Base, false, 16);
        var digit = TrayIconRenderer.RenderBgra(Layer(3), false, 16);

        Assert.IsNotNull(AlphaOf(glyph, 16).InkBounds());
        CollectionAssert.AreNotEqual(glyph, digit);
    }

    [TestMethod]
    [DataRow(DeviceStatus.Disconnected)]
    [DataRow(DeviceStatus.NoSlot)]
    [DataRow(DeviceStatus.VersionMismatch)]
    public void NotConnected_DrawsGlyphEvenWithLayers(DeviceStatus status)
    {
        var glyph    = TrayIconRenderer.RenderBgra(Base, false, 16);
        var degraded = TrayIconRenderer.RenderBgra(new DeviceState(status, new LayerMask(0b1000)), false, 16);

        CollectionAssert.AreEqual(glyph, degraded);
    }

    [TestMethod]
    public void EachDigit_HasInkAndDiffers()
    {
        var seen = new HashSet<string>();
        for (var n = 1; n < 8; n++)
        {
            var bgra = TrayIconRenderer.RenderBgra(Layer(n), false, 16);
            Assert.IsNotNull(AlphaOf(bgra, 16).InkBounds(), $"digit {n} has no ink");
            Assert.IsTrue(seen.Add(Convert.ToBase64String(bgra)), $"digit {n} renders like another digit");
        }
    }

    [TestMethod]
    [DataRow(16)]
    [DataRow(24)]
    [DataRow(32)]
    public void Digit_IsCenteredWithinOnePixel(int size)
    {
        var bounds = AlphaOf(TrayIconRenderer.RenderBgra(Layer(3), false, size), size).InkBounds()!.Value;

        var left   = bounds.MinX;
        var right  = size - 1 - bounds.MaxX;
        var top    = bounds.MinY;
        var bottom = size - 1 - bounds.MaxY;

        Assert.IsTrue(Math.Abs(left - right) <= 1, $"left {left} right {right}");
        Assert.IsTrue(Math.Abs(top - bottom) <= 1, $"top {top} bottom {bottom}");
    }

    [TestMethod]
    [DataRow(16, 14)]
    [DataRow(24, 21)]
    [DataRow(32, 28)]
    public void Glyph_InkFillsIconLikeTheOldFontGlyph(int size, int oldExtent)
    {
        // The Old Segoe Fluent Icons U+E81E Glyph Filled 56 Of 64 Pixels (7/8) On Each Axis
        var bounds = AlphaOf(TrayIconRenderer.RenderBgra(Base, false, size), size).InkBounds()!.Value;
        var width  = bounds.MaxX - bounds.MinX + 1;
        var height = bounds.MaxY - bounds.MinY + 1;

        Assert.IsTrue(Math.Abs(Math.Max(width, height) - oldExtent) <= 1, $"ink {width}x{height}, old glyph {oldExtent}");
        Assert.IsTrue(width >= size * 0.8, $"ink width {width} of {size}");
    }

    [TestMethod]
    [DataRow(false, (byte)0xFF)]
    [DataRow(true, (byte)0x19)]
    public void RenderBgra_TintFollowsTaskbarTheme(bool light, byte shade)
    {
        var bgra   = TrayIconRenderer.RenderBgra(Layer(3), light, 16);
        var opaque = Enumerable.Range(0, 16 * 16).Where(i => bgra[i * 4 + 3] == 255).ToList();

        Assert.IsTrue(opaque.Count > 0);
        foreach (var i in opaque)
        {
            Assert.AreEqual(shade, bgra[i * 4]);
            Assert.AreEqual(shade, bgra[i * 4 + 1]);
            Assert.AreEqual(shade, bgra[i * 4 + 2]);
        }
    }

    [TestMethod]
    public void CreateIcon_ReturnsValidHandle()
    {
        using var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Base, false, 16), 16);

        Assert.IsFalse(icon.IsInvalid);
    }

    [TestMethod]
    [DoNotParallelize]
    public void RenderAndCreate_ThousandTimesDoesNotLeakGdi()
    {
        static uint GdiCount() => PInvoke.GetGuiResources(PInvoke.GetCurrentProcess(), GET_GUI_RESOURCES_FLAGS.GR_GDIOBJECTS);

        // Warm Up Font Caches
        using (TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Layer(1), false, 16), 16))
        {
        }

        var before = GdiCount();
        for (var i = 0; i < 1000; i++)
        {
            using var icon = TrayIconRenderer.CreateIcon(TrayIconRenderer.RenderBgra(Layer(i % 8), i % 2 == 0, 16), 16);
        }

        var after = GdiCount();
        Assert.IsTrue(after <= before + 2, $"GDI objects grew from {before} to {after}");
    }
}
