using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Layers.UI.Controls;

/// <summary>
/// Lays children out left to right at their natural width, wrapping to a new row when the next one doesn't fit.
/// </summary>
/// <remarks>
/// Used for a row's output chips and a macro's steps, so a long macro wraps onto more lines instead of being cut.
/// Collapsed children take no space or spacing, like a StackPanel. Ported from MagicBuds.
/// </remarks>
public sealed partial class WrapPanel : Panel
{
    // Layout Rounding Can Give Arrange A Width A Fraction Under What Measure Asked For
    private const double LayoutSlack = 0.5;

    /// <summary>Gets or sets the gap between children on a row.</summary>
    public double ColumnSpacing { get; set; }

    /// <summary>Gets or sets the gap between rows.</summary>
    public double RowSpacing { get; set; }

    /// <inheritdoc/>
    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
        }

        return Place(availableSize.Width, arrange: false);
    }

    /// <inheritdoc/>
    protected override Size ArrangeOverride(Size finalSize)
    {
        Place(finalSize.Width, arrange: true);
        return finalSize;
    }

    // One Pass For Both Measure And Arrange, So The Two Can't Disagree On Where Rows Break
    private Size Place(double width, bool arrange)
    {
        double x         = 0;
        double y         = 0;
        double rowHeight = 0;
        double widest    = 0;
        var    rowStart  = 0;
        var    visible   = Children.Where(child => child.Visibility == Visibility.Visible).ToList();

        for (var i = 0; i < visible.Count; i++)
        {
            var size = visible[i].DesiredSize;

            // Wrap, Unless This Is The Row's First Child (Too Wide Either Way)
            if (i > rowStart && x + ColumnSpacing + size.Width > width + LayoutSlack)
            {
                y        += rowHeight + RowSpacing;
                x         = 0;
                rowHeight = 0;
                rowStart  = i;
            }

            if (i > rowStart)
            {
                x += ColumnSpacing;
            }

            if (arrange)
            {
                visible[i].Arrange(new Rect(x, y, size.Width, size.Height));
            }

            x        += size.Width;
            rowHeight = Math.Max(rowHeight, size.Height);
            widest    = Math.Max(widest, x);
        }

        return new Size(widest, y + rowHeight);
    }
}
