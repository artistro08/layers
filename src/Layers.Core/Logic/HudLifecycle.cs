namespace Layers.Core.Logic;

/// <summary>
/// Tracks whether the HUD flyout is open, closing, or closed.
/// </summary>
/// <remarks>
/// A stock flyout closes asynchronously: <c>IsOpen</c> turns false as soon as it starts closing (on <c>Hide()</c>, or
/// when it closes on its own), but <c>Closed</c> only fires later, once its content unloads. A layer change landing in
/// that gap must still end with a visible HUD, so a show while closing is remembered and the flyout reopens once
/// <c>Closed</c> fires. Opening again before then would let that late <c>Closed</c> count against the new open.
/// A flyout that never opened (a failed <c>ShowAt</c>) never raises <c>Closed</c>, so it's reset instead; that's why
/// the lifecycle also tracks <c>Opened</c>.
/// </remarks>
public sealed class HudLifecycle
{
    private bool _open;
    private bool _shown;
    private bool _closing;
    private bool _reopen;

    /// <summary>
    /// Records a show request.
    /// </summary>
    /// <remarks>
    /// Returns true only when the flyout is fully closed. While open, the caller just updates the label. While
    /// closing, the reopen is deferred to <see cref="Closed"/>.
    /// </remarks>
    /// <returns>True if the caller must open the flyout now.</returns>
    public bool Show()
    {
        // Deferred Until Closed
        if (_closing)
        {
            _reopen = true;
            return false;
        }

        // Already Up
        if (_open)
        {
            return false;
        }

        _open = true;
        return true;
    }

    /// <summary>
    /// Records that the flyout's <c>Opened</c> fired.
    /// </summary>
    /// <remarks>
    /// From here on, it will raise <c>Closed</c> when it goes away.
    /// </remarks>
    public void Opened() => _shown = true;

    /// <summary>
    /// Records a hide request.
    /// </summary>
    /// <remarks>
    /// Returns false when the flyout isn't open or is already closing, so no second close is needed.
    /// </remarks>
    /// <returns>True if the caller must hide the flyout now.</returns>
    public bool Hide()
    {
        if (!_open || _closing)
        {
            return false;
        }

        _closing = true;
        return true;
    }

    /// <summary>
    /// Reconciles with the flyout's actual <c>IsOpen</c>.
    /// </summary>
    /// <remarks>
    /// Only matters while the lifecycle thinks it's open and not closing, but the flyout isn't open. If it had opened,
    /// it closed on its own and <c>Closed</c> is still coming, so this counts as closing (a show then reopens on
    /// <see cref="Closed"/>). If it never opened, the open failed silently and no <c>Closed</c> will come, so this
    /// resets to closed. Without it, the lifecycle would stay open forever and later shows would only update a label
    /// nobody sees.
    /// </remarks>
    /// <param name="isOpen">The flyout's actual <c>IsOpen</c>.</param>
    /// <returns>True if the open was lost and the lifecycle reset to closed, so the caller can hide the host.</returns>
    public bool Lost(bool isOpen)
    {
        if (!_open || _closing || isOpen)
        {
            return false;
        }

        // Closed On Its Own, Closed Still Coming
        if (_shown)
        {
            _closing = true;
            return false;
        }

        // Never Opened, Nothing Coming
        _open   = false;
        _reopen = false;
        return true;
    }

    /// <summary>
    /// Records that the flyout closed.
    /// </summary>
    /// <remarks>
    /// Any close, requested or not, lands here. If a show arrived while closing, the flyout counts as open again.
    /// </remarks>
    /// <returns>True if the caller must reopen the flyout; false if it can hide the host.</returns>
    public bool Closed()
    {
        _closing = false;
        _shown   = false;
        _open    = _reopen;
        _reopen  = false;
        return _open;
    }
}
