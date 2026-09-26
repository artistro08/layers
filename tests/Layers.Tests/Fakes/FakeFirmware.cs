using Layers.Core.Logic;
using Layers.Core.Services;

namespace Layers.Tests.Fakes;

/// Simulates the config collection of HID Remapper firmware: 8 slots, GET_CONFIG, GET/APPEND expression, RESUME, monitor flag.
internal sealed class FakeConfigChannel : IHidChannel
{
    private readonly Lock _gate = new();
    private readonly List<byte[]> _sent = [];
    private byte[]? _pending;

    public byte Version { get; set; } = Protocol.ConfigVersion;
    public (byte Count, byte[] Bytes)[] Slots { get; } = Enumerable.Range(0, 8).Select(_ => ((byte)0, Array.Empty<byte>())).ToArray();
    public int ShortReplies { get; set; }
    public bool CorruptNextReply { get; set; }
    public Exception? SendFailure { get; set; }
    public int SendAttempts { get; private set; }
    public Action? OnSend { get; set; }
    public bool MonitorEnabled { get; private set; }
    public bool Disposed { get; set; }

    public event EventHandler<byte[]>? InputReport { add { } remove { } }

    public IReadOnlyList<byte[]> Sent { get { lock (_gate) { return _sent.ToList(); } } }
    public IReadOnlyList<byte> Commands => Sent.Select(packet => packet[2]).ToList();
    public int Count(byte command) => Commands.Count(c => c == command);

    public void FillAllSlots()
    {
        for (var i = 0; i < 8; i++)
        {
            Slots[i] = (2, [20, 44]);
        }
    }

    public void WipeSlots()
    {
        for (var i = 0; i < 8; i++)
        {
            Slots[i] = (0, []);
        }
    }

    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            SendAttempts++;
            OnSend?.Invoke();
            if (SendFailure is not null)
            {
                throw SendFailure;
            }

            _sent.Add(report.ToArray());
            switch (report[2])
            {
                case Protocol.CmdGetConfig:
                    _pending = Replies.Config(Version);
                    break;
                case Protocol.CmdGetExpression:
                    var slot = Slots[report[3]];
                    _pending = Replies.Expression(slot.Count, slot.Bytes);
                    break;
                case Protocol.CmdAppendToExpression:
                    Slots[report[3]] = (report[4], report[5..12]);
                    _pending = null;
                    break;
                case Protocol.CmdSetMonitorEnabled:
                    MonitorEnabled = report[3] == 1;
                    _pending = null;
                    break;
                default:
                    _pending = null;
                    break;
            }
        }

        return Task.CompletedTask;
    }

    public Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (ShortReplies > 0)
            {
                ShortReplies--;
                return Task.FromResult(new byte[] { Protocol.ReportIdConfig, 0, 0 });
            }

            // No Pending Reply Is Zeros With A Bad CRC
            var reply = (_pending ?? new byte[Protocol.PacketLength]).ToArray();
            if (CorruptNextReply)
            {
                CorruptNextReply = false;
                reply[5] ^= 0xFF;
            }

            return Task.FromResult(reply);
        }
    }

    public void Dispose() => Disposed = true;
}

/// Simulates the monitor collection: tests push input reports.
internal sealed class FakeMonitorChannel : IHidChannel
{
    public bool Disposed { get; set; }

    public event EventHandler<byte[]>? InputReport;

    public void Raise(byte[] report) => InputReport?.Invoke(this, report);

    public Task SendFeatureAsync(byte[] report, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<byte[]> GetFeatureAsync(byte reportId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public void Dispose() => Disposed = true;
}

/// Simulates DeviceWatcher plus opening. The same channel objects are reused across opens, like a device keeping its RAM.
internal sealed class FakeHidDeviceSource : IHidDeviceSource
{
    public FakeConfigChannel Config { get; } = new();
    public FakeMonitorChannel Monitor { get; } = new();
    public int Opens { get; private set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }

    public event EventHandler<string>? Arrived;
    public event EventHandler<string>? Departed;

    public void Arrive(string id = "dev1") => Arrived?.Invoke(this, id);

    public void Depart(string id = "dev1") => Departed?.Invoke(this, id);

    public void Start() => Started = true;

    public void Stop() => Stopped = true;

    public Task<HidDevicePair> OpenAsync(string deviceId, CancellationToken cancellationToken)
    {
        Opens++;
        Config.Disposed  = false;
        Monitor.Disposed = false;
        return Task.FromResult(new HidDevicePair(Config, Monitor));
    }
}
