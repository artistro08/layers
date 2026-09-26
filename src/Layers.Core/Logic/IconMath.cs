namespace Layers.Core.Logic;

/// <summary>
/// A single-channel coverage buffer, one byte per pixel, row major.
/// </summary>
/// <remarks>
/// The tray icon is monochrome, so it's rasterized as coverage and tinted at the end.
/// </remarks>
/// <param name="width">Width in pixels.</param>
/// <param name="height">Height in pixels.</param>
public sealed class AlphaBuffer(int width, int height)
{
    /// <summary>Gets the width.</summary>
    public int Width { get; } = width;

    /// <summary>Gets the height.</summary>
    public int Height { get; } = height;

    /// <summary>Gets the coverage values, row major.</summary>
    public byte[] Pixels { get; } = new byte[width * height];

    /// <summary>
    /// Gets or sets one pixel.
    /// </summary>
    /// <remarks>
    /// Convenience for tests and the rasterizer.
    /// </remarks>
    /// <param name="x">Column.</param>
    /// <param name="y">Row.</param>
    public byte this[int x, int y]
    {
        get => Pixels[y * Width + x];
        set => Pixels[y * Width + x] = value;
    }

    /// <summary>
    /// Gets the bounding box of non-zero coverage.
    /// </summary>
    /// <remarks>
    /// Inclusive bounds, or <see langword="null"/> for an empty buffer.
    /// </remarks>
    /// <returns>The bounds.</returns>
    public (int MinX, int MinY, int MaxX, int MaxY)? InkBounds()
    {
        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if (this[x, y] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        return maxX < 0 ? null : (minX, minY, maxX, maxY);
    }
}

/// <summary>
/// Pixel math for the tray icon.
/// </summary>
/// <remarks>
/// Ported from <c>compose.rs</c> in Layers 1.0.3.
/// </remarks>
public static class IconMath
{
    /// <summary>
    /// Gets the tray icon size for a DPI.
    /// </summary>
    /// <remarks>
    /// <c>16 * dpi / 96</c> (integer division), rounded up to a multiple of 4 so the 4x supersample divides evenly.
    /// </remarks>
    /// <param name="dpi">The taskbar's DPI.</param>
    /// <returns>The size in pixels.</returns>
    public static int IconSize(uint dpi)
    {
        var size = (int)(16 * dpi / 96);
        return (size + 3) / 4 * 4;
    }

    /// <summary>
    /// Box-filter downsample by an integer factor.
    /// </summary>
    /// <remarks>
    /// Rendering at 4x and reducing gives the digit clean edges at 16 pixels.
    /// </remarks>
    /// <param name="source">The large buffer.</param>
    /// <param name="factor">The reduction factor.</param>
    /// <returns>The reduced buffer.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="factor"/> is not positive.</exception>
    /// <exception cref="ArgumentException"><paramref name="factor"/> doesn't divide both dimensions.</exception>
    public static AlphaBuffer Downsample(AlphaBuffer source, int factor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(factor);
        if (source.Width % factor != 0 || source.Height % factor != 0)
        {
            throw new ArgumentException("factor must divide both dimensions", nameof(factor));
        }

        // Average Each Block
        var result = new AlphaBuffer(source.Width / factor, source.Height / factor);
        var count  = factor * factor;
        for (var y = 0; y < result.Height; y++)
        {
            for (var x = 0; x < result.Width; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++)
                    {
                        sum += source[x * factor + dx, y * factor + dy];
                    }
                }

                result[x, y] = (byte)(sum / count);
            }
        }

        return result;
    }

    /// <summary>
    /// Shifts the buffer so its ink sits centered.
    /// </summary>
    /// <remarks>
    /// Text layout centers the line box (ascent plus descent), not the ink, so a "centered" digit renders low.
    /// Measuring what was actually rasterized and shifting it works for any font.
    /// An empty buffer is returned unchanged.
    /// </remarks>
    /// <param name="source">The buffer.</param>
    /// <returns>A centered copy.</returns>
    public static AlphaBuffer CenterInk(AlphaBuffer source)
    {
        ArgumentNullException.ThrowIfNull(source);

        // Measure
        var result = new AlphaBuffer(source.Width, source.Height);
        var bounds = source.InkBounds();
        if (bounds is null)
        {
            source.Pixels.CopyTo(result.Pixels, 0);
            return result;
        }

        var (minX, minY, maxX, maxY) = bounds.Value;

        // Shift
        var dx = (source.Width - 1 - maxX - minX) / 2;
        var dy = (source.Height - 1 - maxY - minY) / 2;
        for (var y = 0; y < source.Height; y++)
        {
            var sy = y - dy;
            if (sy < 0 || sy >= source.Height)
            {
                continue;
            }

            for (var x = 0; x < source.Width; x++)
            {
                var sx = x - dx;
                if (sx < 0 || sx >= source.Width)
                {
                    continue;
                }

                result[x, y] = source[sx, sy];
            }
        }

        return result;
    }

    /// <summary>
    /// Expands coverage into premultiplied BGRA.
    /// </summary>
    /// <remarks>
    /// This is the format <c>CreateDIBSection</c> and <c>CreateIconIndirect</c> expect for 32-bit icons.
    /// </remarks>
    /// <param name="coverage">The coverage buffer.</param>
    /// <param name="r">Red.</param>
    /// <param name="g">Green.</param>
    /// <param name="b">Blue.</param>
    /// <returns>4 bytes per pixel, B G R A.</returns>
    public static byte[] ToPremultipliedBgra(AlphaBuffer coverage, byte r, byte g, byte b)
    {
        ArgumentNullException.ThrowIfNull(coverage);

        var output = new byte[coverage.Pixels.Length * 4];
        for (var i = 0; i < coverage.Pixels.Length; i++)
        {
            var alpha         = coverage.Pixels[i];
            output[i * 4]     = (byte)(b * alpha / 255);
            output[i * 4 + 1] = (byte)(g * alpha / 255);
            output[i * 4 + 2] = (byte)(r * alpha / 255);
            output[i * 4 + 3] = alpha;
        }

        return output;
    }
}
