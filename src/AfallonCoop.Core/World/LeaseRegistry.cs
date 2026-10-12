namespace AfallonCoop.World;

/// <summary>
/// Host-side registry of who simulates which spawner (MODDING_PLAN §4.4). A spawner is active on exactly one machine,
/// so mobs are never duplicated. Policy:
///  - the first player to come near a free spawner gets it; others nearby only observe;
///  - ownership is sticky; it never moves while the owner is fighting there (except on disconnect);
///  - when the owner leaves, a remaining observer takes over (host preferred), or the spawner goes dormant;
///  - a calm spawner that the host is also near migrates to the host (1 hop to everyone in a star).
/// Every ownership change bumps the epoch, so stale messages from a former owner can be ignored.
/// </summary>
public sealed class LeaseRegistry
{
    public const byte NoOwner = 255;

    public double MigrateToHostAfter = 3.0;   // seconds the spawner must be calm with the host nearby
    public byte HostId = 0;

    public sealed class Lease
    {
        public string Key = "";
        public byte Owner = NoOwner;
        public uint Epoch;
        public readonly HashSet<byte> Subscribers = new();
        public bool InCombat;
        public double CalmSince;
        public override string ToString() => $"{Key}: owner={Owner} epoch={Epoch} subs=[{string.Join(",", Subscribers)}] combat={InCombat}";
    }

    private readonly Dictionary<string, Lease> _leases = new();

    /// <summary>(key, newOwner or NoOwner, epoch, reason).</summary>
    public event Action<Lease, string> OwnerChanged;

    public IReadOnlyDictionary<string, Lease> All => _leases;
    public Lease Get(string key) => _leases.TryGetValue(key, out var l) ? l : null;
    public byte OwnerOf(string key) => _leases.TryGetValue(key, out var l) ? l.Owner : NoOwner;

    public void Subscribe(byte player, string key, double now)
    {
        if (!_leases.TryGetValue(key, out var l)) _leases[key] = l = new Lease { Key = key, CalmSince = now };
        l.Subscribers.Add(player);
        if (l.Owner == NoOwner) SetOwner(l, player, now, "first nearby");
    }

    /// <param name="leftPlace">The owner left the scene (or dungeon instance): its mobs are gone there, so even a fight
    /// moves to someone still nearby (who takes the mobs over), as on a disconnect.</param>
    public void Unsubscribe(byte player, string key, double now, bool leftPlace = false)
    {
        if (!_leases.TryGetValue(key, out var l)) return;
        l.Subscribers.Remove(player);
        if (l.Owner != player) return;
        if (l.InCombat && !leftPlace) return; // the fight follows the owner; decided again when it ends (Tick)
        l.InCombat = false;
        Reassign(l, now, leftPlace ? "owner left the area" : "owner left");
    }

    /// <summary>Owner reports whether anything from this spawner is fighting.</summary>
    public void SetCombat(byte player, string key, bool inCombat, double now)
    {
        if (!_leases.TryGetValue(key, out var l) || l.Owner != player) return;
        if (l.InCombat && !inCombat) l.CalmSince = now;
        l.InCombat = inCombat;
    }

    public void PlayerLeft(byte player, double now)
    {
        foreach (var l in _leases.Values)
        {
            l.Subscribers.Remove(player);
            if (l.Owner == player)
            {
                l.InCombat = false; // a disconnect is the one case where a fight changes hands
                Reassign(l, now, "owner disconnected");
            }
        }
    }

    public void Tick(double now)
    {
        foreach (var l in _leases.Values)
        {
            if (l.Owner == NoOwner || l.InCombat) continue;
            if (!l.Subscribers.Contains(l.Owner))
            {
                Reassign(l, now, "owner left after combat");
                continue;
            }
            if (l.Owner != HostId && l.Subscribers.Contains(HostId) && now - l.CalmSince >= MigrateToHostAfter)
                SetOwner(l, HostId, now, "migrated to host");
        }
    }

    private void Reassign(Lease l, double now, string reason)
    {
        byte next = l.Subscribers.Contains(HostId) ? HostId : l.Subscribers.Count > 0 ? l.Subscribers.Min() : NoOwner;
        SetOwner(l, next, now, next == NoOwner ? "dormant" : reason);
    }

    private void SetOwner(Lease l, byte owner, double now, string reason)
    {
        if (l.Owner == owner) return;
        l.Owner = owner;
        l.Epoch++;
        l.InCombat = false;
        l.CalmSince = now;
        OwnerChanged?.Invoke(l, reason);
    }
}
