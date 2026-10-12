using System;
using System.Text;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.Templates;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Dev: what the game's "game actions" (dialogue nodes, effects, items) do, to see how quests change the world
/// (e.g. a quest giver replaced by a following companion). Lists matching actions and logs every one that runs.
/// </summary>
internal static class DevGameActions
{
    public static string Describe(GameActionsData.GameAction a)
    {
        if (a == null) return "null";
        var sb = new StringBuilder();
        sb.Append(a.type);
        switch (a.type)
        {
            case GameActionsData.GameActionType.NPC: sb.Append($" {a.NPCAction} npc #{a.NPCID} '{NpcName(a.NPCID)}' at {a.SpawnTypes}"); break;
            case GameActionsData.GameActionType.GameObject: sb.Append($" {a.GameObjectAction} '{(a.GameObject != null ? a.GameObject.name : "?")}' str='{a.stringValue1}'"); break;
            case GameActionsData.GameActionType.Effect: sb.Append($" {a.EffectAction} effect #{a.EffectID} '{EffectName(a.EffectID)}'"); break;
            case GameActionsData.GameActionType.Quest: sb.Append($" {a.QuestAction} quest #{a.QuestID}"); break;
            case GameActionsData.GameActionType.Dialogue: sb.Append($" {a.DialogueAction} dialogue #{a.DialogueID}"); break;
            default: sb.Append($" str='{a.stringValue1}' n={a.Amount}"); break;
        }
        return sb.ToString();
    }

    private static string NpcName(int id)
    {
        var all = GameDatabase.Instance.GetNPCs();
        return all.ContainsKey(id) ? all[id].entryName : "?";
    }

    private static string EffectName(int id)
    {
        var all = GameDatabase.Instance.GetEffects();
        return all.ContainsKey(id) ? all[id].entryName : "?";
    }

    /// <summary>Game action templates and effects whose actions mention <paramref name="what"/> (NPC or object name, template name).</summary>
    public static string Find(string what)
    {
        var sb = new StringBuilder();
        bool Match(GameActionsData.GameAction a) =>
            a != null && (NpcName(a.NPCID).IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0 && a.type == GameActionsData.GameActionType.NPC
                          || a.GameObject != null && a.GameObject.name.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0);
        foreach (var t in Resources.FindObjectsOfTypeAll<GameActionsTemplate>())
        {
            if (t?.GameActions == null) continue;
            bool byName = t.name.IndexOf(what, StringComparison.OrdinalIgnoreCase) >= 0;
            bool any = byName;
            foreach (var a in t.GameActions) any |= Match(a);
            if (!any) continue;
            sb.AppendLine($"template '{t.name}':");
            foreach (var a in t.GameActions) sb.AppendLine("  " + Describe(a));
        }
        foreach (var kv in GameDatabase.Instance.GetEffects())
        {
            var e = kv.Value;
            if (e?.ranks == null) continue;
            foreach (var r in e.ranks)
            {
                if (r?.GameActions == null) continue;
                bool any = false;
                foreach (var a in r.GameActions) any |= Match(a);
                if (!any) continue;
                sb.AppendLine($"effect #{e.ID} '{e.entryName}' ({e.effectType}):");
                foreach (var a in r.GameActions) sb.AppendLine("  " + Describe(a));
            }
        }
        return sb.Length > 0 ? sb.ToString() : $"no game actions mention '{what}'";
    }

    /// <summary>Dev: run a game actions template (by name part) on the local player, as a dialogue node would.</summary>
    public static string Run(string what)
    {
        foreach (var t in Resources.FindObjectsOfTypeAll<GameActionsTemplate>())
        {
            if (t?.GameActions == null || t.name.IndexOf(what, StringComparison.OrdinalIgnoreCase) < 0) continue;
            GameActionsManager.Instance.TriggerGameActions(GameInfo.Player, t.GameActions);
            return $"ran '{t.name}'";
        }
        return "no such template";
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameActionsManager), nameof(GameActionsManager.TriggerGameActions))]
        private static void Trace(CombatEntity __0, Il2CppSystem.Collections.Generic.List<GameActionsData.GameAction> __1)
        {
            try
            {
                if (__1 == null) return;
                foreach (var a in __1) Plugin.L.LogInfo($"[game action] on '{(__0 != null ? __0.name : "-")}': {Describe(a)}");
            }
            catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.GenerateNPCEntity))]
        private static void TraceSpawn(RPGNpc __0, bool __1, bool __2, CombatEntity __3, CombatEntity __result)
        {
            try { if (__0 != null && (__1 || __0.isQuestGiver || (int)__0.npcType == 10)) Plugin.L.LogInfo($"[spawn] '{__0.entryName}' type {__0.npcType} pet={__1} persistent={__2} owner='{(__3 != null ? __3.name : "-")}' -> {(__result != null ? __result.name : "null")}"); }
            catch { }
        }
    }
}
