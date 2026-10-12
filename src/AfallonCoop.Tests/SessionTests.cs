using AfallonCoop.Protocol;
using AfallonCoop.Session;
using AfallonCoop.Transport;
using Xunit;

namespace AfallonCoop.Tests;

public class SessionTests
{
    private double _now;
    private double Clock() => _now;

    private static SessionConfig Cfg(string name, string build = "B1") => new() { ModVersion = "0.1", GameBuild = build, PlayerName = name };

    private sealed class Net
    {
        public readonly LoopbackHub Hub = new();
        public CoopSession Host;
        public readonly List<CoopSession> Clients = new();

        public void Pump(int rounds = 6)
        {
            for (int i = 0; i < rounds; i++)
            {
                Host?.Tick();
                foreach (var c in Clients) c.Tick();
            }
        }
    }

    private Net Start(int clients, Func<int, SessionConfig> cfg = null)
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("Host"), Clock);
        for (int i = 0; i < clients; i++)
            n.Clients.Add(CoopSession.StartClient(n.Hub.Connect(), cfg?.Invoke(i) ?? Cfg($"C{i + 1}"), Clock));
        n.Pump();
        return n;
    }

    [Fact]
    public void Handshake_assigns_ids_and_everyone_sees_everyone()
    {
        var n = Start(4);
        Assert.Equal(5, n.Host.PlayerCount);
        foreach (var c in n.Clients)
        {
            Assert.Equal(SessionState.Connected, c.State);
            Assert.Equal(5, c.PlayerCount);
            Assert.NotEqual(ProtocolInfo.HostId, c.LocalId);
        }
        Assert.Equal(4, n.Clients.Select(c => c.LocalId).Distinct().Count());
    }

    [Fact]
    public void Sixth_player_is_rejected_with_reason()
    {
        var n = Start(5);
        var sixth = n.Clients[4];
        Assert.Equal(SessionState.Closed, sixth.State);
        Assert.Contains("full", sixth.Status);
        Assert.Equal(5, n.Host.PlayerCount);
    }

    [Fact]
    public void Different_game_build_is_rejected()
    {
        var n = Start(1, _ => Cfg("Old", build: "B0"));
        Assert.Equal(SessionState.Closed, n.Clients[0].State);
        Assert.Contains("game version", n.Clients[0].Status);
    }

    [Fact]
    public void State_from_one_client_is_relayed_to_host_and_other_clients_but_not_back()
    {
        var n = Start(3);
        var got = new Dictionary<CoopSession, List<PlayerStateMsg>>();
        foreach (var s in n.Clients.Append(n.Host))
        {
            var list = got[s] = new List<PlayerStateMsg>();
            s.StateReceived += m => list.Add(m);
        }
        var sender = n.Clients[0];
        var msg = new PlayerStateMsg { Seq = 1, PosX = 10, PosY = 2, PosZ = -5, RotY = 90, InWorld = true };
        msg.Params.Add(new AnimParam(StableHash.Of("Speed"), AnimParamType.Float, 3.5f));
        msg.Layers.Add(new AnimLayer(1234, 0.5f));
        msg.ActiveParts.Add(StableHash.Of("Stealth visual"));
        sender.SendLocalState(msg);
        n.Pump();

        Assert.Empty(got[sender]);
        foreach (var s in new[] { n.Host, n.Clients[1], n.Clients[2] })
        {
            var m = Assert.Single(got[s]);
            Assert.Equal(sender.LocalId, m.Id);
            Assert.Equal(10, m.PosX);
            Assert.Equal(3.5f, Assert.Single(m.Params).Value);
            Assert.Equal(1234, Assert.Single(m.Layers).StateHash);
            Assert.Equal(StableHash.Of("Stealth visual"), Assert.Single(m.ActiveParts));
        }
    }

    [Fact]
    public void Client_cannot_spoof_another_players_id()
    {
        var n = Start(2);
        PlayerStateMsg seen = null;
        n.Clients[1].StateReceived += m => seen = m;
        var w = new NetWriter();
        new PlayerStateMsg { Id = 0, Seq = 1 }.Write(w); // pretend to be the host
        // SendLocalState overwrites Id, so craft a raw send through the session's own API path:
        n.Clients[0].SendLocalState(new PlayerStateMsg { Id = 0, Seq = 1 });
        n.Pump();
        Assert.NotNull(seen);
        Assert.Equal(n.Clients[0].LocalId, seen.Id);
    }

    [Fact]
    public void Player_info_updates_propagate_and_identity_is_fixed()
    {
        var n = Start(2);
        n.Clients[0].UpdateLocalInfo(p => { p.CharacterName = "Aria"; p.Level = 7; p.Name = "Hacker"; });
        n.Pump();
        var onHost = n.Host.Players[n.Clients[0].LocalId];
        var onOther = n.Clients[1].Players[n.Clients[0].LocalId];
        Assert.Equal("Aria", onHost.CharacterName);
        Assert.Equal(7, onOther.Level);
        Assert.Equal("C1", onHost.Name);
        Assert.Equal("C1", onOther.Name);
    }

    [Fact]
    public void Leaving_client_is_removed_everywhere_and_slot_is_reused()
    {
        var n = Start(2);
        byte leftId = n.Clients[0].LocalId;
        var left = new List<byte>();
        n.Clients[1].PlayerLeft += p => left.Add(p.Id);
        n.Clients[0].Dispose();
        n.Clients.RemoveAt(0);
        n.Pump();
        Assert.Equal(new[] { leftId }, left);
        Assert.Equal(2, n.Host.PlayerCount);

        n.Clients.Add(CoopSession.StartClient(n.Hub.Connect(), Cfg("C3"), Clock));
        n.Pump();
        Assert.Equal(leftId, n.Clients[^1].LocalId);
    }

    [Fact]
    public void Host_leaving_closes_clients_with_reason()
    {
        var n = Start(2);
        n.Host.Dispose();
        n.Host = null;
        n.Pump();
        Assert.All(n.Clients, c => Assert.Equal(SessionState.Closed, c.State));
        Assert.All(n.Clients, c => Assert.Contains("Host left", c.Status));
    }

    [Fact]
    public void Silent_connection_is_dropped_after_hello_timeout()
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("Host"), Clock);
        n.Hub.Connect(); // raw transport, never says Hello
        n.Pump();
        _now += 11;
        n.Pump();
        Assert.Equal(1, n.Host.PlayerCount);
    }

    [Theory]
    [InlineData("", "password")]
    [InlineData("nope", "Wrong password")]
    [InlineData("secret", null)]
    public void A_host_with_a_password_lets_in_only_who_gives_it(string given, string rejectedWith)
    {
        var n = new Net();
        var hostCfg = Cfg("Host"); hostCfg.Password = "secret";
        n.Host = CoopSession.StartHost(n.Hub.Host, hostCfg, Clock);
        var clientCfg = Cfg("C1"); clientCfg.Password = given;
        n.Clients.Add(CoopSession.StartClient(n.Hub.Connect(), clientCfg, Clock));
        n.Pump();
        var c = n.Clients[0];
        if (rejectedWith == null) Assert.Equal(SessionState.Connected, c.State);
        else { Assert.Equal(SessionState.Closed, c.State); Assert.Contains(rejectedWith, c.Status); Assert.Equal(1, n.Host.PlayerCount); }
    }

    [Fact]
    public void A_kicked_player_is_told_why_and_a_banned_one_stays_out()
    {
        var n = Start(1, _ => new SessionConfig { ModVersion = "0.1", GameBuild = "B1", PlayerName = "Pest", ClientId = "pest-pc" });
        var pest = n.Clients[0];
        n.Host.IsBanned = (steam, client) => client == "pest-pc";
        n.Host.Kick(pest.LocalId, "The host has banned you from this game.");
        n.Pump();
        Assert.Equal(SessionState.Closed, pest.State);
        Assert.Contains("banned", pest.Status);
        Assert.Equal(1, n.Host.PlayerCount);

        var again = CoopSession.StartClient(n.Hub.Connect(), new SessionConfig { ModVersion = "0.1", GameBuild = "B1", PlayerName = "Pest", ClientId = "pest-pc" }, Clock);
        n.Clients.Add(again);
        n.Pump();
        Assert.Equal(SessionState.Closed, again.State);
        Assert.Contains("banned", again.Status);
    }

    [Fact]
    public void Targeted_world_stream_reaches_only_its_player()
    {
        var n = Start(3);
        var got = new Dictionary<CoopSession, List<string>>();
        foreach (var s in n.Clients.Append(n.Host)) { var l = got[s] = new List<string>(); s.WorldReceived += (id, b) => l.Add($"{id}:{b[0]}"); }
        var (a, b, c) = (n.Clients[0], n.Clients[1], n.Clients[2]);
        a.SendWorldStreamTo(b.LocalId, new byte[] { 7 });        // client -> client, through the host
        a.SendWorldStreamTo(ProtocolInfo.HostId, new byte[] { 8 }); // client -> host
        n.Host.SendWorldStreamTo(c.LocalId, new byte[] { 9 });    // host -> client
        n.Pump();
        Assert.Equal(new[] { $"{a.LocalId}:7" }, got[b]);
        Assert.Equal(new[] { $"{a.LocalId}:8" }, got[n.Host]);
        Assert.Equal(new[] { $"{ProtocolInfo.HostId}:9" }, got[c]);
        Assert.Empty(got[a]);
    }

    [Fact]
    public void A_throwing_handler_loses_only_its_message()
    {
        var n = Start(2);
        var lines = new List<string>();
        n.Clients[1].ChatReceived += (_, t) => { if (t == "boom") throw new InvalidOperationException(); lines.Add(t); };
        n.Clients[0].SendChat("boom");
        n.Clients[0].SendChat("after");
        n.Pump();
        Assert.Equal(new[] { "after" }, lines);
        Assert.Equal(SessionState.Connected, n.Clients[1].State);
    }

    [Fact]
    public void Chat_reaches_everyone_once()
    {
        var n = Start(2);
        var lines = new List<string>();
        foreach (var s in n.Clients.Append(n.Host)) s.ChatReceived += (id, t) => lines.Add($"{id}:{t}");
        n.Clients[0].SendChat("hi");
        n.Pump();
        Assert.Equal(3, lines.Count);
        Assert.All(lines, l => Assert.EndsWith(":hi", l));
    }
}
