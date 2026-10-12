using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.AI;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The rest of a boss fight, beyond its casts and hits (<see cref="BossSync"/>):
///  - Phases: the mob's machine tells its phase; copies enter it the game's way (InitPhase: preset/model, phase
///    actions), without its visuals (FxSync brings the owner's) and without spawning its adds.
///  - Adds a mob spawns through game actions (phase actions, ability actions): spawned by the mob's machine only and
///    shown to the others as its copies (they are hit, die and fight as synced mobs).
///  - Forced movement (knockback, pull/motion, teleport) on someone else's character or bot: applied by the game of
///    the one it lands on, with the caster's copy or shell there.
///  - Channelled casts: the channel bar on copies, as for casts.
///  - Heroic affixes are the mob machine's: copies roll none of their own, get the owner's (the game's HeroicMobStatus
///    with them: its marks and glow; their states come with the mob's states) and summon or pulse nothing themselves.
/// </summary>
internal static class BossPhases
{
    public const byte KindPhase = 29, KindForce = 30, KindChannel = 31, KindHeroic = 32;
    private static readonly NetWriter W = new();
    private static bool Connected => CoopManager.Connected && CoopManager.Session.PlayerCount > 1 && GameInfo.InWorld;

    // ---------------- phases ----------------

    private static bool _applyingPhase;
    private static float _nextResend;
    private static readonly Dictionary<uint, int> SentPhase = new();

    private static void SendPhase(uint netId, int phase)
    {
        W.Reset(); W.U8(KindPhase); W.U32(netId); W.I32(phase);
        CoopManager.Session.SendWorld(W.Span);
        SentPhase[netId] = phase;
    }

    // ---------------- heroic affixes ----------------

    private static readonly Dictionary<uint, (float mult, List<int> affixes)> SentHeroic = new();
    private static bool _applyingHeroic;

    private static void SendHeroic(uint netId, float mult, List<int> affixes)
    {
        W.Reset(); W.U8(KindHeroic); W.U32(netId); W.F32(mult); W.U8((byte)affixes.Count);
        foreach (var a in affixes) W.I32(a);
        CoopManager.Session.SendWorld(W.Span);
        SentHeroic[netId] = (mult, affixes);
    }

    public static void ReceiveHeroic(byte from, ref NetReader r)
    {
        uint netId = r.U32(); float mult = r.F32();
        var affixes = new List<int>();
        int n = r.U8(); for (int i = 0; i < n; i++) affixes.Add(r.I32());
        var copy = MobSync.GetCopy(netId);
        if (copy == null || copy.GetComponent<HeroicMobStatus>() != null) return;
        _applyingHeroic = true;
        try
        {
            var list = new Il2CppSystem.Collections.Generic.List<HeroicAffix>();
            foreach (var a in affixes) list.Add((HeroicAffix)a);
            copy.gameObject.AddComponent<HeroicMobStatus>().Init(copy, mult, list);
        }
        catch (Exception e) { Plugin.L.LogWarning("Heroic copy: " + e.Message); }
        finally { _applyingHeroic = false; }
    }

    /// <summary>Phases past the first and heroic affixes, now and then: for copies made after the change.</summary>
    public static void Update()
    {
        if (!Connected || Time.unscaledTime < _nextResend) return;
        _nextResend = Time.unscaledTime + 5f;
        var goneHeroic = new List<uint>();
        foreach (var kv in SentHeroic)
        {
            var m = MobSync.GetOwned(kv.Key);
            if (m == null || m.WasCollected || m.Dead) { goneHeroic.Add(kv.Key); continue; }
            SendHeroic(kv.Key, kv.Value.mult, kv.Value.affixes);
        }
        foreach (var id in goneHeroic) SentHeroic.Remove(id);
        var gone = new List<uint>();
        foreach (var kv in SentPhase)
        {
            var m = MobSync.GetOwned(kv.Key);
            if (m == null || m.WasCollected) { gone.Add(kv.Key); continue; }
            if (kv.Value > 0) SendPhase(kv.Key, kv.Value);
        }
        foreach (var id in gone) SentPhase.Remove(id);
    }

    public static void ReceivePhase(byte from, ref NetReader r)
    {
        uint netId = r.U32(); int phase = r.I32();
        var copy = MobSync.GetCopy(netId);
        if (copy == null) return;
        var ai = copy.GetComponent<AIEntity>();
        if (ai == null || ai.currentPhaseIndex == phase) return;
        _applyingPhase = true;
        try { ai.InitPhase(phase); Plugin.L.LogInfo($"Boss phase: {copy.name} enters phase {phase}"); }
        catch (Exception e) { Plugin.L.LogWarning("Boss phase: " + e.Message); }
        finally { _applyingPhase = false; }
    }

    // ---------------- adds ----------------

    private static int _ownedActions;      // game actions of a mob this machine runs are running
    private static bool _filtering;

    private static bool IsAddSpawn(GameActionsData.GameAction a) =>
        a != null && a.type == GameActionsData.GameActionType.NPC && a.NPCAction == GameActionsData.NPCAction.Spawn;

    // ---------------- forced movement ----------------

    private static bool IsForce(RPGEffect e) => e != null && (int)e.effectType is 10 or 16 or 17; // teleport, knockback, motion
    private static bool _applyingForce;
    private const byte ToPlayer = 0, ToEntity = 1;

    private static bool ForwardForce(CombatEntity caster, CombatEntity target, RPGEffect effect, int effectRank)
    {
        if (_applyingForce || !Connected || !IsForce(effect) || !BossSync.Foreign(target)) return false;
        uint casterId = MobSync.SyncedId(caster);
        var me = GameInfo.Player;
        bool casterIsMe = me != null && caster != null && caster.Pointer == me.Pointer;
        if (casterId == 0 && !casterIsMe) return false; // nobody the others know
        W.Reset(); W.U8(KindForce);
        if (PlayerShells.TryGetPlayer(target, out var p)) { W.U8(ToPlayer); W.U32(p); }
        else { W.U8(ToEntity); W.U32(MobSync.SyncedId(target)); }
        W.U32(casterId); W.I32(effect.ID); W.I32(effectRank);
        CoopManager.Session.SendWorld(W.Span);
        return true;
    }

    public static void ReceiveForce(byte from, ref NetReader r)
    {
        byte to = r.U8(); uint target = r.U32(), casterId = r.U32();
        int effectId = r.I32(), effectRank = r.I32();
        CombatEntity t = null;
        if (to == ToPlayer) { if (target == CoopManager.Session.LocalId) t = GameInfo.Player; }
        else t = MobSync.GetOwned(target);
        if (t == null) return;
        CombatEntity caster = casterId != 0 ? MobSync.EntityOf(casterId) : PlayerShells.Get(from);
        var effects = GameDatabase.Instance.GetEffects();
        if (caster == null || !effects.ContainsKey(effectId)) return;
        _applyingForce = true;
        try { CombatManager.Instance.ExecuteEffect(caster, t, effects[effectId], effectRank, null, 0f); }
        catch (Exception e) { Plugin.L.LogWarning("Forced movement: " + e.Message); }
        finally { _applyingForce = false; }
    }

    // ---------------- channelling ----------------

    private static readonly Dictionary<IntPtr, (RPGAbility ability, float start, float duration)> Channeling = new();
    private static bool _applyingChannel;

    private static void SendChannel(CombatEntity e, RPGAbility ability, RPGAbility.RPGAbilityRankData rank, float duration, bool start)
    {
        if (_applyingChannel || !Connected || !MobSync.TryGetOwnedNetId(e, out var netId)) return;
        W.Reset(); W.U8(KindChannel); W.U32(netId); W.Bool(start);
        if (start)
        {
            int rankIndex = ability?.ranks != null && rank != null ? ability.ranks.IndexOf(rank) : 0;
            W.I32(ability?.ID ?? -1); W.I32(rankIndex); W.F32(duration);
        }
        CoopManager.Session.SendWorld(W.Span);
    }

    public static void ReceiveChannel(byte from, ref NetReader r)
    {
        uint netId = r.U32(); bool start = r.Bool();
        int abilityId = start ? r.I32() : -1, rankIndex = start ? r.I32() : -1; float duration = start ? r.F32() : 0f;
        var copy = MobSync.GetCopy(netId);
        if (copy == null) return;
        _applyingChannel = true;
        try
        {
            if (!start) { Channeling.Remove(copy.Pointer); CombatEvents.Instance.OnStoppedChannelingAbility(copy); return; }
            var abilities = GameDatabase.Instance.GetAbilities();
            if (!abilities.ContainsKey(abilityId)) return;
            var ab = abilities[abilityId];
            var rank = ab.ranks != null && rankIndex >= 0 && rankIndex < ab.ranks.Count ? ab.ranks[rankIndex] : null;
            Channeling[copy.Pointer] = (ab, Time.time, duration);
            CombatEvents.Instance.OnStartedChannelingAbility(copy, ab, rank, duration);
        }
        catch (Exception e) { Plugin.L.LogWarning("Channel: " + e.Message); }
        finally { _applyingChannel = false; }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        // ---- phases ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AIEntity), nameof(AIEntity.InitPhase))]
        private static void PhaseEntered(AIEntity __instance, int __0)
        {
            if (_applyingPhase || !Connected) return;
            try
            {
                var m = __instance.GetComponent<MobCombatEntity>();
                if (m != null && MobSync.TryGetOwnedNetId(m, out var netId) && BossSync.IsHostileMob(m)) SendPhase(netId, __0);
            }
            catch { }
        }

        /// <summary>A copy entering its owner's phase: no visuals of its own (the owner's come through FxSync).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AIEntity), nameof(AIEntity.TriggerPhaseTemplates))]
        private static bool PhaseVisuals(AIEntity __instance)
        {
            if (!Connected) return true;
            try { return !MobSync.IsCopy(__instance.GetComponent<MobCombatEntity>()); } catch { return true; }
        }

        // ---- adds ----

        /// <summary>
        /// A copy's game actions spawn no adds (its owner's do, and they reach here as copies). A mob of mine running its
        /// actions: what it spawns is streamed to the others.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameActionsManager), nameof(GameActionsManager.TriggerGameActions))]
        private static bool ActionsStart(GameActionsManager __instance, CombatEntity __0, Il2CppSystem.Collections.Generic.List<GameActionsData.GameAction> __1)
        {
            if (!Connected || __0 == null || __1 == null) return true;
            try
            {
                if (MobSync.IsCopy(__0) && !_filtering)
                {
                    bool any = false;
                    foreach (var a in __1) any |= IsAddSpawn(a);
                    if (!any) return true;
                    var rest = new Il2CppSystem.Collections.Generic.List<GameActionsData.GameAction>();
                    foreach (var a in __1) if (!IsAddSpawn(a)) rest.Add(a);
                    _filtering = true;
                    try { __instance.TriggerGameActions(__0, rest); }
                    finally { _filtering = false; }
                    return false;
                }
                if (MobSync.TryGetOwnedNetId(__0, out _) && BossSync.IsHostileMob(__0)) _ownedActions++;
            }
            catch { }
            return true;
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(GameActionsManager), nameof(GameActionsManager.TriggerGameActions))]
        private static void ActionsEnd(CombatEntity __0)
        {
            try { if (_ownedActions > 0 && __0 != null && MobSync.TryGetOwnedNetId(__0, out _) && BossSync.IsHostileMob(__0)) _ownedActions--; } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.GenerateNPCEntity))]
        private static void AddSpawned(bool __1, CombatEntity __result)
        {
            if (_ownedActions <= 0 || __1 || __result == null || MobSync.CreatingCopy) return;
            try { MobSync.TrackLoose(__result); Plugin.L.LogInfo($"Boss add: {__result.name} shared"); } catch { }
        }

        // ---- forced movement ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.ExecuteEffect))]
        private static bool Force(CombatEntity __0, CombatEntity __1, RPGEffect __2, int __3)
        {
            try { return !ForwardForce(__0, __1, __2, __3); } catch { return true; }
        }

        // ---- channelling ----

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatEvents), nameof(CombatEvents.OnStartedChannelingAbility))]
        private static void ChannelStarted(CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2, float __3)
        { try { SendChannel(__0, __1, __2, __3, true); } catch { } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatEvents), nameof(CombatEvents.OnStoppedChannelingAbility))]
        private static void ChannelStopped(CombatEntity __0) { try { SendChannel(__0, null, null, 0f, false); } catch { } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatEntity), nameof(CombatEntity.TryGetCastBarState))]
        private static bool CopyChannelBar(CombatEntity __instance, ref RPGAbility __0, ref bool __1, ref float __2, ref float __3, ref bool __result)
        {
            if (Channeling.Count == 0 || !Channeling.TryGetValue(__instance.Pointer, out var c)) return true;
            if (c.ability == null || __instance.WasCollected || Time.time - c.start > c.duration + 2f) { Channeling.Remove(__instance.Pointer); return true; }
            __0 = c.ability; __1 = true; __2 = c.duration; __3 = Mathf.Max(0f, c.duration - (Time.time - c.start));
            __result = true;
            return false;
        }

        // ---- heroic ----

        /// <summary>A mob of mine got its heroic affixes: the others' copies get the same.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroicMobStatus), nameof(HeroicMobStatus.Init))]
        private static void HeroicRolled(CombatEntity __0, float __1, Il2CppSystem.Collections.Generic.List<HeroicAffix> __2)
        {
            if (_applyingHeroic || !Connected || __0 == null || !MobSync.TryGetOwnedNetId(__0, out var netId)) return;
            try
            {
                var list = new List<int>();
                if (__2 != null) foreach (var a in __2) list.Add((int)a);
                SendHeroic(netId, __1, list);
            }
            catch { }
        }

        /// <summary>A copy's affix states come with its owner's states (StateSync), not again from here.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(HeroicMobStatus), nameof(HeroicMobStatus.ApplyAffixStates))]
        private static bool NoCopyAffixStates(HeroicMobStatus __instance) { try { return !MobSync.IsCopy(__instance.GetComponent<CombatEntity>()); } catch { return true; } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HeroicTierManager), nameof(HeroicTierManager.TryEmpowerNPC))]
        private static bool NoOwnHeroicOnCopies() => !MobSync.CreatingCopy;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HeroicMobStatus), nameof(HeroicMobStatus.HandleBeaconSummons))]
        private static bool NoCopySummons(HeroicMobStatus __instance) { try { return !MobSync.IsCopy(__instance.GetComponent<CombatEntity>()); } catch { return true; } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HeroicMobStatus), nameof(HeroicMobStatus.HandleImperiousPulse))]
        private static bool NoCopyPulse(HeroicMobStatus __instance) { try { return !MobSync.IsCopy(__instance.GetComponent<CombatEntity>()); } catch { return true; } }
    }
}
