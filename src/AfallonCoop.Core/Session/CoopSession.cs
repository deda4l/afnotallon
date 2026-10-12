using AfallonCoop.Protocol;
using AfallonCoop.Transport;
using AfallonCoop.World;

namespace AfallonCoop.Session;

public sealed class SessionConfig
{
    public string ModVersion = "";
    public string GameBuild = "";
    public string PlayerName = "Player";
    public ulong SteamId;
    /// <summary>This install's own random id (see <see cref="HelloMsg.ClientId"/>).</summary>
    public string ClientId = "";
    /// <summary>Host: players must give it to join (empty = anyone). Client: the one given.</summary>
    public string Password = "";
}

public enum SessionState { Connecting, Handshaking, Connected, Closed }

/// <summary>A member of a group: a player, online (with their session id) or waiting to reconnect.</summary>
public sealed class GroupMemberInfo
{
    public string Key = "";
    public string Name = "";
    public byte Id = GroupRegistry.Offline;
    public int Bots;
    public bool Online => Id != GroupRegistry.Offline;
}

/// <summary>A player group as everyone sees it (decided by the host).</summary>
public sealed class GroupInfo
{
    public byte Id;
    public string LeaderKey = "";
    /// <summary>In group order, including members who are disconnected (their place is kept).</summary>
    public readonly List<GroupMemberInfo> Members = new();
    public GroupMemberInfo LeaderMember => Members.FirstOrDefault(m => m.Key == LeaderKey);
    /// <summary>Session id of the leader, <see cref="GroupRegistry.Offline"/> while they are disconnected.</summary>
    public byte Leader => LeaderMember?.Id ?? GroupRegistry.Offline;
    /// <summary>Members online, by session id.</summary>
    public IEnumerable<byte> Players => Members.Where(m => m.Online).Select(m => m.Id);
    public GroupMemberInfo Member(byte id) => Members.FirstOrDefault(m => m.Online && m.Id == id);
    public int Size => Members.Count + Members.Sum(m => m.Bots);
}

/// <summary>
/// Star topology: clients talk only to the host, the host relays. Host is player id 0.
/// Everything happens inside <see cref="Tick"/> on the caller's thread.
/// </summary>
public sealed class CoopSession : IDisposable
{
    private const double HelloTimeout = 10;

    private readonly ITransport _t;
    private readonly SessionConfig _cfg;
    private readonly Func<double> _clock;
    private readonly NetWriter _w = new();

    // host side
    private readonly Dictionary<int, byte> _connToPlayer = new();
    private readonly Dictionary<int, double> _awaitingHello = new();

    public bool IsHost { get; }
    public byte LocalId { get; private set; }
    public SessionState State { get; private set; }
    public string Status { get; private set; } = "";
    public string Transport => _t.Description;
    public readonly Dictionary<byte, PlayerInfo> Players = new();

    public event Action<PlayerInfo> PlayerJoined;
    public event Action<PlayerInfo> PlayerLeft;
    public event Action<PlayerInfo> PlayerInfoChanged;
    public event Action<PlayerStateMsg> StateReceived;
    public event Action<byte, string> ChatReceived;
    /// <summary>Opaque effect payload from another player (format owned by the game adapter).</summary>
    public event Action<byte, byte[]> FxReceived;
    public event Action<string> Closed;
    public event Action<string> Log;
    /// <summary>Authoritative pause changed (decided by the host, applied by everyone).</summary>
    public event Action<bool, PauseReason> PauseChanged;
    /// <summary>Opaque world payload (mobs...) from the player who owns that object.</summary>
    public event Action<byte, byte[]> WorldReceived;
    /// <summary>A world/fx payload this machine sent: fx?, its first byte (the sender's kind), its size. For traffic views.</summary>
    public event Action<bool, byte, int> PayloadSent;
    /// <summary>Spawner key, new owner (<see cref="LeaseRegistry.NoOwner"/> = dormant), epoch.</summary>
    public event Action<uint, byte, uint> LeaseChanged;

    /// <summary>Groups changed (someone joined, left, a new leader, bot counts).</summary>
    public event Action GroupsChanged;
    /// <summary>The local player is invited by this player.</summary>
    public event Action<byte> GroupInvited;
    /// <summary>A message for the local player about their group (declined, full, removed...).</summary>
    public event Action<string> GroupNotice;
    /// <summary>The group leader sent this bot companion (npc id) of the local player away.</summary>
    public event Action<int> GroupBotDismissed;
    /// <summary>Back in the group after a disconnect: where the character was (scene hash, position).</summary>
    public event Action<int, float, float, float> ReturnRequested;
    /// <summary>A group member's message for the group (sender id, content), see <see cref="GroupCast"/>.</summary>
    public event Action<byte, byte[]> GroupMessage;

    // where each player last was (host): a member who reconnects is sent back there
    private readonly Dictionary<byte, (int scene, float x, float y, float z)> _lastPlace = new();
    private readonly Dictionary<string, (int scene, float x, float y, float z)> _placeByKey = new();

    // groups: the host decides with the registry, everyone keeps the resulting list
    private readonly GroupRegistry _groupReg = new();
    private readonly List<GroupInfo> _groups = new();
    public IReadOnlyList<GroupInfo> Groups => _groups;
    public GroupInfo GroupOf(byte player) => _groups.FirstOrDefault(g => g.Member(player) != null);
    public GroupInfo MyGroup => GroupOf(LocalId);
    public bool InSameGroup(byte a, byte b) { var g = GroupOf(a); return g != null && g.Member(b) != null; }

    // host: each player's identity across reconnects (Steam id, else the install's own id; never the name, which
    // anyone can share)
    private readonly Dictionary<byte, string> _playerKey = new();

    private string KeyOf(PlayerInfo p) => _playerKey.TryGetValue(p.Id, out var k) ? k : "player:" + p.Id;

    /// <summary>
    /// A player's identity kept across sessions (friends): their Steam account; their install's id when that account is
    /// already here (two copies of one account) or unknown.
    /// </summary>
    private string LastingKey(byte id, ulong steamId, string clientId)
    {
        bool shared = Players.Values.Any(p => p.Id != id && p.SteamId == steamId);
        if (steamId != 0 && !shared) return "steam:" + steamId;
        return !string.IsNullOrEmpty(clientId) ? "client:" + clientId : "player:" + id;
    }

    private string MakeKey(byte id, ulong steamId, string clientId)
    {
        string key = steamId != 0 ? "steam:" + steamId : !string.IsNullOrEmpty(clientId) ? "client:" + clientId : "player:" + id;
        // the same account or install twice at once (two copies on one PC): the second one is someone new
        if (_playerKey.Values.Contains(key)) key += "#" + id;
        _playerKey[id] = key;
        return key;
    }

    // leases: the host decides with the registry, everyone keeps a view of the result
    private readonly LeaseRegistry _leases = new();
    private readonly Dictionary<uint, (byte owner, uint epoch)> _leaseView = new();
    private readonly HashSet<uint> _interest = new();
    private readonly Dictionary<uint, bool> _leaseCombat = new();

    // pause: host decides; rule = host paused OR (2+ players and everyone has a menu open)
    private readonly Dictionary<byte, bool> _menuOpen = new();
    private bool _hostPause;
    private bool _localMenuOpen;
    public bool Paused { get; private set; }
    public PauseReason PauseReason { get; private set; }
    public bool HostPauseRequested => _hostPause;

    private CoopSession(ITransport t, SessionConfig cfg, Func<double> clock, bool host)
    {
        _t = t; _cfg = cfg; _clock = clock; IsHost = host;
        _leases.HostId = ProtocolInfo.HostId;
        _leases.OwnerChanged += OnLeaseOwnerChanged;
        _groupReg.Changed += OnGroupsDecided;
        _groupReg.Notice += (player, text) => ToPlayer(player, GroupOp.Notice, w => w.Str(text), () => GroupNotice?.Invoke(text));
        _groupReg.InviteSent += (to, from) => ToPlayer(to, GroupOp.InvitedBy, w => w.U8(from), () => GroupInvited?.Invoke(from));
        _groupReg.BotDismissed += (owner, npc) => ToPlayer(owner, GroupOp.BotDismissed, w => w.I32(npc), () => GroupBotDismissed?.Invoke(npc));
        _groupReg.Rejoined += player =>
        {
            if (!Players.TryGetValue(player, out var info) || !_placeByKey.TryGetValue(KeyOf(info), out var at)) return;
            ToPlayer(player, GroupOp.ReturnTo, w => { w.I32(at.scene); w.F32(at.x); w.F32(at.y); w.F32(at.z); }, () => { });
        };
        _t.Connected += OnConnected;
        _t.Disconnected += OnDisconnected;
        _t.Received += OnReceived;
    }

    public static CoopSession StartHost(ITransport t, SessionConfig cfg, Func<double> clock)
    {
        var s = new CoopSession(t, cfg, clock, true) { LocalId = ProtocolInfo.HostId, State = SessionState.Connected };
        s.Players[ProtocolInfo.HostId] = new PlayerInfo { Id = ProtocolInfo.HostId, Name = cfg.PlayerName, SteamId = cfg.SteamId, Key = s.LastingKey(ProtocolInfo.HostId, cfg.SteamId, cfg.ClientId) };
        s._groupReg.PlayerOnline(ProtocolInfo.HostId, s.MakeKey(ProtocolInfo.HostId, cfg.SteamId, cfg.ClientId), cfg.PlayerName);
        s.Status = "Hosting via " + t.Description;
        return s;
    }

    public static CoopSession StartClient(ITransport t, SessionConfig cfg, Func<double> clock)
    {
        var s = new CoopSession(t, cfg, clock, false) { State = SessionState.Connecting, Status = "Connecting via " + t.Description };
        return s;
    }

    public PlayerInfo Local => Players.TryGetValue(LocalId, out var p) ? p : null;
    public int PlayerCount => Players.Count;
    public int PingToHost => IsHost ? 0 : _t.GetPing(ITransport.ServerConnection);

    public void Tick()
    {
        if (State == SessionState.Closed) return;
        _t.Poll();
        if (IsHost) _leases.Tick(_clock());
        if (IsHost && _awaitingHello.Count > 0)
        {
            double now = _clock();
            foreach (var kv in _awaitingHello.Where(kv => now - kv.Value > HelloTimeout).ToList())
            {
                _awaitingHello.Remove(kv.Key);
                _t.Disconnect(kv.Key, "handshake timeout");
            }
        }
    }

    // ---------------- outgoing ----------------

    /// <summary>Update what others know about us (character chosen, level, scene).</summary>
    public void UpdateLocalInfo(Action<PlayerInfo> change)
    {
        var me = Local;
        if (me == null) return;
        change(me);
        me.Id = LocalId;
        if (IsHost) _groupReg.SetName(LocalId, me.CharacterName);
        _w.Reset(); _w.U8((byte)MsgType.PlayerInfo); me.Write(_w);
        if (IsHost) Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
        else _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
    }

    public void SendLocalState(PlayerStateMsg m)
    {
        if (State != SessionState.Connected) return;
        m.Id = LocalId;
        _w.Reset(); m.Write(_w);
        if (IsHost) Broadcast(_w.Span, Delivery.Unreliable, exceptConn: -1);
        else _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Unreliable);
    }

    public void SendChat(string text)
    {
        if (State != SessionState.Connected || string.IsNullOrWhiteSpace(text)) return;
        _w.Reset(); _w.U8((byte)MsgType.Chat); _w.U8(LocalId); _w.Str(text);
        if (IsHost) Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
        else _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
        ChatReceived?.Invoke(LocalId, text);
    }

    /// <summary>Report whether the local player has a menu open (Esc menu, or not in the world).</summary>
    public void SetLocalMenuOpen(bool open)
    {
        if (open == _localMenuOpen) return;
        _localMenuOpen = open;
        if (IsHost) { _menuOpen[LocalId] = open; RecomputePause(); }
        else if (State == SessionState.Connected)
        {
            _w.Reset(); _w.U8((byte)MsgType.MenuState); _w.Bool(open);
            _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
        }
    }

    /// <summary>Host only: pause or resume the world for everyone.</summary>
    public void SetHostPause(bool on)
    {
        if (!IsHost) return;
        _hostPause = on;
        RecomputePause();
    }

    private void RecomputePause()
    {
        bool allInMenu = Players.Count >= 2 && Players.Keys.All(id => _menuOpen.TryGetValue(id, out var m) && m);
        var reason = _hostPause ? PauseReason.Host : allInMenu ? PauseReason.AllInMenu : PauseReason.None;
        ApplyPause(reason != PauseReason.None, reason);
    }

    private void ApplyPause(bool paused, PauseReason reason)
    {
        if (paused == Paused && reason == PauseReason) return;
        Paused = paused;
        PauseReason = reason;
        if (IsHost)
        {
            WritePauseState();
            Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
        }
        Log?.Invoke(paused ? $"World paused ({reason})" : "World resumed");
        PauseChanged?.Invoke(paused, reason);
    }

    private void WritePauseState()
    {
        _w.Reset(); _w.U8((byte)MsgType.PauseState); _w.Bool(Paused); _w.U8((byte)PauseReason);
    }

    /// <summary>
    /// Send an effect played by the local character to everyone else. Unreliable is for streams where a lost packet
    /// is replaced by the next one (positions); events that happen once must be reliable.
    /// </summary>
    public void SendFx(ReadOnlySpan<byte> payload, bool reliable = true)
    {
        if (State != SessionState.Connected || payload.Length == 0 || payload.Length > 4000) return;
        var type = reliable ? MsgType.Fx : MsgType.FxStream;
        var d = reliable ? Delivery.Reliable : Delivery.UnreliableUnordered;
        PayloadSent?.Invoke(true, payload[0], payload.Length);
        _w.Reset(); _w.U8((byte)type); _w.U8(LocalId); _w.Blob(payload);
        if (IsHost) Broadcast(_w.Span, d, exceptConn: -1);
        else _t.Send(ITransport.ServerConnection, _w.Span, d);
    }

    /// <summary>World payload about objects the local player owns (see <see cref="WorldReceived"/>).</summary>
    public void SendWorld(ReadOnlySpan<byte> payload, bool reliable = true)
    {
        if (State != SessionState.Connected || payload.Length == 0 || payload.Length > 4000) return;
        var type = reliable ? MsgType.World : MsgType.WorldStream;
        var d = reliable ? Delivery.Reliable : Delivery.UnreliableUnordered;
        PayloadSent?.Invoke(false, payload[0], payload.Length);
        _w.Reset(); _w.U8((byte)type); _w.U8(LocalId); _w.Blob(payload);
        if (IsHost) Broadcast(_w.Span, d, exceptConn: -1);
        else _t.Send(ITransport.ServerConnection, _w.Span, d);
    }

    /// <summary>
    /// An unreliable world stream payload for one player only (what is near them: a mob stream is of no use to a player
    /// whose game has no copies of those mobs). The host sends it straight there, a client through the host; it arrives
    /// as <see cref="MsgType.WorldStream"/>.
    /// </summary>
    public void SendWorldStreamTo(byte player, ReadOnlySpan<byte> payload)
    {
        if (State != SessionState.Connected || player == LocalId || payload.Length == 0 || payload.Length > 4000) return;
        PayloadSent?.Invoke(false, payload[0], payload.Length);
        _w.Reset();
        if (IsHost)
        {
            int conn = ConnOf(player);
            if (conn < 0) return;
            _w.U8((byte)MsgType.WorldStream); _w.U8(LocalId); _w.Blob(payload);
            _t.Send(conn, _w.Span, Delivery.UnreliableUnordered);
        }
        else
        {
            _w.U8((byte)MsgType.WorldStreamTo); _w.U8(LocalId); _w.U8(player); _w.Blob(payload);
            _t.Send(ITransport.ServerConnection, _w.Span, Delivery.UnreliableUnordered);
        }
    }

    /// <summary>Host: players kept out, by Steam account (0 = none) or install id; asked when someone says hello.</summary>
    public Func<ulong, string, bool> IsBanned;

    /// <summary>Host: send a player away, with the reason they see.</summary>
    public void Kick(byte player, string reason)
    {
        int conn = IsHost ? ConnOf(player) : -1;
        if (conn < 0) return;
        _w.Reset(); _w.U8((byte)MsgType.Reject); _w.Str(reason);
        _t.Send(conn, _w.Span, Delivery.Reliable);
        _t.Disconnect(conn, reason);
    }

    private int ConnOf(byte player)
    {
        foreach (var kv in _connToPlayer) if (kv.Value == player) return kv.Key;
        return -1;
    }

    // ---------------- leases ----------------

    private static string LeaseKey(uint key) => key.ToString("x8");

    /// <summary>Who simulates spawner <paramref name="key"/>; <see cref="LeaseRegistry.NoOwner"/> if nobody or unknown yet.</summary>
    public byte LeaseOwner(uint key) => _leaseView.TryGetValue(key, out var v) ? v.owner : LeaseRegistry.NoOwner;
    public int LeaseCount => _leaseView.Count;

    /// <summary>The local player came near spawner <paramref name="key"/> (or left it). The host assigns an owner.</summary>
    /// <param name="leftPlace">Not near any more because this player left the scene or dungeon instance: the spawner's
    /// mobs are gone on this machine, so a fight cannot stay with it; the lease moves at once.</param>
    public void SetInterest(uint key, bool near, bool leftPlace = false)
    {
        if (near ? !_interest.Add(key) : !_interest.Remove(key)) return;
        if (IsHost)
        {
            if (near) _leases.Subscribe(LocalId, LeaseKey(key), _clock());
            else _leases.Unsubscribe(LocalId, LeaseKey(key), _clock(), leftPlace);
        }
        else if (State == SessionState.Connected) SendInterest(key, near, leftPlace);
    }

    private void SendInterest(uint key, bool near, bool leftPlace = false)
    {
        _w.Reset(); _w.U8((byte)MsgType.Interest); _w.U32(key); _w.Bool(near); _w.Bool(leftPlace);
        _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
    }

    /// <summary>Owner side: is anything from this spawner fighting (ownership does not move during a fight).</summary>
    public void ReportLeaseCombat(uint key, bool inCombat)
    {
        if (_leaseCombat.TryGetValue(key, out var was) && was == inCombat) return;
        _leaseCombat[key] = inCombat;
        if (IsHost) _leases.SetCombat(LocalId, LeaseKey(key), inCombat, _clock());
        else if (State == SessionState.Connected)
        {
            _w.Reset(); _w.U8((byte)MsgType.LeaseCombat); _w.U32(key); _w.Bool(inCombat);
            _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
        }
    }

    private void OnLeaseOwnerChanged(LeaseRegistry.Lease l, string reason)
    {
        uint key = Convert.ToUInt32(l.Key, 16);
        Log?.Invoke($"Spawner {l.Key}: owner {(l.Owner == LeaseRegistry.NoOwner ? "none" : "#" + l.Owner)} ({reason})");
        WriteLeaseOwner(key, l.Owner, l.Epoch);
        Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
        ApplyLeaseOwner(key, l.Owner, l.Epoch);
    }

    private void WriteLeaseOwner(uint key, byte owner, uint epoch)
    {
        _w.Reset(); _w.U8((byte)MsgType.LeaseOwner); _w.U32(key); _w.U8(owner); _w.U32(epoch);
    }

    private void ApplyLeaseOwner(uint key, byte owner, uint epoch)
    {
        if (_leaseView.TryGetValue(key, out var v) && v.epoch >= epoch) return; // stale or repeated
        _leaseView[key] = (owner, epoch);
        if (owner != LocalId) _leaseCombat.Remove(key);
        LeaseChanged?.Invoke(key, owner, epoch);
    }

    // ---------------- groups ----------------

    public void GroupInvite(byte target) => GroupRequest(GroupOp.Invite, w => w.U8(target));
    public void GroupAnswer(bool accept) => GroupRequest(GroupOp.Answer, w => w.Bool(accept));
    public void GroupLeave() => GroupRequest(GroupOp.Leave, _ => { });
    public void GroupKick(string memberKey) => GroupRequest(GroupOp.Kick, w => w.Str(memberKey));
    public void GroupKick(byte target) { var m = MyGroup?.Member(target); if (m != null) GroupKick(m.Key); }
    public void GroupPromote(byte target) => GroupRequest(GroupOp.Promote, w => w.U8(target));
    public void GroupDismissBot(byte owner, int npcId) => GroupRequest(GroupOp.DismissBot, w => { w.U8(owner); w.I32(npcId); });

    /// <summary>Send content to the other online members of the local player's group (reliable, through the host).</summary>
    public void GroupCast(byte[] data)
    {
        if (data == null || data.Length > ushort.MaxValue) return;
        GroupRequest(GroupOp.Cast, w => w.Blob(data));
    }

    private int _reportedBots = -1;

    /// <summary>The local player's number of bot companions (they count towards the group limit).</summary>
    public void ReportBots(int count)
    {
        if (count == _reportedBots) return;
        _reportedBots = count;
        GroupRequest(GroupOp.Bots, w => w.U8((byte)Math.Clamp(count, 0, 255)));
    }

    /// <summary>Free member slots for the local player's bots (the game asks before recruiting one).</summary>
    public int FreeGroupSlots
    {
        get
        {
            var g = MyGroup;
            int mine = Math.Max(0, _reportedBots);
            return g == null ? GroupRegistry.MaxSize - 1 - mine : GroupRegistry.MaxSize - g.Size;
        }
    }

    private void GroupRequest(GroupOp op, Action<NetWriter> body)
    {
        if (State != SessionState.Connected) return;
        if (IsHost) { var w = new NetWriter(); body(w); var r = new NetReader(w.Span.ToArray()); HostGroupOp(LocalId, op, ref r); return; }
        _w.Reset(); _w.U8((byte)MsgType.Group); _w.U8((byte)op); body(_w);
        _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
    }

    private void HostGroupOp(byte from, GroupOp op, ref NetReader r)
    {
        double now = _clock();
        switch (op)
        {
            case GroupOp.Invite: _groupReg.Invite(from, r.U8(), now); break;
            case GroupOp.Answer: _groupReg.Answer(from, r.Bool(), now); break;
            case GroupOp.Leave: _groupReg.Leave(from); break;
            case GroupOp.Kick: _groupReg.Kick(from, r.Str()); break;
            case GroupOp.Promote: _groupReg.Promote(from, r.U8()); break;
            case GroupOp.DismissBot: { byte owner = r.U8(); _groupReg.DismissBot(from, owner, r.I32()); break; }
            case GroupOp.Bots: _groupReg.SetBots(from, r.U8()); break;
            case GroupOp.Cast:
            {
                var data = r.Blob();
                var g = _groupReg.GroupOf(from);
                if (g == null) break;
                foreach (var m in g.Members)
                    if (m.Online && m.Id != from)
                        ToPlayer(m.Id, GroupOp.FromMember, w => { w.U8(from); w.Blob(data); },
                            () => GroupMessage?.Invoke(from, data));
                break;
            }
        }
    }

    /// <summary>Host: deliver a group message to one player (or raise it here when it is the host).</summary>
    private void ToPlayer(byte player, GroupOp op, Action<NetWriter> body, Action local)
    {
        if (player == LocalId) { local(); return; }
        int conn = ConnOf(player);
        if (conn < 0) return;
        _w.Reset(); _w.U8((byte)MsgType.Group); _w.U8((byte)op); body(_w);
        _t.Send(conn, _w.Span, Delivery.Reliable);
    }

    private void WriteGroupState()
    {
        _w.Reset(); _w.U8((byte)MsgType.Group); _w.U8((byte)GroupOp.State);
        var all = _groupReg.All;
        _w.U8((byte)all.Count);
        foreach (var g in all)
        {
            _w.U8(g.Id); _w.Str(g.LeaderKey); _w.U8((byte)g.Members.Count);
            foreach (var m in g.Members) { _w.Str(m.Key); _w.Str(m.Name); _w.U8(m.Id); _w.U8((byte)Math.Clamp(m.Bots, 0, 255)); }
        }
    }

    private void OnGroupsDecided()
    {
        WriteGroupState();
        Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
        var r = new NetReader(_w.Span.ToArray());
        r.U8(); r.U8();
        ReadGroupState(ref r);
    }

    private void ReadGroupState(ref NetReader r)
    {
        _groups.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            var g = new GroupInfo { Id = r.U8(), LeaderKey = r.Str() };
            int m = r.U8();
            for (int k = 0; k < m; k++) g.Members.Add(new GroupMemberInfo { Key = r.Str(), Name = r.Str(), Id = r.U8(), Bots = r.U8() });
            _groups.Add(g);
        }
        GroupsChanged?.Invoke();
    }

    private void ClientGroupOp(GroupOp op, ref NetReader r)
    {
        switch (op)
        {
            case GroupOp.State: ReadGroupState(ref r); break;
            case GroupOp.InvitedBy: GroupInvited?.Invoke(r.U8()); break;
            case GroupOp.Notice: GroupNotice?.Invoke(r.Str()); break;
            case GroupOp.BotDismissed: GroupBotDismissed?.Invoke(r.I32()); break;
            case GroupOp.ReturnTo: { int scene = r.I32(); float x = r.F32(), y = r.F32(), z = r.F32(); ReturnRequested?.Invoke(scene, x, y, z); break; }
            case GroupOp.FromMember: { byte from = r.U8(); GroupMessage?.Invoke(from, r.Blob()); break; }
        }
    }

    private void Broadcast(ReadOnlySpan<byte> data, Delivery d, int exceptConn)
    {
        foreach (var conn in _connToPlayer.Keys)
            if (conn != exceptConn) _t.Send(conn, data, d);
    }

    // ---------------- transport events ----------------

    private void OnConnected(int conn)
    {
        if (IsHost)
        {
            _awaitingHello[conn] = _clock();
            return;
        }
        State = SessionState.Handshaking;
        Status = "Handshaking...";
        _w.Reset();
        new HelloMsg
        {
            Protocol = ProtocolInfo.Version, ModVersion = _cfg.ModVersion, GameBuild = _cfg.GameBuild,
            PlayerName = _cfg.PlayerName, SteamId = _cfg.SteamId, ClientId = _cfg.ClientId, Password = _cfg.Password ?? "",
        }.Write(_w);
        _t.Send(conn, _w.Span, Delivery.Reliable);
    }

    private void OnDisconnected(int conn, string reason)
    {
        if (IsHost)
        {
            _awaitingHello.Remove(conn);
            if (!_connToPlayer.Remove(conn, out var id)) return;
            if (Players.Remove(id, out var info))
            {
                _menuOpen.Remove(id);
                _leases.PlayerLeft(id, _clock());
                if (_lastPlace.Remove(id, out var place)) _placeByKey[KeyOf(info)] = place;
                _groupReg.PlayerOffline(id); // their place in a group waits for them
                _playerKey.Remove(id);
                _w.Reset(); _w.U8((byte)MsgType.PlayerLeft); _w.U8(id);
                Broadcast(_w.Span, Delivery.Reliable, exceptConn: -1);
                Log?.Invoke($"{info.Name} left ({reason})");
                PlayerLeft?.Invoke(info);
                RecomputePause();
            }
            return;
        }
        Close($"Disconnected: {reason}");
    }

    private void OnReceived(int conn, ReadOnlyMemory<byte> mem)
    {
        try
        {
            var r = new NetReader(mem.Span);
            var type = (MsgType)r.U8();
            if (IsHost) HostHandle(conn, type, ref r, mem.Span);
            else ClientHandle(type, ref r);
        }
        catch (ProtocolException e)
        {
            Log?.Invoke($"Bad packet from {conn}: {e.Message}");
            if (IsHost) _t.Disconnect(conn, "protocol error");
        }
        catch (Exception e)
        {
            // a bug in one handler: that message is lost, the rest of the batch and the connection are not
            Log?.Invoke($"Handling a message from {conn} failed: {e}");
        }
    }

    // ---------------- host ----------------

    private void HostHandle(int conn, MsgType type, ref NetReader r, ReadOnlySpan<byte> raw)
    {
        if (type == MsgType.Hello)
        {
            var hello = HelloMsg.Read(ref r);
            HostAccept(conn, hello);
            return;
        }
        if (!_connToPlayer.TryGetValue(conn, out byte id)) return; // ignore anything before a successful Hello

        switch (type)
        {
            case MsgType.PlayerInfo:
            {
                var info = PlayerInfo.Read(ref r);
                info.Id = id;
                var existing = Players[id];
                info.Name = existing.Name; info.SteamId = existing.SteamId; info.Key = existing.Key; // identity is fixed at handshake
                Players[id] = info;
                _groupReg.SetName(id, info.CharacterName);
                _w.Reset(); _w.U8((byte)MsgType.PlayerInfo); info.Write(_w);
                Broadcast(_w.Span, Delivery.Reliable, exceptConn: conn);
                PlayerInfoChanged?.Invoke(info);
                break;
            }
            case MsgType.PlayerState:
            {
                var m = PlayerStateMsg.Read(ref r);
                m.Id = id;
                if (m.InWorld) _lastPlace[id] = (m.SceneHash, m.PosX, m.PosY, m.PosZ);
                _w.Reset(); m.Write(_w);
                Broadcast(_w.Span, Delivery.Unreliable, exceptConn: conn);
                StateReceived?.Invoke(m);
                break;
            }
            case MsgType.MenuState:
                _menuOpen[id] = r.Bool();
                RecomputePause();
                break;
            case MsgType.Fx:
            case MsgType.FxStream:
            case MsgType.World:
            case MsgType.WorldStream:
            {
                r.U8();
                var blob = r.Blob();
                _w.Reset(); _w.U8((byte)type); _w.U8(id); _w.Blob(blob);
                bool reliable = type is MsgType.Fx or MsgType.World;
                Broadcast(_w.Span, reliable ? Delivery.Reliable : Delivery.UnreliableUnordered, exceptConn: conn);
                if (type is MsgType.Fx or MsgType.FxStream) FxReceived?.Invoke(id, blob);
                else WorldReceived?.Invoke(id, blob);
                break;
            }
            case MsgType.WorldStreamTo:
            {
                r.U8();
                byte to = r.U8();
                var blob = r.Blob();
                if (to == LocalId) { WorldReceived?.Invoke(id, blob); break; }
                int target = ConnOf(to);
                if (target < 0 || target == conn) break;
                _w.Reset(); _w.U8((byte)MsgType.WorldStream); _w.U8(id); _w.Blob(blob); // the sender as the host knows it
                _t.Send(target, _w.Span, Delivery.UnreliableUnordered);
                break;
            }
            case MsgType.Interest:
            {
                uint key = r.U32();
                bool near = r.Bool();
                bool leftPlace = r.Remaining > 0 && r.Bool();
                if (near) _leases.Subscribe(id, LeaseKey(key), _clock());
                else _leases.Unsubscribe(id, LeaseKey(key), _clock(), leftPlace);
                break;
            }
            case MsgType.LeaseCombat:
            {
                uint key = r.U32();
                _leases.SetCombat(id, LeaseKey(key), r.Bool(), _clock());
                break;
            }
            case MsgType.Group:
                HostGroupOp(id, (GroupOp)r.U8(), ref r);
                break;
            case MsgType.Chat:
            {
                r.U8();
                var text = r.Str();
                if (text.Length > 300) text = text.Substring(0, 300);
                _w.Reset(); _w.U8((byte)MsgType.Chat); _w.U8(id); _w.Str(text);
                Broadcast(_w.Span, Delivery.Reliable, exceptConn: conn);
                ChatReceived?.Invoke(id, text);
                break;
            }
        }
    }

    private void HostAccept(int conn, HelloMsg hello)
    {
        _awaitingHello.Remove(conn);
        string reject = null;
        if (hello.Protocol != ProtocolInfo.Version)
            reject = $"Mod protocol mismatch (host {ProtocolInfo.Version}, you {hello.Protocol}). Update the mod.";
        else if (!string.IsNullOrEmpty(_cfg.GameBuild) && !string.IsNullOrEmpty(hello.GameBuild) && hello.GameBuild != _cfg.GameBuild)
            reject = "Different game version than the host. Both players must update Afallon.";
        else if (!string.IsNullOrEmpty(_cfg.Password) && hello.Password != _cfg.Password)
            reject = string.IsNullOrEmpty(hello.Password) ? "This game has a password: enter it under CO-OP > JOIN GAME." : "Wrong password.";
        else if (IsBanned?.Invoke(hello.SteamId, hello.ClientId) == true)
            reject = "The host has banned you from this game.";
        else if (_connToPlayer.ContainsKey(conn))
            reject = "Already connected.";
        else if (Players.Count >= ProtocolInfo.MaxPlayers)
            reject = $"Session is full ({ProtocolInfo.MaxPlayers} players).";

        if (reject != null)
        {
            _w.Reset(); _w.U8((byte)MsgType.Reject); _w.Str(reject);
            _t.Send(conn, _w.Span, Delivery.Reliable);
            _t.Disconnect(conn, reject);
            Log?.Invoke($"Rejected {hello.PlayerName}: {reject}");
            return;
        }

        byte id = 1;
        while (Players.ContainsKey(id)) id++;
        var info = new PlayerInfo { Id = id, Name = string.IsNullOrWhiteSpace(hello.PlayerName) ? $"Player {id}" : hello.PlayerName, SteamId = hello.SteamId, Key = LastingKey(id, hello.SteamId, hello.ClientId) };
        Players[id] = info;
        _connToPlayer[conn] = id;

        _w.Reset(); _w.U8((byte)MsgType.Welcome); _w.U8(id); _w.U8((byte)Players.Count);
        foreach (var p in Players.Values) p.Write(_w);
        _t.Send(conn, _w.Span, Delivery.Reliable);

        _w.Reset(); _w.U8((byte)MsgType.PlayerJoined); info.Write(_w);
        Broadcast(_w.Span, Delivery.Reliable, exceptConn: conn);

        Log?.Invoke($"{info.Name} joined as #{id}");
        PlayerJoined?.Invoke(info);

        foreach (var kv in _leaseView)
        {
            WriteLeaseOwner(kv.Key, kv.Value.owner, kv.Value.epoch);
            _t.Send(conn, _w.Span, Delivery.Reliable);
        }

        WriteGroupState();
        _t.Send(conn, _w.Span, Delivery.Reliable);
        _groupReg.PlayerOnline(id, MakeKey(id, hello.SteamId, hello.ClientId), info.Name); // back in their group if they had one

        _menuOpen[id] = true; // still in the main menu until they report otherwise
        RecomputePause();
        WritePauseState();
        _t.Send(conn, _w.Span, Delivery.Reliable);
    }

    // ---------------- client ----------------

    private void ClientHandle(MsgType type, ref NetReader r)
    {
        switch (type)
        {
            case MsgType.Welcome:
            {
                LocalId = r.U8();
                int n = r.U8();
                Players.Clear();
                for (int i = 0; i < n; i++) { var p = PlayerInfo.Read(ref r); Players[p.Id] = p; }
                State = SessionState.Connected;
                Status = "Connected via " + _t.Description;
                Log?.Invoke($"Joined as #{LocalId}, {Players.Count} players");
                foreach (var p in Players.Values.Where(p => p.Id != LocalId)) PlayerJoined?.Invoke(p);
                _w.Reset(); _w.U8((byte)MsgType.MenuState); _w.Bool(_localMenuOpen);
                _t.Send(ITransport.ServerConnection, _w.Span, Delivery.Reliable);
                foreach (var key in _interest) SendInterest(key, true); // reported before the handshake finished
                if (_reportedBots > 0) { int b = _reportedBots; _reportedBots = -1; ReportBots(b); }
                break;
            }
            case MsgType.Fx:
            case MsgType.FxStream:
            case MsgType.World:
            case MsgType.WorldStream:
            {
                byte id = r.U8();
                var blob = r.Blob();
                if (id == LocalId || !Players.ContainsKey(id)) break; // late from a player who left
                if (type is MsgType.Fx or MsgType.FxStream) FxReceived?.Invoke(id, blob);
                else WorldReceived?.Invoke(id, blob);
                break;
            }
            case MsgType.LeaseOwner:
            {
                uint key = r.U32();
                byte owner = r.U8();
                ApplyLeaseOwner(key, owner, r.U32());
                break;
            }
            case MsgType.Group:
                ClientGroupOp((GroupOp)r.U8(), ref r);
                break;
            case MsgType.PauseState:
            {
                bool paused = r.Bool();
                var reason = (PauseReason)r.U8();
                ApplyPause(paused, reason);
                break;
            }
            case MsgType.Reject:
                Status = r.Str();
                Close(Status);
                break;
            case MsgType.PlayerJoined:
            {
                var p = PlayerInfo.Read(ref r);
                Players[p.Id] = p;
                PlayerJoined?.Invoke(p);
                break;
            }
            case MsgType.PlayerLeft:
            {
                byte id = r.U8();
                if (Players.Remove(id, out var p)) PlayerLeft?.Invoke(p);
                break;
            }
            case MsgType.PlayerInfo:
            {
                var p = PlayerInfo.Read(ref r);
                if (p.Id == LocalId) break;
                Players[p.Id] = p;
                PlayerInfoChanged?.Invoke(p);
                break;
            }
            case MsgType.PlayerState:
            {
                var m = PlayerStateMsg.Read(ref r);
                // unreliable, so it can come after PlayerLeft: it would bring back a puppet whose sequence hides the next one with that id
                if (m.Id != LocalId && Players.ContainsKey(m.Id)) StateReceived?.Invoke(m);
                break;
            }
            case MsgType.Chat:
            {
                byte id = r.U8();
                ChatReceived?.Invoke(id, r.Str());
                break;
            }
        }
    }

    private void Close(string reason)
    {
        if (State == SessionState.Closed) return;
        State = SessionState.Closed;
        Status = reason;
        Log?.Invoke("Session closed: " + reason);
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        if (State != SessionState.Closed)
        {
            if (IsHost) foreach (var c in _connToPlayer.Keys.ToList()) _t.Disconnect(c, "Host left the session");
            else _t.Disconnect(ITransport.ServerConnection, "left");
            _t.Poll();
        }
        Close("Left the session");
        _t.Connected -= OnConnected;
        _t.Disconnected -= OnDisconnected;
        _t.Received -= OnReceived;
        _t.Dispose();
    }
}
