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
        _hud     = store.Load();
        Layers   = Enumerable.Range(0, 8).Select(i => new LayerOptionViewModel(this, i)).ToArray();
    }

    /// <summary>Raised after HUD settings are saved.</summary>
    public event EventHandler<HudSettings>? HudSettingsChanged;

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
