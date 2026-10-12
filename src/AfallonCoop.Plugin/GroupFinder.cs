using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BLINK.RPGBuilder.AI;
using BLINK.RPGBuilder.Dungeons;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The game's Dungeon Finder for a co-op group (WoW-like):
///  - only the leader queues; the finder counts the roles of the whole group (players and their bots) and fills the
///    missing ones with bots, which join the leader (copies for everyone else);
///  - when the leader's finder has a group, every member gets the game's own invitation (same dungeon, same countdown);
///    the dungeon starts when all accepted, a decline or the timeout cancels it for all;
///  - then everyone travels with their own run (own return point) into the group's instance (<see cref="Dungeons"/>).
/// Messages go through the session's group channel.
/// </summary>
internal static class GroupFinder
{
    private const byte MsgRoles = 1, MsgInvite = 2, MsgAnswer = 3, MsgGo = 4, MsgCancel = 5, MsgReadyState = 6, MsgTakeBots = 7;

    /// <summary>Last roles each member reported: their own (preferred) role first, then their bots'.</summary>
    private static readonly Dictionary<byte, List<AdventurerRole>> RolesOf = new();
    private static string _sentRoles = "";
    private static float _nextRoles;

    // leader side: an invitation sent to the group, and who accepted it
    private static int _invitedDungeon;
    private static readonly HashSet<byte> Ready = new();
    private static bool _leaderReady;
    private static FinderPhase _lastPhase;

    // member side: an invitation from the leader is shown by the game's own panel
    private static bool _memberInvite;
    private static bool _going, _cancelling;
    private static bool _planning;

    private static DungeonFinderService Finder => DungeonFinderService.Instance;

    public static void Wire(AfallonCoop.Session.CoopSession s)
    {
        s.GroupMessage += OnMessage;
        s.GroupsChanged += () => { _sentRoles = ""; };
        s.PlayerLeft += p => RolesOf.Remove(p.Id);
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    private static AfallonCoop.Session.GroupInfo Group => CoopManager.Connected ? CoopManager.Session?.MyGroup : null;
    private static bool IAmLeader => Group != null && Group.Leader == CoopManager.Session.LocalId;
    private static bool HasOthers
    {
        get
        {
            var g = Group;
            if (g == null) return false;
            foreach (var id in g.Players) if (id != CoopManager.Session.LocalId) return true;
            return false;
        }
    }

    private static void Cast(byte kind, Action<BinaryWriter> body)
    {
        var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms)) { w.Write(kind); body(w); }
        CoopManager.Session?.GroupCast(ms.ToArray());
    }

    public static void Update()
    {
        var f = Finder;
        if (f == null || Group == null) { _memberInvite = false; _roster.Clear(); ReadyPanel.Render(null); return; }
        ReportRoles();
        GiveBackBots();
        FinishReturns();
        ResumeFights();
        bool groupInvite = f.Phase == FinderPhase.Invitation && (_memberInvite || (IAmLeader && _invitedDungeon != 0));
        if (IAmLeader)
        {
            var phase = f.Phase;
            if (phase != _lastPhase)
            {
                if (phase == FinderPhase.Invitation && HasOthers) InviteGroup(f);
                _lastPhase = phase;
            }
            if (groupInvite) BroadcastRoster(f);
        }
        _shownRoster = groupInvite ? _roster : null;
        ReadyPanel.Render(_shownRoster);
    }

    private static List<Seat> _shownRoster;

    // ---------------- ready check (the invitation window: a check mark on each member's role icon) ----------------

    internal readonly struct Seat
    {
        public readonly AdventurerRole Role; public readonly string Name; public readonly bool Ready;
        public Seat(AdventurerRole role, string name, bool ready) { Role = role; Name = name; Ready = ready; }
    }

    private static readonly List<Seat> _roster = new();
    private static string _rosterSig = "";

    /// <summary>Leader: who goes (players, their bots, the finder's bots) and who is ready (bots always are).</summary>
    private static void BroadcastRoster(DungeonFinderService f)
    {
        var seats = new List<Seat>();
        var s = CoopManager.Session;
        seats.Add(new Seat(f.playerRole, GameInfo.Character?.CharacterName ?? "", _leaderReady));
        try
        {
            var party = DungeonFinderService.Party();
            if (party != null) foreach (var e in party) if (e != null) seats.Add(new Seat(DungeonFinderService.Role(e), e.GetNPCData()?.entryDisplayName ?? "", true));
            var npcs = GameDatabase.Instance.GetNPCs();
            foreach (var id in f.planned)
                if (npcs.TryGetValue(id, out var npc)) seats.Add(new Seat(DungeonFinderRules.RoleOf(npc), npc.entryDisplayName ?? "", true));
        }
        catch { }
        foreach (var id in Group.Players)
        {
            if (id == s.LocalId) continue;
            RolesOf.TryGetValue(id, out var roles);
            var role = roles != null && roles.Count > 0 ? roles[0] : (AdventurerRole)(s.Players.TryGetValue(id, out var p) ? p.Role : 0);
            seats.Add(new Seat(role, Groups.Name(id), Ready.Contains(id)));
            if (roles != null) for (int i = 1; i < roles.Count; i++) seats.Add(new Seat(roles[i], "", true));
        }
        var sb = new System.Text.StringBuilder();
        foreach (var seat in seats) sb.Append((int)seat.Role).Append(seat.Ready ? '+' : '-').Append(seat.Name).Append('|');
        string sig = sb.ToString();
        if (sig == _rosterSig) return;
        _rosterSig = sig;
        _roster.Clear(); _roster.AddRange(seats);
        Cast(MsgReadyState, w =>
        {
            w.Write((byte)seats.Count);
            foreach (var seat in seats) { w.Write((byte)seat.Role); w.Write(seat.Ready); w.Write(seat.Name ?? ""); }
        });
    }

    /// <summary>This player's role in the finder and their bots' roles, for the leader's planning.</summary>
    private static void ReportRoles()
    {
        if (Time.unscaledTime < _nextRoles) return;
        _nextRoles = Time.unscaledTime + 1f;
        var roles = new List<AdventurerRole>();
        try
        {
            var f = Finder;
            roles.Add(f.SelectedRole);
            var party = DungeonFinderService.Party(); // the player's own bot companions (the game's party)
            if (party != null) foreach (var e in party) if (e != null) roles.Add(DungeonFinderService.Role(e));
        }
        catch { return; }
        string sig = string.Join(",", roles);
        if (sig == _sentRoles) return;
        _sentRoles = sig;
        Cast(MsgRoles, w => { w.Write((byte)roles.Count); foreach (var r in roles) w.Write((byte)r); });
    }

    private static void InviteGroup(DungeonFinderService f)
    {
        var d = f.InvitationDungeon;
        if (d == null) return;
        _invitedDungeon = d.ID;
        Ready.Clear();
        _rosterSig = "";
        _leaderReady = false;
        float seconds = f.SecondsRemaining;
        bool random = f.RandomQueue;
        Cast(MsgInvite, w => { w.Write(d.ID); w.Write(seconds); w.Write(random); });
        Plugin.L.LogInfo($"Group finder: group invited to dungeon #{d.ID} ({seconds:0} s)");
    }

    private static void OnMessage(byte from, byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data));
            byte kind = r.ReadByte();
            var g = Group;
            bool fromLeader = g != null && g.Leader == from;
            switch (kind)
            {
                case MsgRoles:
                {
                    int n = r.ReadByte();
                    var list = new List<AdventurerRole>();
                    for (int i = 0; i < n; i++) list.Add((AdventurerRole)r.ReadByte());
                    RolesOf[from] = list;
                    break;
                }
                case MsgInvite: if (fromLeader) ShowInvitation(r.ReadInt32(), r.ReadSingle(), r.ReadBoolean()); break;
                case MsgAnswer: if (IAmLeader) Answer(from, r.ReadBoolean(), r.ReadString()); break;
                case MsgGo: if (fromLeader) Go(r.ReadInt32()); break;
                case MsgCancel: if (fromLeader) CancelHere(r.ReadString()); break;
                case MsgReadyState:
                {
                    if (!fromLeader) break;
                    int n = r.ReadByte();
                    _roster.Clear();
                    for (int i = 0; i < n; i++)
                    {
                        var role = (AdventurerRole)r.ReadByte();
                        bool ready = r.ReadBoolean();
                        _roster.Add(new Seat(role, r.ReadString(), ready));
                    }
                    break;
                }
                case MsgTakeBots: ReceiveBots(from, r); break;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Group finder: " + e.Message); }
    }

    // ---------------- bots when someone leaves the dungeon ----------------
    //
    // The finder's bots are the leader's companions. When the leader (or whoever holds them) leaves the dungeon while
    // group members stay, the bots go on with one of them: that machine makes them its own companions where they stood
    // (same items), and everyone else sees them through the usual pet sync. When the leader comes back into the
    // dungeon, they return to the leader. Nobody left in the dungeon: the game's own dismissal.

    /// <summary>Bots this machine holds for the group that are not in its own finder run, with whose they were.</summary>
    private static readonly Dictionary<int, byte> HeldFrom = new();
    /// <summary>When each member was first seen in this dungeon instance again (bots go back only to someone settled in).</summary>
    private static readonly Dictionary<byte, float> HereSince = new();
    private static float _receivedAt = -100f;

    private static byte? StayingMember()
    {
        var g = Group;
        if (g == null || !Dungeons.InDungeon) return null;
        foreach (var id in g.Players)
            if (id != CoopManager.Session.LocalId && Puppets.TryGetPosition(id, out _)) return id;
        return null;
    }

    /// <summary>The group bots with this player now: the finder run's added bots and the ones held for someone.</summary>
    private static List<BLINK.RPGBuilder.Combat.MobCombatEntity> MyGroupBots()
    {
        var list = new List<BLINK.RPGBuilder.Combat.MobCombatEntity>();
        var ids = new HashSet<int>(HeldFrom.Keys);
        try { var run = Finder?.Run; if (run?.AddedNPCs != null) foreach (var id in run.AddedNPCs) ids.Add(id); } catch { }
        var pets = GameInfo.Player?.GetCurrentPets();
        if (pets == null || ids.Count == 0) return list;
        foreach (var p in pets)
        {
            var m = p?.TryCast<BLINK.RPGBuilder.Combat.MobCombatEntity>();
            var npc = m?.GetNPCData();
            if (npc != null && ids.Contains(npc.ID) && !m.Dead) list.Add(m);
        }
        return list;
    }

    /// <summary>Leaving: the bots stay with a member still in the dungeon. True = handed over (the game must not dismiss them).</summary>
    private static bool HandOff()
    {
        var to = StayingMember();
        var bots = MyGroupBots();
        if (to == null || bots.Count == 0) return false;
        byte me = CoopManager.Session.LocalId;
        Send(to.Value, bots, b => HeldFrom.TryGetValue(b.GetNPCData().ID, out var o) ? o : me);
        var run = Finder?.Run;
        foreach (var b in bots)
        {
            int id = b.GetNPCData().ID;
            HeldFrom.Remove(id);
            try { run?.AddedNPCs?.Remove(id); } catch { }
        }
        Plugin.L.LogInfo($"Group finder: {bots.Count} bots stay with {Groups.Name(to.Value)}");
        Hud.Notify($"Your group's bots stay with {Groups.Name(to.Value)}");
        return true;
    }

    private static void Send(byte to, List<BLINK.RPGBuilder.Combat.MobCombatEntity> bots, Func<BLINK.RPGBuilder.Combat.MobCombatEntity, byte> origin)
    {
        var data = new List<(int id, byte origin, Vector3 pos, float rotY, List<int> armor, List<int> slots, List<byte[]> armorData, List<int> weapons, List<byte[]> weaponData, uint target)>();
        foreach (var b in bots)
        {
            // each item with its slot (the second ring) and its own data (random stats, enchantment): the bot as strong there
            var armor = new List<int>(); var weapons = new List<int>(); var slots = new List<int>(); var ad = new List<int>(); var wd = new List<int>();
            MobSync.ReadGear(b, armor, weapons, slots, ad, wd);
            uint target = 0;
            try { var t = b.GetTarget(); if (t != null && !t.Dead) target = MobSync.SyncedId(t); } catch { } // the mob it fights
            data.Add((b.GetNPCData().ID, origin(b), b.transform.position, b.transform.eulerAngles.y, armor, slots, ad.ConvertAll(ItemBytes), weapons, wd.ConvertAll(ItemBytes), target));
        }
        Cast(MsgTakeBots, w =>
        {
            w.Write(to);
            w.Write((byte)data.Count);
            foreach (var d in data)
            {
                w.Write(d.id); w.Write(d.origin);
                w.Write(d.pos.x); w.Write(d.pos.y); w.Write(d.pos.z); w.Write(d.rotY);
                w.Write((byte)d.armor.Count);
                for (int k = 0; k < d.armor.Count; k++) { w.Write(d.armor[k]); w.Write(d.slots[k]); w.Write(d.armorData[k].Length); w.Write(d.armorData[k]); }
                w.Write((byte)d.weapons.Count);
                for (int k = 0; k < d.weapons.Count; k++) { w.Write(d.weapons[k]); w.Write(d.weaponData[k].Length); w.Write(d.weaponData[k]); }
                w.Write(d.target);
            }
        });
    }

    private static byte[] ItemBytes(int dataId) => dataId >= 0 ? ItemData.Write(dataId) ?? Array.Empty<byte>() : Array.Empty<byte>();

    /// <summary>The bot's item data here, as it was there (the bot now lives here: the game keeps it with the bot).</summary>
    private static int NewItemData(int itemId, byte[] bytes)
    {
        if (itemId <= 0 || bytes == null || bytes.Length == 0) return -1;
        try
        {
            int id = RPGBuilderUtilities.HandleNewItemDATA(itemId, CharacterEntries.ItemEntryState.InWorld);
            ItemData.Read(bytes, id);
            return id;
        }
        catch (Exception e) { Plugin.L.LogWarning("Bot item data: " + e.Message); return -1; }
    }

    private static void ReceiveBots(byte from, BinaryReader r)
    {
        byte to = r.ReadByte();
        int n = r.ReadByte();
        var me = GameInfo.Player;
        byte local = CoopManager.Session.LocalId;
        bool mine = to == local;
        var npcs = GameDatabase.Instance.GetNPCs();
        var run = Finder?.Run;
        int joined = 0;
        for (int i = 0; i < n; i++)
        {
            int id = r.ReadInt32(); byte origin = r.ReadByte();
            var pos = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle()); float rotY = r.ReadSingle();
            var armor = new List<int>(); var slots = new List<int>(); var armorData = new List<byte[]>();
            int na = r.ReadByte();
            for (int k = 0; k < na; k++) { armor.Add(r.ReadInt32()); slots.Add(r.ReadInt32()); armorData.Add(r.ReadBytes(r.ReadInt32())); }
            var weapons = new List<int>(); var weaponData = new List<byte[]>();
            int nw = r.ReadByte();
            for (int k = 0; k < nw; k++) { weapons.Add(r.ReadInt32()); weaponData.Add(r.ReadBytes(r.ReadInt32())); }
            uint target = r.ReadUInt32();
            if (!mine || me == null || !npcs.TryGetValue(id, out var npc)) continue;
            var e = CombatManager.Instance.GenerateNPCEntity(npc, true, false, me, pos, Quaternion.Euler(0, rotY, 0), null);
            var bot = e?.TryCast<BLINK.RPGBuilder.Combat.MobCombatEntity>();
            if (bot == null) continue;
            var ad = new List<int>(); for (int k = 0; k < armor.Count; k++) ad.Add(NewItemData(armor[k], armorData[k]));
            var wd = new List<int>(); for (int k = 0; k < weapons.Count; k++) wd.Add(NewItemData(weapons[k], weaponData[k]));
            MobSync.Dress(bot, armor, weapons, armorData: ad, weaponData: wd, armorSlots: slots);
            MobSync.ReplacePetCopy(from, id); // the sender's copy of it goes in the same frame: no double
            if (target != 0) Engage.Add((bot, target, Time.unscaledTime + 5f));
            joined++;
            if (origin == local && run?.AddedNPCs != null) { if (!run.AddedNPCs.Contains(id)) run.AddedNPCs.Add(id); } // back in its run
            else HeldFrom[id] = origin; // someone else's, or ours without a run (came back through the portal)
        }
        if (joined > 0)
        {
            _receivedAt = Time.unscaledTime;
            Plugin.L.LogInfo($"Group finder: {joined} bots from {Groups.Name(from)}");
            Hud.Notify(joined == 1 ? "A group bot joins you" : $"{joined} group bots join you");
        }
    }

    /// <summary>The bots' leader is back in the dungeon: they return to them.</summary>
    private static void GiveBackBots()
    {
        var g = Group;
        if (g != null)
            foreach (var id in g.Players)
                if (!Puppets.TryGetPosition(id, out _)) HereSince.Remove(id);
                else if (!HereSince.ContainsKey(id)) HereSince[id] = Time.unscaledTime;
        if (HeldFrom.Count == 0 || !Dungeons.InDungeon || Time.unscaledTime - _receivedAt < 15f) return;
        var back = new Dictionary<byte, List<BLINK.RPGBuilder.Combat.MobCombatEntity>>();
        foreach (var b in MyGroupBots())
        {
            if (!HeldFrom.TryGetValue(b.GetNPCData().ID, out var origin) || !HereSince.TryGetValue(origin, out var since) || Time.unscaledTime - since < 5f) continue;
            if (b.IsInCombat()) return; // a fight is finished by those in it: the bots go back once it is over
            if (!back.TryGetValue(origin, out var list)) back[origin] = list = new();
            list.Add(b);
        }
        foreach (var kv in back)
        {
            byte origin = kv.Key;
            Send(origin, kv.Value, _ => origin);
            foreach (var b in kv.Value)
            {
                HeldFrom.Remove(b.GetNPCData().ID);
                Returning.Add((b, origin, Time.unscaledTime + 4f));
            }
            Plugin.L.LogInfo($"Group finder: {kv.Value.Count} bots back to {Groups.Name(origin)}");
        }
    }

    /// <summary>
    /// Handed-over bots go on with the fight they were in: their target (by net id) may become this machine's mob a moment
    /// later (the lease follows the leaving player's), so it is looked up for a few seconds.
    /// </summary>
    private static readonly List<(BLINK.RPGBuilder.Combat.MobCombatEntity bot, uint target, float until)> Engage = new();

    private static void ResumeFights()
    {
        for (int i = Engage.Count - 1; i >= 0; i--)
        {
            var (bot, target, until) = Engage[i];
            var mob = bot != null && !bot.WasCollected ? MobSync.EntityOf(target) : null;
            if (mob != null && !mob.Dead) { try { bot.SetTarget(mob); } catch { } }
            else if (Time.unscaledTime < until && bot != null && !bot.WasCollected) continue;
            Engage.RemoveAt(i);
        }
    }

    /// <summary>Bots given back: each stays until the leader's own one shows up here (or a short timeout), then goes in that frame.</summary>
    private static readonly List<(BLINK.RPGBuilder.Combat.MobCombatEntity bot, byte to, float until)> Returning = new();

    private static void FinishReturns()
    {
        for (int i = Returning.Count - 1; i >= 0; i--)
        {
            var (bot, to, until) = Returning[i];
            if (bot == null || bot.WasCollected) { Returning.RemoveAt(i); continue; }
            int id = bot.GetNPCData()?.ID ?? -1;
            bool arrived = false;
            foreach (var c in MobSync.PetCopiesOf(to)) if (c.GetNPCData()?.ID == id) { arrived = true; break; }
            if (!arrived && Time.unscaledTime < until) continue;
            Returning.RemoveAt(i);
            DungeonFinderService.Dismiss(bot); // the game's own release of a finder bot (removes it here)
        }
    }

    // ---------------- member ----------------

    /// <summary>The leader's finder found a group: the same invitation here, in the game's own panel.</summary>
    private static void ShowInvitation(int dungeonId, float seconds, bool random)
    {
        var f = Finder;
        if (f == null) return;
        if (!GameDatabase.Instance.GetGameScenes().TryGetValue(dungeonId, out var dungeon)) return;
        int level = GameInfo.Character?.Level ?? 0;
        if (!DungeonFinderRules.Eligible(dungeon, level, random))
        {
            Cast(MsgAnswer, w => { w.Write(false); w.Write($"{GameInfo.Character?.CharacterName} cannot enter {dungeon.entryDisplayName} (level {level})"); });
            Hud.Notify($"Your group was invited to {dungeon.entryDisplayName}, but your level does not allow it");
            return;
        }
        if (f.Phase == FinderPhase.Queued || f.Phase == FinderPhase.Invitation) { _cancelling = true; try { f.Cancel(null); } finally { _cancelling = false; } }
        f.InvitationDungeon = dungeon;
        f.randomQueue = random;
        f.playerRole = f.SelectedRole;
        f.partySignature = f.Signature();
        f.deadline = Time.unscaledTime + Mathf.Max(5f, seconds);
        f.Phase = FinderPhase.Invitation;
        _memberInvite = true;
        _roster.Clear();
        Plugin.L.LogInfo($"Group finder: invited to '{dungeon.entryName}' ({seconds:0} s)");
    }

    private static void Go(int dungeonId)
    {
        var f = Finder;
        if (f == null || !_memberInvite || f.Phase != FinderPhase.Invitation || f.InvitationDungeon == null || f.InvitationDungeon.ID != dungeonId) return;
        _memberInvite = false;
        _going = true;
        try { if (!f.Accept()) Hud.Notify("Could not enter the dungeon: " + f.Status); }
        finally { _going = false; }
    }

    private static void CancelHere(string reason)
    {
        var f = Finder;
        _memberInvite = false;
        if (f == null || (f.Phase != FinderPhase.Invitation && f.Phase != FinderPhase.Queued)) return;
        _cancelling = true;
        try { f.Cancel(reason); } finally { _cancelling = false; }
        if (!string.IsNullOrEmpty(reason)) Hud.Notify(reason);
    }

    // ---------------- leader ----------------

    private static void Answer(byte from, bool accept, string reason)
    {
        if (_invitedDungeon == 0) return;
        if (!accept)
        {
            string why = string.IsNullOrEmpty(reason) ? $"{Groups.Name(from)} declined the dungeon" : reason;
            CancelAll(why);
            return;
        }
        Ready.Add(from);
        Hud.Notify($"{Groups.Name(from)} is ready");
        TryStart();
    }

    private static bool AllReady()
    {
        var g = Group;
        if (g == null) return false;
        foreach (var id in g.Players) if (id != CoopManager.Session.LocalId && !Ready.Contains(id)) return false;
        return _leaderReady;
    }

    private static void TryStart()
    {
        var f = Finder;
        if (f == null || !AllReady()) return;
        int dungeon = _invitedDungeon;
        _invitedDungeon = 0;
        _going = true;
        bool ok;
        try { ok = f.Accept(); } finally { _going = false; }
        if (ok) Cast(MsgGo, w => w.Write(dungeon));
        else CancelAll("The dungeon could not start: " + f.Status);
    }

    private static void CancelAll(string reason)
    {
        _invitedDungeon = 0;
        Cast(MsgCancel, w => w.Write(reason ?? ""));
        var f = Finder;
        if (f != null && (f.Phase == FinderPhase.Invitation || f.Phase == FinderPhase.Queued))
        {
            _cancelling = true;
            try { f.Cancel(reason); } finally { _cancelling = false; }
        }
        if (!string.IsNullOrEmpty(reason)) Hud.Notify(reason);
    }

    public static string Describe()
    {
        var f = Finder;
        var parts = new List<string> { $"phase {f?.Phase} '{f?.Status}' planned {f?.planned?.Count} found {f?.found?.Count}", $"leader={IAmLeader}", $"invited #{_invitedDungeon}", $"memberInvite={_memberInvite}", $"ready [{string.Join(",", Ready)}]{(_leaderReady ? "+me" : "")}" };
        foreach (var kv in RolesOf) parts.Add($"#{kv.Key}: {string.Join("/", kv.Value)}");
        parts.Add($"held [{string.Join(",", HeldFrom.Select(kv => $"{kv.Key}<-#{kv.Value}"))}] inDungeon={Dungeons.InDungeon} place='{Dungeons.Place}'");
        foreach (var id in Group?.Players ?? Enumerable.Empty<byte>()) parts.Add($"#{id} here={Puppets.TryGetPosition(id, out _)}");
        return string.Join(" | ", parts);
    }

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>Leaving the dungeon (exit, portal, menu): the bots stay with the group if anyone is still inside.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderService), "DismissAdded")]
        private static void BeforeDismiss()
        {
            try { HandOff(); } catch (Exception e) { Plugin.L.LogWarning("Group finder hand-off: " + e.Message); }
        }

        /// <summary>Bots held outside a run of one's own leave with the scene too: same hand-over, or the game's dismissal.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderService), "BeforeLoad")]
        private static void BeforeLoad()
        {
            try
            {
                if (HeldFrom.Count == 0 || HandOff()) return;
                foreach (var b in MyGroupBots()) if (HeldFrom.ContainsKey(b.GetNPCData().ID)) DungeonFinderService.Dismiss(b);
                HeldFrom.Clear();
            }
            catch (Exception e) { Plugin.L.LogWarning("Group finder hand-off: " + e.Message); }
        }

        /// <summary>The window as a ready check; drawn after the game's own update of it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DungeonFinderInvitation), "Update")]
        private static void InvitationDrawn() => ReadyPanel.Render(_shownRoster);

        /// <summary>
        /// In a group only the leader queues (for everyone). The panel's "find group" button, not Queue itself: a patch
        /// on Queue breaks its Nullable role argument (Il2CppInterop trampoline).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderPanel), "_Awake_b__31_2")]
        private static bool FindGroupClicked()
        {
            if (Group == null || IAmLeader) return true;
            Hud.Notify("Only your group leader can queue for a dungeon");
            return false;
        }

        /// <summary>The leader's plan counts the other players and their bots: bots fill only what is missing.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderService), "PlanParty")]
        private static bool PlanStart(DungeonFinderService __instance, ref bool __result)
        {
            if (_going && !IAmLeader && Group != null)
            {
                __instance.planned.Clear(); // a member brings no bots: the leader's finder added them
                __result = true;
                return false;
            }
            _planning = IAmLeader && HasOthers;
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DungeonFinderService), "PlanParty")]
        private static void PlanEnd() => _planning = false;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderRules), nameof(DungeonFinderRules.MissingRoles))]
        private static void WithGroup(ref Il2CppSystem.Collections.Generic.IEnumerable<AdventurerRole> __0)
        {
            if (!_planning) return;
            var g = Group;
            if (g == null) return;
            var all = new Il2CppSystem.Collections.Generic.List<AdventurerRole>();
            var e = __0.GetEnumerator();
            var step = e.Cast<Il2CppSystem.Collections.IEnumerator>();
            while (step.MoveNext()) all.Add(e.Current);
            foreach (var id in g.Players)
            {
                if (id == CoopManager.Session.LocalId) continue;
                if (RolesOf.TryGetValue(id, out var roles)) foreach (var r in roles) all.Add(r);
                else all.Add((AdventurerRole)(CoopManager.Session.Players.TryGetValue(id, out var p) ? p.Role : 0));
            }
            __0 = all.Cast<Il2CppSystem.Collections.Generic.IEnumerable<AdventurerRole>>();
        }

        /// <summary>Accepting waits for the whole group: the member tells the leader; the leader starts when all are ready.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DungeonFinderService), nameof(DungeonFinderService.Accept))]
        private static bool Accept(ref bool __result)
        {
            if (_going || Group == null) return true;
            if (_memberInvite)
            {
                Cast(MsgAnswer, w => { w.Write(true); w.Write(""); });
                Hud.Notify("Ready. Waiting for the group");
                __result = false;
                return false;
            }
            if (IAmLeader && _invitedDungeon != 0)
            {
                _leaderReady = true;
                if (AllReady()) TryStart(); else Hud.Notify("Ready. Waiting for the group");
                __result = false;
                return false;
            }
            return true;
        }

        /// <summary>Declining (or the countdown running out) cancels it for the group.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DungeonFinderService), nameof(DungeonFinderService.Cancel))]
        private static void Cancelled(string __0)
        {
            if (_cancelling || Group == null) return;
            string me = GameInfo.Character?.CharacterName ?? "A member";
            if (_memberInvite)
            {
                _memberInvite = false;
                Cast(MsgAnswer, w => { w.Write(false); w.Write($"{me} declined the dungeon"); });
            }
            else if (IAmLeader && _invitedDungeon != 0)
            {
                _invitedDungeon = 0;
                Cast(MsgCancel, w => w.Write($"{me} declined the dungeon"));
            }
        }
    }
}
