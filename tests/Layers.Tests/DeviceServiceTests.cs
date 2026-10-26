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
    private List<DeviceConfig> _configs = null!;

    [TestInitialize]
    public void Setup()
    {
        _source  = new FakeHidDeviceSource();
        _time    = new FakeTimeProvider();
        _service = new DeviceService(_source, _time);
        _events  = [];
        _configs = [];
        _service.StateChanged  += (_, state) => { lock (_events) { _events.Add(state); } };
        _service.ConfigChanged += (_, config) => { lock (_configs) { _configs.Add(config); } };
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
        await Eventually.Until(() => _configs.Count == 1);

        // Eight Slot Reads, A Confirming Re-Read Of The Chosen Slot, Then Append, Resume, Monitor On,
        // Then The Config Read: Opening GET_CONFIG, No Mappings, Confirming GET_CONFIG
        byte[] expected = [3, 21, 21, 21, 21, 21, 21, 21, 21, 21, 20, 11, 22, 3, 3];
        CollectionAssert.AreEqual(expected, Config.Commands.ToArray());
        Assert.AreEqual((byte)0, Config.Sent[9][3], "re-reads the chosen slot");
        Assert.AreEqual((byte)0, Config.Sent[10][3], "appends into the first empty slot");
        Assert.IsTrue(Config.MonitorEnabled);
        Assert.AreEqual(new DeviceState(DeviceStatus.Connected, LayerMask.Base), _service.State);
    }

    [TestMethod]
    public async Task Connect_WrongVersionWritesNothing()
    {
        Config.Version = 19;

        await ConnectAsync(DeviceStatus.VersionMismatch);
        await Eventually.Settle();

        // GET_CONFIG, Then The Confirming Re-Read, Then Nothing
        CollectionAssert.AreEqual(new byte[] { 3, 3 }, Config.Commands.ToArray());
    }

    [TestMethod]
    public async Task Connect_AllSlotsFullIsNoSlotWithoutAppend()
    {
        Config.FillAllSlots();

        await ConnectAsync(DeviceStatus.NoSlot);

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
        Assert.AreEqual(1, Config.Count(Protocol.CmdSetMonitorEnabled));
    }

    [TestMethod]
    public async Task Connect_ReusesExistingExpression()
    {
        Config.Slots[3] = (3, Protocol.ExpressionBytes.ToArray());

        await ConnectAsync();

        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
        Assert.AreEqual(0, Config.Count(Protocol.CmdResume));
    }

    [TestMethod]
    public async Task Connect_ForeignVersionReplyIsReRead()
    {
        // The Web Config Tool's Reply Lands In Our GET_CONFIG Read
        Config.ForeignReply = (Protocol.CmdGetConfig, Replies.Config(19));

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        // Two Version Reads, Then The Config Read's Opening And Closing Reads
        Assert.AreEqual(4, Config.Count(Protocol.CmdGetConfig));
    }

    [TestMethod]
    public async Task Connect_ForeignEmptySlotReplyNeverAppends()
    {
        // A Foreign Reply Makes Full Slot 0 Look Empty On The First Read
        Config.FillAllSlots();
        Config.ForeignReply = (Protocol.CmdGetExpression, Replies.Expression(0, []));
        _source.Arrive();

        // The Confirming Re-Read Catches It And The Session Fails Without Writing
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) >= 9);
        await Eventually.Settle();
        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));

        // The Retry Reads Honestly
        _time.Advance(DeviceService.ReconnectDelay);
        await ConnectAsync(DeviceStatus.NoSlot);
        Assert.AreEqual(0, Config.Count(Protocol.CmdAppendToExpression));
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
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 11);

        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression));
    }

    [TestMethod]
    public async Task Reverify_TurnsMonitorBackOnAfterASilentTick()
    {
        await ConnectAsync();

        // The Web Config Tool Turns Monitor Off When It Connects; The First Tick Still Trusts The Install
        Config.MonitorEnabled = false;
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
        await Eventually.Settle();
        Assert.IsFalse(Config.MonitorEnabled);

        // A Second Silent Tick Turns It Back On: Slot Read, Then Monitor On, Nothing Else
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.MonitorEnabled);

        CollectionAssert.AreEqual(new byte[] { Protocol.CmdGetExpression, Protocol.CmdSetMonitorEnabled }, Config.Commands.TakeLast(2).ToArray());
        Assert.AreEqual(1, Config.Count(Protocol.CmdAppendToExpression), "re-enabling monitor is not a reinstall");
    }

    [TestMethod]
    public async Task Reverify_LeavesMonitorAloneWhileReportsFlow()
    {
        await ConnectAsync();
        Config.MonitorEnabled = false;

        // Reports Arriving Prove Monitor Is On, So No Tick Writes To It
        for (var tick = 0; tick < 3; tick++)
        {
            _source.Monitor.Raise(Replies.Monitor((Protocol.SentinelUsage, 1)));
            _time.Advance(DeviceService.VerifyInterval);
            await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10 + tick);
        }

        await Eventually.Settle();
        Assert.IsFalse(Config.MonitorEnabled);
        Assert.AreEqual(1, Config.Count(Protocol.CmdSetMonitorEnabled), "only the install turned monitor on");
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

        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
    }

    [TestMethod]
    public async Task Reverify_OneMissDoesNothing()
    {
        await ConnectAsync();
        Config.WipeSlots();

        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
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
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
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
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) > 9);
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
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) == 10);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdAppendToExpression) == 2);
        await _service.DisposeAsync();

        byte[] allowed = [Protocol.CmdGetConfig, Protocol.CmdResume, Protocol.CmdAppendToExpression, Protocol.CmdGetExpression, Protocol.CmdSetMonitorEnabled, Protocol.CmdGetMapping, Protocol.CmdGetMacro];
        Assert.IsTrue(Config.Commands.All(allowed.Contains));
    }

    // =========================================================================
    // CONFIG READ
    // =========================================================================

    [TestMethod]
    public async Task Connect_ReadsMappingsAfterInstall()
    {
        Config.Mappings.Add((0x00070004, 0xFFF20001, 1, 0));
        Config.Mappings.Add((0x00070005, 0x000C00E9, 2, 0b10));
        Config.Macros[0]        = [0x000700E0, 0x00070006, 0, 0x00070019];
        Config.DescriptorNumber = 2;

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        // Install Commands First, Then GET_CONFIG, Two GET_MAPPING, One GET_MACRO, Then The Confirming GET_CONFIG
        var tail = Config.Commands.Skip(13).ToArray();
        CollectionAssert.AreEqual(new byte[] { 3, 6, 6, 17, 3 }, tail);
        var config = _configs[0];
        Assert.AreEqual((byte)2, config.DescriptorNumber);
        Assert.HasCount(2, config.Mappings);
        Assert.IsTrue(config.Mappings[1].Tap);
        Assert.HasCount(2, config.Macros[0]);
        Assert.IsEmpty(config.Macros[1]);
        Assert.AreSame(config, _service.Config);
    }

    [TestMethod]
    public async Task Connect_LongMacroReadsAllChunks()
    {
        Config.Mappings.Add((1, 0xFFF20002, 1, 0));
        Config.Macros[1] = Enumerable.Range(1, 13).Select(i => (uint)i).ToArray();   // 13 Usages, No Separators: 6 + 6 + 1

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        Assert.AreEqual(3, Config.Count(Protocol.CmdGetMacro));
        Assert.HasCount(13, _configs[0].Macros[1][0]);
    }

    [TestMethod]
    public async Task Connect_LongMacroStopsAtTheCap()
    {
        // A Run Of Full Chunks (A Stray Reply, Or Garbage) Must Not Loop Forever
        Config.Mappings.Add((1, 0xFFF20002, 1, 0));
        Config.Macros[1] = Enumerable.Range(1, Protocol.MaxMacroUsages + 60).Select(i => (uint)i).ToArray();

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        var chunks = (Protocol.MaxMacroUsages + Protocol.MacroItemsPerPacket - 1) / Protocol.MacroItemsPerPacket;
        Assert.AreEqual(chunks, Config.Count(Protocol.CmdGetMacro));
    }

    [TestMethod]
    public async Task Connect_ReadsEachMacroSlotOnce()
    {
        Config.Mappings.Add((1, 0xFFF20002, 1, 0));
        Config.Mappings.Add((2, 0xFFF20002, 2, 0));
        Config.Macros[1] = [5];

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        Assert.AreEqual(1, Config.Count(Protocol.CmdGetMacro));
    }

    [TestMethod]
    public async Task Connect_NoSlotStillReadsMappings()
    {
        Config.FillAllSlots();
        Config.Mappings.Add((1, 2, 1, 0));

        await ConnectAsync(DeviceStatus.NoSlot);
        await Eventually.Until(() => _configs.Count == 1);
    }

    [TestMethod]
    public async Task Connect_WrongVersionReadsNoMappings()
    {
        Config.Version = 19;
        Config.Mappings.Add((1, 2, 1, 0));

        await ConnectAsync(DeviceStatus.VersionMismatch);
        await Eventually.Settle();

        Assert.AreEqual(0, Config.Count(Protocol.CmdGetMapping));
        Assert.IsEmpty(_configs);
        Assert.IsNull(_service.Config);
    }

    [TestMethod]
    public async Task Refresh_RunsOnNextTickAndCoalesces()
    {
        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);
        var before = Config.Count(Protocol.CmdGetConfig);

        _service.RequestConfigRefresh();
        _service.RequestConfigRefresh();
        _service.RequestConfigRefresh();
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => _configs.Count == 2);
        await Eventually.Settle();

        Assert.AreEqual(before + 2, Config.Count(Protocol.CmdGetConfig), "one read: opening GET_CONFIG and confirming GET_CONFIG");
    }

    [TestMethod]
    public async Task Refresh_DiscardsWhenCountMovesMidRead()
    {
        Config.Mappings.Add((1, 2, 1, 0));
        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        // The Web Tool Saves While We Read: One GET_CONFIG Sees A Different Count Than The Other
        Config.ForeignReply = (Protocol.CmdGetConfig, Replies.Config(18, mappingCount: 5));
        _service.RequestConfigRefresh();
        _time.Advance(DeviceService.VerifyInterval);

        // The Foreign Count Of 5 Drives Five GET_MAPPING Reads, Then The Closing GET_CONFIG Disagrees
        await Eventually.Until(() => Config.Count(Protocol.CmdGetConfig) >= 5);
        await Eventually.Settle();

        Assert.AreEqual(6, Config.Count(Protocol.CmdGetMapping), "one from the first read, five from the poisoned one");
        Assert.HasCount(1, _configs);
    }

    [TestMethod]
    public async Task Refresh_BadReadRaisesNothing()
    {
        Config.Mappings.Add((1, 2, 1, 0));
        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        Config.ShortReplies = DeviceService.ReadAttempts;   // The Refresh's First Read Exhausts Its Retries
        _service.RequestConfigRefresh();
        _time.Advance(DeviceService.VerifyInterval);

        // Walk The Doubling Retry Delays To Exhaustion
        for (var i = 0; i < DeviceService.ReadAttempts + 2; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(5));
            await Eventually.Settle();
        }

        Assert.HasCount(1, _configs);
        Assert.AreEqual(DeviceStatus.Connected, _service.State.Status);
    }

    [TestMethod]
    public async Task Refresh_LargeTableDoesNotBlockVerify()
    {
        for (var i = 0; i < 300; i++)
        {
            Config.Mappings.Add(((uint)i, 2, 1, 0));
        }

        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);
        Assert.HasCount(300, _configs[0].Mappings);

        var verifies = Config.Count(Protocol.CmdGetExpression);
        _time.Advance(DeviceService.VerifyInterval);
        await Eventually.Until(() => Config.Count(Protocol.CmdGetExpression) > verifies);
    }

    [TestMethod]
    public async Task Disconnect_KeepsLastConfig()
    {
        Config.Mappings.Add((1, 2, 1, 0));
        await ConnectAsync();
        await Eventually.Until(() => _configs.Count == 1);

        _source.Depart();
        await Eventually.Until(() => _service.State.Status == DeviceStatus.Disconnected);

        Assert.IsNotNull(_service.Config);
    }
}
