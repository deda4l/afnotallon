using AfallonCoop.Session;
using AfallonCoop.Transport;
using Xunit;

namespace AfallonCoop.Tests;

/// <summary>Player groups over a real session: invites, the limit of 5 with bot companions, leaders, leaving.</summary>
public class GroupTests
{
    private double _now;
    private double Clock() => _now;

    private static SessionConfig Cfg(string name, string id = null) => new() { ModVersion = "0.1", GameBuild = "B1", PlayerName = name, ClientId = id ?? name };

    private sealed class Net
    {
        public readonly LoopbackHub Hub = new();
        public CoopSession Host;
        public readonly List<CoopSession> Clients = new();
        public IEnumerable<CoopSession> All => Clients.Prepend(Host);
        public void Pump(int rounds = 6) { for (int i = 0; i < rounds; i++) foreach (var s in All) s.Tick(); }
    }

    private Net Start(int clients)
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("Host"), Clock);
        for (int i = 0; i < clients; i++) n.Clients.Add(CoopSession.StartClient(n.Hub.Connect(), Cfg($"C{i + 1}"), Clock));
        n.Pump();
        return n;
    }

    private static void Join(Net n, CoopSession leader, CoopSession who)
    {
        who.GroupInvited += _ => who.GroupAnswer(true);
        leader.GroupInvite(who.LocalId);
        n.Pump();
    }

    [Fact]
    public void Invite_and_accept_makes_a_group_everyone_sees()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        byte invitedBy = 255;
        b.GroupInvited += from => invitedBy = from;
        a.GroupInvite(b.LocalId); n.Pump();
        Assert.Equal(a.LocalId, invitedBy);
        b.GroupAnswer(true); n.Pump();
        foreach (var s in n.All)
        {
            Assert.True(s.InSameGroup(a.LocalId, b.LocalId));
            Assert.Equal(a.LocalId, s.GroupOf(b.LocalId).Leader);
            Assert.False(s.InSameGroup(a.LocalId, n.Host.LocalId));
        }
    }

    [Fact]
    public void Declined_invite_tells_the_inviter_and_makes_no_group()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        string notice = "";
        a.GroupNotice += t => notice = t;
        b.GroupInvited += _ => b.GroupAnswer(false);
        a.GroupInvite(b.LocalId); n.Pump();
        Assert.Contains("declined", notice);
        Assert.Null(a.MyGroup);
    }

    [Fact]
    public void Bot_companions_count_towards_the_limit_of_five()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        a.ReportBots(2); b.ReportBots(2); n.Pump();
        Join(n, n.Host, a);                          // host + a + 2 bots = 4
        Assert.Equal(4, n.Host.MyGroup.Size);
        string notice = "";
        n.Host.GroupNotice += t => notice = t;
        bool asked = false;
        b.GroupInvited += _ => asked = true;
        n.Host.GroupInvite(b.LocalId); n.Pump();     // + b + 2 bots = 7 > 5
        Assert.False(asked);
        Assert.Contains("companion", notice);
        b.ReportBots(0); n.Pump();
        Join(n, n.Host, b);                          // + b = 5
        Assert.Equal(5, b.MyGroup.Size);
        Assert.Equal(0, a.FreeGroupSlots);           // no room for another bot
    }

    [Fact]
    public void Only_the_leader_invites_and_a_leaving_leader_hands_over()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        Join(n, n.Host, a);
        Join(n, n.Host, b);
        string notice = "";
        a.GroupNotice += t => notice = t;
        a.GroupInvite(b.LocalId); n.Pump();
        Assert.Contains("leader", notice);
        n.Host.GroupLeave(); n.Pump();
        foreach (var s in n.All) Assert.Equal(a.LocalId, s.GroupOf(b.LocalId).Leader);
        Assert.Null(n.Host.MyGroup);
    }

    [Fact]
    public void Kicked_and_last_members_are_out_and_the_group_dissolves()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        Join(n, a, b);
        string notice = "";
        b.GroupNotice += t => notice = t;
        a.GroupKick(b.LocalId); n.Pump();
        Assert.Contains("removed", notice);
        foreach (var s in n.All) Assert.Empty(s.Groups); // a alone is no group
    }

    [Fact]
    public void A_disconnected_member_keeps_their_place_and_gets_it_back()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        Join(n, n.Host, a);
        Join(n, n.Host, b);
        a.SendLocalState(new AfallonCoop.Protocol.PlayerStateMsg { InWorld = true, SceneHash = 7, PosX = 10, PosY = 2, PosZ = -3 }); n.Pump();
        a.Dispose(); n.Clients.Remove(a); n.Pump();
        var g = b.MyGroup;
        Assert.Equal(3, g.Members.Count);
        Assert.False(g.Members[1].Online); // a's place, second, waits for them
        Assert.Equal(new byte[] { n.Host.LocalId, b.LocalId }, g.Players);

        var back = CoopSession.StartClient(n.Hub.Connect(), Cfg("C1"), Clock);
        (int scene, float x)? returnTo = null;
        back.ReturnRequested += (sc, x, y, z) => returnTo = (sc, x);
        n.Clients.Add(back); n.Pump();
        g = b.MyGroup;
        Assert.True(g.Members[1].Online);
        Assert.Equal(back.LocalId, g.Members[1].Id);
        Assert.True(back.InSameGroup(back.LocalId, n.Host.LocalId));
        Assert.Equal((7, 10f), returnTo);
    }

    [Fact]
    public void A_disconnected_leader_hands_over_and_waits_as_a_member()
    {
        var n = Start(2);
        var (a, b) = (n.Clients[0], n.Clients[1]);
        Join(n, a, b);
        Join(n, a, n.Host);
        a.Dispose(); n.Clients.Remove(a); n.Pump();
        Assert.Equal(b.LocalId, n.Host.MyGroup.Leader);
        Assert.Equal(3, n.Host.MyGroup.Members.Count);
    }

    [Fact]
    public void Same_name_is_not_the_same_player()
    {
        var n = new Net();
        n.Host = CoopSession.StartHost(n.Hub.Host, Cfg("Host"), Clock);
        var a = CoopSession.StartClient(n.Hub.Connect(), Cfg("Bob", "pc-1"), Clock);
        var b = CoopSession.StartClient(n.Hub.Connect(), Cfg("Bob", "pc-2"), Clock);
        n.Clients.Add(a); n.Clients.Add(b); n.Pump();
        Join(n, n.Host, a);
        a.Dispose(); n.Clients.Remove(a); n.Pump();
        Assert.Null(b.MyGroup); // the other Bob does not take the first Bob's place
        var bob1 = CoopSession.StartClient(n.Hub.Connect(), Cfg("Bob", "pc-1"), Clock);
        n.Clients.Add(bob1); n.Pump();
        Assert.True(bob1.InSameGroup(bob1.LocalId, n.Host.LocalId));
    }

    [Fact]
    public void Leader_hands_the_lead_to_another_player()
    {
        var n = Start(1);
        var a = n.Clients[0];
        Join(n, n.Host, a);
        n.Host.GroupPromote(a.LocalId); n.Pump();
        foreach (var s in n.All) Assert.Equal(a.LocalId, s.MyGroup.Leader);
    }

    [Fact]
    public void Late_joiner_gets_the_groups()
    {
        var n = Start(1);
        Join(n, n.Host, n.Clients[0]);
        var late = CoopSession.StartClient(n.Hub.Connect(), Cfg("Late"), Clock);
        n.Clients.Add(late); n.Pump();
        Assert.True(late.InSameGroup(n.Host.LocalId, n.Clients[0].LocalId));
    }

    [Fact]
    public void Leader_sends_a_members_bot_away_on_its_owners_machine()
    {
        var n = Start(1);
        var a = n.Clients[0];
        a.ReportBots(1); n.Pump();
        Join(n, n.Host, a);
        int dismissed = -1;
        a.GroupBotDismissed += npc => dismissed = npc;
        n.Host.GroupDismissBot(a.LocalId, 312); n.Pump();
        Assert.Equal(312, dismissed);
    }
}
