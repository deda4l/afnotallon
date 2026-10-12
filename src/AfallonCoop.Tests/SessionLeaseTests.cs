using AfallonCoop.Session;
using AfallonCoop.Transport;
using AfallonCoop.World;
using Xunit;

namespace AfallonCoop.Tests;

/// <summary>Spawner leases over a real session: interest reports, owner broadcasts, hand-overs, late joiners.</summary>
public class SessionLeaseTests
{
    private double _now;
    private double Clock() => _now;
    private const uint Cave = 0xC0FFEE01;

    private static SessionConfig Cfg(string name) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = name };

    private sealed class Net
    {
        public readonly LoopbackHub Hub = new();
        public CoopSession Host;
        public readonly List<CoopSession> Clients = new();
        public IEnumerable<CoopSession> All => Clients.Prepend(Host);

        public void Pump(int rounds = 6)
        {
            for (int i = 0; i < rounds; i++) foreach (var s in All) s.Tick();
        }
    }

    private Net Start(int clients)
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("Host"), Clock);
        for (int i = 0; i < clients; i++) n.Clients.Add(CoopSession.StartClient(n.Hub.Connect(), Cfg($"C{i + 1}"), Clock));
        n.Pump();
        return n;
    }

    [Fact]
    public void First_player_near_a_spawner_owns_it_and_everyone_knows()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        a.SetInterest(Cave, true);
        n.Pump();
        b.SetInterest(Cave, true);
        n.Pump();
        foreach (var s in n.All) Assert.Equal(a.LocalId, s.LeaseOwner(Cave));
    }

    [Fact]
    public void Owner_leaving_hands_the_spawner_to_whoever_is_still_near()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        a.SetInterest(Cave, true); n.Pump();
        b.SetInterest(Cave, true); n.Pump();
        var seen = new List<byte>();
        b.LeaseChanged += (k, owner, e) => seen.Add(owner);
        a.SetInterest(Cave, false); n.Pump();
        Assert.Equal(b.LocalId, b.LeaseOwner(Cave));
        Assert.Equal(new[] { b.LocalId }, seen);
    }

    [Fact]
    public void Ownership_stays_with_a_fighting_owner_and_moves_when_the_fight_ends()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        a.SetInterest(Cave, true); n.Pump();
        b.SetInterest(Cave, true); n.Pump();
        a.ReportLeaseCombat(Cave, true); n.Pump();
        a.SetInterest(Cave, false); n.Pump();
        Assert.Equal(a.LocalId, b.LeaseOwner(Cave)); // kiting the mobs away keeps them with their owner
        a.ReportLeaseCombat(Cave, false); n.Pump();
        _now += 1; n.Pump();
        Assert.Equal(b.LocalId, b.LeaseOwner(Cave));
    }

    [Fact]
    public void Disconnect_of_the_owner_moves_the_spawner_even_mid_fight()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        a.SetInterest(Cave, true); n.Pump();
        b.SetInterest(Cave, true); n.Pump();
        a.ReportLeaseCombat(Cave, true); n.Pump();
        a.Dispose(); n.Clients.Remove(a); n.Pump();
        Assert.Equal(b.LocalId, b.LeaseOwner(Cave));
        Assert.Equal(b.LocalId, n.Host.LeaseOwner(Cave));
    }

    [Fact]
    public void Calm_spawner_migrates_to_the_host_when_the_host_is_near()
    {
        var n = Start(1);
        var a = n.Clients[0];
        a.SetInterest(Cave, true); n.Pump();
        n.Host.SetInterest(Cave, true); n.Pump();
        Assert.Equal(a.LocalId, a.LeaseOwner(Cave));
        _now += 5; n.Pump();
        Assert.Equal(n.Host.LocalId, a.LeaseOwner(Cave));
    }

    [Fact]
    public void Late_joiner_learns_existing_owners()
    {
        var n = Start(1);
        n.Clients[0].SetInterest(Cave, true); n.Pump();
        var late = CoopSession.StartClient(n.Hub.Connect(), Cfg("Late"), Clock);
        n.Clients.Add(late); n.Pump();
        Assert.Equal(n.Clients[0].LocalId, late.LeaseOwner(Cave));
    }

    [Fact]
    public void Interest_reported_before_the_handshake_is_sent_after_it()
    {
        var n = Start(0);
        var c = CoopSession.StartClient(n.Hub.Connect(), Cfg("Early"), Clock);
        c.SetInterest(Cave, true); // still connecting
        n.Clients.Add(c); n.Pump();
        Assert.Equal(c.LocalId, n.Host.LeaseOwner(Cave));
    }

    [Fact]
    public void World_payload_is_relayed_to_everyone_else_with_the_sender_id()
    {
        var n = Start(2);
        var got = new List<(string who, byte id, byte data)>();
        n.Host.WorldReceived += (id, d) => got.Add(("host", id, d[0]));
        n.Clients[1].WorldReceived += (id, d) => got.Add(("b", id, d[0]));
        n.Clients[0].WorldReceived += (id, d) => got.Add(("a", id, d[0]));
        n.Clients[0].SendWorld(new byte[] { 7 });
        n.Clients[0].SendWorld(new byte[] { 8 }, reliable: false);
        n.Pump();
        Assert.Contains(("host", n.Clients[0].LocalId, (byte)7), got);
        Assert.Contains(("b", n.Clients[0].LocalId, (byte)8), got);
        Assert.DoesNotContain(got, g => g.who == "a");
    }

    [Fact]
    public void Nobody_near_means_dormant()
    {
        var n = Start(1);
        var a = n.Clients[0];
        a.SetInterest(Cave, true); n.Pump();
        a.SetInterest(Cave, false); n.Pump();
        Assert.Equal(LeaseRegistry.NoOwner, n.Host.LeaseOwner(Cave));
    }
}
