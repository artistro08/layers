using System.ComponentModel;
using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Layers.UI.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using static Layers.UI.SheetChips;

namespace Layers.UI;

/// <summary>
/// The shortcut sheet: every mapping by layer, laid out like the PowerToys Shortcut Guide, with a status footer.
/// </summary>
/// <remarks>
/// One section per layer on a subtle card, headed by its title and a green Active badge on the layer you're on that fades in and out. Within a layer each mapped key
/// is one divided block: the key on the left, and against the right edge everything it does there, one line each, so a
/// key's tap and hold read together. A one-step macro shows what it sends, with the macro's number on hover; a longer
/// macro shows its steps, an arrow, then the macro. An output with Tap, Hold,
/// or Sticky is wrapped in one chip of the flag's color, with a divider and the flag's name at the end. Keys are styled
/// like the PowerToys Shortcut Guide's caps; mouse buttons, media keys, layers, and macros carry a solid icon and a
/// label. The header's search box filters key blocks and hides sections left empty. The sections are built in code on
/// every <c>Sections</c> change rather than through data templates, so Native AOT needs no template binding; a layer
/// switch only toggles the pills.
/// </remarks>
public sealed partial class ShortcutSheetView : UserControl
{
    // Fluent Motion: https://learn.microsoft.com/windows/apps/design/motion/timing-and-easing
    private static readonly TimeSpan FadeIn  = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(167);

    // Each Section's Active Pill By Layer, And Each Key Block With Its Searchable Text
    private readonly Dictionary<int, Border> _pills = [];
    private readonly Dictionary<Border, (Storyboard Story, bool Show)> _fades = [];
    private readonly List<(FrameworkElement Section, List<(FrameworkElement Row, string Text)> Rows)> _built = [];

    /// <summary>
    /// Creates the view.
    /// </summary>
    /// <remarks>
    /// The view follows <paramref name="model"/> for as long as it lives.
    /// </remarks>
    /// <param name="model">The sheet's view model.</param>
    public ShortcutSheetView(ShortcutSheetViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        Model = model;
        InitializeComponent();
        Model.PropertyChanged += Model_PropertyChanged;
        RebuildSections();
    }

    /// <summary>Raised on Esc.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Raised by the footer's settings button.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised by the footer's edit button.</summary>
    public event EventHandler? EditRequested;

    /// <summary>Gets the view model.</summary>
    public ShortcutSheetViewModel Model { get; }

    internal IReadOnlyList<FrameworkElement> SectionElements => SectionsPanel.Children.OfType<FrameworkElement>().ToList();

    internal int VisibleRowCount => _built.Where(s => s.Section.Visibility == Visibility.Visible).Sum(s => s.Rows.Count(r => r.Row.Visibility == Visibility.Visible));

    /// <summary>
    /// Asks the host to close, as Esc does.
    /// </summary>
    /// <remarks>
    /// Public so the window and tests can share the one path.
    /// </remarks>
    public void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    internal void FocusList() => SearchBox.Focus(FocusState.Programmatic);

    // =========================================================================
    // SECTIONS
    // =========================================================================

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShortcutSheetViewModel.Sections):
                RebuildSections();
                break;
            case nameof(ShortcutSheetViewModel.ActiveLayers):
            case nameof(ShortcutSheetViewModel.MarksActive):
                UpdatePills();
                break;
            default:
                break;
        }
    }

    private void RebuildSections()
    {
        SectionsPanel.Children.Clear();
        _pills.Clear();
        _fades.Clear();
        _built.Clear();
        foreach (var section in Model.Sections)
        {
            SectionsPanel.Children.Add(BuildSection(section));
        }

        ApplyFilter();
    }

    // A Layer Switch: The Same Blocks, Only The Pills Move
    private void UpdatePills()
    {
        foreach (var (layer, pill) in _pills)
        {
            Fade(pill, IsActiveLayer(layer));
        }
    }

    private bool IsActiveLayer(int layer) => Model.MarksActive && Model.ActiveLayers.Active.Contains(layer);

    // A Layer's Section: Its Title And Active Pill, Then One Divided Block Per Key
    private Border BuildSection(LayerSection section)
    {
        var panel = new StackPanel();

        // A Fixed Height, So Showing The Badge Never Moves Anything
        var header = new Grid { Height = 24, Margin = new Thickness(0, 0, 0, 4) };
        header.Children.Add(new TextBlock
        {
            Text              = section.Title,
            Style             = Resource<Style>("BodyStrongTextBlockStyle"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        var pill        = ActivePill();
        pill.Visibility = IsActiveLayer(section.Layer) ? Visibility.Visible : Visibility.Collapsed;
        header.Children.Add(pill);
        _pills[section.Layer] = pill;
        panel.Children.Add(header);

        var blocks = new List<(FrameworkElement Row, string Text)>();
        for (var i = 0; i < section.Keys.Count; i++)
        {
            var key   = section.Keys[i];
            var block = BuildBlock(key, last: i == section.Keys.Count - 1);
            blocks.Add((block, $"{section.Title} {Label(key.Input)} {MeaningOf(key.Input)} {string.Join(" ", key.Rows.Select(SearchText))} {string.Join(" ", key.Rows.SelectMany(r => OutputNames(r.Output)).Select(MeaningOf))}"));
            panel.Children.Add(block);
        }

        var card = new Border
        {
            Child           = panel,
            Padding         = new Thickness(12, 8, 12, 4),
            Background      = Resource<Brush>("CardBackgroundFillColorDefaultBrush"),
            BorderBrush     = Resource<Brush>("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius    = Resource<CornerRadius>("OverlayCornerRadius"),
        };

        _built.Add((card, blocks));
        return card;
    }

    // The Layer You're On: A Green Chip Shaped Like The Tap, Hold, And Sticky Chips, Tinted Stronger So It Stands Out
    private static Border ActivePill() => new()
    {
        Tag                 = "Active",
        Background          = Resource<Brush>("ActiveChipBackgroundBrush"),
        CornerRadius        = Resource<CornerRadius>("ControlCornerRadius"),
        Padding             = new Thickness(8, 5, 8, 5),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment   = VerticalAlignment.Center,
        Child               = ChipText("Active", Resource<Brush>("ActiveChipForegroundBrush")),
    };

    // Fades A Badge In Or Out; It Leaves The Layout Only Once It Has Faded, So Nothing Moves Mid-Fade. A Quick
    // Toggle Turns The Running Fade Around From Where It Is, So A Stale One Can Never Finish Last.
    private void Fade(Border pill, bool show)
    {
        if (_fades.TryGetValue(pill, out var running))
        {
            if (running.Show == show)
            {
                return;
            }

            var opacity = pill.Opacity;
            running.Story.Stop();
            pill.Opacity = opacity;
            _fades.Remove(pill);
        }
        else if (show == (pill.Visibility == Visibility.Visible))
        {
            return;
        }

        if (show && pill.Visibility == Visibility.Collapsed)
        {
            pill.Opacity    = 0;
            pill.Visibility = Visibility.Visible;
        }

        var fade = new DoubleAnimation
        {
            To             = show ? 1 : 0,
            Duration       = show ? FadeIn : FadeOut,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(fade, pill);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var story = new Storyboard();
        story.Children.Add(fade);
        story.Completed += (_, _) =>
        {
            if (!_fades.TryGetValue(pill, out var current) || current.Story != story)
            {
                return;
            }

            _fades.Remove(pill);
            pill.Opacity = show ? 1 : 0;
            if (!show)
            {
                pill.Visibility = Visibility.Collapsed;
            }
        };
        _fades[pill] = (story, show);
        story.Begin();
    }

    // =========================================================================
    // KEY BLOCKS
    // =========================================================================

    // One Key: Its Chip On The Left; Against The Right Edge, Everything It Does On This Layer, One Line Each (Tap And
    // Hold Stack Here); A Hairline Below The Block
    private Border BuildBlock(KeyGroup key, bool last)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var input = Named(key.Input, Model.Meanings);
        input.VerticalAlignment = key.Rows.Count > 1 ? VerticalAlignment.Top : VerticalAlignment.Center;

        var lines = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        foreach (var row in key.Rows)
        {
            lines.Children.Add(Line(row, Model.Meanings));
        }

        Grid.SetColumn(input, 0);
        Grid.SetColumn(lines, 1);
        grid.Children.Add(input);
        grid.Children.Add(lines);

        return new Border
        {
            Child           = grid,
            Padding         = new Thickness(0, 8, 0, 8),
            BorderThickness = new Thickness(0, 0, 0, last ? 0 : 1),
            BorderBrush     = Resource<Brush>("DividerStrokeColorDefaultBrush"),
        };
    }

    // One Thing The Key Does: A Macro's Steps First, Then The Output (Wrapped In Its Flag's Chip)
    private static StackPanel Line(ShortcutRow row, IReadOnlyDictionary<int, string> meanings)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right };
        if (row.Output is MacroOutput macro && macro.Steps.Count > 1)
        {
            AddSteps(line, macro);
            line.Children.Add(Separator("\uE72A", 12));
        }

        line.Children.Add(Action(row, meanings));
        return line;
    }

    // The Output Chips; With Tap, Hold, Or Sticky They Sit Inside One Chip Of That Color, Then A Divider, Then The Label
    private static FrameworkElement Action(ShortcutRow row, IReadOnlyDictionary<int, string> meanings)
    {
        var outputs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        var names   = OutputNames(row.Output);
        for (var i = 0; i < names.Count; i++)
        {
            if (i > 0)
            {
                outputs.Children.Add(Separator("", 8));
            }

            outputs.Children.Add(Named(names[i], meanings));
        }

        // A One-Step Macro Shows What It Sends, After A Bare Macro Icon And A Light Divider; Its Number Is There On Hover
        if (row.Output is MacroOutput { Steps.Count: 1 } single)
        {
            outputs.Children.Insert(0, Marker(SheetIcons.For($"Macro {single.Number}")!.Value.Icon(IconSize)));
            outputs.Children.Insert(1, Divider());
            ToolTipService.SetToolTip(outputs, $"Macro {single.Number}");
        }

        if (row.Output is MacroOutput { Steps.Count: 0 })
        {
            outputs.Children.Add(PlainTag("Empty"));
        }

        var flags = Flags(row).ToList();
        if (flags.Count == 0)
        {
            return outputs;
        }

        var foreground = Resource<Brush>($"{flags[0]}ChipForegroundBrush");
        foreach (var divider in outputs.Children.OfType<Microsoft.UI.Xaml.Shapes.Rectangle>())
        {
            divider.Fill    = foreground;
            divider.Opacity = 0.5;
        }

        var inside     = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        inside.Children.Add(outputs);
        inside.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width             = 1,
            Height            = 14,
            Opacity           = 0.5,
            Fill              = foreground,
            VerticalAlignment = VerticalAlignment.Center,
        });
        inside.Children.Add(ChipText(string.Join(" · ", flags), foreground));

        return new Border
        {
            Child             = inside,
            Background        = Resource<Brush>($"{flags[0]}ChipBackgroundBrush"),
            CornerRadius      = Resource<CornerRadius>("ControlCornerRadius"),
            Padding           = new Thickness(row.Output is MacroOutput { Steps.Count: 1 } ? 11 : 3, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    // Keys As Theirs; A One-Step Macro As That Step's Keys, Since What It Sends Is What Matters; Any Other Macro As Its Chip
    private static IReadOnlyList<string> OutputNames(ShortcutOutput output) => output switch
    {
        MacroOutput { Steps.Count: 1 } macro => macro.Steps[0],
        MacroOutput macro => [$"Macro {macro.Number}"],
        KeysOutput keys   => keys.Keys,
        _                 => [],
    };

    // Sticky, Tap, And Hold, In That Order
    private static IEnumerable<string> Flags(ShortcutRow row)
    {
        if (row.Sticky)
        {
            yield return "Sticky";
        }

        if (row.Tap)
        {
            yield return "Tap";
        }

        if (row.Hold)
        {
            yield return "Hold";
        }
    }

    // A Macro's Steps: Keys Joined By "+", Steps By A Chevron
    private static void AddSteps(Panel keys, MacroOutput macro)
    {
        for (var i = 0; i < macro.Steps.Count; i++)
        {
            if (i > 0)
            {
                keys.Children.Add(Separator("\uE76C", 10));
            }

            for (var k = 0; k < macro.Steps[i].Count; k++)
            {
                if (k > 0)
                {
                    keys.Children.Add(Separator("\uE710", 8));
                }

                keys.Children.Add(Chip(macro.Steps[i][k]));
            }
        }
    }

    // A Described Expression's Words, For Search
    private string MeaningOf(string name) =>
        ExpressionMeaning.Slot(name) is int slot && Model.Meanings.TryGetValue(slot, out var text) ? text : string.Empty;

    private static string SearchText(ShortcutRow row)
    {
        var steps = row.Output is MacroOutput macro ? $"Macro {macro.Number} {string.Join(" ", macro.Steps.SelectMany(step => step).Select(Label))}" : string.Empty;
        return $"{string.Join(" ", OutputNames(row.Output).Select(Label))} {string.Join(" ", Flags(row))} {steps}";
    }

    // =========================================================================
    // SEARCH
    // =========================================================================

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();

    // Rows Whose Text Doesn't Contain The Query Hide, And So Do Sections Left Empty
    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        foreach (var (section, rows) in _built)
        {
            var any = false;
            foreach (var (row, text) in rows)
            {
                var hit        = query.Length == 0 || text.Contains(query, StringComparison.OrdinalIgnoreCase);
                row.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
                any           |= hit;
            }

            section.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // =========================================================================
    // HANDLERS
    // =========================================================================

    private void Escape_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        RequestClose();
    }

    private void Edit_Click(object sender, RoutedEventArgs e) => EditRequested?.Invoke(this, EventArgs.Empty);

    private void Settings_Click(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
}
