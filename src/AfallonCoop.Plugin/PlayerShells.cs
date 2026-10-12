using System;
using System.Collections.Generic;
using BLINK.RPGBuilder.AI;
using BLINK.RPGBuilder.Characters;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// An invisible combat entity at each remote player's puppet, so this machine's game has something to fight:
/// its mobs can aggro on and hit a remote player, and a remote player's hits on this machine's mobs have a caster.
/// Built like the game's own adventurer bots (GenerateNPCEntity from one fixed adventurer template, kept out of the
/// bot world simulation), with AI, navigation, body and dressing off. What happens to it is never applied to it:
/// hits on a shell are forwarded to the real player's machine (see MobSync).
/// </summary>
internal static class PlayerShells
{
    private static bool _spawning;
    private static RPGNpc _template;
    private static readonly Dictionary<byte, MobCombatEntity> ByPlayer = new();
    private static readonly Dictionary<IntPtr, byte> PlayerOf = new();
    private static readonly Dictionary<byte, float> RetryAt = new();
    private static string _scene = "";

    public static bool TryGetPlayer(CombatEntity e, out byte player)
    {
        player = 0;
        return e != null && PlayerOf.TryGetValue(e.Pointer, out player);
    }

    public static MobCombatEntity Get(byte player) =>
        ByPlayer.TryGetValue(player, out var s) && s != null && !s.WasCollected ? s : null;

    public static int Count => ByPlayer.Count;

    public static void LateUpdate()
    {
        if (!CoopManager.Connected || !GameInfo.InWorld) { Clear(); return; }
        if (Dungeons.Place != _scene) { Clear(); _scene = Dungeons.Place; }

        var seen = new HashSet<byte>();
        foreach (var (id, tr) in Puppets.Visible())
        {
            seen.Add(id);
            var shell = Get(id);
            if (shell == null)
            {
                if (RetryAt.TryGetValue(id, out var t) && Time.unscaledTime < t) continue;
                RetryAt[id] = Time.unscaledTime + 3f;
                shell = Spawn(id, tr);
                if (shell == null) continue;
            }
            shell.transform.SetPositionAndRotation(tr.position, tr.rotation);
            // A dead player is a dead target here too: the game's AI drops dead targets (no death of the shell
            // itself: nothing is killed, nothing rewarded).
            ApplyPlayerStats(id, shell);
            bool dead = Puppets.IsDead(id);
            if (shell.Dead != dead) { shell.Dead = dead; Plugin.L.LogInfo($"Player shell #{id}: {(dead ? "dead" : "alive")}"); }
        }
        foreach (var id in new List<byte>(ByPlayer.Keys))
            if (!seen.Contains(id)) Remove(id);
    }

    private static MobCombatEntity Spawn(byte id, Transform at)
    {
        _template ??= FindTemplate();
        if (_template == null) return null;
        var session = CoopManager.Session;
        string name = session != null && session.Players.TryGetValue(id, out var info) && info.CharacterName.Length > 0
            ? info.CharacterName : $"Player {id}";
        var npc = Object.Instantiate(_template);
        npc.entryName = npc.entryDisplayName = name;
        // Inspecting or trading would be with the template bot: off until players can do that with each other.
        npc.isInspectable = npc.isTradable = npc.isMerchant = npc.isQuestGiver = npc.isDialogue = false;
        CombatEntity e;
        _spawning = true;
        try { e = CombatManager.Instance.GenerateNPCEntity(npc, false, false, null, at.position, at.rotation, null); }
        catch (Exception ex) { Plugin.L.LogWarning("Player shell: " + ex.Message); e = null; }
        finally { _spawning = false; }
        var m = e?.TryCast<MobCombatEntity>();
        if (m == null) return null;

        foreach (var b in new Behaviour[] { m.GetComponent<AIEntity>(), m.GetComponent<AdventurerGroundMount>(), m.GetComponentInChildren<NPCEquipmentAppearance>(true) })
            if (b != null) b.enabled = false;
        var agent = m.GetComponent<NavMeshAgent>();
        if (agent != null) agent.enabled = false;
        foreach (var r in m.GetComponentsInChildren<Renderer>(true)) r.enabled = false; // the puppet is what people see
        var anim = m.ThisAnimator ?? m.GetComponentInChildren<Animator>();
        if (anim != null) anim.enabled = false;
        m.name = $"AfCoop_Shell_{id}";

        ByPlayer[id] = m;
        PlayerOf[m.Pointer] = id;
        Plugin.L.LogInfo($"Player shell for #{id} '{name}' ready");
        return m;
    }

    private static readonly Dictionary<byte, string> AppliedSig = new();

    // ---- a hit shown before its player's machine has applied it (client-side prediction) ----
    //
    // A hit of mine on an enemy player (a battleground) is shown on their shell at once; their own health comes back
    // with their state a round trip later. Until then the shell keeps the predicted health instead of being put back
    // to the last health heard (a jump up and down again). A hit they did not take (out of reach by then, protected)
    // is corrected when their health next comes, at the latest after a second.
    private static readonly Dictionary<byte, float> PredictedAt = new();

    public static void Predict(byte player) => PredictedAt[player] = Time.unscaledTime;

    private static bool HoldingPrediction(byte id)
    {
        if (!PredictedAt.TryGetValue(id, out var t)) return false;
        float now = Time.unscaledTime;
        int ping = 0;
        try { ping = CoopManager.Session?.PingToHost ?? 0; } catch { }
        float roundTrip = Mathf.Clamp(0.15f + ping * 2f / 1000f, 0.15f, 0.8f); // through the host: up to two legs each way
        if (now - t > 1f || Puppets.VitalsHeardAt(id) >= t + roundTrip) { PredictedAt.Remove(id); return false; }
        return true;
    }
    private static readonly Dictionary<(byte, int), float> SetMax = new();

    /// <summary>
    /// The player's own numbers on their shell (health and the other vitality stats, level), so whatever the game shows
    /// about it (group frames, target frame) is the player's; name and class icon from the player's info.
    /// </summary>
    private static void ApplyPlayerStats(byte id, MobCombatEntity shell)
    {
        var vitals = Puppets.Vitals(id);
        var s = CoopManager.Session;
        var info = s != null && s.Players.TryGetValue(id, out var p) ? p : null;
        string sig = info == null ? "" : $"{info.CharacterName}|{info.Level}|{info.ClassId}";
        bool infoChanged = !AppliedSig.TryGetValue(id, out var was) || was != sig;
        AppliedSig[id] = sig;
        try
        {
            if (info != null && infoChanged)
            {
                var npc = shell.GetNPCData();
                if (npc != null && info.CharacterName.Length > 0) npc.entryName = npc.entryDisplayName = info.CharacterName;
                if (info.Level > 0 && shell.GetLevel() != info.Level) shell.SetLevel(info.Level);
            }
            if (vitals != null)
                foreach (var v in vitals.Values)
                {
                    try
                    {
                        // Every frame: the shell's own regeneration would otherwise drift it back to full.
                        var key = (id, v.Stat);
                        if (!SetMax.TryGetValue(key, out var max) || Mathf.Abs(max - v.Max) > 0.5f)
                        {
                            CombatUtilities.SetMaxStatValue(shell, v.Stat, v.Max); // this also refills: current is set right after
                            SetMax[key] = v.Max;
                        }
                        if (v.Stat == MobSync.HealthStat && HoldingPrediction(id)) continue;
                        if (Mathf.Abs(CombatUtilities.GetCurrentStatValue(shell, v.Stat) - v.Current) > 0.5f)
                        {
                            CombatUtilities.SetCurrentStatValue(shell, v.Stat, v.Current);
                            GroupFrames.Refresh(shell); // the frames redraw on their own timer otherwise
                            // the target frame and nameplates redraw on the game's stat event, which a direct set does not raise
                            var stats = GameDatabase.Instance.GetStats();
                            if (stats.ContainsKey(v.Stat)) CombatEvents.Instance?.OnStatValueChanged(shell, stats[v.Stat], v.Current, v.Max);
                        }
                    }
                    catch { } // the shell's template lacks this stat
                }
        }
        catch (Exception e) { Plugin.L.LogWarning("Player shell stats: " + e.Message); }
    }

    /// <summary>One fixed adventurer template (lowest ID): the body is hidden, so race and gender do not matter.</summary>
    private static RPGNpc FindTemplate()
    {
        RPGNpc best = null;
        foreach (var kv in GameDatabase.Instance.GetNPCs())
            if (kv.Value != null && (int)kv.Value.npcType == 9 && (best == null || kv.Key < best.ID)) best = kv.Value;
        if (best == null) Plugin.L.LogWarning("Player shells: no adventurer template in the database");
        return best;
    }

    private static void Remove(byte id)
    {
        AppliedSig.Remove(id);
        PredictedAt.Remove(id);
        foreach (var k in new List<(byte, int)>(SetMax.Keys)) if (k.Item1 == id) SetMax.Remove(k);
        if (ByPlayer.Remove(id, out var s) && s != null && !s.WasCollected)
        {
            PlayerOf.Remove(s.Pointer);
            Object.Destroy(s.gameObject);
        }
    }

    public static void Clear()
    {
        foreach (var id in new List<byte>(ByPlayer.Keys)) Remove(id);
        PlayerOf.Clear();
        RetryAt.Clear();
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerWorldSimulation), nameof(AdventurerWorldSimulation.Register))]
        private static bool NoRegister() => !_spawning && !MobSync.CreatingCopy; // shells and bot copies are not this machine's bots

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerWorldSimulation), nameof(AdventurerWorldSimulation.PrepareSpawn))]
        private static bool AllowShell(ref bool __result)
        {
            if (!_spawning && !MobSync.CreatingCopy) return true;
            __result = true;
            return false;
        }

        /// <summary>The conversation window's title for a player is their name, not the shell's template's.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionHeader), nameof(BLINK.RPGBuilder.UI.NPCInteractionHeader.Show))]
        private static void PlayerTitle(BLINK.RPGBuilder.UI.NPCInteractionHeader __instance, CombatEntity __1)
        {
            try
            {
                if (__1 == null || !PlayerOf.TryGetValue(__1.Pointer, out var id) || __instance.titleText == null) return;
                __instance.titleText.text = Groups.Name(id);
            }
            catch { }
        }

        /// <summary>
        /// Portraits (target and group frames) are shot from the entity's model, which for a shell is its hidden
        /// template body: the shot is taken of the player's puppet instead.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.Voice.NPCPortraitRenderer), nameof(BLINK.RPGBuilder.Voice.NPCPortraitRenderer.ShowSource))]
        private static void PortraitOfPuppet(ref GameObject __0)
        {
            try
            {
                if (__0 == null || PlayerOf.Count == 0) return;
                var owner = __0.GetComponentInParent<MobCombatEntity>(true);
                if (owner == null || !PlayerOf.TryGetValue(owner.Pointer, out var id)) return;
                if (Puppets.TryGetVisual(id, out var go, out var anim)) __0 = anim != null ? anim.gameObject : go;
            }
            catch { }
        }

        /// <summary>The puppet carries the player's name; the shell's would be its template's (a bot's).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.ScreenSpaceNameplates), nameof(BLINK.RPGBuilder.UI.ScreenSpaceNameplates.RegisterNewNameplate))]
        private static bool NoNameplate(CombatEntity __0) => !_spawning && !(__0 != null && PlayerOf.ContainsKey(__0.Pointer));

        // Roaming adventurers look around (a physics overlap) for adventurers to group with, trade with or talk to; a
        // shell (a player) and a copy (someone else's mob or bot, a mirror without AI) are not adventurers to them.

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForAdventurerToGroup))]
        private static void GroupScanBefore(BLINK.RPGBuilder.AI.AIStateRoaming __instance, out int __state) => __state = (int)__instance.currentAdventurerState;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForAdventurerToGroup))]
        private static void GroupScanAfter(BLINK.RPGBuilder.AI.AIStateRoaming __instance, int __state)
        {
            try { if (NotAnAdventurer(__instance.groupMeetTarget)) { __instance.groupMeetTarget = null; __instance.currentAdventurerState = (BLINK.RPGBuilder.AI.AIStateRoaming.AdventurerState)__state; } }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForAdventurerTradeRequest))]
        private static void TradeScanBefore(BLINK.RPGBuilder.AI.AIStateRoaming __instance, out int __state) => __state = (int)__instance.currentAdventurerState;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForAdventurerTradeRequest))]
        private static void TradeScanAfter(BLINK.RPGBuilder.AI.AIStateRoaming __instance, int __state)
        {
            try { if (NotAnAdventurer(__instance.tradeRequestTargetAdventurer)) { __instance.tradeRequestTargetAdventurer = null; __instance.currentAdventurerState = (BLINK.RPGBuilder.AI.AIStateRoaming.AdventurerState)__state; } }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForNPCs))]
        private static void NpcScanBefore(BLINK.RPGBuilder.AI.AIStateRoaming __instance, out int __state) => __state = (int)__instance.currentAdventurerState;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.AI.AIStateRoaming), nameof(BLINK.RPGBuilder.AI.AIStateRoaming.ScanForNPCs))]
        private static void NpcScanAfter(BLINK.RPGBuilder.AI.AIStateRoaming __instance, int __state)
        {
            try { if (NotAnAdventurer(__instance.targetNPC)) { __instance.targetNPC = null; __instance.currentAdventurerState = (BLINK.RPGBuilder.AI.AIStateRoaming.AdventurerState)__state; } }
            catch { }
        }

        private static bool NotAnAdventurer(CombatEntity e) => e != null && (PlayerOf.ContainsKey(e.Pointer) || MobSync.IsCopy(e));

        /// <summary>The target frame names a shell by its template (a bot's name): the player's name instead.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(TargetInfoDisplayManager), nameof(TargetInfoDisplayManager.InitTargetUI))]
        private static void TargetName(TargetInfoDisplayManager __instance)
        {
            try
            {
                var t = GameInfo.Player?.GetTarget();
                if (t != null && PlayerOf.TryGetValue(t.Pointer, out var id) && __instance.targetNameText != null) __instance.targetNameText.text = Groups.Name(id);
            }
            catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TargetInfoDisplayManager), nameof(TargetInfoDisplayManager.InitTargetOfTargetUI))]
        private static void TargetOfTargetName(TargetInfoDisplayManager __instance, CombatEntity __0)
        {
            try
            {
                if (__0 != null && PlayerOf.TryGetValue(__0.Pointer, out var id) && __instance.targetOfTargetNameText != null) __instance.targetOfTargetNameText.text = Groups.Name(id);
            }
            catch { }
        }

        /// <summary>"Invite to group" on a shell would recruit its bot template as a companion: it invites the player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.InviteToGroup))]
        private static bool NoRecruitingShells(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            var e = __instance.entity;
            if (e == null || !PlayerOf.TryGetValue(e.Pointer, out var player)) return true;
            CoopManager.Session?.GroupInvite(player); // a player: a co-op group invite instead
            try { __instance.Hide(); } catch { }
            return false;
        }

        // A shell stands for a player: wherever the game asks who is friend or foe, it answers as for the local
        // player (mobs aggro on it and take its hits like a player's; players cannot hurt it).

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.PvP.PvPCombat), nameof(BLINK.RPGBuilder.PvP.PvPCombat.CanAffect))]
        private static void CanAffectAsPlayer(ref CombatEntity __0, ref CombatEntity __1) { AsPlayer(ref __0); AsPlayer(ref __1); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.GetCombatNodeAlignment))]
        private static void AlignmentAsPlayer(ref CombatEntity __0, ref CombatEntity __1) { AsPlayer(ref __0); AsPlayer(ref __1); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FactionManager), nameof(FactionManager.GetEntityStanceToFaction))]
        private static void StanceAsPlayer(ref CombatEntity __0) => AsPlayer(ref __0);

        private static void AsPlayer(ref CombatEntity e)
        {
            if (e == null || PlayerOf.Count == 0 || !PlayerOf.ContainsKey(e.Pointer)) return;
            if (Duel.IsOpponentShell(e)) return; // my duel opponent is no friend: the game's duel rules decide
            if (Battlegrounds.Contains(e)) return; // a battleground's member: its team decides (the game's rules)
            var me = GameInfo.Player;
            if (me != null) e = me;
        }
    }
}
