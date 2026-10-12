namespace AfallonCoop.World;

/// <summary>
/// Host-side player groups (MODDING_PLAN phase 4). The game itself has no group of players, only a player with
/// companion bots; a co-op group joins several players, each with their bot companions.
/// Rules (decided with the user):
///  - at most <see cref="MaxSize"/> members: players plus their adventurer bot companions (pets and summons do not count);
///  - a player's bots come into the group with them; the leader may send a bot away;
///  - the leader invites, removes and hands the lead to another player; an invite waits <see cref="InviteTimeout"/> s;
///  - a member who disconnects keeps their place (no time limit) and gets it back on reconnecting, recognised by a
///    stable key (Steam id, else name); a disconnected leader hands the lead to the next member online;
///  - a group of one member is dissolved.
/// </summary>
public sealed class GroupRegistry
{
    public const int MaxSize = 5;
    public const double InviteTimeout = 60;
    public const byte Offline = 255;

    public sealed class Member
    {
        public string Key = "";
        public string Name = "";
        public byte Id = Offline;   // session player id while online
        public int Bots;            // bot companions (kept while offline: they come back with the player)
        public bool Online => Id != Offline;
    }

    public sealed class Group
    {
        public byte Id;
        public string LeaderKey = "";
        public readonly List<Member> Members = new(); // join order = position in the group
        public Member Leader => Members.FirstOrDefault(m => m.Key == LeaderKey);
        public int Size => Members.Count + Members.Sum(m => m.Bots);
        public override string ToString() => $"#{Id} leader={LeaderKey} [{string.Join(",", Members.Select(m => m.Key + (m.Online ? "" : "(off)")))}]";
    }

    private readonly List<Group> _groups = new();
    private readonly Dictionary<byte, string> _keyOf = new();   // online player -> key
    private readonly Dictionary<byte, string> _nameOf = new();
    private readonly Dictionary<byte, int> _bots = new();       // online players not (yet) in a group
    private readonly Dictionary<byte, (byte from, double at)> _invites = new(); // invitee -> inviter
    private byte _nextId = 1;

    /// <summary>The groups changed (send the new state to everyone).</summary>
    public event Action Changed;
    /// <summary>(player, message): tell one player why something did not happen, or what happened to them.</summary>
    public event Action<byte, string> Notice;
    /// <summary>(invitee, inviter): ask the invitee.</summary>
    public event Action<byte, byte> InviteSent;
    /// <summary>(owner, npc id): the leader sent this bot of the owner's away.</summary>
    public event Action<byte, int> BotDismissed;
    /// <summary>(player): a member came back to their group after a disconnect.</summary>
    public event Action<byte> Rejoined;

    public IReadOnlyList<Group> All => _groups;

    private Member MemberOf(byte player, out Group g)
    {
        g = null;
        if (!_keyOf.TryGetValue(player, out var key)) return null;
        foreach (var grp in _groups)
        {
            var m = grp.Members.FirstOrDefault(x => x.Key == key);
            if (m != null) { g = grp; return m; }
        }
        return null;
    }

    public Group GroupOf(byte player) { MemberOf(player, out var g); return g; }
    public bool IsLeader(byte player) { var m = MemberOf(player, out var g); return m != null && g.LeaderKey == m.Key; }
    public int BotsOf(byte player) { var m = MemberOf(player, out _); return m?.Bots ?? (_bots.TryGetValue(player, out var n) ? n : 0); }

    /// <summary>A player is online (handshake done). Someone who left a group by disconnecting takes their place again.</summary>
    public void PlayerOnline(byte player, string key, string name)
    {
        _keyOf[player] = key;
        _nameOf[player] = name;
        var m = MemberOf(player, out var g);
        if (m == null || m.Online) return;
        m.Id = player;
        m.Name = name;
        Notice?.Invoke(player, "You are back in your group.");
        Rejoined?.Invoke(player);
        Changed?.Invoke();
    }

    /// <summary>A player disconnected: their place in a group is kept for them.</summary>
    public void PlayerOffline(byte player)
    {
        _invites.Remove(player);
        foreach (var kv in _invites.Where(kv => kv.Value.from == player).ToList()) _invites.Remove(kv.Key);
        var m = MemberOf(player, out var g);
        _keyOf.Remove(player); _nameOf.Remove(player); _bots.Remove(player);
        if (m == null) return;
        m.Id = Offline;
        if (g.LeaderKey == m.Key) PassLead(g);
        Changed?.Invoke();
    }

    private void PassLead(Group g)
    {
        var next = g.Members.FirstOrDefault(x => x.Online && x.Key != g.LeaderKey);
        if (next == null) return; // nobody online: the lead waits with the leader
        g.LeaderKey = next.Key;
        Notice?.Invoke(next.Id, "You are the group leader now.");
    }

    /// <summary>The name to show for a member (their character's, once they are in the world).</summary>
    public void SetName(byte player, string name)
    {
        if (string.IsNullOrEmpty(name)) return;
        _nameOf[player] = name;
        var m = MemberOf(player, out _);
        if (m == null || m.Name == name) return;
        m.Name = name;
        Changed?.Invoke();
    }

    /// <summary>A player's number of bot companions (their machine reports it whenever it changes).</summary>
    public void SetBots(byte player, int count)
    {
        count = Math.Max(0, count);
        var m = MemberOf(player, out _);
        if (m == null) { _bots[player] = count; return; }
        if (m.Bots == count) return;
        m.Bots = count;
        Changed?.Invoke();
    }

    public void Invite(byte from, byte to, double now)
    {
        if (from == to || !_keyOf.ContainsKey(to)) return;
        var g = GroupOf(from);
        if (g != null && !IsLeader(from)) { Notice?.Invoke(from, "Only the group leader can invite."); return; }
        if (GroupOf(to) != null) { Notice?.Invoke(from, "That player is already in a group."); return; }
        int size = g?.Size ?? 1 + BotsOf(from);
        if (size + 1 + BotsOf(to) > MaxSize)
        {
            Notice?.Invoke(from, BotsOf(to) > 0
                ? $"No room: they bring {BotsOf(to)} companion(s), the group would exceed {MaxSize}."
                : $"The group is full ({MaxSize}).");
            return;
        }
        if (_invites.TryGetValue(to, out var pending) && now - pending.at < InviteTimeout)
        {
            Notice?.Invoke(from, "That player is already considering an invitation.");
            return;
        }
        _invites[to] = (from, now);
        InviteSent?.Invoke(to, from);
        Notice?.Invoke(from, "Invitation sent.");
    }

    public void Answer(byte player, bool accept, double now)
    {
        if (!_invites.Remove(player, out var inv)) return;
        if (now - inv.at > InviteTimeout) { Notice?.Invoke(player, "The invitation expired."); return; }
        if (!accept) { Notice?.Invoke(inv.from, "The invitation was declined."); return; }
        if (GroupOf(player) != null || !_keyOf.ContainsKey(inv.from)) return;
        var g = GroupOf(inv.from);
        if (g != null && !IsLeader(inv.from)) { Notice?.Invoke(player, "The inviter is no longer the leader."); return; }
        int size = g?.Size ?? 1 + BotsOf(inv.from);
        if (size + 1 + BotsOf(player) > MaxSize) { Notice?.Invoke(player, "The group is full now."); return; }
        if (g == null)
        {
            g = new Group { Id = _nextId++, LeaderKey = _keyOf[inv.from] };
            if (_nextId == 0) _nextId = 1;
            g.Members.Add(NewMember(inv.from));
            _groups.Add(g);
        }
        g.Members.Add(NewMember(player));
        Changed?.Invoke();
    }

    private Member NewMember(byte player)
    {
        var m = new Member { Key = _keyOf[player], Name = _nameOf.TryGetValue(player, out var n) ? n : "", Id = player, Bots = _bots.TryGetValue(player, out var b) ? b : 0 };
        _bots.Remove(player);
        return m;
    }

    public void Leave(byte player)
    {
        _invites.Remove(player);
        var m = MemberOf(player, out var g);
        if (m == null) return;
        Remove(g, m);
        _bots[player] = m.Bots;
        Changed?.Invoke();
    }

    private void Remove(Group g, Member m)
    {
        g.Members.Remove(m);
        if (g.Members.Count <= 1)
        {
            foreach (var x in g.Members) if (x.Online) { _bots[x.Id] = x.Bots; Notice?.Invoke(x.Id, "Your group was disbanded."); }
            _groups.Remove(g);
        }
        else if (g.LeaderKey == m.Key)
        {
            var next = g.Members.FirstOrDefault(x => x.Online) ?? g.Members[0];
            g.LeaderKey = next.Key;
            if (next.Online) Notice?.Invoke(next.Id, "You are the group leader now.");
        }
    }

    /// <summary>The leader removes a member (online or waiting for a reconnect), by their key.</summary>
    public void Kick(byte leader, string key)
    {
        var lm = MemberOf(leader, out var g);
        if (lm == null || g.LeaderKey != lm.Key || key == lm.Key) return;
        var m = g.Members.FirstOrDefault(x => x.Key == key);
        if (m == null) return;
        if (m.Online) { Notice?.Invoke(m.Id, "You were removed from the group."); _bots[m.Id] = m.Bots; }
        Remove(g, m);
        Changed?.Invoke();
    }

    /// <summary>The lead goes to another player of the group who is online (bots cannot lead).</summary>
    public void Promote(byte leader, byte target)
    {
        var lm = MemberOf(leader, out var g);
        var tm = MemberOf(target, out var tg);
        if (lm == null || tm == null || g != tg || g.LeaderKey != lm.Key || !tm.Online) return;
        g.LeaderKey = tm.Key;
        Notice?.Invoke(target, "You are the group leader now.");
        Changed?.Invoke();
    }

    /// <summary>The leader sends one of a member's bot companions away (the owner's machine dismisses it).</summary>
    public void DismissBot(byte leader, byte owner, int npcId)
    {
        var g = GroupOf(leader);
        if (g == null || !IsLeader(leader) || GroupOf(owner) != g || BotsOf(owner) == 0) return;
        BotDismissed?.Invoke(owner, npcId);
    }
}
