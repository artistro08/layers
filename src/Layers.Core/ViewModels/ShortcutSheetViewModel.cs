using Layers.Core.Logic;

namespace Layers.Core.ViewModels;

/// <summary>
/// Backs the shortcut sheet flyout.
/// </summary>
/// <remarks>
/// Holds the device state and the last config read. Setting either rebuilds <see cref="Sections"/> and raises change
/// notifications for every derived property, so an open flyout follows layer switches and refreshes live. The sections
/// don't depend on the active layer, so a layer switch only raises <c>ActiveLayers</c> and the view moves its highlight
/// rather than rebuilding every chip on each key press. The list shows while connected (with or without a free
/// slot) once a config has been read. Disconnected and unsupported firmware show a placeholder instead; before the
/// first read nothing shows, unless that read failed.
/// </remarks>
public sealed class ShortcutSheetViewModel : ObservableObject
{
    private DeviceState _state = DeviceState.Initial;
    private DeviceConfig? _config;
    private bool _refreshFailed;
    private IReadOnlyList<LayerSection> _sections = [];
    private IReadOnlyDictionary<int, string> _meanings = new Dictionary<int, string>();

    /// <summary>Gets or sets the device state.</summary>
    public DeviceState State
    {
        get => _state;
        set
        {
            var previous = _state;
            if (!SetField(ref _state, value))
            {
                return;
            }

            // Only The Layers Moved: The Same Rows, A New Highlight
            if (previous.Status == value.Status)
            {
                OnPropertyChanged(nameof(ActiveLayers));
                return;
            }

            Rebuild();
            OnPropertyChanged(nameof(MarksActive));
            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(Tone));
        }
    }

    /// <summary>Gets or sets the last config read from the device, or <see langword="null"/> before the first.</summary>
    public DeviceConfig? Config
    {
        get => _config;
        set
        {
            // Every Open Re-Reads The Device; An Identical Read Keeps The Built Sheet, So Nothing Flashes
            if (Same(_config, value) || !SetField(ref _config, value))
            {
                return;
            }

            Rebuild();
        }
    }

    /// <summary>Gets or sets what the user says each expression means, by slot. The sheet draws these in place of "Expression N".</summary>
    public IReadOnlyDictionary<int, string> Meanings
    {
        get => _meanings;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            _meanings = value;
            OnPropertyChanged();
            Rebuild();
        }
    }

    /// <summary>Gets or sets a value indicating whether the last requested refresh produced nothing.</summary>
    public bool RefreshFailed
    {
        get => _refreshFailed;
        set
        {
            if (!SetField(ref _refreshFailed, value))
            {
                return;
            }

            OnPropertyChanged(nameof(ShowRefreshNote));
            OnPropertyChanged(nameof(EmptyMessage));
            OnPropertyChanged(nameof(HasEmptyMessage));
        }
    }

    /// <summary>Gets the sections to show. Empty unless the list shows.</summary>
    public IReadOnlyList<LayerSection> Sections => _sections;

    /// <summary>Gets the active layers. Raised on its own when only the layers moved.</summary>
    public LayerMask ActiveLayers => _state.Layers;

    /// <summary>Gets a value indicating whether the active layers are known, so the sheet highlights them. Off without a free slot.</summary>
    public bool MarksActive => _state.Status == DeviceStatus.Connected;

    /// <summary>Gets the footer status text.</summary>
    public string StatusLabel => StatusText.Label(_state.Status);

    /// <summary>Gets the footer status dot color.</summary>
    public StatusTone Tone => _state.Status switch
    {
        DeviceStatus.Connected    => StatusTone.Success,
        DeviceStatus.Disconnected => StatusTone.Critical,
        _                         => StatusTone.Caution,
    };

    /// <summary>Gets a value indicating whether the list area shows rather than a placeholder or nothing.</summary>
    public bool ShowList => IsReadable && _config is not null;

    /// <summary>Gets a value indicating whether the header's "Couldn't refresh" note shows: a failed refresh while a list exists.</summary>
    public bool ShowRefreshNote => _refreshFailed && ShowList;

    /// <summary>Gets the placeholder text, or <see langword="null"/> when the list (or nothing) shows.</summary>
    public string? EmptyMessage => _state.Status switch
    {
        DeviceStatus.Disconnected    => "Plug in your HID Remapper",
        DeviceStatus.VersionMismatch => StatusText.Detail(DeviceStatus.VersionMismatch),
        _ when _config is null && _refreshFailed => "Couldn't read shortcuts",
        _ when _config is not null && _sections.Count == 0 => "No shortcuts set up",
        _ => null,
    };

    /// <summary>Gets a value indicating whether the placeholder shows. A bool, because a one-way x:Bind through a function skips null arguments.</summary>
    public bool HasEmptyMessage => EmptyMessage is not null;

    // Configs Compare By Content: Their Lists Are Fresh Objects On Every Read
    private static bool Same(DeviceConfig? a, DeviceConfig? b) =>
        a is not null && b is not null
        && a.DescriptorNumber == b.DescriptorNumber
        && a.Mappings.SequenceEqual(b.Mappings)
        && a.Macros.Count == b.Macros.Count
        && a.Macros.Zip(b.Macros).All(pair => pair.First.Count == pair.Second.Count
            && pair.First.Zip(pair.Second).All(step => step.First.SequenceEqual(step.Second)));

    // Connected Or NoSlot: The Mappings Are Readable
    private bool IsReadable => _state.Status is DeviceStatus.Connected or DeviceStatus.NoSlot;

    private IReadOnlyList<LayerSection> Build() => IsReadable && _config is not null ? ShortcutSheet.Build(_config) : [];

    private void Rebuild()
    {
        _sections = Build();

        OnPropertyChanged(nameof(Sections));
        OnPropertyChanged(nameof(ActiveLayers));
        OnPropertyChanged(nameof(ShowList));
        OnPropertyChanged(nameof(ShowRefreshNote));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(HasEmptyMessage));
    }
}
