using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Layers.Core.ViewModels;

/// <summary>
/// Minimal <see cref="INotifyPropertyChanged"/> base for the view models.
/// </summary>
/// <remarks>
/// Stock BCL only. No MVVM library.
/// </remarks>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc/>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raises <see cref="PropertyChanged"/>.</summary>
    /// <remarks>Defaults to the calling member's name.</remarks>
    /// <param name="name">The property name.</param>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Sets a field and raises <see cref="PropertyChanged"/> if it changed.</summary>
    /// <remarks>Uses <see cref="EqualityComparer{T}.Default"/>.</remarks>
    /// <typeparam name="T">Field type.</typeparam>
    /// <param name="field">The backing field.</param>
    /// <param name="value">The new value.</param>
    /// <param name="name">The property name.</param>
    /// <returns><see langword="true"/> if the value changed.</returns>
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
