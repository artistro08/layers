using Layers.Core.Logic;
using Layers.Core.Services;
using Layers.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Layers.Tests;

[TestClass]
public sealed class DeviceServiceTests : IAsyncDisposable
{
    private FakeHidDeviceSource _source = null!;
    private FakeTimeProvider _time = null!;
    private DeviceService _service = null!;
    private List<DeviceState> _events = null!;

    [TestInitialize]
    public void Setup()
    {
        _source  = new FakeHidDeviceSource();
        _time    = new FakeTimeProvider();
        _service = new DeviceService(_source, _time);
        _events  = [];
        _service.StateChanged += (_, state) => { lock (_events) { _events.Add(state); } };
        _service.Start();
    }

    // MSTest Disposes Each Test Instance After The Test
    public ValueTask DisposeAsync() => _service.DisposeAsync();

    private FakeConfigChannel Config => _source.Config;

    private async Task ConnectAsync(DeviceStatus expected = DeviceStatus.Connected)
    {
        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == expected);
    }

    // =========================================================================
    // CONNECT
    // =========================================================================

    [TestMethod]
    public void Start_StartsSourceAndBeginsDisconnected()
    {
        Assert.IsTrue(_source.Started);
        Assert.AreEqual(DeviceState.Initial, _service.State);
    }

    [TestMethod]
    public async Task Connect_SendsSequenceInOrder()
    {
        await ConnectAsync();

        byte[] expected = [3, 21, 21, 21, 21, 21, 21, 21, 21, 20, 11, 22];
        CollectionAssert.AreEqual(expected, Config.Commands.ToArray());
        Assert.AreEqual((byte)0, Config.Sent[9][3], "appends into the first empty slot");
        Assert.IsTrue(Config.MonitorEnabled);
        Assert.AreEqual(new DeviceState(DeviceStatus.Connected, LayerMask.Base), _service.State);
    }

    [TestMethod]
    public async Task Connect_WrongVersionWritesNothing()
    {
        Config.Version = 19;

        await ConnectAsync(DeviceStatus.VersionMismatch);
        await Eventually.Settle();

        CollectionAssert.AreEqual(new byte[] { 3 }, Config.Commands.ToArray());
    }

    [TestMethod]
    public async Task Connect_AllSlotsFullIsNoSlotWithoutAppend()
    {
        Config.FillAllSlots();

        await ConnectAsync(DeviceStatus.NoSlot);

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Config.Commands[^1]);
    }

    [TestMethod]
    public async Task Connect_ReusesExistingExpression()
    {
        Config.Slots[3] = (3, Protocol.ExpressionBytes.ToArray());

        await ConnectAsync();

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
    }

    // =========================================================================
    // LIVE REPORTS
    // =========================================================================

    [TestMethod]
    public async Task LiveReports_ChangePosted()
    {
        await ConnectAsync();

        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));

        Assert.AreEqual(new LayerMask(0b100), _service.State.Layers);
    }

    [TestMethod]
    public async Task LiveReports_ReportRightAfterConnectKept()
    {
        // Report Arrives The Instant Connected Is Announced
        _service.StateChanged += (_, state) =>
        {
            if (state == new DeviceState(DeviceStatus.Connected, LayerMask.Base))
            {
                _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
            }
        };

        await ConnectAsync();

        await Eventually.Until(() => _service.State.Layers == new LayerMask(0b100));
    }

    [TestMethod]
    public async Task LiveReports_SameMaskIgnored()
    {
        await ConnectAsync();
        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
        var count = _events.Count;

        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));

        Assert.AreEqual(count, _events.Count);
    }

    [TestMethod]
    public async Task LiveReports_MalformedIgnored()
    {
        await ConnectAsync();
        var count = _events.Count;

        _source.Monitor.Raise([Protocol.ReportIdMonitor, 0]);
        _source.Monitor.Raise([]);
        _source.Monitor.Raise(Replies.Monitor((0x0009_0001u, 1)));

        Assert.AreEqual(count, _events.Count);
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    // =========================================================================
    // RE-VERIFY
    // =========================================================================

    [TestMethod]
    public async Task Reverify_PresentDoesNothing()
    {
        await ConnectAsync();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);

        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression));
    }

    [TestMethod]
    public async Task Reverify_TickRightAfterConnectKept()
    {
        // Time Moves The Instant Connected Is Announced
        var advanced = false;
        _service.StateChanged += (_, state) =>
        {
            if (state.Status == DeviceStatus.Connected && !advanced)
            {
                advanced = true;
                _time.Advance(DeviceService.VerifyInterval);
            }
        };

        await ConnectAsync();

        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
    }

    [TestMethod]
    public async Task Reverify_OneMissDoesNothing()
    {
        await ConnectAsync();
        Config.WipeSlots();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        await Eventually.Settle();

        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression));
    }

    [TestMethod]
    public async Task Reverify_TwoMissesReinstallAndResetLayer()
    {
        await ConnectAsync();
        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
        Config.WipeSlots();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
        await Eventually.Until(() => _service.State.Layers == LayerMask.Base);

        Assert.AreEqual(2, Config.Count(Protocol.CmdResume));
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    [TestMethod]
    public async Task Reverify_FailedReinstallIsNotFatal()
    {
        await ConnectAsync();
        Config.WipeSlots();
        Config.SendFailure = new IOException("device busy");

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Settle();
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Settle();

        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);

        Config.SendFailure = null;
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) > 8);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
    }

    [TestMethod]
    public async Task Reverify_SkippedWhenNoSlot()
    {
        Config.FillAllSlots();
        await ConnectAsync(DeviceStatus.NoSlot);

        _time.Advance(DeviceService.VerifyInterval * 3);
        await Eventually.Settle();

        Assert.AreEqual(8, Config.Count(Protocol.CmdGetExpression));
    }

    // =========================================================================
    // RETRIES AND ERRORS
    // =========================================================================

    [TestMethod]
    public async Task Read_RetriesShortReplies()
    {
        Config.ShortReplies = 9;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 50);

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Read_RejectsBadCrcAndRetries()
    {
        Config.CorruptNextReply = true;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 5);

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Read_GivesUpAfterTenAttemptsThenReconnects()
    {
        Config.ShortReplies = DeviceService.ReadAttempts;

        _source.Arrive();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 100);

        Assert.AreEqual(2, _source.Opens);
    }

    [TestMethod]
    public async Task SessionError_RetriesAfterDelay()
    {
        Config.SendFailure = new IOException("gone");

        _source.Arrive();
        await Eventually.Until(() => Config.SendAttempts >= 1 && Config.Disposed);
        Assert.AreEqual(DeviceStatus.Disconnected, _service.State.Status);

        Config.SendFailure = null;
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Connected, _time, stepMs: 100);
        Assert.IsTrue(_source.Opens >= 2);
    }

    // =========================================================================
    // PLUG, UNPLUG, SHUTDOWN
    // =========================================================================

    [TestMethod]
    public async Task Departed_ShowsDisconnectedAndArrivedReconnects()
    {
        await ConnectAsync();

        _source.Depart();
        Assert.AreEqual(DeviceState.Initial, _service.State);
        await Eventually.Until(() => Config.Disposed && _source.Monitor.Disposed);

        await ConnectAsync();
        Assert.AreEqual(2, _source.Opens);
    }

    [TestMethod]
    public async Task Departed_SessionCleanupRunsOffTheWatcherThread()
    {
        await ConnectAsync();
        using var inside_depart = new ThreadLocal<bool>();
        var inline_send         = false;
        Config.OnSend           = () => inline_send |= inside_depart.Value;

        inside_depart.Value = true;
        _source.Depart();
        inside_depart.Value = false;
        await Eventually.Until(() => Config.Disposed);

        // Monitor Off Must Not Run Inline Under The Service Lock
        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Config.Commands[^1]);
        Assert.IsFalse(inline_send);
    }

    [TestMethod]
    public async Task ThrowingHandler_DoesNotStopTheService()
    {
        var later_calls = 0;
        _service.StateChanged += (_, _) => throw new InvalidOperationException("bad handler");
        _service.StateChanged += (_, _) => Interlocked.Increment(ref later_calls);

        await ConnectAsync();
        await Eventually.Settle();
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);

        _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 0b100)));
        Assert.AreEqual(new LayerMask(0b100), _service.State.Layers);

        _source.Depart();
        await Eventually.Until(() => Config.Disposed);
        await ConnectAsync();
        Assert.AreEqual(2, _source.Opens);
        Assert.IsTrue(Volatile.Read(ref later_calls) >= 4, "handlers after the throwing one still run");
    }

    [TestMethod]
    public async Task Departed_OtherDeviceIgnored()
    {
        await ConnectAsync();

        _source.Depart("someone-else");

        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    [TestMethod]
    public async Task SecondArrival_WhileConnectedIgnored()
    {
        await ConnectAsync();

        _source.Arrive("dev2");
        await Eventually.Settle();

        Assert.AreEqual(1, _source.Opens);
    }

    [TestMethod]
    public async Task Dispose_TurnsMonitorOffAndClosesChannels()
    {
        await ConnectAsync();

        await _service.DisposeAsync();

        Assert.AreEqual(Protocol.CmdSetMonitorEnabled, Config.Sent[^1][2]);
        Assert.AreEqual((byte)0, Config.Sent[^1][3]);
        Assert.IsTrue(Config.Disposed);
        Assert.IsTrue(_source.Monitor.Disposed);
        Assert.IsTrue(_source.Stopped);
    }

    [TestMethod]
    public async Task Dispose_IsIdempotent()
    {
        await _service.DisposeAsync();
        await _service.DisposeAsync();
    }

    [TestMethod]
    public async Task NeverSendsPersistClearOrSuspend()
    {
        await ConnectAsync();
        Config.WipeSlots();
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 9);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
        await _service.DisposeAsync();

        byte[] allowed = [Protocol.CmdGetConfig, Protocol.CmdResume, Protocol.CmdAppendToExpression, Protocol.CmdGetExpression, Protocol.CmdSetMonitorEnabled];
        Assert.IsTrue(Config.Commands.All(allowed.Contains));
    }
}
