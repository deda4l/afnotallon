using System;
using BLINK.RPGBuilder.Combat;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Dev: logs how a named NPC casts (start, completion, reset, interruption, with times), to tell a broken
/// animation from casts that restart over and over. `casttrace <name part>` turns it on, `casttrace` off.
/// </summary>
internal static class DevCastTrace
{
    private static string _filter;

    public static string Set(string filter)
    {
        _filter = string.IsNullOrWhiteSpace(filter) ? null : filter.Trim();
        return _filter == null ? "cast trace off" : $"cast trace on for '{_filter}'";
    }

    public static bool Matches(MobCombatEntity e)
    {
        if (_filter == null || e == null) return false;
        string name;
        try { name = e.GetNPCData()?.entryName ?? e.name; } catch { name = e.name; }
        return name.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void Log(MobCombatEntity e, string what)
    {
        if (!Matches(e)) return;
        string name;
        try { name = e.GetNPCData()?.entryName ?? e.name; } catch { name = e.name; }
        string target = "-";
        try { var t = e.GetCurrentTargetCasted(); if (t != null) target = t.name; } catch { }
        Plugin.L.LogInfo($"Cast {Time.time:0.00} '{name}' {what} target='{target}' copy={MobSync.IsCopy(e)}");
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix, HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.InitCasting))]
        private static void Init(MobCombatEntity __instance, RPGAbility __0) => Log(__instance, "start " + (__0 != null ? __0.entryName : "?"));

        [HarmonyPostfix, HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.CastingCompleted))]
        private static void Done(MobCombatEntity __instance) => Log(__instance, "completed");

        [HarmonyPostfix, HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.ResetCasting))]
        private static void Reset(MobCombatEntity __instance) => Log(__instance, "reset");

        [HarmonyPostfix, HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.InterruptCastActions))]
        private static void Interrupt(MobCombatEntity __instance) => Log(__instance, "interrupted");
    }
}
