#if BATTLEGROUNDS // the game ships no battleground yet (no scene, finder tab off): build with -p:Battlegrounds=true
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.PvP;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The game's battlegrounds (BattlegroundService: two teams of adventurers race for five bases) with players on both
/// sides. The game runs a match for one player, who is always on the blue team; here one machine runs it, the
/// match's authority, and everyone else in it follows, as with the mobs of a dungeon:
///
///  - Queue: a player (a group's leader, for the group) queues with the game's finder; the host gathers whoever
///    queues for the same battleground within <see cref="Window"/> seconds into one match. Players of a group are on one
///    team, everyone else on a random one. The authority is the host if it queued, else the first who did.
///  - Invitation: everyone gets the game's own invitation; accepting is a ready check (who is ready shows in the
///    window). Once all answered (or the time is up) those who accepted travel; the authority's game plans the teams
///    with the players in them (their own bots join their team while there is room; adventurers fill the rest) and
///    spawns the adventurers, which everyone else sees as copies.
///  - The match: the authority's game runs the bases, the score, the timer and its adventurers, and tells the others.
///    Each player's own character (its health, death, wave respawn, its capture channel) lives on their machine; a
///    finished capture is told to the authority, which assaults the base the game's way.
///  - Fighting: a hit on an enemy player is shown at once and applied by the victim's machine against what the attacker
///    saw (lag compensation: the victim checks the hit against where it was at the moment the attacker saw it).
///  - Honor and wins: each machine gives its own player's and bots' rewards the game's way; kills and the resource
///    milestones earned elsewhere are told to the earner.
///  - If the authority leaves, the next player in the match takes over its adventurers where they stand.
/// </summary>
internal static class Battlegrounds
{
    public const byte Kind = 38;
    private const byte OpQueue = 1, OpUnqueue = 2, OpMatch = 3, OpAnswer = 4, OpReady = 5, OpGo = 6, OpCancel = 7, OpLead = 8,
        OpParty = 9, OpState = 10, OpAssault = 11, OpKill = 12, OpHonor = 13, OpStats = 14, OpHit = 15, OpAlert = 16;

    /// <summary>Seconds after the first queue during which others join the same match.</summary>
    public const float Window = 30f;
    private const float StateEvery = 0.5f, StatsEvery = 1f, ArrivalWait = 60f;

    private static readonly NetWriter W = new();
    private static BattlegroundService Svc { get { try { return BattlegroundService.Instance; } catch { return null; } } }
    private static AfallonCoop.Session.CoopSession S => CoopManager.Connected ? CoopManager.Session : null;
    private static byte Me => S?.LocalId ?? 0;
    private static bool Coop => S != null && S.PlayerCount >= 2;

    // ================================================================ the match this player is in

    private sealed class Match
    {
        public uint Id;
        public int Def;
        public byte Authority;
        public float Seconds;
        public bool Going;                                     // travelling or in the battleground
        public readonly Dictionary<byte, PvPTeam> Team = new(); // the players and their teams
        public readonly Dictionary<byte, List<int>> Bots = new(); // each player's bots that go along (npc ids)
        public readonly Dictionary<byte, byte> Answers = new();  // 0 waiting, 1 ready, 2 declined
        public float InvitedAt;
    }

    private static Match _match;
    private static bool _going, _fromNet, _inDefeat;
    private static bool _wasInArena;
    private static PvPTeam _completedSeen = (PvPTeam)(-9);

    /// <summary>In a running match (travelling there or in it).</summary>
    public static bool InMatch => _match != null && _match.Going;
    private static bool IAmAuthority => _match != null && _match.Authority == Me;
    private static PvPTeam MyTeam => _match != null && _match.Team.TryGetValue(Me, out var t) ? t : PvPTeam.Blue;

    private static BattlegroundDefinition DefOf(int index)
    {
        try { var all = Svc?.Settings?.Battlegrounds; return all != null && index >= 0 && index < all.Length ? all[index] : null; }
        catch { return null; }
    }

    private static int IndexOf(BattlegroundDefinition d)
    {
        try
        {
            var all = Svc?.Settings?.Battlegrounds;
            if (all == null || d == null) return -1;
            for (int i = 0; i < all.Length; i++) if (all[i] != null && all[i].Pointer == d.Pointer) return i;
        }
        catch { }
        return -1;
    }

    /// <summary>The instance of a battleground scene (for <see cref="Dungeons.Place"/>): the match's own, null if not ours.</summary>
    public static string InstanceFor(string scene)
    {
        if (!InMatch) return null;
        var def = DefOf(_match.Def);
        try
        {
            var gs = RPGBuilderUtilities.GetGameSceneFromName(scene);
            if (def?.Scene == null || gs == null || gs.ID != def.Scene.ID) return null;
        }
        catch { return null; }
        return "bg" + _match.Id.ToString("x8");
    }

    private static void Send(byte op, Action<NetWriter> body)
    {
        var s = S;
        if (s == null) return;
        W.Reset(); W.U8(Kind); W.U8(op);
        body?.Invoke(W);
        s.SendWorld(W.Span);
    }

    // ================================================================ queue (any machine) and the host's matchmaking

    /// <summary>Host: who queued for which battleground, in arrival order (a group is one unit).</summary>
    private sealed class Gathering { public int Def; public float Since; public readonly List<List<byte>> Units = new(); }
    private static readonly List<Gathering> Gatherings = new();

    private static List<byte> MyQueueUnit()
    {
        var g = S?.MyGroup;
        if (g != null && g.Leader == Me) return g.Players.ToList();
        return new List<byte> { Me };
    }

    private static void OnQueued(BattlegroundDefinition def)
    {
        int idx = IndexOf(def);
        if (idx < 0) return;
        var unit = MyQueueUnit();
        Send(OpQueue, w => { w.U8((byte)idx); w.U8((byte)unit.Count); foreach (var p in unit) w.U8(p); });
        if (S.IsHost) HostQueue(Me, idx, unit);
        Plugin.L.LogInfo($"Battleground: queued for #{idx} ({unit.Count} players)");
    }

    private static void OnUnqueued()
    {
        var unit = MyQueueUnit();
        Send(OpUnqueue, w => { w.U8((byte)unit.Count); foreach (var p in unit) w.U8(p); });
        if (S.IsHost) HostUnqueue(unit);
    }

    /// <summary>A group's leader queued: the members are queued too, with the game's own queue (their status, their checks).</summary>
    private static void MemberQueue(byte from, int def, List<byte> unit)
    {
        if (from == Me || !unit.Contains(Me)) return;
        var svc = Svc;
        var d = DefOf(def);
        if (svc == null || d == null) return;
        if (svc.Phase == BattlegroundPhase.Queued && svc.Definition != null && svc.Definition.Pointer == d.Pointer) return;
        _fromNet = true;
        bool ok;
        try { ok = svc.Queue(d); } finally { _fromNet = false; }
        if (ok) { Hud.Notify($"Your group is queued for {Localize.Source(d.DisplayName)}"); return; }
        Hud.Notify($"Your group queued for a battleground, but: {svc.Status}");
        Send(OpUnqueue, w => { w.U8(1); w.U8(Me); });
        if (S.IsHost) HostUnqueue(new List<byte> { Me });
    }

    private static void HostQueue(byte from, int def, List<byte> unit)
    {
        HostUnqueue(unit);
        var g = Gatherings.FirstOrDefault(x => x.Def == def);
        if (g == null) Gatherings.Add(g = new Gathering { Def = def, Since = Time.unscaledTime });
        g.Units.Add(unit);
        Plugin.L.LogInfo($"Battleground (host): #{from} queued {string.Join(",", unit)} for #{def}, {g.Units.Sum(u => u.Count)} waiting");
    }

    private static void HostUnqueue(List<byte> players)
    {
        foreach (var g in Gatherings) foreach (var u in g.Units) u.RemoveAll(players.Contains);
        foreach (var g in Gatherings) g.Units.RemoveAll(u => u.Count == 0);
        Gatherings.RemoveAll(g => g.Units.Count == 0);
    }

    /// <summary>Host: a gathering becomes a match when its window is over, or at once if every player here queued.</summary>
    private static void HostMatchmaking()
    {
        var s = S;
        if (s == null || !s.IsHost || Gatherings.Count == 0) return;
        foreach (var g in Gatherings.ToList())
        {
            var queued = new HashSet<byte>(g.Units.SelectMany(u => u));
            bool everyone = s.Players.Keys.All(queued.Contains);
            if (!everyone && Time.unscaledTime - g.Since < Window) continue;
            Gatherings.Remove(g);
            MakeMatch(g);
        }
    }

    private static readonly System.Random Rng = new();

    private static void MakeMatch(Gathering g)
    {
        var def = DefOf(g.Def);
        if (def == null) return;
        int size = def.TeamSize;
        var team = new Dictionary<byte, PvPTeam>();
        int blue = 0, red = 0;
        var later = new List<List<byte>>();
        // groups first (the hardest to seat), each on a random team with room for all of it; then the others at random
        var units = g.Units.OrderBy(_ => Rng.Next()).OrderByDescending(u => u.Count).ToList();
        foreach (var u in units)
        {
            bool blueFits = blue + u.Count <= size, redFits = red + u.Count <= size;
            if (!blueFits && !redFits) { later.Add(u); continue; }
            bool toBlue = blueFits && (!redFits || Rng.Next(2) == 0);
            foreach (var p in u) team[p] = toBlue ? PvPTeam.Blue : PvPTeam.Red;
            if (toBlue) blue += u.Count; else red += u.Count;
        }
        if (later.Count > 0) Gatherings.Add(new Gathering { Def = g.Def, Since = Time.unscaledTime, Units = { } }.With(later));
        if (team.Count == 0) return;
        // the authority: the host if it queued, else the first who did
        byte authority = team.ContainsKey(ProtocolInfo.HostId) ? ProtocolInfo.HostId : g.Units.First(u => u.Any(team.ContainsKey)).First(team.ContainsKey);
        // the authority's game always plays blue: the colours swap if needed (which side is which is up to chance anyway)
        if (team[authority] == PvPTeam.Red) foreach (var p in team.Keys.ToList()) team[p] = team[p] == PvPTeam.Blue ? PvPTeam.Red : PvPTeam.Blue;
        uint id = (uint)Rng.Next(1, int.MaxValue);
        float seconds = 40f;
        try { seconds = Mathf.Max(15f, Svc.Settings.InvitationSeconds); } catch { }
        Plugin.L.LogInfo($"Battleground (host): match {id:x8} #{g.Def}, authority #{authority}: {string.Join(", ", team.Select(kv => $"#{kv.Key} {kv.Value}"))}");
        Send(OpMatch, w =>
        {
            w.U32(id); w.U8((byte)g.Def); w.U8(authority); w.F32(seconds);
            w.U8((byte)team.Count); foreach (var kv in team) { w.U8(kv.Key); w.U8((byte)kv.Value); }
        });
        Invited(id, g.Def, authority, seconds, team);
    }

    private static Gathering With(this Gathering g, List<List<byte>> units) { g.Units.AddRange(units); return g; }

    // ================================================================ invitation and ready check

    private static void Invited(uint id, int def, byte authority, float seconds, Dictionary<byte, PvPTeam> team)
    {
        if (!team.ContainsKey(Me)) return;
        var svc = Svc;
        var d = DefOf(def);
        if (svc == null || d == null) return;
        if (svc.Phase != BattlegroundPhase.Queued && svc.Phase != BattlegroundPhase.Idle)
        {
            Send(OpAnswer, w => { w.U32(id); w.U8(2); });
            return;
        }
        _match = new Match { Id = id, Def = def, Authority = authority, Seconds = seconds, InvitedAt = Time.unscaledTime };
        foreach (var kv in team) { _match.Team[kv.Key] = kv.Value; _match.Answers[kv.Key] = 0; }
        svc.Definition = d;
        svc.Phase = BattlegroundPhase.Invitation;
        svc.deadline = Time.time + seconds;
        svc.status = "Your battleground is ready";
        // the authority plans with everyone's bots: which of mine could come
        var party = MyPartyIds();
        Send(OpParty, w => { w.U32(id); w.U8((byte)party.Count); foreach (var n in party) w.I32(n); });
        _match.Bots[Me] = party;
        Plugin.L.LogInfo($"Battleground: invited to match {id:x8} ({d.DisplayName}), team {MyTeam}, authority #{authority}");
    }

    private static List<int> MyPartyIds()
    {
        var list = new List<int>();
        try { foreach (var e in BattlegroundService.ExistingParty()) { var n = e?.GetNPCData(); if (n != null) list.Add(n.ID); } }
        catch { }
        return list;
    }

    private static void Answer(bool yes)
    {
        if (_match == null || _match.Going) return;
        _match.Answers[Me] = (byte)(yes ? 1 : 2);
        uint id = _match.Id;
        Send(OpAnswer, w => { w.U32(id); w.U8((byte)(yes ? 1 : 2)); });
        if (IAmAuthority) { BroadcastReady(); TryFinish(); }
        if (!yes) { _match = null; return; }
        Hud.Notify("Ready. Waiting for the others");
    }

    private static void BroadcastReady()
    {
        var m = _match;
        Send(OpReady, w => { w.U32(m.Id); w.U8((byte)m.Answers.Count); foreach (var kv in m.Answers) { w.U8(kv.Key); w.U8(kv.Value); } });
    }

    /// <summary>Authority: everyone answered, or the time is up (a little before the others' countdowns end).</summary>
    private static void TryFinish()
    {
        var m = _match;
        if (m == null || m.Going || !IAmAuthority) return;
        bool allAnswered = m.Answers.Values.All(a => a != 0);
        if (!allAnswered && Time.unscaledTime - m.InvitedAt < m.Seconds - 1.5f) return;
        var accepted = m.Answers.Where(kv => kv.Value == 1).Select(kv => kv.Key).OrderBy(p => p).ToList();
        if (accepted.Count == 0) { Cancel("Nobody accepted the battleground"); return; }
        if (!accepted.Contains(Me))
        {
            // I am not going: the first who is takes the match over (it knows the answers from the ready checks)
            byte next = accepted[0];
            m.Authority = next;
            Send(OpLead, w => { w.U32(m.Id); w.U8(next); w.U16(0); });
            Plugin.L.LogInfo($"Battleground: not going, match {m.Id:x8} passes to #{next}");
            _match = null;
            return;
        }
        foreach (var p in m.Team.Keys.ToList()) if (!accepted.Contains(p)) { m.Team.Remove(p); m.Bots.Remove(p); }
        AllowBots(m);
        var svc = Svc;
        // the game's own plan with the players in it: enough adventurers for the rest?
        if (svc == null || !svc.PlanTeams()) { Cancel(svc?.Status ?? "The battleground could not start"); return; }
        Send(OpGo, w =>
        {
            w.U32(m.Id); w.U8(Me);
            w.U8((byte)m.Team.Count);
            foreach (var kv in m.Team)
            {
                w.U8(kv.Key); w.U8((byte)kv.Value);
                var bots = m.Bots.TryGetValue(kv.Key, out var b) ? b : new List<int>();
                w.U8((byte)bots.Count); foreach (var n in bots) w.I32(n);
            }
        });
        Go();
    }

    /// <summary>Each team: its players first, then their bots while there is room (the authority's first).</summary>
    private static void AllowBots(Match m)
    {
        var def = DefOf(m.Def);
        int size = def?.TeamSize ?? 5;
        foreach (PvPTeam t in new[] { PvPTeam.Blue, PvPTeam.Red })
        {
            var players = m.Team.Where(kv => kv.Value == t).Select(kv => kv.Key).OrderBy(p => p == m.Authority ? -1 : p).ToList();
            int room = size - players.Count;
            foreach (var p in players)
            {
                var all = m.Bots.TryGetValue(p, out var b) ? b : new List<int>();
                var take = all.Take(Math.Max(0, room)).ToList();
                room -= take.Count;
                m.Bots[p] = take;
            }
        }
    }

    private static void Go()
    {
        var svc = Svc;
        if (svc == null || _match == null) return;
        _match.Going = true;
        _going = true;
        bool ok;
        try { ok = svc.Accept(); } finally { _going = false; }
        if (!ok) { Hud.Notify("Could not enter the battleground: " + svc.Status); _match = null; }
        else Plugin.L.LogInfo($"Battleground: entering match {_match.Id:x8} as {MyTeam}");
    }

    private static void Cancel(string reason)
    {
        var m = _match;
        if (m == null) return;
        Send(OpCancel, w => { w.U32(m.Id); w.Str(reason ?? ""); });
        CancelHere(reason);
    }

    private static void CancelHere(string reason)
    {
        _match = null;
        var svc = Svc;
        if (svc != null && (svc.Phase == BattlegroundPhase.Invitation || svc.Phase == BattlegroundPhase.Queued))
        {
            _fromNet = true;
            try { svc.Cancel(reason); } finally { _fromNet = false; }
        }
        if (!string.IsNullOrEmpty(reason)) Hud.Notify(reason);
    }

    /// <summary>The ready check in the game's invitation window (its two lines): the teams, then who is ready.</summary>
    private static string ReadyText()
    {
        var m = _match;
        var svc = Svc;
        if (m == null || m.Going || svc?.Definition == null) return null;
        int size = svc.Definition.TeamSize;
        var sb = new StringBuilder();
        sb.Append($"{size} vs {size}");
        if (m.Team.TryGetValue(Me, out var t)) sb.Append($" · your team: {svc.TeamName(t)}");
        sb.Append($"\nReady {m.Answers.Values.Count(a => a == 1)}/{m.Answers.Count}: ");
        bool first = true;
        foreach (var kv in m.Answers.OrderBy(k => k.Key))
        {
            if (!first) sb.Append(", ");
            first = false;
            string color = kv.Value == 1 ? "#7CFC7C" : kv.Value == 2 ? "#FF6B6B" : "#B0B0B0";
            string name = kv.Key == Me ? (GameInfo.Character?.CharacterName ?? "You") : Groups.Name(kv.Key);
            sb.Append($"<color={color}>{name}</color>");
        }
        return sb.ToString();
    }

    // ================================================================ members: everyone in the match, on every machine

    private static readonly Dictionary<uint, PvPTeam> NpcTeams = new(); // the authority's adventurers by net id
    private static float _arenaSince = -1f, _nextSync, _nextState, _nextStats;

    /// <summary>This machine runs it: anything here that is not another player's shell nor a copy of someone else's.</summary>
    private static bool IsLocal(CombatEntity e) => e != null && !PlayerShells.TryGetPlayer(e, out _) && !MobSync.IsCopy(e);

    /// <summary>The player whose machine runs this entity (their character's shell, a copy of their bot or adventurer).</summary>
    private static bool TryGetOwner(CombatEntity e, out byte player)
    {
        if (PlayerShells.TryGetPlayer(e, out player)) return true;
        return MobSync.TryGetCopyOwner(e, out player);
    }

    public static bool Contains(CombatEntity e)
    {
        if (!InMatch || e == null) return false;
        try { return Svc != null && Svc.Contains(e); } catch { return false; }
    }

    private static BattlegroundService.Member MemberOf(CombatEntity e)
    {
        try { var svc = Svc; return svc != null && e != null && svc.lookup.TryGetValue(e, out var m) ? m : null; }
        catch { return null; }
    }

    private static void AddMember(BattlegroundService svc, CombatEntity e, PvPTeam team, bool nameplate)
    {
        var m = new BattlegroundService.Member { Entity = e, Team = team, OriginalParty = false, Objective = -1, NextThink = Time.time };
        svc.members.Add(m);
        svc.lookup[e] = m;
        if (nameplate) try { UIEvents.Instance?.OnEnableNameplate(e); } catch { }
    }

    /// <summary>
    /// The game's member list holds what this machine sees of the match: its own player and bots (the game put them in),
    /// the other players' shells and bots, the authority's adventurers. Kept up as they come and go.
    /// </summary>
    private static void SyncMembers(BattlegroundService svc)
    {
        var members = svc.members;
        for (int i = members.Count - 1; i >= 0; i--)
        {
            var m = members[i];
            if (m != null && m.Entity != null && !m.Entity.WasCollected) continue;
            members.RemoveAt(i);
        }
        var stale = new List<CombatEntity>();
        foreach (var kv in svc.lookup) if (kv.Key == null || kv.Key.WasCollected) stale.Add(kv.Key);
        foreach (var k in stale) svc.lookup.Remove(k);

        var me = GameInfo.Player;
        var mine = MyTeam;
        foreach (var m in members)
        {
            var e = m.Entity;
            if (e == null) continue;
            if (IsLocal(e))
            {
                // the game seats the local player and their bots on blue: my team is the match's
                bool ownSide = me != null && (e.Pointer == me.Pointer || m.OriginalParty); // the player and the bots that came with them
                if (ownSide && m.Team != mine)
                {
                    m.Team = mine;
                    try { BattlegroundService.Teleport(e, svc.SpawnPosition(mine, members.IndexOf(m))); } catch { }
                }
                if (IAmAuthority && !ownSide && e.TryCast<MobCombatEntity>() is { } bot && !MobSync.IsOwned(bot)) MobSync.TrackLoose(bot, bot: true);
                if (IAmAuthority && !ownSide && MobSync.TryGetOwnedNetId(e, out var nid)) NpcTeams[nid] = m.Team;
            }
            else m.RespawnAt = float.MaxValue; // its own machine respawns it; here it only follows
        }

        foreach (var kv in _match.Team)
        {
            if (kv.Key == Me) continue;
            var shell = PlayerShells.Get(kv.Key);
            if (shell != null && !svc.lookup.ContainsKey(shell)) AddMember(svc, shell, kv.Value, nameplate: false);
            foreach (var bot in MobSync.PetCopiesOf(kv.Key))
                if (bot != null && PvPCombat.IsIndependentAdventurer(bot) && !svc.lookup.ContainsKey(bot)) AddMember(svc, bot, kv.Value, nameplate: true);
        }
        if (!IAmAuthority)
            foreach (var kv in NpcTeams)
            {
                var e = MobSync.EntityOf(kv.Key);
                if (e != null && !svc.lookup.ContainsKey(e)) AddMember(svc, e, kv.Value, nameplate: true);
            }
    }

    private static bool EveryoneArrived(BattlegroundService svc)
    {
        foreach (var p in _match.Team.Keys)
        {
            if (p == Me) continue;
            var shell = PlayerShells.Get(p);
            if (shell == null || !svc.lookup.ContainsKey(shell)) return false;
        }
        return true;
    }

    // ================================================================ per frame

    public static void Update()
    {
        var svc = Svc;
        if (svc == null || !CoopManager.Connected) return;
        HostMatchmaking();
        if (_match != null && !_match.Going)
        {
            if (IAmAuthority) TryFinish();
            if (svc.Phase != BattlegroundPhase.Invitation && _match != null && !_match.Going) _match = null; // expired or declined here
            return;
        }
        if (!InMatch) return;
        TrackOwnPosition();
        bool inArena = svc.Arena != null;
        if (!inArena)
        {
            if (_wasInArena && svc.Phase == BattlegroundPhase.Idle) End();
            return;
        }
        if (!_wasInArena) { _wasInArena = true; _arenaSince = Time.unscaledTime; _completedSeen = (PvPTeam)(-9); Plugin.L.LogInfo($"Battleground: in the arena, match {_match.Id:x8}, authority #{_match.Authority}"); }
        if (Time.unscaledTime >= _nextSync) { _nextSync = Time.unscaledTime + 0.25f; SyncMembers(svc); }
        if (Time.unscaledTime >= _nextStats) { _nextStats = Time.unscaledTime + StatsEvery; SendStats(svc); }
        if (IAmAuthority && Time.unscaledTime >= _nextState) { _nextState = Time.unscaledTime + StateEvery; SendState(svc); }
        WinsForMyTeam(svc);
    }

    private static void End()
    {
        Plugin.L.LogInfo("Battleground: match over here");
        _match = null;
        _wasInArena = false;
        NpcTeams.Clear();
        History.Clear();
    }

    /// <summary>
    /// The game counts a win only for blue (its player's team): for a player on red the match's result is put right
    /// (once, when it ends here).
    /// </summary>
    private static void WinsForMyTeam(BattlegroundService svc)
    {
        if (svc.Phase != BattlegroundPhase.Completed || _completedSeen == svc.WinningTeam) return;
        _completedSeen = svc.WinningTeam;
        if (MyTeam != PvPTeam.Red || svc.WinningTeam == PvPTeam.None) return;
        try
        {
            var data = svc.Data;
            if (data == null) return;
            if (svc.WinningTeam == PvPTeam.Red) { data.Losses--; data.Wins++; }
            else { data.Wins--; data.Losses++; }
            RPGBuilderJsonSaver.SaveCharacterData();
            Plugin.L.LogInfo($"Battleground: result for red counted ({svc.WinningTeam} won): wins {data.Wins}, losses {data.Losses}");
        }
        catch (Exception e) { Plugin.L.LogWarning("Battleground result: " + e.Message); }
    }

    // ---------------- authority: the match's state for everyone ----------------

    private static string _sentNodes = "";

    private static void SendState(BattlegroundService svc)
    {
        var arena = svc.Arena;
        var objs = arena.Objectives;
        float prep = Mathf.Max(0f, svc.deadline - Time.time);
        float elapsed = svc.Phase == BattlegroundPhase.Fighting || svc.Phase == BattlegroundPhase.Completed ? svc.Elapsed : 0f;
        var refs = new List<(CombatEntity e, int a, int d)>();
        foreach (var m in svc.members) if (m?.Entity != null && (m.Assaults > 0 || m.Defenses > 0)) refs.Add((m.Entity, m.Assaults, m.Defenses));
        uint id = _match.Id;
        Send(OpState, w =>
        {
            w.U32(id);
            w.U8((byte)svc.Phase); w.U8((byte)(int)svc.WinningTeam); w.F32(prep); w.F32(elapsed);
            w.I32(svc.BlueScore); w.I32(svc.RedScore);
            w.U8((byte)objs.Length);
            for (int i = 0; i < objs.Length; i++)
            {
                var st = objs[i].State;
                w.U8((byte)(int)st.Owner); w.U8((byte)(int)st.Assault); w.F32(st.SecondsRemaining);
            }
            w.U8((byte)NpcTeams.Count); foreach (var kv in NpcTeams) { w.U32(kv.Key); w.U8((byte)kv.Value); }
            w.U8((byte)Math.Min(refs.Count, 255));
            foreach (var x in refs.Take(255)) { WriteRef(w, x.e); w.U8((byte)Math.Min(x.a, 255)); w.U8((byte)Math.Min(x.d, 255)); }
        });
    }

    /// <summary>A change of the bases goes out at once, not with the next state.</summary>
    private static void StateNow() => _nextState = 0f;

    private static void ReceiveState(byte from, ref NetReader r)
    {
        uint id = r.U32();
        var svc = Svc;
        if (!InMatch || id != _match.Id || from != _match.Authority || svc?.Arena == null || IAmAuthority) return;
        var phase = (BattlegroundPhase)r.U8(); var winner = (PvPTeam)(sbyte)r.U8();
        float prep = r.F32(), elapsed = r.F32();
        int blue = r.I32(), red = r.I32();
        var objs = svc.Arena.Objectives;
        int n = r.U8();
        for (int i = 0; i < n; i++)
        {
            var owner = (PvPTeam)(sbyte)r.U8(); var assault = (PvPTeam)(sbyte)r.U8(); float secs = r.F32();
            if (i >= objs.Length) continue;
            var st = objs[i].State;
            st.Owner = owner; st.Assault = assault; st.SecondsRemaining = secs;
        }
        int nn = r.U8();
        for (int i = 0; i < nn; i++) NpcTeams[r.U32()] = (PvPTeam)r.U8();
        int na = r.U8();
        for (int i = 0; i < na; i++)
        {
            var e = ReadRef(ref r);
            int a = r.U8(), d = r.U8();
            var m = MemberOf(e);
            if (m != null) { m.Assaults = a; m.Defenses = d; }
        }
        svc.BlueScore = blue; svc.RedScore = red;
        int step = Math.Max(1, svc.Definition.ResourceMilestone);
        svc.blueMilestone = blue / step; svc.redMilestone = red / step; // so a takeover does not award them again

        if (svc.Phase == BattlegroundPhase.Preparing)
        {
            svc.deadline = Time.time + prep;
            if (phase == BattlegroundPhase.Fighting || phase == BattlegroundPhase.Completed) StartFighting(svc, elapsed);
        }
        if (svc.Phase == BattlegroundPhase.Fighting)
        {
            float started = Time.time - elapsed;
            if (Mathf.Abs(svc.battleStarted - started) > 0.5f) svc.battleStarted = started;
            if (phase == BattlegroundPhase.Completed)
            {
                Plugin.L.LogInfo($"Battleground: the match is over, {winner} won");
                try { svc.Complete(winner); } catch (Exception e) { Plugin.L.LogWarning("Battleground complete: " + e.Message); }
            }
        }
    }

    /// <summary>The fight begins (as the game begins it after the preparation): the gates open, the clock runs.</summary>
    private static void StartFighting(BattlegroundService svc, float elapsed)
    {
        svc.Phase = BattlegroundPhase.Fighting;
        svc.battleStarted = Time.time - elapsed;
        try { svc.Arena.SetGates(false); } catch { }
        foreach (var m in svc.members) if (m?.Entity != null && !PlayerShells.TryGetPlayer(m.Entity, out _)) try { UIEvents.Instance?.OnEnableNameplate(m.Entity); } catch { }
        Plugin.L.LogInfo("Battleground: the fight begins");
    }

    // ---------------- follower: what the game's Update would do for this machine's own characters ----------------

    private static void FollowerTick(BattlegroundService svc)
    {
        if (svc.Arena == null) return;
        var members = svc.members;
        for (int i = 0; i < members.Count; i++)
        {
            var m = members[i];
            var e = m?.Entity;
            if (e == null || !IsLocal(e)) continue;
            if (svc.Phase == BattlegroundPhase.Preparing)
            {
                var spawn = m.Team == PvPTeam.Blue ? svc.Arena.BlueSpawn : svc.Arena.RedSpawn;
                if (spawn != null && Vector3.Distance(e.transform.position, spawn.position) > 25f)
                    BattlegroundService.Teleport(e, svc.SpawnPosition(m.Team, i));
            }
            else if (svc.Phase == BattlegroundPhase.Fighting)
            {
                if (!e.Dead)
                {
                    if (!svc.Arena.PlayBounds.Contains(e.transform.position))
                    {
                        PvPCombat.StopCombat(e, true);
                        BattlegroundService.Teleport(e, svc.SpawnPosition(m.Team, i));
                    }
                }
                else if (m.RespawnAt > 0f && m.RespawnAt <= Time.time)
                {
                    svc.Respawn(m);
                    _nextStats = 0f; // the spawn protection goes out at once
                }
            }
        }
        if (svc.Phase != BattlegroundPhase.Fighting) return;
        FollowerCaptures(svc);
        foreach (var o in svc.Arena.Objectives) // the contested countdown runs on between the authority's states
            if (o.State.Assault != PvPTeam.None) o.State.SecondsRemaining = Mathf.Max(0f, o.State.SecondsRemaining - Time.deltaTime);
    }

    /// <summary>
    /// The game's capture channel for this machine's own characters (TickCaptures): interrupted the same way; when it
    /// completes, the authority assaults the base (the game's BeginAssault there).
    /// </summary>
    private static void FollowerCaptures(BattlegroundService svc)
    {
        var caps = svc.captures;
        for (int i = caps.Count - 1; i >= 0; i--)
        {
            var c = caps[i];
            var e = c.Entity;
            var m = MemberOf(e);
            bool ok = e != null && !e.Dead && m != null && c.Objective != null && c.Objective.State.CanAssault(m.Team)
                      && (e.transform.position - c.StartPosition).sqrMagnitude <= 0.16f && !(m.LastHitAt > c.StartTime)
                      && !e.IsStunned() && !e.IsSleeping() && !e.Knockedback && !e.Casting && !e.Channeling && !e.isMounted;
            if (ok && c.EndTime > Time.time) continue;
            caps.RemoveAt(i);
            if (!ok) continue;
            int node = -1;
            var objs = svc.Arena.Objectives;
            for (int k = 0; k < objs.Length; k++) if (objs[k] != null && objs[k].Pointer == c.Objective.Pointer) node = k;
            if (node < 0) continue;
            var team = m.Team;
            uint id = _match.Id;
            Send(OpAssault, w => { w.U32(id); w.U8((byte)node); w.U8((byte)team); WriteRef(w, e); });
            Plugin.L.LogInfo($"Battleground: capture of base {node} done, told the authority");
        }
    }

    /// <summary>Authority: a capture finished elsewhere, the game's way (TickCaptures' end).</summary>
    private static void ReceiveAssault(byte from, ref NetReader r)
    {
        uint id = r.U32(); int node = r.U8(); var team = (PvPTeam)r.U8(); var e = ReadRef(ref r);
        var svc = Svc;
        if (!InMatch || id != _match.Id || !IAmAuthority || svc?.Arena == null || svc.Phase != BattlegroundPhase.Fighting) return;
        var objs = svc.Arena.Objectives;
        if (node >= objs.Length) return;
        var o = objs[node];
        var m = MemberOf(e);
        if (!o.State.CanAssault(team)) return;
        var before = o.State.Owner;
        if (!o.State.BeginAssault(team, svc.Definition.CaptureSeconds)) return;
        string verb;
        if (before != team) { if (m != null) m.Assaults++; verb = " assaulted "; }
        else { if (m != null) m.Defenses++; verb = " defended "; }
        PvPCombat.Alert(PvPCombat.Name(e) + verb + o.DisplayName + "!");
        StateNow();
    }

    // ---------------- stats each machine owns: its own characters' kills, deaths, honor, damage, healing ----------------

    private static void SendStats(BattlegroundService svc)
    {
        var mine = new List<BattlegroundService.Member>();
        foreach (var m in svc.members) if (m?.Entity != null && IsLocal(m.Entity)) mine.Add(m);
        if (mine.Count == 0) return;
        uint id = _match.Id;
        Send(OpStats, w =>
        {
            w.U32(id); w.U8((byte)Math.Min(mine.Count, 255));
            foreach (var m in mine.Take(255))
            {
                WriteRef(w, m.Entity);
                w.U16((ushort)m.Kills); w.U16((ushort)m.Deaths); w.I32(m.Honor); w.F32(m.DamageDone); w.F32(m.HealingDone);
                w.F32(Mathf.Max(0f, m.ProtectedUntil - Time.time));
            }
        });
    }

    private static void ReceiveStats(ref NetReader r)
    {
        uint id = r.U32();
        int n = r.U8();
        bool ours = InMatch && id == _match.Id;
        for (int i = 0; i < n; i++)
        {
            var e = ReadRef(ref r);
            int k = r.U16(), d = r.U16(), h = r.I32(); float dmg = r.F32(), heal = r.F32(), prot = r.F32();
            if (!ours || IsLocal(e)) continue;
            var m = MemberOf(e);
            if (m == null) continue;
            m.Kills = k; m.Deaths = d; m.Honor = h; m.DamageDone = dmg; m.HealingDone = heal;
            m.ProtectedUntil = prot > 0f ? Time.time + prot : 0f;
        }
    }

    // ---------------- kills and honor earned on another machine ----------------

    private static BattlegroundService.Member _victim, _killer;
    private static int _deathsBefore;

    /// <summary>Owner of the killer: the kill counts here, with its honor (the game's AddHonor for its own character).</summary>
    private static void ReceiveKill(ref NetReader r)
    {
        uint id = r.U32(); var e = ReadRef(ref r);
        var svc = Svc;
        if (!InMatch || id != _match.Id || !IsLocal(e) || svc == null) return;
        var m = MemberOf(e);
        if (m == null) return;
        m.Kills++;
        svc.AddHonor(m, svc.Definition.HonorPerKill);
        if (e.Pointer == GameInfo.Player?.Pointer) try { svc.Data.HonorableKills++; } catch { }
        Plugin.L.LogInfo($"Battleground: a kill for {PvPCombat.Name(e)} (+{svc.Definition.HonorPerKill} honor)");
        _nextStats = 0f;
    }

    private static void ReceiveHonor(ref NetReader r)
    {
        uint id = r.U32(); var e = ReadRef(ref r); int amount = r.I32();
        var svc = Svc;
        if (!InMatch || id != _match.Id || !IsLocal(e) || svc == null) return;
        var m = MemberOf(e);
        if (m != null) svc.AddHonor(m, amount);
    }

    // ================================================================ PvP hits with lag compensation

    /// <summary>Where this player was, by this machine's clock (the one the others see it on), for the last two seconds.</summary>
    private static readonly List<(double t, Vector3 p, bool dead)> History = new();
    private static double _lifeStart;
    private static Vector3 _lastPos;
    private const double Keep = 2.0, MaxRewind = 1.0;
    private const float MaxReach = 45f;

    private static void TrackOwnPosition()
    {
        var me = GameInfo.Player;
        if (me == null) return;
        double now = CoopManager.Now;
        var p = me.transform.position;
        bool dead = me.Dead;
        if (History.Count > 0)
        {
            var last = History[History.Count - 1];
            if ((last.dead && !dead) || (p - _lastPos).sqrMagnitude > 15f * 15f) _lifeStart = now; // respawned or teleported: a new life
            if (last.dead && !dead) RefreshHealth(me);
        }
        _lastPos = p;
        History.Add((now, p, dead));
        while (History.Count > 0 && now - History[0].t > Keep) History.RemoveAt(0);
    }

    /// <summary>Up from the battleground's defeat: the health bars redraw on the game's stat event, which its respawn does not raise.</summary>
    private static void RefreshHealth(CombatEntity me)
    {
        try
        {
            var stats = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetStats();
            int hs = MobSync.HealthStat;
            if (stats.ContainsKey(hs)) CombatEvents.Instance?.OnStatValueChanged(me, stats[hs], CombatUtilities.GetCurrentStatValue(me, hs), CombatUtilities.GetMaxStatValue(me, hs));
        }
        catch { }
    }

    private static bool WhereAt(double t, out Vector3 p, out bool dead)
    {
        p = default; dead = false;
        if (History.Count == 0) return false;
        for (int i = History.Count - 1; i >= 0; i--)
            if (History[i].t <= t)
            {
                var a = History[i];
                if (i + 1 < History.Count)
                {
                    var b = History[i + 1];
                    float k = (float)((t - a.t) / Math.Max(1e-6, b.t - a.t));
                    p = Vector3.Lerp(a.p, b.p, k);
                }
                else p = a.p;
                dead = a.dead;
                return true;
            }
        p = History[0].p; dead = History[0].dead;
        return true;
    }

    /// <summary>
    /// A hit of the local player on an enemy player's shell: sent to them with the moment of them it was aimed at (their
    /// clock, as their pose shown here) and where the attacker stood. True = shown here as a prediction.
    /// </summary>
    public static bool ForwardHit(byte victim, MobCombatEntity shell, CombatCalculations.DamageResult dr, int stat)
    {
        var svc = Svc;
        var me = GameInfo.Player;
        if (!InMatch || svc == null || svc.Phase != BattlegroundPhase.Fighting || me == null || dr.caster == null || dr.caster.Pointer != me.Pointer) return false;
        if (!svc.Contains(shell) || !PvPCombat.CanAffect(me, shell, true)) return false;
        double seen = Puppets.ViewTime(victim, CoopManager.Now);
        var at = me.transform.position;
        uint id = _match.Id;
        Send(OpHit, w =>
        {
            w.U32(id); w.U8(victim); w.F32(dr.DamageAmount); w.I32(stat); w.Bool(dr.IsAutoAttack); w.Bool(dr.IsDamageOverTime);
            w.F64(double.IsNaN(seen) ? -1 : seen); w.F32(at.x); w.F32(at.y); w.F32(at.z);
        });
        return true;
    }

    private static int _hitsTaken, _hitsRefused;

    private static void ReceiveHit(byte from, ref NetReader r)
    {
        uint id = r.U32(); byte victim = r.U8(); float amount = r.F32(); int stat = r.I32(); bool auto = r.Bool(), dot = r.Bool();
        double seen = r.F64(); var at = new Vector3(r.F32(), r.F32(), r.F32());
        if (!InMatch || id != _match.Id || victim != Me) return;
        var me = GameInfo.Player;
        var shell = PlayerShells.Get(from);
        if (me == null || shell == null || me.Dead) return;
        double now = CoopManager.Now;
        string refuse = null;
        if (seen >= 0)
        {
            if (seen < _lifeStart - 0.05) refuse = "aimed at a former life";
            else if (now - seen > MaxRewind) refuse = $"too old ({(now - seen) * 1000:0} ms)";
            else if (WhereAt(seen, out var p, out var wasDead))
            {
                if (wasDead) refuse = "dead then";
                else if (Vector3.Distance(p, at) > MaxReach) refuse = $"out of reach then ({Vector3.Distance(p, at):0} m)";
            }
        }
        if (refuse != null)
        {
            _hitsRefused++;
            Plugin.L.LogInfo($"Battleground: hit {amount:0} from #{from} refused: {refuse}");
            return;
        }
        _hitsTaken++;
        me.TakeDamage(new CombatCalculations.DamageResult { caster = shell, target = me, DamageAmount = amount, IsAutoAttack = auto, IsDamageOverTime = dot, DamageActionType = "" }, null, stat);
    }

    // ================================================================ the authority leaves: the next one takes over

    private static void HandOver()
    {
        var m = _match;
        var s = S;
        if (m == null || s == null || !IAmAuthority) return;
        string place = Dungeons.Place;
        byte? next = null;
        foreach (var p in m.Team.Keys.OrderBy(p => p))
            if (p != Me && s.Players.TryGetValue(p, out var info) && info.Scene == place) { next = p; break; }
        if (next == null) return;
        var npcs = NpcTeams.Keys.ToList();
        uint id = m.Id;
        byte to = next.Value;
        Send(OpLead, w => { w.U32(id); w.U8(to); w.U16((ushort)npcs.Count); foreach (var n in npcs) w.U32(n); });
        m.Authority = to;
        Plugin.L.LogInfo($"Battleground: leaving, match {id:x8} and {npcs.Count} adventurers pass to #{to}");
    }

    private static void NewAuthority(byte to, List<uint> npcs)
    {
        var m = _match;
        if (m == null) return;
        m.Authority = to;
        if (!m.Going) { if (to == Me) TryFinish(); return; }
        int adopted = 0;
        foreach (var n in npcs)
        {
            if (to == Me) { if (MobSync.Adopt(n) != null) adopted++; }
            else MobSync.Reassign(n, to);
        }
        if (to == Me)
        {
            Plugin.L.LogInfo($"Battleground: this machine runs the match now ({adopted}/{npcs.Count} adventurers taken over)");
            Hud.Notify("You now host this battleground");
            StateNow();
        }
    }

    /// <summary>A player left the session: if they ran the match, the next one takes it over (before their copies go).</summary>
    public static void OnPlayerLeft(byte player)
    {
        var m = _match;
        if (m == null || !m.Team.ContainsKey(player)) return;
        m.Team.Remove(player);
        m.Answers.Remove(player);
        m.Bots.Remove(player);
        if (m.Authority != player) return;
        var s = S;
        string place = Dungeons.Place;
        byte next = Me;
        foreach (var p in m.Team.Keys.OrderBy(p => p))
            if (p == Me || (s != null && s.Players.TryGetValue(p, out var info) && (!m.Going || info.Scene == place))) { next = p; break; }
        Plugin.L.LogInfo($"Battleground: the match's authority #{player} left, #{next} takes over");
        NewAuthority(next, NpcTeams.Keys.ToList());
    }

    // ================================================================ messages

    private static void WriteRef(NetWriter w, CombatEntity e)
    {
        var me = GameInfo.Player;
        if (e != null && me != null && e.Pointer == me.Pointer) { w.U8(0); w.U32(Me); return; }
        if (PlayerShells.TryGetPlayer(e, out var p)) { w.U8(0); w.U32(p); return; }
        w.U8(1); w.U32(MobSync.SyncedId(e));
    }

    private static CombatEntity ReadRef(ref NetReader r)
    {
        byte kind = r.U8(); uint id = r.U32();
        if (kind == 0) return (byte)id == Me ? GameInfo.Player : PlayerShells.Get((byte)id);
        return MobSync.EntityOf(id);
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8();
        var s = S;
        if (s == null) return;
        switch (op)
        {
            case OpQueue:
            {
                int def = r.U8(); int n = r.U8();
                var unit = new List<byte>(); for (int i = 0; i < n; i++) unit.Add(r.U8());
                if (s.IsHost) HostQueue(from, def, unit);
                MemberQueue(from, def, unit);
                break;
            }
            case OpUnqueue:
            {
                int n = r.U8(); var unit = new List<byte>(); for (int i = 0; i < n; i++) unit.Add(r.U8());
                if (s.IsHost) HostUnqueue(unit);
                if (from != Me && unit.Contains(Me) && Svc?.Phase == BattlegroundPhase.Queued) // the leader left the queue
                {
                    _fromNet = true;
                    try { Svc.Cancel("Your group left the battleground queue"); } finally { _fromNet = false; }
                }
                break;
            }
            case OpMatch:
            {
                uint id = r.U32(); int def = r.U8(); byte authority = r.U8(); float seconds = r.F32();
                int n = r.U8(); var team = new Dictionary<byte, PvPTeam>();
                for (int i = 0; i < n; i++) { byte p = r.U8(); team[p] = (PvPTeam)r.U8(); }
                if (from == ProtocolInfo.HostId) Invited(id, def, authority, seconds, team);
                break;
            }
            case OpParty:
            {
                uint id = r.U32(); int n = r.U8(); var ids = new List<int>(); for (int i = 0; i < n; i++) ids.Add(r.I32());
                if (_match != null && _match.Id == id) _match.Bots[from] = ids;
                break;
            }
            case OpAnswer:
            {
                uint id = r.U32(); byte a = r.U8();
                if (_match == null || _match.Id != id || _match.Going || !_match.Answers.ContainsKey(from)) break;
                _match.Answers[from] = a;
                if (IAmAuthority) { BroadcastReady(); if (a == 1) Hud.Notify($"{Groups.Name(from)} is ready"); TryFinish(); }
                break;
            }
            case OpReady:
            {
                uint id = r.U32(); int n = r.U8();
                for (int i = 0; i < n; i++) { byte p = r.U8(); byte a = r.U8(); if (_match != null && _match.Id == id && !_match.Going && p != Me) _match.Answers[p] = a; }
                break;
            }
            case OpGo:
            {
                uint id = r.U32(); byte authority = r.U8(); int n = r.U8();
                var team = new Dictionary<byte, PvPTeam>(); var bots = new Dictionary<byte, List<int>>();
                for (int i = 0; i < n; i++)
                {
                    byte p = r.U8(); team[p] = (PvPTeam)r.U8();
                    int nb = r.U8(); var list = new List<int>(); for (int k = 0; k < nb; k++) list.Add(r.I32());
                    bots[p] = list;
                }
                if (_match == null || _match.Id != id || _match.Going) break;
                if (!team.ContainsKey(Me)) { _match = null; break; }
                _match.Authority = authority;
                _match.Team.Clear(); foreach (var kv in team) _match.Team[kv.Key] = kv.Value;
                _match.Bots.Clear(); foreach (var kv in bots) _match.Bots[kv.Key] = kv.Value;
                Go();
                break;
            }
            case OpCancel:
            {
                uint id = r.U32(); string reason = r.Str();
                if (_match != null && _match.Id == id && !_match.Going) CancelHere(reason);
                break;
            }
            case OpLead:
            {
                uint id = r.U32(); byte to = r.U8(); int n = r.U16();
                var npcs = new List<uint>(); for (int i = 0; i < n; i++) npcs.Add(r.U32());
                if (_match != null && _match.Id == id) NewAuthority(to, npcs);
                break;
            }
            case OpState: ReceiveState(from, ref r); break;
            case OpAssault: ReceiveAssault(from, ref r); break;
            case OpKill:
            {
                byte to = r.U8();
                if (to == Me) ReceiveKill(ref r);
                break;
            }
            case OpHonor:
            {
                byte to = r.U8();
                if (to == Me) ReceiveHonor(ref r);
                break;
            }
            case OpStats: ReceiveStats(ref r); break;
            case OpHit: ReceiveHit(from, ref r); break;
            case OpAlert:
            {
                uint id = r.U32(); string text = r.Str();
                if (InMatch && id == _match.Id && from == _match.Authority)
                {
                    _fromNet = true;
                    try { PvPCombat.Alert(text); } finally { _fromNet = false; }
                }
                break;
            }
        }
    }

    // ================================================================ dev

    public static string Describe()
    {
        var svc = Svc;
        if (svc == null) return "no battleground service";
        var sb = new StringBuilder();
        sb.Append($"phase {svc.Phase} '{svc.Status}'");
        if (svc.Definition != null) sb.Append($" | {svc.Definition.DisplayName} size {svc.Definition.TeamSize}");
        var m = _match;
        if (m != null)
        {
            sb.Append($" | match {m.Id:x8} going={m.Going} authority=#{m.Authority}{(IAmAuthority ? " (me)" : "")} myTeam={MyTeam}");
            sb.Append(" | teams " + string.Join(",", m.Team.Select(kv => $"#{kv.Key}:{kv.Value}")));
            if (!m.Going) sb.Append(" | answers " + string.Join(",", m.Answers.Select(kv => $"#{kv.Key}:{kv.Value}")));
            sb.Append(" | bots " + string.Join(",", m.Bots.Select(kv => $"#{kv.Key}:{kv.Value.Count}")));
        }
        if (svc.Arena != null)
        {
            sb.Append($" | score {svc.BlueScore}:{svc.RedScore} elapsed {svc.Elapsed:0} prep {Mathf.Max(0, svc.deadline - Time.time):0}");
            var objs = svc.Arena.Objectives;
            sb.Append(" | bases");
            for (int i = 0; i < objs.Length; i++) sb.Append($" {i}:{objs[i].State.Owner}{(objs[i].State.Assault != PvPTeam.None ? "<" + objs[i].State.Assault + $" {objs[i].State.SecondsRemaining:0}s" : "")}");
            int blue = 0, red = 0, shells = 0, copies = 0;
            foreach (var x in svc.members)
            {
                if (x == null) continue;
                if (x.Team == PvPTeam.Blue) blue++; else red++;
                if (PlayerShells.TryGetPlayer(x.Entity, out _)) shells++; else if (MobSync.IsCopy(x.Entity)) copies++;
            }
            sb.Append($" | members blue {blue} red {red} (shells {shells}, copies {copies}), npcs {NpcTeams.Count}, captures {svc.captures.Count}");
            sb.Append($" | hits taken {_hitsTaken} refused {_hitsRefused}");
        }
        if (Gatherings.Count > 0) sb.Append(" | host gathering " + string.Join(";", Gatherings.Select(g => $"#{g.Def} {g.Units.Sum(u => u.Count)} for {Time.unscaledTime - g.Since:0}s")));
        return sb.ToString();
    }

    public static string DevScore()
    {
        var svc = Svc;
        if (svc == null) return "none";
        var sb = new StringBuilder();
        foreach (var m in svc.members)
        {
            if (m?.Entity == null) continue;
            string who = PvPCombat.Name(m.Entity);
            string kind = IsLocal(m.Entity) ? "own" : PlayerShells.TryGetPlayer(m.Entity, out var p) ? $"shell#{p}" : MobSync.IsCopy(m.Entity) ? "copy" : "?";
            sb.Append($"{who} [{m.Team} {kind}{(m.Entity.Dead ? " dead" : "")}] K{m.Kills} D{m.Deaths} A{m.Assaults} Df{m.Defenses} H{m.Honor} dmg {m.DamageDone:0} heal {m.HealingDone:0}; ");
        }
        return sb.ToString();
    }

    /// <summary>Dev: queue for battleground #index (as the finder's button does).</summary>
    public static string DevQueue(int index)
    {
        var svc = Svc; var d = DefOf(index);
        if (svc == null || d == null) return "no such battleground";
        bool ok = svc.Queue(d);
        return ok ? $"queued for {d.DisplayName}" : "not queued: " + svc.Status;
    }

    public static string DevList()
    {
        var all = Svc?.Settings?.Battlegrounds;
        if (all == null) return "none";
        var sb = new StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            var d = all[i];
            if (d == null) continue;
            bool loadable = false;
            try { loadable = BLINK.RPGBuilder.Dungeons.DungeonFinderService.CanLoadScene(d.Scene?.entryName); } catch { }
            sb.Append($"#{i} '{d.DisplayName}' size {d.TeamSize} lvl {d.MinimumLevel}+ scene {d.Scene?.entryName} loadable={loadable}; ");
        }
        try { var tab = UnityEngine.Object.FindObjectOfType<BattlegroundFinderTab>(true); sb.Append($"finder tab: {(tab == null ? "none" : "enabled=" + tab.EnableBattlegroundTab + " button=" + (tab.BattlegroundsTab != null && tab.BattlegroundsTab.gameObject.activeInHierarchy))}"); } catch (Exception e) { sb.Append(e.Message); }
        return sb.ToString();
    }

    /// <summary>Dev: start a capture of base #i (teleport next to it first).</summary>
    public static string DevCapture(int i, bool go)
    {
        var svc = Svc; var me = GameInfo.Player;
        if (svc?.Arena == null || me == null) return "not in a battleground";
        var objs = svc.Arena.Objectives;
        if (i < 0 || i >= objs.Length) return "no such base";
        if (go) { GameInfo.TeleportPlayer(objs[i].transform.position + Vector3.right * 2f); return $"at {objs[i].DisplayName}"; }
        if (svc.BeginCapture(me, objs[i])) return $"capturing {objs[i].DisplayName}";
        var m = MemberOf(me);
        return $"could not start the capture: member={(m != null ? m.Team.ToString() : "no")} canAssault={(m != null && objs[i].State.CanAssault(m.Team))} dist={Vector3.Distance(me.transform.position, objs[i].transform.position):0.0} stunned={me.IsStunned()} casting={me.Casting} channeling={me.Channeling} mounted={me.isMounted} dead={me.Dead} phase={svc.Phase}";
    }

    /// <summary>Dev: the local player hits the nearest enemy player through the game's damage path.</summary>
    public static string DevHit(float amount)
    {
        var svc = Svc; var me = GameInfo.Player;
        if (svc == null || me == null || !InMatch) return "not in a match";
        MobCombatEntity best = null; float bd = float.MaxValue;
        foreach (var kv in _match.Team)
        {
            if (kv.Key == Me || kv.Value == MyTeam) continue;
            var sh = PlayerShells.Get(kv.Key);
            if (sh == null) continue;
            float d = Vector3.Distance(sh.transform.position, me.transform.position);
            if (d < bd) { bd = d; best = sh; }
        }
        if (best == null) return "no enemy player here";
        best.TakeDamage(new CombatCalculations.DamageResult { caster = me, target = best, DamageAmount = amount, DamageActionType = "" }, null, MobSync.HealthStat);
        return $"hit {PvPCombat.Name(best)} at {bd:0} m";
    }

    public static string DevGotoEnemy()
    {
        var me = GameInfo.Player;
        if (me == null || !InMatch) return "not in a match";
        foreach (var kv in _match.Team)
        {
            if (kv.Key == Me || kv.Value == MyTeam) continue;
            if (Puppets.TryGetPosition(kv.Key, out var p)) { GameInfo.TeleportPlayer(p + Vector3.forward * 3f); return $"next to #{kv.Key}"; }
        }
        return "no enemy player here";
    }

    // ================================================================ the game's service, patched

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>In a group only its leader queues (for everyone in it).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Queue))]
        private static bool QueueStart(ref bool __result)
        {
            if (_fromNet || !Coop) return true;
            var g = S.MyGroup;
            if (g == null || g.Leader == Me) return true;
            Hud.Notify("Only your group leader can queue for a battleground");
            __result = false;
            return false;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Queue))]
        private static void Queued(BattlegroundDefinition __0, bool __result)
        {
            if (!__result || _fromNet || !Coop) return;
            try { OnQueued(__0); } catch (Exception e) { Plugin.L.LogWarning("Battleground queue: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Cancel))]
        private static void CancelStart(BattlegroundService __instance, out BattlegroundPhase __state) => __state = __instance.Phase;

        /// <summary>Leaving the queue is told to the host; declining (or letting it expire) is an answer to the ready check.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Cancel))]
        private static void Cancelled(BattlegroundService __instance, BattlegroundPhase __state)
        {
            if (_fromNet || !Coop || __instance.Phase != BattlegroundPhase.Idle) return;
            try
            {
                if (__state == BattlegroundPhase.Queued) OnUnqueued();
                else if (__state == BattlegroundPhase.Invitation && _match != null && !_match.Going) Answer(false);
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground cancel: " + e.Message); }
        }

        /// <summary>
        /// The game's own queue would find a match for this player alone: in co-op the host matches whoever queued
        /// together, so the queue only waits. The match running: a follower does not run the game's battle logic.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), "Update")]
        private static bool ServiceUpdate(BattlegroundService __instance)
        {
            try
            {
                if (__instance.Phase == BattlegroundPhase.Queued && Coop) { __instance.nextQueueCheck = Time.time + 5f; return true; }
                if (!InMatch || __instance.Arena == null) return true;
                if (__instance.Phase != BattlegroundPhase.Preparing && __instance.Phase != BattlegroundPhase.Fighting) return true;
                if (!IAmAuthority) { FollowerTick(__instance); return false; }
                // authority: the fight begins once everyone is in (or after a while), whatever the count of the game's members
                if (__instance.Phase == BattlegroundPhase.Preparing && !__instance.assembling && __instance.spawningID < 0 && __instance.deadline - Time.time <= 0f)
                {
                    bool all = EveryoneArrived(__instance);
                    if (!all && Time.unscaledTime - _arenaSince < ArrivalWait) { __instance.deadline = Time.time + 1f; return true; }
                    if (__instance.members.Count != __instance.Definition.TeamSize * 2)
                    {
                        StartFighting(__instance, 0f);
                        PvPCombat.Alert($"The battle for {Localize.Source(__instance.Definition.DisplayName)} has begun");
                        StateNow();
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground update: " + e.Message); }
            return true;
        }

        /// <summary>Accepting is a ready check: the authority sends everyone off when all answered.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Accept))]
        private static bool AcceptClicked(ref bool __result)
        {
            if (_going || _match == null || _match.Going) return true;
            if (_match.Answers.TryGetValue(Me, out var a) && a == 1) { __result = false; return false; }
            Answer(true);
            __result = false;
            return false;
        }

        /// <summary>
        /// The game's team plan with players in it. Authority: its own team blue with its players and their bots,
        /// the other players and their bots red, adventurers for the rest (the game's candidates and balancing).
        /// Followers: nothing to plan, their bots that go along are the ones the authority allowed.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), "PlanTeams")]
        private static bool Plan(BattlegroundService __instance, ref bool __result)
        {
            if (_match == null || !Coop) return true;
            try
            {
                __instance.plannedBlue.Clear(); __instance.plannedRed.Clear(); __instance.originalParty.Clear();
                var allowed = _match.Bots.TryGetValue(Me, out var b) ? b : new List<int>();
                foreach (var id in allowed) __instance.originalParty.Add(id);
                if (!IAmAuthority) { __result = true; return false; }
                __result = PlanWithPlayers(__instance, allowed);
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground plan: " + e.Message); __result = false; }
            return false;
        }

        /// <summary>A follower's arrival: the game would leave if its own count of members is not the full teams (the others come on their own).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Leave))]
        private static bool LeaveStart(BattlegroundService __instance)
        {
            try
            {
                if (!InMatch) return true;
                if (__instance.assembling && __instance.status == Localize.UI("session.the_teams_could_not_be_filled_7c0670a7"))
                {
                    __instance.deadline = Time.time + __instance.Definition.PreparationSeconds;
                    __instance.status = Localize.UI("session.prepare_for_battle_capture_and_defend_the_five_bases_31f1517e");
                    return false;
                }
                HandOver();
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground leave: " + e.Message); }
            return true;
        }

        /// <summary>A death here: the kill is counted where the killer lives.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Defeat))]
        private static bool DefeatStart(BattlegroundService __instance, CombatEntity __0, ref bool __result)
        {
            _victim = null; _killer = null;
            if (!InMatch) return true;
            try
            {
                if (__0 != null && !IsLocal(__0) && __instance.lookup.ContainsKey(__0))
                {
                    // someone else's character: their machine decides; here it only looks defeated
                    if (!__0.Dead) __0.EnterBattlegroundDefeat();
                    __result = true;
                    return false;
                }
                _inDefeat = true;
                if (__0 == null || !__instance.lookup.TryGetValue(__0, out var v)) return true;
                _victim = v; _deathsBefore = v.Deaths;
                if (v.LastAttacker != null && Time.time - v.LastHitAt <= 10f && __instance.lookup.TryGetValue(v.LastAttacker, out var k) && k.Team != v.Team) _killer = k;
            }
            catch { }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattlegroundService), nameof(BattlegroundService.Defeat))]
        private static void DefeatEnd()
        {
            _inDefeat = false;
            try
            {
                if (_victim == null || _killer == null || _victim.Deaths == _deathsBefore) return;
                var killer = _killer.Entity;
                if (killer == null || IsLocal(killer) || !TryGetOwner(killer, out var owner)) return;
                uint id = _match.Id;
                Send(OpKill, w => { w.U8(owner); w.U32(id); WriteRef(w, killer); });
                _nextStats = 0f;
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground kill: " + e.Message); }
            finally { _victim = null; _killer = null; }
        }

        /// <summary>
        /// Honor goes to this machine's own characters the game's way. Someone else's: their machine gives it (kills are
        /// told to them; the authority tells them their share of the resource milestones; the match's end is everyone's own).
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), "AddHonor")]
        private static bool HonorFor(BattlegroundService __instance, BattlegroundService.Member __0, int __1)
        {
            if (!InMatch || __0?.Entity == null || IsLocal(__0.Entity)) return true;
            try
            {
                if (IAmAuthority && !_inDefeat && __instance.Phase == BattlegroundPhase.Fighting && __1 > 0 && TryGetOwner(__0.Entity, out var owner))
                {
                    __0.Honor += __1;
                    uint id = _match.Id; var e = __0.Entity;
                    Send(OpHonor, w => { w.U8(owner); w.U32(id); WriteRef(w, e); w.I32(__1); });
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Battleground honor: " + e.Message); }
            return false;
        }

        /// <summary>A shell's or copy's look of defeat ends with its own machine's word, not by this machine's respawn.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), "Respawn")]
        private static bool NoForeignRespawn(BattlegroundService.Member __0) => !InMatch || __0?.Entity == null || IsLocal(__0.Entity);

        /// <summary>The authority's alerts (bases assaulted, captured, the battle begun) are everyone's.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PvPCombat), nameof(PvPCombat.Alert))]
        private static void AlertAll(string __0)
        {
            if (_fromNet || !InMatch || !IAmAuthority || string.IsNullOrEmpty(__0)) return;
            var svc = Svc;
            if (svc == null || svc.Phase != BattlegroundPhase.Fighting) return;
            uint id = _match.Id;
            Send(OpAlert, w => { w.U32(id); w.Str(__0); });
        }

        /// <summary>The base's prompt: "Defend" for the local player's own team (the game assumes blue).</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattlegroundObjective), nameof(BattlegroundObjective.getInteractableName))]
        private static void PromptForMyTeam(BattlegroundObjective __instance, ref string __result)
        {
            if (!InMatch || MyTeam == PvPTeam.Blue) return;
            try
            {
                string name = Localize.Source(__instance.DisplayName);
                __result = __instance.State.Owner == MyTeam && __instance.State.IsContested
                    ? Localize.UIFormat("session.defend_0_e89f595a", name) : Localize.UIFormat("session.assault_0_04fd6178", name);
            }
            catch { }
        }

        /// <summary>The invitation window shows the ready check.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BattlegroundInvitation), "Update")]
        private static void ReadyCheck(BattlegroundInvitation __instance)
        {
            try
            {
                var text = ReadyText();
                if (text != null && __instance.Detail != null) __instance.Detail.text = text;
            }
            catch { }
        }
    }

    /// <summary>The authority's plan (PlanTeams with players): see <see cref="Patches"/>.</summary>
    private static bool PlanWithPlayers(BattlegroundService svc, List<int> myBots)
    {
        var m = _match;
        var def = svc.Definition;
        int size = def.TeamSize;
        int blueUsed = 0, redUsed = 0;
        var taken = new HashSet<int>();
        foreach (var kv in m.Team)
        {
            int bots = m.Bots.TryGetValue(kv.Key, out var b) ? b.Count : 0;
            if (kv.Value == PvPTeam.Blue) blueUsed += 1 + bots; else redUsed += 1 + bots;
        }
        foreach (var kv in m.Bots) foreach (var id in kv.Value) taken.Add(id);
        foreach (var id in myBots) svc.plannedBlue.Add(id); // the game registers them with me (they are in my party)
        int blueNeed = size - blueUsed, redNeed = size - redUsed;
        if (blueNeed < 0 || redNeed < 0) return false;
        var candidates = new List<RPGNpc>();
        // every player's own bots stay theirs: a same adventurer in this machine's world does not come twice
        var all = new HashSet<int>(taken);
        foreach (var kv in m.Bots) foreach (var id in kv.Value) all.Add(id);
        foreach (var n in svc.Candidates()) if (n != null && !all.Contains(n.ID)) candidates.Add(n);
        if (candidates.Count < blueNeed + redNeed)
        {
            svc.status = $"Waiting for adventurers ({candidates.Count} of {blueNeed + redNeed} needed)";
            return false;
        }
        var world = BLINK.RPGBuilder.AI.AdventurerWorldSimulation.Instance;
        int LevelOf(RPGNpc n) { try { var p = world?.Engine?.Progress(n); return p != null ? p.level : n.MinLevel; } catch { return n.MinLevel; } }
        bool Healer(RPGNpc n) { try { return BLINK.RPGBuilder.AI.AdventurerSpecialization.For(n)?.Role == BLINK.RPGBuilder.AI.AdventurerRole.Healer; } catch { return false; } }
        int blueCount = blueUsed, redCount = redUsed, blueLevel = 0, redLevel = 0, blueHealers = 0, redHealers = 0;
        foreach (var n in candidates.Take(blueNeed + redNeed).OrderByDescending(LevelOf))
        {
            int lvl = LevelOf(n); bool healer = Healer(n);
            bool toBlue;
            if (blueCount >= size) toBlue = false;
            else if (redCount >= size) toBlue = true;
            else if (healer && blueHealers != redHealers) toBlue = blueHealers < redHealers;
            else if (blueLevel < redLevel) toBlue = true;
            else if (blueLevel != redLevel) toBlue = false;
            else toBlue = blueCount <= redCount;
            if (toBlue) { svc.plannedBlue.Add(n.ID); blueCount++; blueLevel += lvl; if (healer) blueHealers++; }
            else { svc.plannedRed.Add(n.ID); redCount++; redLevel += lvl; if (healer) redHealers++; }
        }
        Plugin.L.LogInfo($"Battleground plan: blue {blueUsed} players+bots + {svc.plannedBlue.Count - myBots.Count} adventurers, red {redUsed} + {svc.plannedRed.Count}");
        return blueCount == size && redCount == size;
    }

    /// <summary>A copy in the match whose owner says it is down: the battleground's defeat, not a death (MobSync).</summary>
    public static bool Defeated(CombatEntity e)
    {
        if (!Contains(e)) return false;
        try { if (!e.Dead) e.EnterBattlegroundDefeat(); } catch { }
        return true;
    }

    /// <summary>A copy in the match up again on its owner: out of the battleground's defeat where it stands.</summary>
    public static bool Revive(CombatEntity e)
    {
        if (!Contains(e)) return false;
        try { if (e.Dead) e.ExitBattlegroundDefeat(e.transform.position); } catch { }
        return true;
    }
}
#endif
