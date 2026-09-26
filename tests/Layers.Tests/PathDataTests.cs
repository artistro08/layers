using System.Numerics;
using Layers.Core.Logic;

namespace Layers.Tests;

[TestClass]
public sealed class PathDataTests
{
    private static Vector2 P(float x, float y) => new(x, y);

    [TestMethod]
    public void Parse_SingleLineFigure()
    {
        var figures = PathData.Parse("M1 2L3 4Z");

        Assert.HasCount(1, figures);
        Assert.AreEqual(P(1, 2), figures[0].Start);
        CollectionAssert.AreEqual(new[] { PathSegment.Line(P(3, 4)) }, figures[0].Segments.ToArray());
    }

    [TestMethod]
    public void Parse_CubicIsThreePoints()
    {
        var figures = PathData.Parse("M0 0C1 2 3 4 5 6Z");

        CollectionAssert.AreEqual(new[] { PathSegment.Cubic(P(1, 2), P(3, 4), P(5, 6)) }, figures[0].Segments.ToArray());
    }

    [TestMethod]
    public void Parse_EachMoveToStartsAFigure()
    {
        var figures = PathData.Parse("M0 0L1 1ZM5 5L6 6Z");

        Assert.HasCount(2, figures);
        Assert.AreEqual(P(5, 5), figures[1].Start);
    }

    [TestMethod]
    public void Parse_NegativeAndFractional()
    {
        var figures = PathData.Parse("M-1.5 2.25L0.5 -3Z");

        Assert.AreEqual(P(-1.5f, 2.25f), figures[0].Start);
        CollectionAssert.AreEqual(new[] { PathSegment.Line(P(0.5f, -3)) }, figures[0].Segments.ToArray());
    }

    [TestMethod]
    public void Parse_CommasAndWhitespaceSeparate()
    {
        var a = PathData.Parse("M0,0 L1,1 Z");
        var b = PathData.Parse("M0 0L1 1Z");

        CollectionAssert.AreEqual(b[0].Segments.ToArray(), a[0].Segments.ToArray());
    }

    [TestMethod]
    public void Parse_BareCoordinatesRepeatTheCommand()
    {
        var figures = PathData.Parse("M0 0L1 1 2 2Z");

        Assert.HasCount(2, figures[0].Segments);
        Assert.AreEqual(PathSegment.Line(P(2, 2)), figures[0].Segments[1]);
    }

    [TestMethod]
    public void Parse_VAndHKeepTheOtherCoordinate()
    {
        var figures = PathData.Parse("M1 2V5H8Z");

        CollectionAssert.AreEqual(new[] { PathSegment.Line(P(1, 5)), PathSegment.Line(P(8, 5)) }, figures[0].Segments.ToArray());
    }

    [TestMethod]
    [DataRow("M0 0l1 1Z", DisplayName = "relative line")]
    [DataRow("M0 0v5Z", DisplayName = "relative vertical")]
    [DataRow("M0 0h5Z", DisplayName = "relative horizontal")]
    [DataRow("M0 0A1 1 0 0 1 2 2Z", DisplayName = "arc")]
    [DataRow("M0 0L1Z", DisplayName = "truncated run")]
    [DataRow("1 1Z", DisplayName = "no leading command")]
    [DataRow("V5Z", DisplayName = "V before M")]
    [DataRow("", DisplayName = "empty")]
    public void Parse_RejectsUnsupportedOrMalformed(string data)
    {
        Assert.ThrowsExactly<FormatException>(() => PathData.Parse(data));
    }

    [TestMethod]
    public void LayersGlyph_ParsesIntoThreeFigures()
    {
        var figures = PathData.Parse(PathData.LayersGlyph);

        Assert.HasCount(3, figures);
        Assert.IsTrue(figures.All(f => f.Segments.Count > 0));
    }

    [TestMethod]
    public void LayersGlyph_StaysInsideItsViewBox()
    {
        foreach (var figure in PathData.Parse(PathData.LayersGlyph))
        {
            var points = figure.Segments.SelectMany(s => new[] { s.Control1, s.Control2, s.End }).Append(figure.Start);
            foreach (var point in points)
            {
                Assert.IsTrue(point.X is >= 0 and <= PathData.LayersGlyphViewBox && point.Y is >= 0 and <= PathData.LayersGlyphViewBox, $"{point} escapes the view box");
            }
        }
    }
}
