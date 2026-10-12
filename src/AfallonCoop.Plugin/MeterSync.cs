using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The game's damage meter (DPSMeterManager) counts only the local player and their pets, per encounter (the local
/// player's combat). In a group, as in WoW, it is the group's:
///  - each machine tells its group what its meter counted for its player and its bots (damage and healing, every half
///    second); the others add it to their own meter with the game's AddAmount, on that player's shell and that bot's
///    copy;
///  - the encounter is the group's: it starts when anyone in the group fights (a report while this meter is idle starts
///    it), the local player entering combat during it does not restart it, and it ends once nobody fights;
///  - the group's players and their bots always have their rows, named and coloured as players and their bots.
/// </summary>
internal static class MeterSync
{
    public const byte Kind = 21;
    private const float SendEvery = 0.25f;
    private static readonly NetWriter W = new();
    private static readonly Dictionary<uint, (float dmg, float heal)> Pending = new(); // 0 = my player, else my bot's net id
    private static float _nextSend;
    private static bool _applying, _ending;
    private static float _remoteAt = -100f, _nextRows, _nextBeat;
    private const float GroupCalm = 3f; // no report from the group this long: they are out of combat
    private static DPSMeterManager _meter;

    private static DPSMeterManager Meter
    {
        get
        {
            if (_meter == null) try { _meter = UnityEngine.Object.FindObjectOfType<DPSMeterManager>(true); } catch { }
            return _meter;
        }
    }

    private static void Counted(CombatEntity e, float amount, bool healing)
    {
        if (_applying || e == null || amount <= 0f || CoopManager.Session?.MyGroup == null) return;
        var me = GameInfo.Player;
        uint key;
        if (me != null && e.Pointer == me.Pointer) key = 0;
        else if (!MobSync.TryGetOwnedNetId(e, out key)) return; // a pet of mine the others see as a copy
        Pending.TryGetValue(key, out var v);
        Pending[key] = healing ? (v.dmg, v.heal + amount) : (v.dmg + amount, v.heal);
    }

    public static void Update()
    {
        KeepEncounter();
        if (Time.unscaledTime >= _nextRows) { _nextRows = Time.unscaledTime + 2f; EnsureRows(); }
        if (Time.unscaledTime < _nextSend) return;
        var m = Meter;
        bool tracking = m != null && m.isTracking;
        // amounts as they come; while my encounter runs, at least once a second (the group knows I still fight,
        // through a bot's pause between casts)
        if (Pending.Count == 0 && !(tracking && Time.unscaledTime >= _nextBeat)) return;
        _nextSend = Time.unscaledTime + SendEvery;
        _nextBeat = Time.unscaledTime + 1f;
        var s = CoopManager.Session;
        if (s?.MyGroup == null) { Pending.Clear(); return; }
        float elapsed = m != null && m.isTracking ? Time.time - m.combatStartTime : 0f; // how long the encounter runs here
        W.Reset(); W.U8(Kind); W.F32(elapsed); W.U8((byte)Math.Min(Pending.Count, 255));
        int n = 0;
        foreach (var kv in Pending) { if (n++ >= 255) break; W.U32(kv.Key); W.F32(kv.Value.dmg); W.F32(kv.Value.heal); }
        Pending.Clear();
        s.SendWorld(W.Span);
    }

    public static void Receive(byte from, ref NetReader r)
    {
        float theirElapsed = r.F32();
        int n = r.U8();
        var s = CoopManager.Session;
        bool group = s != null && s.InSameGroup(s.LocalId, from);
        var meter = group ? Meter : null;
        if (meter != null && theirElapsed > 0f)
        {
            // their encounter runs: so does the group's
            _remoteAt = Time.unscaledTime;
            if (!meter.isTracking) StartEncounter(meter);
            float theirStart = Time.time - theirElapsed;
            if (theirStart < meter.combatStartTime) meter.combatStartTime = theirStart;
        }
        for (int i = 0; i < n; i++)
        {
            uint key = r.U32(); float dmg = r.F32(), heal = r.F32();
            if (meter == null) continue;
            CombatEntity e = key == 0 ? PlayerShells.Get(from) : MobSync.GetCopy(key);
            if (e == null) continue;
            if (!meter.isTracking) { _remoteAt = Time.unscaledTime; StartEncounter(meter); } // amounts from someone not tracking
            _applying = true;
            try
            {
                if (dmg > 0f) meter.AddAmount(e, dmg, false);
                if (heal > 0f) meter.AddAmount(e, heal, true);
            }
            catch (Exception ex) { Plugin.L.LogWarning("Meter sync: " + ex.Message); }
            finally { _applying = false; }
        }
    }

    private static bool GroupFighting => Time.unscaledTime - _remoteAt < GroupCalm;

    private static void StartEncounter(DPSMeterManager m)
    {
        var me = GameInfo.Player;
        if (me == null) return;
        Plugin.L.LogInfo("Meter: the group fights, encounter started");
        try { m.OnCombatEntered(me); } catch (Exception e) { Plugin.L.LogWarning("Meter encounter: " + e.Message); }
        EnsureRows();
    }

    /// <summary>The local player is out of combat and so is the group: the encounter ends (the game's own end).</summary>
    private static void KeepEncounter()
    {
        var m = _meter;
        var me = GameInfo.Player;
        if (m == null || me == null || !m.isTracking || me.InCombat || GroupFighting) return;
        if (CoopManager.Session?.MyGroup == null) return;
        _ending = true;
        try { m.OnCombatExited(me); } catch { }
        finally { _ending = false; }
    }

    /// <summary>A row for each player of the group and each of their bots, as for one's own companions.</summary>
    private static void EnsureRows()
    {
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        var m = Meter;
        if (g == null || m?.activeEntries == null) return;
        var have = new HashSet<IntPtr>();
        foreach (var e in m.activeEntries) if (e?.entity != null) have.Add(e.entity.Pointer);
        var add = new List<CombatEntity>();
        foreach (var id in g.Players)
        {
            if (id == s.LocalId) continue;
            var shell = PlayerShells.Get(id);
            if (shell != null && !have.Contains(shell.Pointer)) add.Add(shell);
            foreach (var pet in MobSync.PetCopiesOf(id))
            {
                bool bot = false; try { bot = (int)pet.GetNPCData().npcType == 9; } catch { }
                if (bot && !have.Contains(pet.Pointer)) add.Add(pet);
            }
        }
        if (add.Count == 0) return;
        _applying = true;
        try { foreach (var e in add) m.AddAmount(e, 0f, false); } // the game's own row, at zero
        catch (Exception ex) { Plugin.L.LogWarning("Meter rows: " + ex.Message); }
        finally { _applying = false; }
    }

    /// <summary>A player's row: their name, the players' bar colour and their class icon (not their shell's template).</summary>
    private static void PlayerRow(DPSMeterManager m, DPSMeterManager.DPSEntry entry)
    {
        if (entry?.entity == null || entry.uiSlot == null || !PlayerShells.TryGetPlayer(entry.entity, out var id)) return;
        var slot = entry.uiSlot;
        if (slot.nameText != null) slot.nameText.text = Groups.Name(id);
        if (slot.barFill != null) slot.barFill.color = m.playerBarColor;
        try
        {
            var s = CoopManager.Session;
            if (s != null && s.Players.TryGetValue(id, out var info))
            {
                var classes = GameDatabase.Instance.GetClasses();
                if (classes.ContainsKey(info.ClassId) && slot.portraitImage != null)
                {
                    var c = classes[info.ClassId];
                    var icon = c.icon != null ? c.icon : c.entryIcon;
                    if (icon != null) slot.portraitImage.sprite = icon;
                }
            }
        }
        catch { }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.CreateEntry))]
        private static void Created(DPSMeterManager __instance, DPSMeterManager.DPSEntry __result)
        {
            try { PlayerRow(__instance, __result); } catch { }
        }

        /// <summary>The local player entering combat during the group's encounter does not restart it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.OnCombatEntered))]
        private static bool Entered(DPSMeterManager __instance) => !(__instance.isTracking && GroupFighting && CoopManager.Session?.MyGroup != null);

        /// <summary>The local player leaving combat while the group still fights does not end it (KeepEncounter does).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.OnCombatExited))]
        private static bool Exited() => _ending || !(GroupFighting && CoopManager.Session?.MyGroup != null);

        /// <summary>The meter redraws its rows: the players' keep their own name and look.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.UpdateDisplay))]
        private static void Redrawn(DPSMeterManager __instance)
        {
            try { if (__instance.activeEntries != null) foreach (var e in __instance.activeEntries) PlayerRow(__instance, e); } catch { }
        }

        /// <summary>Rows made when an encounter starts: the group's too.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.BuildEntries))]
        private static void Built() { try { EnsureRows(); } catch { } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(DPSMeterManager), nameof(DPSMeterManager.AddAmount))]
        private static void Added(DPSMeterManager __instance, CombatEntity __0, float __1, bool __2)
        {
            try { _meter = __instance; Counted(__0, __1, __2); } catch { }
        }
    }
}
