using AfallonCoop.Protocol;
using AfallonCoop.Session;
using AfallonCoop.Transport;
using Xunit;

namespace AfallonCoop.Tests;

public class PauseTests
{
    private static SessionConfig Cfg(string n) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = n };

    private sealed class Net
    {
        public readonly LoopbackHub Hub = new();
        public CoopSession Host;
        public readonly List<CoopSession> Clients = new();
        public IEnumerable<CoopSession> All => Clients.Prepend(Host);
        public void Pump() { for (int i = 0; i < 8; i++) { Host.Tick(); foreach (var c in Clients) c.Tick(); } }
    }

    private static Net Start(int clients, bool clientsInWorld = true)
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("H"), () => 0);
        for (int i = 0; i < clients; i++)
        {
            var c = CoopSession.StartClient(n.Hub.Connect(), Cfg("C" + i), () => 0);
            c.SetLocalMenuOpen(!clientsInWorld);
            n.Clients.Add(c);
        }
        n.Pump();
        return n;
    }

    [Fact]
    public void Host_pause_pauses_everyone_and_resume_resumes()
    {
        var n = Start(2);
        n.Host.SetHostPause(true);
        n.Pump();
        Assert.All(n.All, s => { Assert.True(s.Paused); Assert.Equal(PauseReason.Host, s.PauseReason); });
        n.Host.SetHostPause(false);
        n.Pump();
        Assert.All(n.All, s => Assert.False(s.Paused));
    }

    [Fact]
    public void One_player_in_menu_does_not_pause_the_world()
    {
        var n = Start(2);
        n.Clients[0].SetLocalMenuOpen(true);
        n.Pump();
        Assert.All(n.All, s => Assert.False(s.Paused));
    }

    [Fact]
    public void Everyone_in_menu_pauses_and_anyone_leaving_menu_resumes()
    {
        var n = Start(2);
        n.Host.SetLocalMenuOpen(true);
        n.Clients[0].SetLocalMenuOpen(true);
        n.Pump();
        Assert.All(n.All, s => Assert.False(s.Paused));
        n.Clients[1].SetLocalMenuOpen(true);
        n.Pump();
        Assert.All(n.All, s => { Assert.True(s.Paused); Assert.Equal(PauseReason.AllInMenu, s.PauseReason); });
        n.Clients[0].SetLocalMenuOpen(false);
        n.Pump();
        Assert.All(n.All, s => Assert.False(s.Paused));
    }

    [Fact]
    public void Host_pause_wins_over_menus()
    {
        var n = Start(1);
        n.Host.SetLocalMenuOpen(true);
        n.Clients[0].SetLocalMenuOpen(true);
        n.Host.SetHostPause(true);
        n.Pump();
        Assert.Equal(PauseReason.Host, n.Clients[0].PauseReason);
        n.Host.SetHostPause(false);
        n.Pump();
        Assert.Equal(PauseReason.AllInMenu, n.Clients[0].PauseReason);
    }

    [Fact]
    public void Alone_in_menu_is_not_a_coop_pause()
    {
        var n = Start(0);
        n.Host.SetLocalMenuOpen(true);
        n.Pump();
        Assert.False(n.Host.Paused);
    }

    [Fact]
    public void Late_joiner_receives_current_pause_and_leaving_menu_holder_updates_rule()
    {
        var n = Start(1);
        n.Host.SetHostPause(true);
        n.Pump();
        var late = CoopSession.StartClient(n.Hub.Connect(), Cfg("Late"), () => 0);
        n.Clients.Add(late);
        n.Pump();
        Assert.True(late.Paused);
        Assert.Equal(PauseReason.Host, late.PauseReason);
    }

    [Fact]
    public void Client_still_in_main_menu_counts_as_in_menu_until_it_reports()
    {
        var n = Start(1, clientsInWorld: false); // client sits in the main menu
        n.Host.SetLocalMenuOpen(true);
        n.Pump();
        Assert.True(n.Host.Paused);
        n.Clients[0].SetLocalMenuOpen(false); // entered the world
        n.Pump();
        Assert.False(n.Host.Paused);
    }

    [Fact]
    public void Player_info_with_appearance_round_trips()
    {
        var n = Start(1);
        n.Clients[0].UpdateLocalInfo(p =>
        {
            p.RaceId = 3; p.Gender = "Female"; p.Appearance = "hair=2;face=1";
            p.Armor = new List<int> { 101, 102 }; p.Weapons = new List<int> { 7, 0 };
        });
        n.Pump();
        var seen = n.Host.Players[n.Clients[0].LocalId];
        Assert.Equal("hair=2;face=1", seen.Appearance);
        Assert.Equal(new[] { 101, 102 }, seen.Armor);
        Assert.Equal(new[] { 7, 0 }, seen.Weapons);
    }
}

public class FxTests
{
    [Fact]
    public void Fx_from_a_client_reaches_host_and_other_clients_with_sender_id()
    {
        var hub = new LoopbackHub();
        SessionConfig Cfg(string n) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = n };
        var host = CoopSession.StartHost(hub.Host, Cfg("H"), () => 0);
        var a = CoopSession.StartClient(hub.Connect(), Cfg("A"), () => 0);
        var b = CoopSession.StartClient(hub.Connect(), Cfg("B"), () => 0);
        void Pump() { for (int i = 0; i < 8; i++) { host.Tick(); a.Tick(); b.Tick(); } }
        Pump();
        var got = new List<(string who, byte id, byte[] data)>();
        host.FxReceived += (id, d) => got.Add(("host", id, d));
        a.FxReceived += (id, d) => got.Add(("a", id, d));
        b.FxReceived += (id, d) => got.Add(("b", id, d));
        a.SendFx(new byte[] { 1, 2, 3 });
        host.SendFx(new byte[] { 9 });
        Pump();
        Assert.Contains(got, g => g.who == "host" && g.id == a.LocalId && g.data.SequenceEqual(new byte[] { 1, 2, 3 }));
        Assert.Contains(got, g => g.who == "b" && g.id == a.LocalId);
        Assert.DoesNotContain(got, g => g.who == "a" && g.id == a.LocalId);
        Assert.Contains(got, g => g.who == "a" && g.id == 0 && g.data[0] == 9);
        Assert.DoesNotContain(got, g => g.who == "host" && g.id == 0);
    }

    [Fact]
    public void Unreliable_fx_stream_is_relayed_like_fx()
    {
        var hub = new LoopbackHub();
        SessionConfig Cfg(string n) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = n };
        var host = CoopSession.StartHost(hub.Host, Cfg("H"), () => 0);
        var a = CoopSession.StartClient(hub.Connect(), Cfg("A"), () => 0);
        var b = CoopSession.StartClient(hub.Connect(), Cfg("B"), () => 0);
        void Pump() { for (int i = 0; i < 8; i++) { host.Tick(); a.Tick(); b.Tick(); } }
        Pump();
        var got = new List<(string who, byte id, byte[] data)>();
        host.FxReceived += (id, d) => got.Add(("host", id, d));
        b.FxReceived += (id, d) => got.Add(("b", id, d));
        a.SendFx(new byte[] { 31, 7 }, reliable: false);
        Pump();
        Assert.Contains(got, g => g.who == "host" && g.id == a.LocalId && g.data.SequenceEqual(new byte[] { 31, 7 }));
        Assert.Contains(got, g => g.who == "b" && g.id == a.LocalId && g.data.SequenceEqual(new byte[] { 31, 7 }));
    }

    [Fact]
    public void Writer_can_patch_a_count_written_before_the_items()
    {
        var w = new NetWriter();
        w.U8(31); int at = w.Length; w.U16(0); w.U16(5); w.U16(6);
        w.PatchU16(at, 2);
        var r = new NetReader(w.Span);
        Assert.Equal(31, r.U8());
        Assert.Equal(2, r.U16());
        Assert.Throws<ArgumentOutOfRangeException>(() => w.PatchU16(w.Length - 1, 1));
    }
}
