using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.World;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Corrupted dungeon runs (the game's keystones: a token at the altar raises the dungeon's corruption level and adds
/// its affixes, a countdown, then the run's timer and its reward bag), for a group in its dungeon instance.
///  - The group's leader starts it at the altar (others are told to ask the leader); when the leader is not in this
///    instance (someone running keys with their bots), whoever is there does.
///  - The altar's start goes to the group's players in this instance: their game takes the same level and affixes and
///    starts the same countdown at its altar, so the barrier, the timer, the boss list and the reward bag are each
///    one's own game's (everyone gets their bag, as everyone gets their own loot).
///  - Someone arriving later (or back from a disconnect) gets the run's state from those inside: level, affixes, the
///    timer's time left and the bosses already killed.
///  - Affixes acting on the party run on every machine for its own player and companions (the game's GetParty is
///    local). Affix NPCs (orbs, spirits, ghosts) are spawned by the machine running the mobs only, and shown to the
///    others as its copies, with the game's own behaviour on them (an orb's blast, a spirit's cast hit each one's party).
///  - Infested is the mob machine's: it rolls which mobs are infested and when they pulse; copies get the same flag
///    and pulse when told (its heal and the empowering of mobs around: copies hit with it here).
/// </summary>
internal static class Corruption
{
    public const byte Kind = 23;
    private const byte OpStart = 1, OpState = 2, OpAsk = 3, OpInfested = 4, OpPulse = 5;
    private const float AskAfter = 3f; // in the world that long in a new place: the group's state has come by then
    private static readonly NetWriter W = new();
    private static string _place = "";
    private static float _askAt = -1f;
    private static int _levelBefore;
    private static bool _spawningAffixNpc;

    private static DungeonCorruptionManager Manager { get { try { return DungeonCorruptionManager.Instance; } catch { return null; } } }
    private static DungeonTimerManager Timer { get { try { return DungeonTimerManager.Instance; } catch { return null; } } }

    /// <summary>Group players other than me in this dungeon instance.</summary>
    private static List<byte> Others()
    {
        var list = new List<byte>();
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected || !Dungeons.InDungeon || s.MyGroup == null) return list;
        foreach (var id in s.MyGroup.Players)
            if (id != s.LocalId && s.Players.TryGetValue(id, out var p) && p.Scene == Dungeons.Place) list.Add(id);
        return list;
    }

    private static bool FromHere(byte from)
    {
        var s = CoopManager.Session;
        return s != null && Dungeons.InDungeon && s.InSameGroup(s.LocalId, from)
            && s.Players.TryGetValue(from, out var p) && p.Scene == Dungeons.Place;
    }

    private static List<int> Affixes(DungeonCorruptionManager m)
    {
        var list = new List<int>();
        try { if (m.activeAffixes != null) foreach (var a in m.activeAffixes) list.Add((int)a); } catch { }
        return list;
    }

    private static Il2CppSystem.Collections.Generic.List<int> ToIl2Cpp(List<int> list)
    {
        var l = new Il2CppSystem.Collections.Generic.List<int>();
        foreach (var v in list) l.Add(v);
        return l;
    }

    private static CorruptionAltar Altar()
    {
        try { var a = CorruptionAltar.ActiveAltar; if (a != null) return a; } catch { }
        try { return UnityEngine.Object.FindObjectOfType<CorruptionAltar>(); } catch { return null; }
    }

    // ---------------- the altar: the leader's ----------------

    private static bool MayStart(out string why)
    {
        why = null;
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        if (g == null || !Dungeons.InDungeon || g.Leader == s.LocalId) return true;
        if (!Others().Contains(g.Leader)) return true; // the leader is not here: whoever is
        why = $"Only the group leader ({Groups.Name(g.Leader)}) can activate the altar";
        return false;
    }

    /// <summary>The run started here (the altar's confirm): the group's players here start it too.</summary>
    private static void Started()
    {
        var m = Manager;
        if (m == null || m.CurrentCorruptionLevel <= _levelBefore || Others().Count == 0) return;
        var affixes = Affixes(m);
        W.Reset(); W.U8(Kind); W.U8(OpStart); W.I32(m.CurrentCorruptionLevel); W.U8((byte)affixes.Count);
        foreach (var a in affixes) W.I32(a);
        CoopManager.Session.SendWorld(W.Span);
        Plugin.L.LogInfo($"Corruption: started at level {m.CurrentCorruptionLevel} [{string.Join(",", affixes)}], told the group");
    }

    private static void OnStart(byte from, ref NetReader r)
    {
        int level = r.I32();
        var affixes = new List<int>();
        int n = r.U8();
        for (int i = 0; i < n; i++) affixes.Add(r.I32());
        if (!FromHere(from)) return;
        var m = Manager;
        if (m == null || m.CurrentCorruptionLevel > 0) return;
        Apply(m, level, affixes);
        var altar = Altar();
        try { if (altar != null && !altar.isConsumed) altar.StartActivationCountdown(); } catch (Exception e) { Plugin.L.LogWarning("Corruption countdown: " + e.Message); }
        Hud.Notify($"{Groups.Name(from)} activated the altar: corruption level {level}");
    }

    private static void Apply(DungeonCorruptionManager m, int level, List<int> affixes)
    {
        try
        {
            if (affixes.Count > 0) m.AddActiveAffixes(ToIl2Cpp(affixes));
            m.IncreaseCorruption(level - m.CurrentCorruptionLevel);
            Plugin.L.LogInfo($"Corruption: level {m.CurrentCorruptionLevel} [{string.Join(",", Affixes(m))}]");
        }
        catch (Exception e) { Plugin.L.LogWarning("Corruption apply: " + e.Message); }
    }

    // ---------------- the run's state, for whoever comes in later ----------------

    public static void Update()
    {
        if (!GameInfo.InWorld) return;
        var place = Dungeons.Place;
        if (place != _place) { _place = place; _askAt = Dungeons.InDungeon ? Time.unscaledTime + AskAfter : -1f; }
        if (_askAt < 0f || Time.unscaledTime < _askAt) return;
        _askAt = -1f;
        var m = Manager;
        if (m == null || m.CurrentCorruptionLevel > 0 || Others().Count == 0) return;
        W.Reset(); W.U8(Kind); W.U8(OpAsk);
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>Someone came in: the run as it is here, if it has started.</summary>
    private static void OnAsk(byte from)
    {
        if (!FromHere(from)) return;
        var m = Manager;
        var t = Timer;
        if (m == null || t == null || m.CurrentCorruptionLevel <= 0) return;
        if (!t.IsTimerRunning && !t.IsCompleted && !t.IsFailed) return; // still counting down at the altar
        var affixes = Affixes(m);
        var killed = new List<int>();
        try { if (t.bossKillStatus != null) foreach (var kv in t.bossKillStatus) if (kv.Value) killed.Add(kv.Key); } catch { }
        W.Reset(); W.U8(Kind); W.U8(OpState); W.I32(m.CurrentCorruptionLevel); W.U8((byte)affixes.Count);
        foreach (var a in affixes) W.I32(a);
        W.U8((byte)((t.IsTimerRunning ? 1 : 0) | (t.IsCompleted ? 2 : 0) | (t.IsFailed ? 4 : 0)));
        W.F32(t.TimeRemaining);
        W.U8((byte)killed.Count);
        foreach (var k in killed) W.I32(k);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnState(byte from, ref NetReader r)
    {
        int level = r.I32();
        var affixes = new List<int>();
        int n = r.U8();
        for (int i = 0; i < n; i++) affixes.Add(r.I32());
        byte flags = r.U8();
        float left = r.F32();
        var killed = new List<int>();
        n = r.U8();
        for (int i = 0; i < n; i++) killed.Add(r.I32());
        if (!FromHere(from)) return;
        var m = Manager;
        var t = Timer;
        if (m == null || t == null) return;
        bool running = (flags & 1) != 0;
        if (m.CurrentCorruptionLevel != 0) return; // mine runs already (another answer came first)
        // arrived after the start: the run as it is, the altar spent
        Apply(m, level, affixes);
        var altar = Altar();
        try { if (altar != null && !altar.isConsumed) altar.OnCountdownFinished(); } catch (Exception e) { Plugin.L.LogWarning("Corruption altar: " + e.Message); }
        if (!t.IsTimerRunning && running) try { t.StartDungeonTimer(); } catch { }
        t.TimeRemaining = left;
        if (!running)
        {
            // over already: no second reward here
            t.IsTimerRunning = false;
            t.IsCompleted = (flags & 2) != 0;
            t.IsFailed = (flags & 4) != 0;
        }
        try { t.OnTimerUpdated?.Invoke(); } catch { }
        Hud.Notify(running ? $"Corrupted run in progress: level {level}" : $"This corrupted run (level {level}) is over");
        // bosses killed before I came (their copies never died here)
        try
        {
            var status = t.bossKillStatus;
            if (status == null) return;
            bool any = false;
            foreach (var id in killed) if (status.ContainsKey(id) && !status[id]) { status[id] = true; any = true; }
            if (any) t.OnBossKilled?.Invoke();
        }
        catch { }
    }

    // ---------------- Infested: the mob machine's roll and pulses ----------------

    private static readonly Dictionary<uint, bool> SentInfested = new();
    private static float _nextInfestedResend;
    private static bool _pulsing;

    private static CorruptionAffixDriver Driver { get { try { return UnityEngine.Object.FindObjectOfType<CorruptionAffixDriver>(); } catch { return null; } } }

    /// <summary>Copies roll nothing and pulse on their own never; mobs of mine: their roll goes out.</summary>
    private static void BeforeSlowTick()
    {
        foreach (var e in MobSync.AllSynced())
        {
            if (e == null || !MobSync.IsCopy(e)) continue;
            var st = e.GetComponent<CorruptionMobState>();
            if (st == null) continue;
            st.InfestedRolled = true;
            st.NextInfestedPulse = float.MaxValue;
        }
    }

    private static void AfterSlowTick()
    {
        bool resend = Time.unscaledTime >= _nextInfestedResend;
        if (resend) _nextInfestedResend = Time.unscaledTime + 5f;
        foreach (var e in MobSync.AllSynced())
        {
            if (e == null || MobSync.IsCopy(e) || !MobSync.TryGetOwnedNetId(e, out var id)) continue;
            var st = e.GetComponent<CorruptionMobState>();
            if (st == null || !st.InfestedRolled) continue;
            bool inf = st.Infested;
            if (SentInfested.TryGetValue(id, out var was) && was == inf && !(resend && inf)) continue;
            SentInfested[id] = inf;
            W.Reset(); W.U8(Kind); W.U8(OpInfested); W.U32(id); W.Bool(inf);
            CoopManager.Session.SendWorld(W.Span);
        }
    }

    private static void OnInfested(ref NetReader r)
    {
        uint id = r.U32(); bool inf = r.Bool();
        var copy = MobSync.GetCopy(id);
        if (copy == null) return;
        try
        {
            var st = CorruptionMobState.GetOrAdd(copy);
            st.InfestedRolled = true;
            st.NextInfestedPulse = float.MaxValue;
            if (st.Infested == inf) return;
            st.Infested = inf;
            st.RefreshGlow();
        }
        catch (Exception e) { Plugin.L.LogWarning("Infested copy: " + e.Message); }
    }

    private static void OnPulse(ref NetReader r)
    {
        uint id = r.U32();
        var copy = MobSync.GetCopy(id);
        var driver = Driver;
        if (copy == null || driver == null) return;
        _pulsing = true;
        try { driver.HandleInfestedPulse(copy); }
        catch (Exception e) { Plugin.L.LogWarning("Infested pulse: " + e.Message); }
        finally { _pulsing = false; }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        switch (r.U8())
        {
            case OpInfested: OnInfested(ref r); break;
            case OpPulse: OnPulse(ref r); break;
            case OpStart: OnStart(from, ref r); break;
            case OpState: OnState(from, ref r); break;
            case OpAsk: OnAsk(from); break;
        }
    }

    // ---------------- affix NPCs: the mobs' machine spawns them ----------------

    /// <summary>
    /// The affix NPC near <paramref name="pos"/> is spawned here only if the mob it comes from runs here: the nearest
    /// corruption mob at that spot (a dying mob's ghost, a fighting mob's orb or spirit) is mine, or, for a spawn at a
    /// party member (an afflicted spirit), I run some corruption mob in this fight.
    /// </summary>
    private static bool SpawnsHere(Vector3 pos)
    {
        if (!CoopManager.Connected || !Dungeons.InDungeon) return true;
        CombatEntity nearest = null;
        float best = 4f;
        bool runAnyInCombat = false;
        var all = GameState.combatEntities;
        if (all == null) return true;
        foreach (var e in all)
        {
            if (e == null || !CorruptionAffixOps.IsCorruptionMob(e, true)) continue;
            float d = Vector3.Distance(e.transform.position, pos);
            if (d < best) { best = d; nearest = e; }
            if (!MobSync.IsCopy(e) && !e.IsDead() && e.InCombat) runAnyInCombat = true;
        }
        if (nearest != null) return !MobSync.IsCopy(nearest);
        return runAnyInCombat;
    }

    /// <summary>A copy of an affix NPC gets the game's behaviour of that NPC (its blast or cast hits this machine's party).</summary>
    public static void DressCopy(MobCombatEntity m, int npcId)
    {
        if (m == null || Manager == null) return;
        try
        {
            var s = CorruptionAffixSettings.Get();
            if (s == null) return;
            if (npcId == s.ExplosiveOrbNpcID) CorruptionAffixDriver.__c.__9._HandleExplosive_b__30_0(m);
            else if (npcId == s.AfflictedSpiritNpcID) CorruptionAffixDriver.__c.__9._HandleAfflicted_b__31_0(m);
            else if (npcId == s.IncorporealSpiritNpcID) CorruptionAffixDriver.__c.__9._HandleIncorporeal_b__32_0(m);
        }
        catch (Exception e) { Plugin.L.LogWarning("Affix copy: " + e.Message); }
    }

    /// <summary>As the altar's confirm with a token of this level and these affixes (dev).</summary>
    public static string DevStart(int level, List<int> affixes)
    {
        var m = Manager;
        if (m == null) return "no corruption manager here";
        if (!MayStart(out var why)) return why;
        _levelBefore = m.CurrentCorruptionLevel;
        Apply(m, level, affixes);
        try { Altar()?.StartActivationCountdown(); } catch (Exception e) { return "countdown: " + e.Message; }
        Started();
        return Dev();
    }

    public static string Dev()
    {
        var m = Manager; var t = Timer;
        if (m == null) return "no corruption manager here";
        return $"level {m.CurrentCorruptionLevel} affixes [{string.Join(",", Affixes(m))}] altar consumed {Altar()?.isConsumed}" +
               (t == null ? " no timer" : $" timer running {t.IsTimerRunning} done {t.IsCompleted} failed {t.IsFailed} left {t.TimeRemaining:0}") +
               " infested [" + string.Join(",", InfestedHere()) + "]";
    }

    private static List<string> InfestedHere()
    {
        var l = new List<string>();
        foreach (var e in MobSync.AllSynced())
        {
            var st = e != null ? e.GetComponent<CorruptionMobState>() : null;
            if (st != null && st.InfestedRolled && st.Infested) l.Add($"{MobSync.SyncedId(e):x8}{(MobSync.IsCopy(e) ? "c" : "o")}");
        }
        l.Sort();
        return l;
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CorruptionAltar), nameof(CorruptionAltar.TryInteract))]
        private static bool LeaderOnly()
        {
            if (MayStart(out var why)) return true;
            Hud.Notify(why);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CorruptionAltarPanel), nameof(CorruptionAltarPanel.ClickConfirm))]
        private static void BeforeConfirm() { try { _levelBefore = Manager?.CurrentCorruptionLevel ?? 0; } catch { } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CorruptionAltarPanel), nameof(CorruptionAltarPanel.ClickConfirm))]
        private static void AfterConfirm() { try { Started(); } catch (Exception e) { Plugin.L.LogWarning("Corruption start: " + e.Message); } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CorruptionAffixDriver), nameof(CorruptionAffixDriver.SlowTick))]
        private static void SlowTickStart() { if (CoopManager.Connected) try { BeforeSlowTick(); } catch { } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CorruptionAffixDriver), nameof(CorruptionAffixDriver.SlowTick))]
        private static void SlowTickEnd() { if (CoopManager.Connected) try { AfterSlowTick(); } catch { } }

        /// <summary>A mob of mine pulses: its copies pulse with it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CorruptionAffixDriver), nameof(CorruptionAffixDriver.HandleInfestedPulse))]
        private static void Pulsed(CombatEntity __0)
        {
            if (_pulsing || !CoopManager.Connected || !MobSync.TryGetOwnedNetId(__0, out var id)) return;
            W.Reset(); W.U8(Kind); W.U8(OpPulse); W.U32(id);
            try { CoopManager.Session.SendWorld(W.Span); } catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CorruptionAffixDriver), nameof(CorruptionAffixDriver.SpawnAffixNpc))]
        private static bool SpawnAffixNpc(Vector3 __1)
        {
            try { if (!SpawnsHere(__1)) return false; } catch { }
            _spawningAffixNpc = true;
            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(CorruptionAffixDriver), nameof(CorruptionAffixDriver.SpawnAffixNpc))]
        private static void SpawnedAffixNpc() => _spawningAffixNpc = false;

        /// <summary>An affix NPC spawned here: the others get its copy.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.GenerateNPCEntity))]
        private static void Generated(CombatEntity __result)
        {
            if (_spawningAffixNpc && __result != null) try { MobSync.TrackLoose(__result); } catch { }
        }
    }
}
