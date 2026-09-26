using System.ComponentModel;
using System.Globalization;
using System.Numerics;
using Layers.Core.Logic;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Layers.Core.Services;

/// <summary>
/// Draws the tray icon.
/// </summary>
/// <remarks>
/// <para>
/// Layer 0, or any status other than Connected, shows the filled layers glyph (<see cref="PathData.LayersGlyph"/>).
/// Otherwise the highest active layer is drawn as a digit in Segoe UI Variable Display Bold, filling the whole icon: a
/// corner badge wasn't readable at 16 px.
/// </para>
/// <para>
/// Uses plain GDI, which is AOT-safe with no COM. The glyph path is filled white, or the digit is drawn white with
/// <c>ANTIALIASED_QUALITY</c>, on a black 32bpp DIB at 4x size, and the red channel is read back as coverage. It's then downsampled, re-centered on the
/// actual ink, and tinted: white on a dark taskbar, <c>#191919</c> on a light one. Every GDI object is released in
/// <c>finally</c>.
/// </para>
/// </remarks>
public static class TrayIconRenderer
{
    private const string DigitFont      = "Segoe UI Variable Display";
    private const int Supersample       = 4;
    private const byte DarkTaskbarInk   = 0xFF;
    private const byte LightTaskbarInk  = 0x19;
    private const float GlyphMargin     = 1f / 16;

    // The Vendored Glyph Is A Constant The Tests Parse, So This Never Throws At Runtime
    private static readonly IReadOnlyList<PathFigure> LayersFigures = PathData.Parse(PathData.LayersGlyph);

    /// <summary>
    /// Renders the icon's pixels.
    /// </summary>
    /// <remarks>
    /// Pure apart from GDI rasterization, so tests can inspect the pixels.
    /// </remarks>
    /// <param name="state">The device state.</param>
    /// <param name="lightTaskbar">Whether the taskbar is light.</param>
    /// <param name="size">Icon size in pixels, from <see cref="IconMath.IconSize"/>.</param>
    /// <returns>Premultiplied BGRA, top-down.</returns>
    public static byte[] RenderBgra(DeviceState state, bool lightTaskbar, int size)
    {
        // Pick Glyph Or Digit
        var badge = state.Status == DeviceStatus.Connected ? state.Layers.Badge : null;
        var large = badge is int digit
            ? Rasterize(digit.ToString(CultureInfo.InvariantCulture), DigitFont, bold: true, size * Supersample)
            : Rasterize(LayersFigures, size * Supersample);

        // Reduce, Center, Tint
        var small = IconMath.CenterInk(IconMath.Downsample(large, Supersample));
        var ink   = lightTaskbar ? LightTaskbarInk : DarkTaskbarInk;

        return IconMath.ToPremultipliedBgra(small, ink, ink, ink);
    }

    /// <summary>
    /// Wraps premultiplied BGRA pixels in an HICON.
    /// </summary>
    /// <remarks>
    /// The color and mask bitmaps are deleted before returning, since <c>CreateIconIndirect</c> copies them.
    /// The caller owns the returned handle.
    /// </remarks>
    /// <param name="bgra">Pixels from <see cref="RenderBgra"/>.</param>
    /// <param name="size">Icon size.</param>
    /// <returns>The icon.</returns>
    /// <exception cref="ArgumentException"><paramref name="bgra"/> isn't <paramref name="size"/> x <paramref name="size"/> BGRA.</exception>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    public static unsafe DestroyIconSafeHandle CreateIcon(byte[] bgra, int size)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        if (bgra.Length != size * size * 4)
        {
            throw new ArgumentException($"Expected {size * size * 4} bytes for a {size}x{size} BGRA buffer, got {bgra.Length}.", nameof(bgra));
        }

        var info  = TopDownInfo(size);
        void* bits;
        var color = PInvoke.CreateDIBSection(HDC.Null, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);
        var mask  = PInvoke.CreateBitmap(size, size, 1, 1, null);
        try
        {
            if (color.IsNull || mask.IsNull)
            {
                throw new Win32Exception();
            }

            // Copy Pixels
            bgra.AsSpan().CopyTo(new Span<byte>(bits, bgra.Length));

            // Build The Icon
            var iconInfo = new ICONINFO { fIcon = true, hbmColor = color, hbmMask = mask };
            var icon     = PInvoke.CreateIconIndirect(in iconInfo);
            if (icon.IsInvalid)
            {
                throw new Win32Exception();
            }

            return icon;
        }
        finally
        {
            PInvoke.DeleteObject(color);
            PInvoke.DeleteObject(mask);
        }
    }

    /// <summary>
    /// Draws text as coverage.
    /// </summary>
    /// <remarks>
    /// The em height equals <paramref name="size"/>, and the text is centered with <c>DT_CENTER | DT_VCENTER</c>.
    /// <see cref="IconMath.CenterInk"/> fixes the vertical offset afterwards.
    /// </remarks>
    /// <param name="text">Text or glyph.</param>
    /// <param name="family">Font family.</param>
    /// <param name="bold">Bold weight.</param>
    /// <param name="size">Square size in pixels.</param>
    /// <returns>Coverage.</returns>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    internal static unsafe AlphaBuffer Rasterize(string text, string family, bool bold, int size)
    {
        // CreateFont's Generated Overload Cannot Marshal Its Byte-Sized Enum Parameters (FONT_CHARSET etc. Are BYTE In The
        // Win32 ABI But Marshaled As U4), So The Font Is Built Via CreateFontIndirect And A LOGFONTW Instead
        var logFont = default(LOGFONTW);
        logFont.lfHeight        = -size;
        logFont.lfWeight        = bold ? 700 : 400;
        logFont.lfCharSet       = FONT_CHARSET.DEFAULT_CHARSET;
        logFont.lfOutPrecision  = FONT_OUTPUT_PRECISION.OUT_TT_PRECIS;
        logFont.lfClipPrecision = FONT_CLIP_PRECISION.CLIP_DEFAULT_PRECIS;
        logFont.lfQuality       = FONT_QUALITY.ANTIALIASED_QUALITY;
        family.CopyTo(logFont.lfFaceName.AsSpan());
        var font = PInvoke.CreateFontIndirect(&logFont);

        try
        {
            if (font.IsNull)
            {
                throw new Win32Exception();
            }

            return RasterizeWith(size, dc =>
            {
                // Draw White Text
                var oldFont = PInvoke.SelectObject(dc, font);
                PInvoke.SetBkMode(dc, BACKGROUND_MODE.TRANSPARENT);
                PInvoke.SetTextColor(dc, new COLORREF(0x00FFFFFF));
                var rect = new RECT(0, 0, size, size);
                fixed (char* textPtr = text)
                {
                    PInvoke.DrawText(dc, (PCWSTR)textPtr, -1, ref rect, DRAW_TEXT_FORMAT.DT_CENTER | DRAW_TEXT_FORMAT.DT_VCENTER | DRAW_TEXT_FORMAT.DT_SINGLELINE | DRAW_TEXT_FORMAT.DT_NOPREFIX);
                }
                PInvoke.SelectObject(dc, oldFont);
            });
        }
        finally
        {
            PInvoke.DeleteObject(font);
        }
    }

    /// <summary>
    /// Fills path figures as coverage.
    /// </summary>
    /// <remarks>
    /// The figures' own bounds (every start, control, and end point) are fitted into the square with a 1/16 margin on
    /// the longer side, keeping the aspect ratio. The vendored glyph keeps its 24 px view box padding, so drawing the
    /// view box instead left the tray icon about 3/4 full. This matches the ink extent of the earlier Segoe Fluent
    /// Icons glyph (U+E81E), which filled 56 of 64 pixels. Figures are filled white with the nonzero winding rule,
    /// matching SVG and XAML. GDI fills without antialiasing, so callers pass a supersampled size and downsample.
    /// </remarks>
    /// <param name="figures">Parsed figures.</param>
    /// <param name="size">Square size in pixels.</param>
    /// <returns>Coverage.</returns>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    internal static unsafe AlphaBuffer Rasterize(IReadOnlyList<PathFigure> figures, int size)
    {
        // Fit The Figures' Bounds Into The Square
        var points = figures.SelectMany(f => f.Segments.SelectMany(s => s.IsCubic ? new[] { s.Control1, s.Control2, s.End } : [s.End]).Prepend(f.Start)).ToList();
        var min    = points.Aggregate(Vector2.Min);
        var max    = points.Aggregate(Vector2.Max);
        var extent = max - min;
        var scale  = size * (1 - 2 * GlyphMargin) / Math.Max(extent.X, extent.Y);
        var origin = (new Vector2(size) - extent * scale) / 2 - min * scale;
        System.Drawing.Point ToPixel(Vector2 point) => new((int)MathF.Round(origin.X + point.X * scale), (int)MathF.Round(origin.Y + point.Y * scale));

        return RasterizeWith(size, dc =>
        {
            // Trace The Figures
            var bezier = stackalloc System.Drawing.Point[3];
            PInvoke.BeginPath(dc);
            foreach (var figure in figures)
            {
                var start = ToPixel(figure.Start);
                PInvoke.MoveToEx(dc, start.X, start.Y, null);
                foreach (var segment in figure.Segments)
                {
                    if (segment.IsCubic)
                    {
                        bezier[0] = ToPixel(segment.Control1);
                        bezier[1] = ToPixel(segment.Control2);
                        bezier[2] = ToPixel(segment.End);
                        PInvoke.PolyBezierTo(dc, bezier, 3);
                    }
                    else
                    {
                        var end = ToPixel(segment.End);
                        PInvoke.LineTo(dc, end.X, end.Y);
                    }
                }
                PInvoke.CloseFigure(dc);
            }
            PInvoke.EndPath(dc);

            // Fill White With The Nonzero Rule
            var oldBrush = PInvoke.SelectObject(dc, PInvoke.GetStockObject(GET_STOCK_OBJECT_FLAGS.WHITE_BRUSH));
            _ = PInvoke.SetPolyFillMode(dc, CREATE_POLYGON_RGN_MODE.WINDING);
            PInvoke.FillPath(dc);
            PInvoke.SelectObject(dc, oldBrush);
        });
    }

    /// <summary>
    /// Runs a white-on-black drawing into a fresh DIB and reads the coverage back.
    /// </summary>
    /// <remarks>
    /// Owns the memory DC and bitmap and always releases them. <paramref name="draw"/> must restore anything it
    /// selects into the DC.
    /// </remarks>
    /// <param name="size">Square size in pixels.</param>
    /// <param name="draw">Draws white onto the black DC.</param>
    /// <returns>Coverage from the red channel.</returns>
    /// <exception cref="Win32Exception">A GDI call failed.</exception>
    private static unsafe AlphaBuffer RasterizeWith(int size, Action<HDC> draw)
    {
        var info  = TopDownInfo(size);
        var dc    = PInvoke.CreateCompatibleDC(HDC.Null);
        void* bits;
        var bitmap = PInvoke.CreateDIBSection(dc, &info, DIB_USAGE.DIB_RGB_COLORS, &bits, HANDLE.Null, 0);

        var oldBitmap = HGDIOBJ.Null;
        try
        {
            if (dc.IsNull || bitmap.IsNull)
            {
                throw new Win32Exception();
            }

            // Draw White On Black
            oldBitmap = PInvoke.SelectObject(dc, bitmap);
            draw(dc);
            PInvoke.GdiFlush();

            // Red Channel Is Coverage
            var coverage = new AlphaBuffer(size, size);
            var pixels   = new ReadOnlySpan<byte>(bits, size * size * 4);
            for (var i = 0; i < coverage.Pixels.Length; i++)
            {
                coverage.Pixels[i] = pixels[i * 4 + 2];
            }

            return coverage;
        }
        finally
        {
            if (!oldBitmap.IsNull)
            {
                PInvoke.SelectObject(dc, oldBitmap);
            }

            PInvoke.DeleteObject(bitmap);
            PInvoke.DeleteDC(dc);
        }
    }

    private static unsafe BITMAPINFO TopDownInfo(int size)
    {
        var info = new BITMAPINFO();
        info.bmiHeader.biSize     = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth    = size;
        info.bmiHeader.biHeight   = -size;
        info.bmiHeader.biPlanes   = 1;
        info.bmiHeader.biBitCount = 32;
        return info;
    }
}
