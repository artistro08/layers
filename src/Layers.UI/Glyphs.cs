using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using PathData = Layers.Core.Logic.PathData;

namespace Layers.UI;

/// <summary>
/// XAML geometry for the vendored layers glyph.
/// </summary>
/// <remarks>
/// Segoe Fluent Icons has no filled layers glyph, so the menu and HUD draw <see cref="PathData.LayersGlyph"/> in a
/// <c>PathIcon</c>. The geometry is built from the same parsed figures the tray icon fills, scaled from the 24 px view
/// box to the icon size, so the path lives in one place.
/// </remarks>
public static class Glyphs
{
    /// <summary>
    /// Builds the layers glyph at a size.
    /// </summary>
    /// <remarks>
    /// Returns a new geometry on every call, since a XAML geometry can't be shared between elements. Called from
    /// x:Bind.
    /// </remarks>
    /// <param name="size">Icon box size in DIPs, for example 16 in the menu and 20 in the HUD.</param>
    /// <returns>A filled, closed, nonzero-winding geometry that fits a <paramref name="size"/> square.</returns>
    public static Geometry Layers(double size)
    {
        var scale    = size / PathData.LayersGlyphViewBox;
        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };

        Point ToPoint(System.Numerics.Vector2 point) => new(point.X * scale, point.Y * scale);

        // One Closed Figure Per Subpath
        foreach (var source in PathData.Parse(PathData.LayersGlyph))
        {
            var figure = new PathFigure { StartPoint = ToPoint(source.Start), IsClosed = true, IsFilled = true };
            foreach (var segment in source.Segments)
            {
                figure.Segments.Add(segment.IsCubic
                    ? new BezierSegment { Point1 = ToPoint(segment.Control1), Point2 = ToPoint(segment.Control2), Point3 = ToPoint(segment.End) }
                    : new LineSegment { Point = ToPoint(segment.End) });
            }
            geometry.Figures.Add(figure);
        }

        return geometry;
    }
}
