using Layers.Core.Logic;

namespace Layers.Core.ViewModels;

/// <summary>
/// Color of the status dot.
/// </summary>
/// <remarks>
/// The UI maps these to <c>SystemFillColorSuccessBrush</c>, <c>SystemFillColorCautionBrush</c>, and
/// <c>SystemFillColorCriticalBrush</c>.
/// </remarks>
public enum StatusTone
{
    /// <summary>Connected.</summary>
    Success,

    /// <summary>NoSlot or VersionMismatch.</summary>
    Caution,

    /// <summary>Disconnected.</summary>
    Critical,
}

/// <summary>
/// Backs the tray menu's status and layer rows.
/// </summary>
/// <remarks>
/// Setting <see cref="State"/> raises change notifications for every derived property, so an open menu updates live.
/// </remarks>
public sealed class TrayMenuViewModel : ObservableObject
{
    private DeviceState _state = DeviceState.Initial;

    /// <summary>Gets or sets the device state.</summary>
    public DeviceState State
    {
        get => _state;
        set
        {
            if (!SetField(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StatusLabel));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(HasStatusDetail));
            OnPropertyChanged(nameof(LayerLabel));
            OnPropertyChanged(nameof(Tone));
        }
    }

    /// <summary>Gets the status row text.</summary>
    public string StatusLabel => StatusText.Label(_state.Status);

    /// <summary>Gets the degraded-state explanation, if any.</summary>
    public string? StatusDetail => StatusText.Detail(_state.Status);

    /// <summary>Gets a value indicating whether the detail row shows.</summary>
    public bool HasStatusDetail => StatusDetail is not null;

    /// <summary>Gets the layer row text.</summary>
    public string LayerLabel => _state.Layers.Label;

    /// <summary>Gets the status dot color.</summary>
    public StatusTone Tone => _state.Status switch
    {
        DeviceStatus.Connected    => StatusTone.Success,
        DeviceStatus.Disconnected => StatusTone.Critical,
        _                         => StatusTone.Caution,
    };
}
