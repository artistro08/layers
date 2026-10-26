using System.ComponentModel;
using Layers.Core.Logic;
using Layers.Core.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Navigation;

namespace Layers.UI;

/// <summary>
/// Settings › Shortcuts: the PowerToys picker for the shortcut sheet's hotkey.
/// </summary>
/// <remarks>
/// Press the new keys in the picker's dialog; Reset picks Win+Shift+L, Clear turns the hotkey off. While the dialog
/// listens the app lets go of its own hotkey, so pressing it reaches the dialog. A combination Windows or another app
/// holds shows a warning under the card and cannot be saved; one that turns out taken only when registered is undone,
/// and the note says so. Below it, the web config tool's address that the sheet's pencil opens, then one card per
/// expression the device uses that lets the user say what it does, with a key picker that inserts keys as chips.
/// </remarks>
public sealed partial class ShortcutsPage : Page
{
    private const string ConflictMessage = "Another app or Windows already uses this shortcut.";

    // Typing Pauses This Long Before A Meaning Is Saved And The Sheet Redrawn
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(400);

    private SettingsViewModel _model = null!;
    private Hotkey? _before;

    /// <summary>Creates the page.</summary>
    public ShortcutsPage()
    {
        InitializeComponent();
        Picker.DefaultHotkeySettings = HotkeySettings.FromHotkey(Hotkey.Default);
        Picker.CheckConflict         = hotkey => _model.IsHotkeyFree?.Invoke(hotkey) == false ? ConflictMessage : null;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        _model = (SettingsViewModel)e.Parameter;
        _model.PropertyChanged += Model_PropertyChanged;
        Load();
        BuildExpressions();
        EditorUrlBox.Text = _model.EditorUrlText;
    }

    /// <inheritdoc/>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        _model.PropertyChanged -= Model_PropertyChanged;
        CloseDialogs();
    }

    /// <summary>
    /// Closes an open picker dialog, which lets go of the keyboard.
    /// </summary>
    /// <remarks>
    /// The Settings window calls it when it closes, since WinUI may not unload the page then.
    /// </remarks>
    public void CloseDialogs() => Picker.CloseDialog();

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.Expressions))
        {
            BuildExpressions();
            return;
        }

        if (e.PropertyName is nameof(SettingsViewModel.Shortcut) or nameof(SettingsViewModel.ShortcutTaken) or nameof(SettingsViewModel.ShortcutNote))
        {
            Load();
        }
    }

    // The Saved Hotkey As Key Caps; A Taken One Shows Them In The Warning State With The Reason Under The Card
    private void Load()
    {
        Picker.HotkeySettings = _model.Shortcut is { } hotkey ? HotkeySettings.FromHotkey(hotkey) : new HotkeySettings();
        Picker.HasConflict    = _model.ShortcutTaken;

        var message = _model.ShortcutNote ?? (_model.ShortcutTaken ? ConflictMessage : null);
        Picker.Tooltip  = message;
        Note.Message    = message ?? string.Empty;
        Note.IsOpen     = message is not null;
        Note.Visibility = message is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    // The App's Own Hotkey Lets Go While The Dialog Listens, So Pressing It Reaches The Dialog
    private void Picker_DialogOpening(object sender, EventArgs e)
    {
        _before             = _model.Shortcut;
        _model.ShortcutNote = null;
        _model.NotifyShortcutDialog(open: true);
    }

    // ...And Comes Back After. A Combination Another App Took Since The Dialog Checked It Gives Way To The One Before.
    private void Picker_DialogClosed(object sender, EventArgs e)
    {
        _model.NotifyShortcutDialog(open: false);
        if (_model.ShortcutTaken && _model.Shortcut != _before)
        {
            _model.ShortcutNote = "That shortcut is taken, so the previous one was kept.";
            _model.SetShortcut(_before);
        }
    }

    // A New Combination, The Default (Reset), Or Empty (Cleared)
    private void Picker_HotkeySettingsChanged(object sender, EventArgs e) =>
        _model.SetShortcut(Picker.HotkeySettings is { } settings && !settings.IsEmpty() ? settings.ToHotkey() : null);

    // =========================================================================
    // WEB CONFIG TOOL
    // =========================================================================

    // A Usable Address Saves As It's Typed; Anything Else Says So And Keeps The Last Good One
    private void EditorUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _model.EditorUrlText      = EditorUrlBox.Text;
        EditorUrlError.Visibility = _model.EditorUrlInvalid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EditorUrlReset_Click(object sender, RoutedEventArgs e)
    {
        _model.ResetEditorUrl();
        EditorUrlBox.Text = _model.EditorUrlText;
    }

    // =========================================================================
    // EXPRESSIONS
    // =========================================================================

    /// <summary>Gets the expression cards, for tests: each with its slot, text box, and preview.</summary>
    internal List<ExpressionCard> ExpressionCards { get; } = [];

    /// <summary>
    /// Inserts a key at the card's caret, as picking it does.
    /// </summary>
    /// <remarks>
    /// The text is saved through the text box, so the sheet updates at once.
    /// </remarks>
    /// <param name="card">The card.</param>
    /// <param name="name">The key's name.</param>
    internal static void InsertKey(ExpressionCard card, string name)
    {
        var box   = card.Box;
        var at    = Math.Clamp(box.SelectionStart, 0, box.Text.Length);
        var token = ExpressionMeaning.Token(name);
        box.Text           = box.Text.Insert(at, token);
        box.SelectionStart = at + token.Length;
        box.Focus(FocusState.Programmatic);
        card.Save();
    }

    /// <summary>
    /// Lists the picker's keys matching a search.
    /// </summary>
    /// <param name="query">The search; empty lists every key.</param>
    /// <returns>The key names, in table order.</returns>
    internal static IReadOnlyList<string> PickerItems(string query) =>
        UsageNames.AllNames.Where(name => query.Length == 0 || name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

    private void BuildExpressions()
    {
        ExpressionsPanel.Children.Clear();
        ExpressionCards.Clear();
        foreach (var expression in _model.Expressions)
        {
            ExpressionsPanel.Children.Add(BuildCard(expression));
        }

        ExpressionsNote.Visibility = _model.ShowExpressionsNote ? Visibility.Visible : Visibility.Collapsed;
    }

    // A Card: The Expression And What It Drives, Then The Meaning's Box With The Key Button, Then The Meaning As The Sheet Draws It
    private Border BuildCard(ExpressionMeaningViewModel expression)
    {
        var panel = new StackPanel { Spacing = 8 };

        var heading = new StackPanel { Spacing = 2 };
        heading.Children.Add(new TextBlock { Text = expression.Title });
        heading.Children.Add(new TextBlock
        {
            Text       = expression.Drives,
            Style      = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        });
        panel.Children.Add(heading);

        var box = new TextBox
        {
            Text            = expression.Text,
            PlaceholderText = "What does it do? For example: Hold {Back} and swipe left",
            TextWrapping    = TextWrapping.Wrap,
        };
        AutomationProperties.SetName(box, $"What {expression.Title} does");

        var keyButton = new Button
        {
            Content = new FontIcon { Glyph = "\uE765", FontSize = 16 },
            Padding = new Thickness(8, 6, 8, 6),
        };
        ToolTipService.SetToolTip(keyButton, "Add a key");
        AutomationProperties.SetName(keyButton, "Add a key");

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(keyButton, 1);
        row.Children.Add(box);
        row.Children.Add(keyButton);
        panel.Children.Add(row);

        var preview = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(preview);

        var card = new ExpressionCard(expression.Slot, box, preview, () => expression.Text = box.Text);
        ExpressionCards.Add(card);
        ShowPreview(card);

        // The Preview Follows Each Keystroke; Saving (Which Redraws The Sheet) Waits For A Pause, Or For Focus To Leave
        var pause = DispatcherQueue.CreateTimer();
        pause.Interval    = SaveDelay;
        pause.IsRepeating = false;
        pause.Tick       += (_, _) => card.Save();
        box.TextChanged  += (_, _) =>
        {
            ShowPreview(card);
            pause.Stop();
            pause.Start();
        };
        box.LostFocus += (_, _) =>
        {
            pause.Stop();
            card.Save();
        };

        keyButton.Flyout = BuildPicker(card);

        return new Border
        {
            Child           = panel,
            Padding         = new Thickness(16, 12, 16, 12),
            Background      = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush     = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius    = (CornerRadius)Application.Current.Resources["ControlCornerRadius"],
        };
    }

    private static void ShowPreview(ExpressionCard card)
    {
        var empty               = ExpressionMeaning.Parse(card.Box.Text).Count == 0;
        card.Preview.Content    = empty ? null : SheetChips.Meaning(card.Box.Text);
        card.Preview.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // The Key Picker: A Search Box Over Every Key As A Chip; Clicking One Inserts It At The Caret
    private static Flyout BuildPicker(ExpressionCard card)
    {
        var search = new TextBox { PlaceholderText = "Search keys" };
        AutomationProperties.SetName(search, "Search keys");

        var list = new ListView
        {
            Width              = 280,
            Height             = 300,
            SelectionMode      = ListViewSelectionMode.None,
            IsItemClickEnabled = true,
        };

        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { search, list } } };

        void Fill() => list.ItemsSource = PickerItems(search.Text.Trim())
            .Select(name =>
            {
                var chip = SheetChips.Chip(name);
                chip.Tag                 = name;
                chip.HorizontalAlignment = HorizontalAlignment.Left;
                return (object)chip;
            })
            .ToList();

        search.TextChanged += (_, _) => Fill();
        list.ItemClick     += (_, e) =>
        {
            if (e.ClickedItem is FrameworkElement { Tag: string name })
            {
                flyout.Hide();
                InsertKey(card, name);
            }
        };
        flyout.Opening += (_, _) =>
        {
            search.Text = string.Empty;
            Fill();
        };
        flyout.Opened += (_, _) => search.Focus(FocusState.Programmatic);

        return flyout;
    }
}

/// <summary>
/// One expression card's parts, for the page and its tests.
/// </summary>
/// <param name="Slot">The expression slot.</param>
/// <param name="Box">The meaning's text box.</param>
/// <param name="Preview">The meaning as the sheet draws it.</param>
/// <param name="Save">Saves the box's text now.</param>
internal sealed record ExpressionCard(int Slot, TextBox Box, ContentControl Preview, Action Save);
