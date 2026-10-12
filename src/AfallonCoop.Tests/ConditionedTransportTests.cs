using AfallonCoop.Transport;
using Xunit;

namespace AfallonCoop.Tests;

public class ConditionedTransportTests
{
    private double _now;

    private (ConditionedTransport host, List<byte> got) Pair(out ITransport client)
    {
        var hub = new LoopbackHub();
        var host = new ConditionedTransport(hub.Host, () => _now);
        client = hub.Connect();
        var got = new List<byte>();
        client.Received += (c, d) => got.Add(d.Span[1]);
        host.Poll(); client.Poll();
        return (host, got);
    }

    [Fact]
    public void Latency_holds_packets_until_half_the_ping_has_passed()
    {
        var (host, got) = Pair(out var client);
        host.Conditions.PingMs = 100;
        host.Send(1, new byte[] { 18, 1 }, Delivery.Reliable);
        host.Poll(); client.Poll();
        Assert.Empty(got);
        _now += 0.051; host.Poll(); client.Poll();
        Assert.Equal(new byte[] { 1 }, got);
    }

    [Fact]
    public void Jitter_never_reorders_reliable_packets()
    {
        var (host, got) = Pair(out var client);
        host.Conditions.PingMs = 50; host.Conditions.JitterMs = 80; host.Conditions.LossPercent = 20;
        for (byte i = 0; i < 100; i++) host.Send(1, new byte[] { 18, i }, Delivery.Reliable);
        for (int t = 0; t < 100; t++) { _now += 0.01; host.Poll(); client.Poll(); }
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (byte)i), got);
    }

    [Fact]
    public void Loss_drops_only_unreliable_packets()
    {
        var (host, got) = Pair(out var client);
        host.Conditions.LossPercent = 50;
        for (byte i = 0; i < 200; i++) host.Send(1, new byte[] { 7, i }, Delivery.Unreliable);
        for (int t = 0; t < 10; t++) { _now += 0.01; host.Poll(); client.Poll(); }
        Assert.InRange(got.Count, 60, 140);
        Assert.Equal(200 - got.Count, host.Stats.Dropped);
    }

    [Fact]
    public void Traffic_is_counted_per_message_type()
    {
        var (host, _) = Pair(out var client);
        host.Send(1, new byte[] { 19, 0, 0, 0 }, Delivery.UnreliableUnordered);
        host.Send(1, new byte[] { 7, 0 }, Delivery.Unreliable);
        var report = host.Stats.Report(t => t == 19 ? "WorldStream" : "PlayerState");
        Assert.Contains("WorldStream", report);
        Assert.Equal(6, host.Stats.TotalOut);
    }
}
