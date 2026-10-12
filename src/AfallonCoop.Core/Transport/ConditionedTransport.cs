using System.Diagnostics;

namespace AfallonCoop.Transport;

/// <summary>Network conditions to imitate on top of a real (or loopback) transport.</summary>
public sealed class NetConditions
{
    /// <summary>Added round-trip time, ms. Each side delays its own sends by half, so two conditioned peers see this ping.</summary>
    public int PingMs;
    /// <summary>Random extra one-way delay, 0..JitterMs.</summary>
    public int JitterMs;
    /// <summary>Unreliable packets lost, percent. Reliable ones are delayed by one extra round trip instead (a resend).</summary>
    public double LossPercent;

    public bool Active => PingMs > 0 || JitterMs > 0 || LossPercent > 0;
    public override string ToString() => Active ? $"ping +{PingMs} ms, jitter {JitterMs} ms, loss {LossPercent:0.#}%" : "off";
}

/// <summary>
/// Wraps a transport to (1) imitate latency, jitter and loss on outgoing packets and (2) count traffic per message
/// type, both ways. Reliable packets keep their order (a late resend holds back the ones after it, as in a real
/// reliable channel); unreliable ones may arrive out of order or not at all.
/// </summary>
public sealed class ConditionedTransport : ITransport
{
    private readonly ITransport _inner;
    private readonly Func<double> _clock;
    private readonly Random _rng;

    private readonly struct Pending
    {
        public readonly double Due; public readonly int Conn; public readonly byte[] Data; public readonly Delivery Delivery; public readonly long Order;
        public Pending(double due, int conn, byte[] data, Delivery d, long order) { Due = due; Conn = conn; Data = data; Delivery = d; Order = order; }
    }

    private readonly List<Pending> _queue = new();
    private readonly Dictionary<int, double> _lastReliableDue = new();
    private long _order;

    public NetConditions Conditions { get; } = new();
    public TrafficStats Stats { get; } = new();

    public ConditionedTransport(ITransport inner, Func<double> clock = null, int seed = 1)
    {
        _inner = inner;
        var sw = Stopwatch.StartNew();
        _clock = clock ?? (() => sw.Elapsed.TotalSeconds);
        _rng = new Random(seed);
        _inner.Connected += c => Connected?.Invoke(c);
        _inner.Disconnected += (c, r) => Disconnected?.Invoke(c, r);
        _inner.Received += (c, data) =>
        {
            Stats.Count(_clock(), incoming: true, data.Length > 0 ? data.Span[0] : (byte)0, data.Length);
            Received?.Invoke(c, data);
        };
    }

    public bool IsServer => _inner.IsServer;
    public string Description => Conditions.Active ? $"{_inner.Description} [sim {Conditions}]" : _inner.Description;
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        double now = _clock();
        Stats.Count(now, incoming: false, data.Length > 0 ? data[0] : (byte)0, data.Length);
        var c = Conditions;
        if (!c.Active && _queue.Count == 0) { _inner.Send(connection, data, delivery); return; }

        double delay = (c.PingMs / 2.0 + _rng.NextDouble() * c.JitterMs) / 1000.0;
        if (delivery == Delivery.Reliable)
        {
            if (c.LossPercent > 0 && _rng.NextDouble() * 100 < c.LossPercent) delay += c.PingMs / 1000.0 + 0.05; // resend
            double due = now + delay;
            if (_lastReliableDue.TryGetValue(connection, out var last) && due < last) due = last; // keep order
            _lastReliableDue[connection] = due;
            _queue.Add(new Pending(due, connection, data.ToArray(), delivery, _order++));
        }
        else
        {
            if (c.LossPercent > 0 && _rng.NextDouble() * 100 < c.LossPercent) { Stats.Dropped++; return; }
            _queue.Add(new Pending(now + delay, connection, data.ToArray(), delivery, _order++));
        }
    }

    public void Poll()
    {
        if (_queue.Count > 0)
        {
            double now = _clock();
            var due = _queue.Where(p => p.Due <= now).OrderBy(p => p.Due).ThenBy(p => p.Order).ToList();
            if (due.Count > 0)
            {
                _queue.RemoveAll(p => p.Due <= now);
                foreach (var p in due) _inner.Send(p.Conn, p.Data, p.Delivery);
            }
        }
        _inner.Poll();
    }

    public void Disconnect(int connection, string reason) => _inner.Disconnect(connection, reason);

    public int GetPing(int connection)
    {
        int p = _inner.GetPing(connection);
        return p < 0 ? p : p + Conditions.PingMs;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>Bytes and packets per message type (first byte), in and out, over a sliding window.</summary>
public sealed class TrafficStats
{
    private const double Window = 5.0;
    private readonly Queue<(double t, bool inc, byte type, int bytes)> _log = new();
    public long TotalOut, TotalIn;
    public int Dropped;

    public void Count(double now, bool incoming, byte type, int bytes)
    {
        if (incoming) TotalIn += bytes; else TotalOut += bytes;
        _log.Enqueue((now, incoming, type, bytes));
        while (_log.Count > 0 && now - _log.Peek().t > Window) _log.Dequeue();
    }

    /// <summary>Per type and direction: bytes per second over the last few seconds.</summary>
    public List<(byte type, bool incoming, double bytesPerSecond)> Rates() =>
        _log.GroupBy(e => (e.type, e.inc)).Select(g => (g.Key.type, g.Key.inc, g.Sum(e => e.bytes) / Window)).ToList();

    /// <summary>Per type: bytes/s and packets/s out and in, averaged over the last few seconds.</summary>
    public string Report(Func<byte, string> typeName)
    {
        var rows = _log.GroupBy(e => (e.type, e.inc))
            .Select(g => (g.Key.type, g.Key.inc, bps: g.Sum(e => e.bytes) / Window, pps: g.Count() / Window, avg: g.Average(e => e.bytes)))
            .OrderByDescending(r => r.bps).ToList();
        var sb = new System.Text.StringBuilder();
        double outBps = rows.Where(r => !r.inc).Sum(r => r.bps), inBps = rows.Where(r => r.inc).Sum(r => r.bps);
        sb.AppendLine($"total out {outBps / 1024:0.0} KB/s, in {inBps / 1024:0.0} KB/s (last {Window:0} s); lost by sim: {Dropped}");
        foreach (var r in rows)
            sb.AppendLine($"  {(r.inc ? "in " : "out")} {typeName(r.type),-12} {r.bps / 1024,6:0.00} KB/s  {r.pps,6:0.0} pkt/s  avg {r.avg,5:0} B");
        return sb.ToString();
    }
}
