using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Mobs' area attacks (bosses' slams, novas, cleaves, lances; the ones with ground telegraphs) are fair for every player:
///  - Casts: a mob's cast reaches the others with its ability (FxSync), and their game raises its own cast event for
///    the mob's copy, timed with the copy's motion: its ground telegraph, the cast bar over its head, its hands' glow.
///  - Hits: an area ability of a mob (AOE, cone, line) is executed by the game of each player, on the mob's copy, at the
///    moment it is cast there: it hits that machine's own player, pets and bots, where they stand on their own screen.
///    The mob's machine does not hit other players' shells or bots with it (their own game does).
///  - Ground telegraphs left by a hit (pools that blast after a delay) appear on every machine, each blasting its own
///    player, pets and bots.
///  - A mob's hit effect (the ability's hitEffect prefab, where its projectile lands or on whom it hits: a burning patch
///    with its warning ring and its fire trigger) appears on every machine; environment triggers burn each machine's own.
///  - An area ability's effects reach only the machine's own entities (no second debuff, pool or action on a shell).
/// </summary>
internal static class BossSync
{
    public const byte Kind = 27;
    public const byte KindHitEffect = 28;
    private static readonly NetWriter W = new();
    public const byte TargetNone = 0, TargetPlayer = 1, TargetEntity = 2;

    private static readonly Dictionary<IntPtr, (int ability, int rank)> RankIndex = new();

    private static bool Connected => CoopManager.Connected && CoopManager.Session.PlayerCount > 1 && GameInfo.InWorld;

    private static bool TryRank(RPGAbility.RPGAbilityRankData rank, out int abilityId, out int rankIndex)
    {
        abilityId = rankIndex = -1;
        if (rank == null) return false;
        if (RankIndex.Count == 0)
            foreach (var kv in GameDatabase.Instance.GetAbilities())
                if (kv.Value?.ranks != null)
                    for (int i = 0; i < kv.Value.ranks.Count; i++)
                        if (kv.Value.ranks[i] != null) RankIndex[kv.Value.ranks[i].Pointer] = (kv.Key, i);
        if (!RankIndex.TryGetValue(rank.Pointer, out var v)) return false;
        abilityId = v.ability; rankIndex = v.rank;
        return true;
    }

    private static (RPGAbility ability, RPGAbility.RPGAbilityRankData rank) Resolve(int abilityId, int rankIndex)
    {
        var abilities = GameDatabase.Instance.GetAbilities();
        if (!abilities.ContainsKey(abilityId)) return (null, null);
        var a = abilities[abilityId];
        return (a, a.ranks != null && rankIndex >= 0 && rankIndex < a.ranks.Count ? a.ranks[rankIndex] : null);
    }

    internal static bool IsHostileMob(CombatEntity e)
    {
        try { var npc = e?.GetNPCData(); return npc != null && (int)npc.npcType <= 3 && !e.IsPet(); } catch { return false; }
    }

    /// <summary>Abilities that act in space (cone, AOE, line, projectile, ground, targeted projectile): run by every game.</summary>
    public static bool IsSpatialRank(RPGAbility.RPGAbilityRankData rank) =>
        rank != null && (int)rank.targetType is 1 or 2 or 3 or 4 or 6 or 8;

    /// <summary>A hit of this spatial ability by this mob lands where its victim plays, not here (see the class).</summary>
    public static bool ResolvedByVictim(CombatEntity caster, RPGAbility.RPGAbilityRankData rank) =>
        Connected && IsSpatialRank(rank) && MobSync.TryGetOwnedNetId(caster, out _) && IsHostileMob(caster);

    /// <summary>A mob's spatial ability others execute themselves: no mirror of its projectile or ground object.</summary>
    public static bool ExecutedByAll(CombatEntity caster, RPGAbility.RPGAbilityRankData rank) =>
        Connected && IsSpatialRank(rank) && IsHostileMob(caster) && MobSync.SyncedId(caster) != 0;

    /// <summary>Someone else's: a shell, or a copy of a mob, bot or pet another machine runs.</summary>
    internal static bool Foreign(CombatEntity e) => e != null && (PlayerShells.TryGetPlayer(e, out _) || MobSync.IsCopy(e));

    // ---------------- telegraphs on copies ----------------

    /// <summary>
    /// A mob's cast reached here (its copy): the game's own cast event for the copy, so everything the game shows for a
    /// cast shows (its ground telegraph, the cast bar over its head, its hands' glow).
    /// </summary>
    public static void CastStarted(CombatEntity copy, int abilityId, int rankIndex, float castDuration)
    {
        var (ability, rank) = Resolve(abilityId, rankIndex);
        if (copy == null || ability == null || rank == null) return;
        Casting[copy.Pointer] = (ability, Time.time, castDuration);
        try { CombatEvents.Instance.OnStartedCastingAbility(copy, ability, rank, castDuration); }
        catch (Exception e) { Plugin.L.LogWarning("Mob cast: " + e.Message); }
    }

    public static void CastStopped(CombatEntity copy)
    {
        if (copy == null) return;
        Casting.Remove(copy.Pointer);
        try { CombatEvents.Instance.OnStoppedCastingAbility(copy); } catch { }
    }

    /// <summary>Copies' casts as their owners cast them, for the cast bars (the game reads them off the entity).</summary>
    private static readonly Dictionary<IntPtr, (RPGAbility ability, float start, float duration)> Casting = new();

    // ---------------- spatial abilities: each player's game ----------------

    private static bool _mirroring;

    /// <summary>
    /// The mob's spatial ability, executed here on its copy at the moment it was used there (render-delayed), against
    /// its target as seen here: the game's own execution for its type (a cone, an area, a line, a projectile flying,
    /// a ground effect under the target).
    /// </summary>
    public static void Execute(CombatEntity copy, int abilityId, int rankIndex, byte targetKind, uint targetId)
    {
        var (ability, rank) = Resolve(abilityId, rankIndex);
        var cm = CombatManager.Instance;
        if (copy == null || rank == null || cm == null) return;
        CombatEntity target = null;
        if (targetKind == TargetPlayer) target = targetId == CoopManager.Session.LocalId ? GameInfo.Player : PlayerShells.Get((byte)targetId);
        else if (targetKind == TargetEntity) target = MobSync.EntityOf(targetId);
        _mirroring = true;
        try
        {
            switch ((int)rank.targetType)
            {
                case 1: cm.EXECUTE_CONE_ABILITY(copy, rank); break;
                case 2: cm.EXECUTE_AOE_ABILITY(copy, rank); break;
                case 3: cm.EXECUTE_LINEAR_ABILITY(copy, ability, rank); break;
                case 4:
                case 8: cm.StartCoroutine(cm.EXECUTE_PROJECTILE_ABILITY(copy, target, ability, rank)); break;
                case 6:
                    var at = target != null && !target.Dead ? target : copy; // as the game: under the target, else the caster
                    cm.EXECUTE_GROUND_ABILITY(copy, ability, rank, at.transform.position);
                    break;
            }
            if (BossTrace.On) Plugin.L.LogInfo($"[boss] executed {ability.entryName} ({rank.targetType}) on copy {copy.name} at {(target != null ? target.name : "-")}");
        }
        catch (Exception e) { Plugin.L.LogWarning("Mob ability: " + e.Message); }
        finally { _mirroring = false; }
    }

    private static void Used(CombatEntity caster, CombatEntity target, RPGAbility.RPGAbilityRankData rank)
    {
        if (_mirroring || !ResolvedByVictim(caster, rank) || !TryRank(rank, out var abilityId, out var rankIndex)) return;
        byte kind = TargetNone; uint id = 0;
        var me = GameInfo.Player;
        if (target != null && me != null && target.Pointer == me.Pointer) { kind = TargetPlayer; id = CoopManager.Session.LocalId; }
        else if (PlayerShells.TryGetPlayer(target, out var p)) { kind = TargetPlayer; id = p; }
        else if (target != null && (id = MobSync.SyncedId(target)) != 0) kind = TargetEntity;
        FxSync.SendMobAbility(caster, abilityId, rankIndex, kind, id);
    }

    // ---------------- ground telegraphs left by hits: everywhere ----------------

    private static RPGAbility.RPGAbilityRankData _hitRank;
    private static bool _spawningRemote;

    private static void Spawned(GroundTelegraph t, int shape, Vector3 pos, Quaternion rot, float size, float duration, int effect, CombatEntity caster, float cone, float width)
    {
        if (_spawningRemote || t == null || !Connected || caster == null) return;
        uint casterId = MobSync.SyncedId(caster);
        if (casterId == 0 || !IsHostileMob(caster)) return;
        int abilityId = -1, rankIndex = -1;
        if (_hitRank != null) TryRank(_hitRank, out abilityId, out rankIndex);
        W.Reset(); W.U8(Kind); W.I32(StableHash.Of(Dungeons.Place)); W.U32(casterId); W.U8((byte)shape);
        W.F32(pos.x); W.F32(pos.y); W.F32(pos.z); W.F32(rot.x); W.F32(rot.y); W.F32(rot.z); W.F32(rot.w);
        W.F32(size); W.F32(duration); W.I32(effect); W.F32(cone); W.F32(width);
        W.Bool(t.hitPlayerSideOnly); W.I32(abilityId); W.I32(rankIndex);
        CoopManager.Session.SendWorld(W.Span);
    }

    public static void Receive(byte from, ref NetReader r)
    {
        int place = r.I32();
        uint casterId = r.U32();
        int shape = r.U8();
        var pos = new Vector3(r.F32(), r.F32(), r.F32());
        var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
        float size = r.F32(), duration = r.F32();
        int effect = r.I32();
        float cone = r.F32(), width = r.F32();
        bool playerSideOnly = r.Bool();
        int abilityId = r.I32(), rankIndex = r.I32();
        if (!Connected || place != StableHash.Of(Dungeons.Place)) return;
        var caster = MobSync.EntityOf(casterId);
        if (caster == null) return; // not near it here
        var (_, rank) = Resolve(abilityId, rankIndex);
        _spawningRemote = true;
        try
        {
            var t = GroundTelegraph.Spawn((GroundTelegraph.TelegraphShape)shape, pos, rot, size, duration, effect, caster, cone, width, AbilityTelegraphManager.GetMaterial());
            if (t != null)
            {
                t.hitPlayerSideOnly = playerSideOnly;
                if (rank != null) t.impactVFXPrefab = rank.TelegraphSpawnVFX;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Ground telegraph: " + e.Message); }
        finally { _spawningRemote = false; }
    }

    // ---------------- hit effects: everywhere ----------------

    private static CombatEntity _hitCaster;   // the caster whose hit is being resolved here (projectile or ability)
    private static bool _spawningHitEffect;
    private const byte HitAtPoint = 0, HitOnPlayer = 1, HitOnEntity = 2;

    private static bool SendsHitEffects(CombatEntity caster) =>
        Connected && caster != null && MobSync.TryGetOwnedNetId(caster, out _) && IsHostileMob(caster);
    // (a spatial ability's hit effects are made by each game's own execution of it; see SendHitEffect)

    private static void SendHitEffect(RPGAbility.RPGAbilityRankData rank, byte mode, uint target, Vector3 pos)
    {
        if (_spawningHitEffect || rank?.hitEffect == null || IsSpatialRank(rank) || !SendsHitEffects(_hitCaster) || !TryRank(rank, out var abilityId, out var rankIndex)) return;
        W.Reset(); W.U8(KindHitEffect); W.I32(StableHash.Of(Dungeons.Place)); W.I32(abilityId); W.I32(rankIndex);
        W.U8(mode); W.U32(target); W.F32(pos.x); W.F32(pos.y); W.F32(pos.z);
        CoopManager.Session.SendWorld(W.Span);
        if (BossTrace.On) Plugin.L.LogInfo($"[boss] hit effect sent: {rank.hitEffect.name} mode {mode} target {target} at {pos}");
    }

    /// <summary>The hit effect on that entity, as the mob's machine had it (who was hit there, here).</summary>
    private static void OnHitEntity(CombatEntity hit, RPGAbility.RPGAbilityRankData rank)
    {
        if (hit == null || rank?.hitEffect == null || !SendsHitEffects(_hitCaster)) return;
        var me = GameInfo.Player;
        if (me != null && hit.Pointer == me.Pointer) SendHitEffect(rank, HitOnPlayer, CoopManager.Session.LocalId, hit.transform.position);
        else if (PlayerShells.TryGetPlayer(hit, out var p)) SendHitEffect(rank, HitOnPlayer, p, hit.transform.position);
        else { uint id = MobSync.SyncedId(hit); SendHitEffect(rank, id != 0 ? HitOnEntity : HitAtPoint, id, hit.transform.position); }
    }

    public static void ReceiveHitEffect(byte from, ref NetReader r)
    {
        int place = r.I32(), abilityId = r.I32(), rankIndex = r.I32();
        byte mode = r.U8(); uint target = r.U32();
        var pos = new Vector3(r.F32(), r.F32(), r.F32());
        if (!Connected || place != StableHash.Of(Dungeons.Place)) return;
        var (_, rank) = Resolve(abilityId, rankIndex);
        if (rank?.hitEffect == null) return;
        CombatEntity on = null;
        if (mode == HitOnPlayer) on = target == CoopManager.Session.LocalId ? GameInfo.Player : PlayerShells.Get((byte)target);
        else if (mode == HitOnEntity) on = MobSync.EntityOf(target);
        if (BossTrace.On) Plugin.L.LogInfo($"[boss] hit effect received: {rank.hitEffect.name} mode {mode} on {(on != null ? on.name : "-")} at {pos}");
        _spawningHitEffect = true;
        try
        {
            if (on != null) CombatManager.Instance.SpawnHitEffect(on, rank); // the game's own placement (socket, offset, attached)
            else UnityEngine.Object.Destroy(UnityEngine.Object.Instantiate(rank.hitEffect, pos, Quaternion.identity), rank.hitEffectDuration);
        }
        catch (Exception e) { Plugin.L.LogWarning("Hit effect: " + e.Message); }
        finally { _spawningHitEffect = false; }
    }

    /// <summary>Dev: an ability's hit effect on the local player, as a mob's hit would leave it.</summary>
    public static string DevHitEffect(int abilityId)
    {
        var (_, rank) = Resolve(abilityId, 0);
        var me = GameInfo.Player;
        if (rank?.hitEffect == null || me == null) return "no hit effect";
        CombatManager.Instance.SpawnHitEffect(me, rank);
        return "spawned " + rank.hitEffect.name;
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>A copy's cast bar (over its head, in the target frame): the cast its owner is making.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatEntity), nameof(CombatEntity.TryGetCastBarState))]
        private static bool CopyCastBar(CombatEntity __instance, ref RPGAbility __0, ref bool __1, ref float __2, ref float __3, ref bool __result)
        {
            if (Casting.Count == 0 || !Casting.TryGetValue(__instance.Pointer, out var c)) return true;
            if (c.ability == null || __instance.WasCollected || Time.time - c.start > c.duration + 2f) { Casting.Remove(__instance.Pointer); return true; } // its stop went missing
            __0 = c.ability; __1 = false; __2 = c.duration; __3 = Mathf.Max(0f, c.duration - (Time.time - c.start));
            __result = true;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AbilityTelegraphManager), nameof(AbilityTelegraphManager.OnAbilityHit))]
        private static void HitStart(RPGAbility.RPGAbilityRankData __2) => _hitRank = __2;

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(AbilityTelegraphManager), nameof(AbilityTelegraphManager.OnAbilityHit))]
        private static void HitEnd() => _hitRank = null;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GroundTelegraph), nameof(GroundTelegraph.Spawn))]
        private static void GroundSpawned(GroundTelegraph __result, GroundTelegraph.TelegraphShape __0, Vector3 __1, Quaternion __2, float __3, float __4, int __5, CombatEntity __6, float __7, float __8)
        {
            try { Spawned(__result, (int)__0, __1, __2, __3, __4, __5, __6, __7, __8); } catch (Exception e) { Plugin.L.LogWarning("Ground telegraph send: " + e.Message); }
        }

        // ---- hit effects ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection), nameof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection.CombatNodeHit))]
        private static void ProjectileHitStart(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection __instance) { try { _hitCaster = __instance.casterNode; } catch { } }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection), nameof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection.CombatNodeHit))]
        private static void ProjectileHitEnd() => _hitCaster = null;

        /// <summary>A projectile landing on the ground: its hit effect where it landed.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection), nameof(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection.EnvironmentHit))]
        private static void ProjectileLanded(BLINK.RPGBuilder.LogicMono.ProjectileHitDetection __instance)
        {
            try
            {
                _hitCaster = __instance.casterNode;
                SendHitEffect(__instance.rankREF, HitAtPoint, 0, __instance.transform.position);
            }
            catch { }
            finally { _hitCaster = null; }
        }

        /// <summary>A mob of mine uses an ability: a spatial one goes to the others, who execute it themselves.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.HandleAbilityTypeActions))]
        private static void AbilityActionsStart(CombatEntity __0, CombatEntity __1, RPGAbility.RPGAbilityRankData __3)
        {
            _hitCaster = __0;
            try { Used(__0, __1, __3); } catch (Exception e) { Plugin.L.LogWarning("Mob ability send: " + e.Message); }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.HandleAbilityTypeActions))]
        private static void AbilityActionsEnd() => _hitCaster = null;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.SpawnHitEffect))]
        private static void HitEffectSpawned(CombatEntity __0, RPGAbility.RPGAbilityRankData __1) { try { OnHitEntity(__0, __1); } catch { } }

        /// <summary>An environment trigger (a burning patch, a hazard) burns this machine's own entities; others' burn theirs.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.World.TriggerEffectEnvironment), nameof(BLINK.RPGBuilder.World.TriggerEffectEnvironment.ApplyEffectToTarget))]
        private static bool OwnEntitiesOnly(CombatEntity __0) => !(Connected && Foreign(__0));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.World.TriggerEffectEnvironment), nameof(BLINK.RPGBuilder.World.TriggerEffectEnvironment.OnTriggerEnter))]
        private static bool OwnEntitiesEnter(Collider __0)
        {
            if (!Connected || __0 == null) return true;
            try { return !Foreign(__0.GetComponentInParent<CombatEntity>()); } catch { return true; }
        }

        /// <summary>An area ability of a mob does nothing to someone else's entity here: their own game applies it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.ExecuteAbilityEffects))]
        private static bool AreaEffectsOnOwn(CombatEntity __0, CombatEntity __1, RPGAbility.RPGAbilityRankData __2)
        {
            try { return !(Connected && IsSpatialRank(__2) && IsHostileMob(__0) && Foreign(__1)); } catch { return true; }
        }

        /// <summary>A ground telegraph blasts this machine's own player, pets, bots and mobs; others' are blasted by their game.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(GroundTelegraph), nameof(GroundTelegraph.CollectTargetsInside))]
        private static void OwnTargetsOnly(GroundTelegraph __instance, Il2CppSystem.Collections.Generic.List<CombatEntity> __result)
        {
            if (__result == null || !Connected || !IsHostileMob(__instance.casterEntity)) return;
            try { for (int i = __result.Count - 1; i >= 0; i--) if (Foreign(__result[i])) __result.RemoveAt(i); } catch { }
        }
    }
}
