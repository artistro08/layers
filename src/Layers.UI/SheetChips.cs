using Layers.Core.Logic;
using Layers.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Layers.UI;

/// <summary>
/// The shortcut sheet's chips, shared with the Settings window's expression editor.
/// </summary>
/// <remarks>
/// A keyboard key is a PowerToys Shortcut Guide cap. A mouse button, media key, scroll, layer, or macro is the same
/// chip with a solid icon and a label; an expression's icon is an asterisk. An expression the user has described draws as
/// a bare asterisk, a light divider, then that description: its text, with each <c>{Key}</c> as a chip.
/// </remarks>
internal static class SheetChips
{
    /// <summary>A cap's height: 12 pt text plus 5 px padding and a 1 px border on each side.</summary>
    internal const double CapHeight = 24;

    /// <summary>The size of a chip's icon.</summary>
    internal const double IconSize = 14;

    /// <summary>Draws a key as a chip.</summary>
    /// <param name="name">The key's name as the web config tool gives it.</param>
    /// <returns>The chip.</returns>
    internal static FrameworkElement Chip(string name)
    {
        if (SheetIcons.For(name) is not { } icon)
        {
            return new KeyVisual
            {
                Content           = name,
                Style             = Resource<Style>("SheetKeyVisualStyle"),
                RenderKeyAsGlyph  = true,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var glyph   = Tint(icon.Icon(IconSize));
        glyph.Margin            = new Thickness(0, -1, 0, -1);
        glyph.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(glyph);
        content.Children.Add(ChipText(icon.Label, Resource<Brush>("TextFillColorSecondaryBrush")));

        return ChipBorder(content, Resource<Brush>("ControlFillColorInputActiveBrush"), Resource<Brush>("ControlElevationBorderBrush"));
    }

    /// <summary>
    /// Draws a key, or the user's description in its place when it is an expression they have described.
    /// </summary>
    /// <remarks>
    /// The expression's own name stays on hover.
    /// </remarks>
    /// <param name="name">The key's name.</param>
    /// <param name="meanings">The user's expression meanings by slot.</param>
    /// <returns>The chip or the description.</returns>
    internal static FrameworkElement Named(string name, IReadOnlyDictionary<int, string> meanings)
    {
        ArgumentNullException.ThrowIfNull(meanings);

        if (ExpressionMeaning.Slot(name) is int slot && meanings.TryGetValue(slot, out var text) && ExpressionMeaning.Parse(text).Count > 0)
        {
            var marked = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            marked.Children.Add(Marker(SheetIcons.For(name)!.Value.Icon(IconSize)));
            marked.Children.Add(Divider());
            marked.Children.Add(Meaning(text));
            ToolTipService.SetToolTip(marked, name);
            return marked;
        }

        return Chip(name);
    }

    /// <summary>
    /// Draws a description: its text, with each <c>{Key}</c> as a chip.
    /// </summary>
    /// <remarks>
    /// Wraps, so a long description takes more lines instead of being cut.
    /// </remarks>
    /// <param name="text">The description.</param>
    /// <returns>The drawn description.</returns>
    internal static WrapPanel Meaning(string? text)
    {
        var panel = new WrapPanel { ColumnSpacing = 4, RowSpacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var part in ExpressionMeaning.Parse(text))
        {
            panel.Children.Add(part.IsKey
                ? Chip(part.Text)
                : new TextBlock { Text = part.Text, Height = CapHeight, Padding = new Thickness(0, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        }

        return panel;
    }

    /// <summary>A bare icon in the secondary text color, marking what follows as a macro or an expression.</summary>
    /// <param name="icon">The icon, from <see cref="SheetIcons"/>.</param>
    /// <returns>The icon, recolored and centered.</returns>
    internal static FrameworkElement Marker(FrameworkElement icon)
    {
        ArgumentNullException.ThrowIfNull(icon);

        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = new Thickness(0, 0, 2, 0);
        return Tint(icon);
    }

    // Icons Take The Secondary Text Color, Like The Chip's Label, So They Don't Read Heavier Than The Text
    private static FrameworkElement Tint(FrameworkElement icon)
    {
        var brush = Resource<Brush>("TextFillColorSecondaryBrush");
        switch (icon)
        {
            case Viewbox { Child: Panel frame }:
                foreach (var path in frame.Children.OfType<IconElement>())
                {
                    path.Foreground = brush;
                }

                break;
            case Viewbox { Child: IconElement single }:
                single.Foreground = brush;
                break;
            default:
                break;
        }

        return icon;
    }

    /// <summary>A light 1 x 14 divider after a marker.</summary>
    /// <returns>The divider.</returns>
    internal static Microsoft.UI.Xaml.Shapes.Rectangle Divider() => new()
    {
        Width             = 1,
        Height            = 14,
        Margin            = new Thickness(2, 0, 2, 0),
        Fill              = Resource<Brush>("DividerStrokeColorDefaultBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>A neutral tag, for an empty macro slot.</summary>
    /// <param name="text">The tag's text.</param>
    /// <returns>The tag.</returns>
    internal static Border PlainTag(string text) =>
        ChipBorder(ChipText(text, Resource<Brush>("TextFillColorSecondaryBrush")), Resource<Brush>("ControlFillColorInputActiveBrush"), Resource<Brush>("ControlElevationBorderBrush"));

    /// <summary>A chip's box, matching SheetKeyVisualStyle: padding 6,5, a 1 px border, control corners.</summary>
    /// <param name="child">What the chip holds.</param>
    /// <param name="background">The fill.</param>
    /// <param name="border">The stroke.</param>
    /// <returns>The box.</returns>
    internal static Border ChipBorder(UIElement child, Brush background, Brush border) => new()
    {
        Child             = child,
        Background        = background,
        BorderBrush       = border,
        BorderThickness   = new Thickness(1),
        CornerRadius      = Resource<CornerRadius>("ControlCornerRadius"),
        Padding           = new Thickness(6, 5, 6, 5),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>12 pt text centered in a 12 px box with tight line bounds, exactly as KeyCharPresenter draws a cap's text.</summary>
    /// <param name="text">The text.</param>
    /// <param name="foreground">Its color.</param>
    /// <returns>The text box.</returns>
    internal static Grid ChipText(string text, Brush foreground) => new()
    {
        Height            = 12,
        VerticalAlignment = VerticalAlignment.Center,
        Children          =
        {
            new TextBlock
            {
                Text              = text,
                FontSize          = 12,
                Foreground        = foreground,
                TextLineBounds    = TextLineBounds.Tight,
                VerticalAlignment = VerticalAlignment.Center,
            },
        },
    };

    /// <summary>The name a chip shows, for search.</summary>
    /// <param name="name">The key's name.</param>
    /// <returns>Its label.</returns>
    internal static string Label(string name) => SheetIcons.For(name)?.Label ?? name;

    /// <summary>An arrow, chevron, or "+" between keys, centered on a cap's height.</summary>
    /// <param name="glyph">The Segoe Fluent glyph.</param>
    /// <param name="size">Its size.</param>
    /// <returns>The separator.</returns>
    internal static FontIcon Separator(string glyph, double size) => new()
    {
        Glyph             = glyph,
        FontSize          = size,
        Height            = CapHeight,
        Foreground        = Resource<Brush>("TextFillColorSecondaryBrush"),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Looks up an app resource.</summary>
    /// <typeparam name="T">The resource's type.</typeparam>
    /// <param name="key">Its key.</param>
    /// <returns>The resource.</returns>
    internal static T Resource<T>(string key) => (T)Application.Current.Resources[key];
}
