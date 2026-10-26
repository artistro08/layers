using System.Reflection;
using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Core.ViewModels;

/// <summary>
/// Backs the Settings window.
/// </summary>
/// <remarks>
/// Every change saves immediately. There's no Save button. <see cref="HudSettingsChanged"/> tells the app so the HUD
/// rules use the new values on the next layer change.
/// </remarks>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsStore _store;
    private readonly IStartupRegistration _startup;
    private HudSettings _hud;
    private StartupState _startupState = new(false, false, null);
    private Hotkey? _shortcut;
    private string _editorUrlText = string.Empty;
    private bool _editorUrlInvalid;
    private bool _shortcutTaken;
    private string? _shortcutNote;

    /// <summary>
    /// Creates the view model and loads HUD settings.
    /// </summary>
    /// <remarks>
    /// Call <see cref="LoadStartupAsync"/> afterwards, since the startup state is async.
    /// </remarks>
    /// <param name="store">Settings storage.</param>
    /// <param name="startup">Start at sign-in control.</param>
    public SettingsViewModel(SettingsStore store, IStartupRegistration startup)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(startup);

        _store   = store;
        _startup = startup;
        _hud      = store.Load();
        _shortcut      = store.LoadShortcut();
        _editorUrlText = store.LoadEditorUrl().AbsoluteUri;
        Layers    = Enumerable.Range(0, 8).Select(i => new LayerOptionViewModel(this, i)).ToArray();
    }

    /// <summary>Raised after HUD settings are saved.</summary>
    public event EventHandler<HudSettings>? HudSettingsChanged;

    /// <summary>Raised after an expression's meaning is saved, with every meaning by slot.</summary>
    public event EventHandler<IReadOnlyDictionary<int, string>>? MeaningsChanged;

    /// <summary>Raised after the shortcut sheet hotkey is saved. <see langword="null"/> means off.</summary>
    public event EventHandler<Hotkey?>? ShortcutChanged;

    /// <summary>Raised when the picker's dialog starts (<see langword="true"/>) or stops listening for keys. The app lets go of its hotkey in between.</summary>
    public event EventHandler<bool>? ShortcutDialogOpenChanged;

    /// <summary>Gets or sets the app's check for whether Windows would let it register a combination. <see langword="null"/> means no check.</summary>
    public Func<Hotkey, bool>? IsHotkeyFree { get; set; }

    /// <summary>
    /// Tells the app the picker's dialog opened or closed.
    /// </summary>
    /// <param name="open">Whether the dialog is now listening.</param>
    public void NotifyShortcutDialog(bool open) => ShortcutDialogOpenChanged?.Invoke(this, open);

    // =========================================================================
    // SHORTCUT
    // =========================================================================

    // =========================================================================
    // EDITOR ADDRESS
    // =========================================================================

    /// <summary>
    /// Gets or sets the web config tool's address as typed.
    /// </summary>
    /// <remarks>
    /// A usable address saves at once; anything else sets <see cref="EditorUrlInvalid"/> and keeps the last good one.
    /// </remarks>
    public string EditorUrlText
    {
        get => _editorUrlText;
        set
        {
            if (!SetField(ref _editorUrlText, value ?? string.Empty))
            {
                return;
            }

            var ok = SettingsStore.TryParseEditorUrl(_editorUrlText, out var url);
            if (ok)
            {
                _store.SaveEditorUrl(url!);
            }

            EditorUrlInvalid = !ok;
        }
    }

    /// <summary>Gets a value indicating whether the typed address can't be used.</summary>
    public bool EditorUrlInvalid
    {
        get => _editorUrlInvalid;
        private set => SetField(ref _editorUrlInvalid, value);
    }

    /// <summary>Puts the address back to remapper.org's config tool.</summary>
    public void ResetEditorUrl() => EditorUrlText = SettingsStore.DefaultEditorUrl.AbsoluteUri;

    // =========================================================================
    // EXPRESSIONS
    // =========================================================================

    /// <summary>
    /// Sets the expressions the device's mappings use, from the app's last config read.
    /// </summary>
    /// <remarks>
    /// <see cref="Expressions"/> lists these, plus any slot that has a saved meaning, so a meaning stays editable after
    /// its mapping is removed.
    /// </remarks>
    public IReadOnlyList<ExpressionUse> ExpressionUses
    {
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            var meanings = _store.LoadMeanings();
            var uses     = value.ToDictionary(use => use.Slot);
            Expressions  = uses.Keys.Concat(meanings.Keys).Distinct().Order()
                .Select(slot => new ExpressionMeaningViewModel(this, slot, uses.GetValueOrDefault(slot)?.Drives, meanings.GetValueOrDefault(slot)))
                .ToList();
            OnPropertyChanged(nameof(Expressions));
            OnPropertyChanged(nameof(ShowExpressionsNote));
        }
    }

    /// <summary>Gets one card per expression, lowest slot first.</summary>
    public IReadOnlyList<ExpressionMeaningViewModel> Expressions { get; private set; } = [];

    /// <summary>Gets a value indicating whether the "connect your HID Remapper" note shows: no expressions are known yet.</summary>
    public bool ShowExpressionsNote => Expressions.Count == 0;

    internal void SaveMeaning(int slot, string? text)
    {
        _store.SaveMeaning(slot, text);
        MeaningsChanged?.Invoke(this, _store.LoadMeanings());
    }

    /// <summary>Gets the shortcut sheet hotkey, or <see langword="null"/> when off.</summary>
    public Hotkey? Shortcut => _shortcut;

    /// <summary>Gets or sets a value indicating whether Windows or another app holds the saved hotkey.</summary>
    public bool ShortcutTaken
    {
        get => _shortcutTaken;
        set => SetField(ref _shortcutTaken, value);
    }

    /// <summary>Gets or sets a note under the picker, for example that a taken hotkey was not kept.</summary>
    public string? ShortcutNote
    {
        get => _shortcutNote;
        set
        {
            if (SetField(ref _shortcutNote, value))
            {
                OnPropertyChanged(nameof(HasShortcutNote));
            }
        }
    }

    /// <summary>Gets a value indicating whether a note shows under the picker.</summary>
    public bool HasShortcutNote => _shortcutNote is not null;

    /// <summary>
    /// Saves a new hotkey and tells the app to register it.
    /// </summary>
    /// <remarks>
    /// <see langword="null"/> turns the hotkey off. The app answers through <see cref="ShortcutTaken"/>.
    /// </remarks>
    /// <param name="hotkey">The hotkey, or <see langword="null"/> for off.</param>
    public void SetShortcut(Hotkey? hotkey)
    {
        _store.SaveShortcut(hotkey);
        _shortcut = hotkey;
        OnPropertyChanged(nameof(Shortcut));
        ShortcutChanged?.Invoke(this, hotkey);
    }

    // =========================================================================
    // HUD
    // =========================================================================

    /// <summary>Gets or sets the Show HUD switch.</summary>
    public bool HudEnabled
    {
        get => _hud.HudEnabled;
        set
        {
            if (value == _hud.HudEnabled)
            {
                return;
            }

            Apply(_hud with { HudEnabled = value });
            OnPropertyChanged();
            foreach (var layer in Layers)
            {
                layer.RaiseEnabledChanged();
            }
        }
    }

    /// <summary>Gets or sets the HUD Show Duration slider, in seconds.</summary>
    /// <remarks>
    /// Snapped to tenths of a second and clamped to 0.5..5.0. A value that rounds to the stored one does nothing, so a
    /// two-way binding echo can't loop.
    /// </remarks>
    public double HudHoldSeconds
    {
        get => _hud.HudHoldMs / 1000.0;
        set
        {
            var ms = HudSettings.ClampHold((int)Math.Round(value * 10) * 100);
            if (ms == _hud.HudHoldMs)
            {
                return;
            }

            Apply(_hud with { HudHoldMs = ms });
            OnPropertyChanged();
        }
    }

    /// <summary>Gets or sets the "Open HUD on" choice, as its drop-down index.</summary>
    /// <remarks>
    /// 0 is Primary, 1 Cursor, 2 Focused window, the order of <see cref="HudMonitorMode"/> and of the drop-down's items.
    /// An unchanged or out-of-range index (the drop-down reports -1 while its items load) does nothing, so a two-way
    /// binding echo can't loop.
    /// </remarks>
    public int HudMonitorIndex
    {
        get => (int)_hud.HudMonitor;
        set
        {
            var mode = (HudMonitorMode)value;
            if (mode == _hud.HudMonitor || !Enum.IsDefined(mode))
            {
                return;
            }

            Apply(_hud with { HudMonitor = mode });
            OnPropertyChanged();
        }
    }

    /// <summary>Gets the 8 layer checkboxes, layers 0 through 7.</summary>
    public IReadOnlyList<LayerOptionViewModel> Layers { get; }

    internal bool IsLayerShown(int index) => (_hud.HudSuppressedLayers & (1 << index)) == 0;

    internal void SetLayerShown(int index, bool shown)
    {
        var mask = shown
            ? _hud.HudSuppressedLayers & ~(1 << index)
            : _hud.HudSuppressedLayers | (1 << index);

        Apply(_hud with { HudSuppressedLayers = (byte)mask });
    }

    private void Apply(HudSettings next)
    {
        _hud = next;
        _store.Save(next);
        HudSettingsChanged?.Invoke(this, next);
    }

    // =========================================================================
    // START AT SIGN-IN
    // =========================================================================

    /// <summary>Gets a value indicating whether the app starts at sign-in.</summary>
    public bool StartAtSignIn => _startupState.IsEnabled;

    /// <summary>Gets a value indicating whether the switch can change it.</summary>
    public bool StartupControllable => _startupState.IsControllable;

    /// <summary>Gets why the switch is locked, if it is.</summary>
    public string? StartupNote => _startupState.Note;

    /// <summary>Gets a value indicating whether the note shows.</summary>
    public bool HasStartupNote => _startupState.Note is not null;

    /// <summary>
    /// Reads the startup state.
    /// </summary>
    /// <remarks>
    /// Called when the window opens.
    /// </remarks>
    /// <returns>A task.</returns>
    public async Task LoadStartupAsync() => ApplyStartup(await _startup.GetStateAsync().ConfigureAwait(true));

    /// <summary>
    /// Turns start at sign-in on or off.
    /// </summary>
    /// <remarks>
    /// Does nothing if the value is unchanged, so a two-way binding echo can't loop.
    /// </remarks>
    /// <param name="enabled">The requested value.</param>
    /// <returns>A task.</returns>
    public async Task SetStartAtSignInAsync(bool enabled)
    {
        if (enabled == StartAtSignIn)
        {
            return;
        }

        ApplyStartup(await _startup.SetEnabledAsync(enabled).ConfigureAwait(true));
    }

    private void ApplyStartup(StartupState state)
    {
        _startupState = state;
        OnPropertyChanged(nameof(StartAtSignIn));
        OnPropertyChanged(nameof(StartupControllable));
        OnPropertyChanged(nameof(StartupNote));
        OnPropertyChanged(nameof(HasStartupNote));
    }

    // =========================================================================
    // ABOUT
    // =========================================================================

    /// <summary>Gets the app version, for example "2.0.0".</summary>
    public string Version { get; } =
        typeof(SettingsViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}

/// <summary>
/// One layer's "show HUD for this layer" checkbox.
/// </summary>
/// <remarks>
/// Checked means the layer is not muted. Disabled while Show HUD is off.
/// </remarks>
public sealed class LayerOptionViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;

    internal LayerOptionViewModel(SettingsViewModel owner, int index)
    {
        _owner = owner;
        Index  = index;
        Label  = $"Layer {index}";
    }

    /// <summary>Gets the layer number.</summary>
    public int Index { get; }

    /// <summary>Gets the checkbox label.</summary>
    public string Label { get; }

    /// <summary>Gets or sets whether the HUD shows for this layer.</summary>
    public bool IsShown
    {
        get => _owner.IsLayerShown(Index);
        set
        {
            if (value == IsShown)
            {
                return;
            }

            _owner.SetLayerShown(Index, value);
            OnPropertyChanged();
        }
    }

    /// <summary>Gets a value indicating whether the checkbox is enabled.</summary>
    public bool IsEnabled => _owner.HudEnabled;

    internal void RaiseEnabledChanged() => OnPropertyChanged(nameof(IsEnabled));
}

/// <summary>
/// One expression's card in Settings › Shortcuts.
/// </summary>
/// <remarks>
/// Setting <see cref="Text"/> saves at once, as every other setting does.
/// </remarks>
public sealed class ExpressionMeaningViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private string _text;

    internal ExpressionMeaningViewModel(SettingsViewModel owner, int slot, IReadOnlyList<string>? drives, string? text)
    {
        _owner = owner;
        _text  = text ?? string.Empty;
        Slot   = slot;
        Drives = drives switch
        {
            null           => "Not mapped right now",
            { Count: 0 }   => "Fed by a key",
            _              => $"Drives {string.Join(", ", drives)}",
        };
    }

    /// <summary>Gets the expression slot, 1 through 8.</summary>
    public int Slot { get; }

    /// <summary>Gets the card's title, for example "Expression 7".</summary>
    public string Title => $"Expression {Slot}";

    /// <summary>Gets what the expression drives, for the card's subtitle.</summary>
    public string Drives { get; }

    /// <summary>Gets or sets the meaning, with keys in braces. Saving a blank one removes it.</summary>
    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? string.Empty))
            {
                _owner.SaveMeaning(Slot, _text);
            }
        }
    }
}
