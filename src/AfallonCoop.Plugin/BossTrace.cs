using System;
using System.Collections.Generic;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>Dev: what a boss fight does here — cast telegraphs, ground telegraphs and whom they hit, AoE executions.</summary>
internal static class BossTrace
{
    public static bool On;

    public static string Set(string arg) { On = arg != "off"; return "boss trace " + (On ? "on" : "off"); }

    private static string N(CombatEntity e)
    {
        if (e == null) return "null";
        string kind = MobSync.IsCopy(e) ? "copy" : PlayerShells.TryGetPlayer(e, out var p) ? "shell#" + p : e.IsPlayer() ? "PLAYER" : MobSync.SyncedId(e) != 0 ? "owned" : "local";
        string name; try { name = e.GetNPCData()?.entryName ?? e.name; } catch { name = e.name; }
        return $"{name}[{kind}]";
    }

    private static void Log(string s) => Plugin.L.LogInfo("[boss] " + s);

    /// <summary>Abilities with telegraphs in the database.</summary>
    public static string Abilities()
    {
        var parts = new List<string>();
        foreach (var kv in GameDatabase.Instance.GetAbilities())
        {
            var a = kv.Value;
            if (a?.ranks == null) continue;
            foreach (var r in a.ranks)
                if (r != null && (r.GroundTelegraphEnabled || r.TelegraphSpawnOnHit))
                { parts.Add($"{a.ID}:{a.entryName} type {r.targetType} act {r.activationType} cast {r.castTime} tele {r.GroundTelegraphEnabled} spawnOnHit {r.TelegraphSpawnOnHit}"); break; }
        }
        return parts.Count + " | " + string.Join(" | ", parts);
    }

    [HarmonyPatch]
    internal static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AbilityTelegraphManager), nameof(AbilityTelegraphManager.OnStartedCasting))]
        private static void Started(CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2, float __3)
        { if (On && __2 != null && __2.GroundTelegraphEnabled) Log($"cast telegraph {N(__0)} {__1?.entryName} type {__2.targetType} cast {__3:0.0}"); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AbilityTelegraphManager), nameof(AbilityTelegraphManager.OnAbilityHit))]
        private static void Hit(CombatEntity __0, CombatEntity __1, RPGAbility.RPGAbilityRankData __2)
        { if (On && __2 != null && __2.TelegraphSpawnOnHit) Log($"on-hit telegraph {N(__0)} -> {N(__1)}"); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GroundTelegraph), nameof(GroundTelegraph.Spawn))]
        private static void Spawned(GroundTelegraph __result, Vector3 __1)
        { if (On) Log($"ground telegraph spawned at {__1} by {N(__result?.casterEntity)} effect {__result?.damageEffectID} playerSideOnly {__result?.hitPlayerSideOnly}"); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GroundTelegraph), nameof(GroundTelegraph.CollectTargetsInside))]
        private static void Collected(GroundTelegraph __instance, Il2CppSystem.Collections.Generic.List<CombatEntity> __result)
        {
            if (!On) return;
            var names = new List<string>();
            if (__result != null) foreach (var e in __result) names.Add(N(e));
            Log($"ground telegraph of {N(__instance.casterEntity)} impact: [{string.Join(", ", names)}]");
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EXECUTE_AOE_ABILITY))]
        private static void Aoe(CombatEntity __0) { if (On) Log($"EXECUTE_AOE {N(__0)}"); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EXECUTE_CONE_ABILITY))]
        private static void Cone(CombatEntity __0) { if (On) Log($"EXECUTE_CONE {N(__0)}"); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EXECUTE_LINEAR_ABILITY))]
        private static void Linear(CombatEntity __0, RPGAbility __1) { if (On) Log($"EXECUTE_LINEAR {N(__0)} {__1?.entryName}"); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EXECUTE_GROUND_ABILITY))]
        private static void Ground(CombatEntity __0, RPGAbility __1, Vector3 __3) { if (On) Log($"EXECUTE_GROUND {N(__0)} {__1?.entryName} at {__3}"); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.checkTarget))]
        private static void Checked(CombatEntity __0, CombatEntity __1, bool __result)
        { if (On && (PlayerShells.TryGetPlayer(__1, out _) || (__1 != null && __1.IsPlayer()))) Log($"checkTarget {N(__0)} -> {N(__1)} = {__result}"); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.TakeDamage))]
        private static void MobDamaged(MobCombatEntity __instance, CombatCalculations.DamageResult __0) => Damaged(__instance, __0);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerCombatEntity), nameof(PlayerCombatEntity.TakeDamage))]
        private static void PlayerDamaged(PlayerCombatEntity __instance, CombatCalculations.DamageResult __0) => Damaged(__instance, __0);

        private static void Damaged(CombatEntity __instance, CombatCalculations.DamageResult __0)
        {
            if (!On || __0 == null) return;
            if (PlayerShells.TryGetPlayer(__instance, out _) || __instance.IsPlayer())
                Log($"damage {N(__0.caster)} -> {N(__instance)} {__0.DamageAmount:0} action '{__0.DamageActionType}'");
        }
    }
}
