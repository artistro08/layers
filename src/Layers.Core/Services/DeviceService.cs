using System.Diagnostics;
using Layers.Core.Logic;

namespace Layers.Core.Services;

/// <summary>
/// Keeps a live link to the HID Remapper and reports its status and active layers.
/// </summary>
/// <remarks>
/// <para>
/// On arrival it opens both collections, checks the config version is 18, claims a slot (reusing ours, else the first
/// empty one), appends <c>layer_state 0xFF000001 monitor</c>, sends RESUME, and enables Monitor mode. Layer changes then
/// arrive as input reports.
/// </para>
/// <para>
/// Every 2 seconds it checks our slot still holds the expression. The web config tool's "load config" sends
/// CLEAR_EXPRESSIONS, which wipes it. After 2 misses in a row it reinstalls and resets the layer to 0, because RESUME
/// resets the firmware's layer state. Waiting for 2 misses keeps the append clear of the tool's CLEAR, write, PERSIST
/// burst, so our expression never lands in the user's flash.
/// </para>
/// <para>
/// Any session error shows Disconnected and retries after 2 seconds while the device is present. Unplugging cancels
/// the session. <see cref="StateChanged"/> is raised on background threads, in order, under an internal lock:
/// handlers must be quick and must marshal to the UI thread themselves.
/// </para>
/// </remarks>
public sealed class DeviceService : IAsyncDisposable
{
    // =========================================================================
    // CONSTANTS
    // =========================================================================

    /// <summary>Consecutive failed checks before reinstalling.</summary>
    public const int MissesBeforeReinstall = 2;

    /// <summary>Feature report read attempts before a session fails.</summary>
    public const int ReadAttempts = 10;

    private static readonly TimeSpan FirstRetryDelay   = TimeSpan.FromMilliseconds(2);
    private static readonly TimeSpan MonitorOffTimeout = TimeSpan.FromMilliseconds(500);

    // =========================================================================
    // STATE
    // =========================================================================

    private readonly IHidDeviceSource _source;
    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private DeviceState _state = DeviceState.Initial;
    private string? _deviceId;
    private CancellationTokenSource? _sessionCancel;
    private Task _sessionTask = Task.CompletedTask;
    private bool _disposed;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <remarks>
    /// Nothing happens until <see cref="Start"/>.
    /// </remarks>
    /// <param name="source">Finds and opens devices.</param>
    /// <param name="time">Clock for retries and checks. Tests pass a fake.</param>
    public DeviceService(IHidDeviceSource source, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(time);

        _source = source;
        _time   = time;
    }

    /// <summary>Raised when the status or layers change.</summary>
    public event EventHandler<DeviceState>? StateChanged;

    /// <summary>Gets the delay before retrying a failed session.</summary>
    public static TimeSpan ReconnectDelay { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets how often our expression is checked.</summary>
    public static TimeSpan VerifyInterval { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets the current state.</summary>
    public DeviceState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    // =========================================================================
    // LIFECYCLE
    // =========================================================================

    /// <summary>
    /// Starts watching for the device.
    /// </summary>
    /// <remarks>
    /// Call once.
    /// </remarks>
    public void Start()
    {
        _source.Arrived  += OnArrived;
        _source.Departed += OnDeparted;
        _source.Start();
    }

    /// <summary>
    /// Stops watching, turns Monitor mode off, and closes the device.
    /// </summary>
    /// <remarks>
    /// The expression stays in device RAM, where it's inert once Monitor is off. Removing it would need
    /// CLEAR_EXPRESSIONS, which would destroy the user's own expressions. Safe to call more than once.
    /// </remarks>
    /// <returns>A task that completes when the session has ended.</returns>
    public async ValueTask DisposeAsync()
    {
        Task session;
        Task cancelling;
        CancellationTokenSource? cancel;

        // Stop Accepting Work And Flag The Session Under The Lock, So No Publish Slips In After This
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed      = true;
            session        = _sessionTask;
            cancel         = _sessionCancel;
            cancelling     = cancel?.CancelAsync() ?? Task.CompletedTask;
            _sessionCancel = null;
            _deviceId      = null;
        }

        _source.Arrived  -= OnArrived;
        _source.Departed -= OnDeparted;
        _source.Stop();

        // End The Session
        await cancelling.ConfigureAwait(false);
        await session.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        cancel?.Dispose();
    }

    private void OnArrived(object? sender, string deviceId)
    {
        lock (_gate)
        {
            if (_disposed || _deviceId is not null)
            {
                return;
            }

            _deviceId      = deviceId;
            _sessionCancel = new CancellationTokenSource();
            var token      = _sessionCancel.Token;
            var previous   = _sessionTask;

            // Let A Departed Session Finish Closing First, So Dispose Awaiting The Last Task Awaits Them All
            _sessionTask = Task.Run(
                async () =>
                {
                    await WaitForPreviousAsync(previous).ConfigureAwait(false);
                    await RunDeviceAsync(deviceId, token).ConfigureAwait(false);
                },
                CancellationToken.None);
        }
    }

    private void OnDeparted(object? sender, string deviceId)
    {
        lock (_gate)
        {
            if (_deviceId != deviceId)
            {
                return;
            }

            // Cancel First So The Ending Session Can't Publish Over This
            // CancelAsync flags the token now but runs the session's cleanup off this lock and off the watcher thread
            _deviceId      = null;
            _              = _sessionCancel?.CancelAsync();
            _sessionCancel = null;
            SetStateLocked(DeviceState.Initial);
        }
    }

    // =========================================================================
    // SESSIONS
    // =========================================================================

    private async Task WaitForPreviousAsync(Task previous)
    {
        try
        {
            // Bounded, So A Close That Ignores Its Token Can't Block A Replug Forever
            await previous.WaitAsync(ReconnectDelay, _time).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Trace.TraceWarning("previous device session still closing; starting anyway");
        }
#pragma warning disable CA1031 // The previous session's outcome doesn't matter to the new one
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceInformation("previous device session ended with: {0}", ex.Message);
        }
    }

    private async Task RunDeviceAsync(string deviceId, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await RunSessionAsync(deviceId, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // Any device failure ends the session and retries; nothing may escape to the UI
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceWarning("device session failed: {0}", ex.Message);
            }

            // Back Off Then Retry
            Publish(DeviceState.Initial, token);
            try
            {
                await Task.Delay(ReconnectDelay, _time, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunSessionAsync(string deviceId, CancellationToken token)
    {
        using var pair = await _source.OpenAsync(deviceId, token).ConfigureAwait(false);

        // Version Gate
        var version = await ReadAsync<byte>(pair.Config, Protocol.GetConfig(), Protocol.TryParseConfigVersion, token).ConfigureAwait(false);
        if (version != Protocol.ConfigVersion)
        {
            Publish(new DeviceState(DeviceStatus.VersionMismatch, LayerMask.Base), token);
            await Task.Delay(Timeout.InfiniteTimeSpan, _time, token).ConfigureAwait(false);
            return;
        }

        // Install
        var (status, slot) = await InstallAsync(pair.Config, token).ConfigureAwait(false);

        // Live Layer Reports
        void OnInput(object? sender, byte[] report)
        {
            if (!Protocol.TryParseMonitorReport(report, out var layers))
            {
                return;
            }

            lock (_gate)
            {
                // Nothing To Show Until The Session Is Announced
                if (token.IsCancellationRequested || _state.Status == DeviceStatus.Disconnected)
                {
                    return;
                }

                SetStateLocked(_state with { Layers = layers });
            }
        }

        // Listen And Start The Clock Before Announcing, So Nothing Right After It Is Missed
        using var timer = new PeriodicTimer(VerifyInterval, _time);
        pair.Monitor.InputReport += OnInput;
        try
        {
            Publish(new DeviceState(status, LayerMask.Base), token);

            // Periodic Re-Verify
            var misses = 0;
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (State.Status != DeviceStatus.Connected)
                {
                    continue;
                }

                if (slot is byte current && await SlotHoldsOursAsync(pair.Config, current, token).ConfigureAwait(false))
                {
                    misses = 0;
                    continue;
                }

                if (++misses < MissesBeforeReinstall)
                {
                    continue;
                }

                // Reinstall After Consecutive Misses
                misses = 0;
                try
                {
                    (status, slot) = await InstallAsync(pair.Config, token).ConfigureAwait(false);
                    Publish(new DeviceState(status, LayerMask.Base), token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Trace.TraceWarning("reinstall failed: {0}", ex.Message);
                }
            }
        }
        finally
        {
            pair.Monitor.InputReport -= OnInput;
            await SendMonitorOffAsync(pair.Config).ConfigureAwait(false);
        }
    }

    // =========================================================================
    // DEVICE COMMANDS
    // =========================================================================

    private async Task<(DeviceStatus Status, byte? Slot)> InstallAsync(IHidChannel config, CancellationToken token)
    {
        // Read All Slots
        var slots = new List<SlotContents>(Protocol.ExpressionSlots);
        for (byte i = 0; i < Protocol.ExpressionSlots; i++)
        {
            slots.Add(await ReadAsync<SlotContents>(config, Protocol.GetExpression(i), Protocol.TryParseExpressionResponse, token).ConfigureAwait(false));
        }

        // Append Into An Empty Slot
        var choice = Protocol.ChooseSlot(slots);
        if (choice.Kind == SlotChoiceKind.Empty)
        {
            await config.SendFeatureAsync(Protocol.AppendExpression(choice.Slot), token).ConfigureAwait(false);
            await config.SendFeatureAsync(Protocol.Resume(), token).ConfigureAwait(false);
        }

        // Monitor On
        await config.SendFeatureAsync(Protocol.SetMonitorEnabled(true), token).ConfigureAwait(false);

        return choice.Kind == SlotChoiceKind.NoneFree
            ? (DeviceStatus.NoSlot, null)
            : (DeviceStatus.Connected, choice.Slot);
    }

    private async Task<bool> SlotHoldsOursAsync(IHidChannel config, byte slot, CancellationToken token)
    {
        try
        {
            var contents = await ReadAsync<SlotContents>(config, Protocol.GetExpression(slot), Protocol.TryParseExpressionResponse, token).ConfigureAwait(false);
            return contents.IsOurs;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A Failed Read Counts As A Miss
            Trace.TraceInformation("verify read failed: {0}", ex.Message);
            return false;
        }
    }

    private async Task<T> ReadAsync<T>(IHidChannel config, byte[] request, TryParse<T> parse, CancellationToken token)
    {
        await config.SendFeatureAsync(request, token).ConfigureAwait(false);

        // The Firmware Answers Asynchronously, So Retry With A Doubling Delay
        var delay = FirstRetryDelay;
        for (var attempt = 0; attempt < ReadAttempts; attempt++)
        {
            try
            {
                var reply = await config.GetFeatureAsync(Protocol.ReportIdConfig, token).ConfigureAwait(false);
                if (reply.Length >= Protocol.PacketLength && parse(reply, out var value))
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Trace.TraceInformation("feature read failed: {0}", ex.Message);
            }

            await Task.Delay(delay, _time, token).ConfigureAwait(false);
            delay *= 2;
        }

        throw new DeviceProtocolException("no valid response after 10 retries");
    }

    private async Task SendMonitorOffAsync(IHidChannel config)
    {
        using var timeout = new CancellationTokenSource(MonitorOffTimeout, _time);
        try
        {
            await config.SendFeatureAsync(Protocol.SetMonitorEnabled(false), timeout.Token).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Best effort: the device may already be gone
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Trace.TraceInformation("monitor off failed: {0}", ex.Message);
        }
    }

    // =========================================================================
    // PUBLISHING
    // =========================================================================

    private void Publish(DeviceState next, CancellationToken token)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            SetStateLocked(next);
        }
    }

    private void SetStateLocked(DeviceState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        if (StateChanged is not { } handlers)
        {
            return;
        }

        // One Bad Handler Must Not Kill The Session Or Starve The Others
        foreach (var handler in handlers.GetInvocationList().Cast<EventHandler<DeviceState>>())
        {
            try
            {
                handler(this, next);
            }
#pragma warning disable CA1031 // Subscriber bugs are logged, never allowed onto HID or watcher threads
            catch (Exception ex)
#pragma warning restore CA1031
            {
                Trace.TraceError("StateChanged handler failed: {0}", ex);
            }
        }
    }
}
