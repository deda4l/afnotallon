using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// World quests are the world's, not each machine's: which quest each zone offers, which are active and for how long
/// all come from one machine per area, the host when it is there (else the lowest player in that area), and the others
/// follow it with the game's own steps (a quest's activation auto-accepts it for whoever is in its zone, its end
/// abandons or turns it in). Their own rolls (the zone's pick, the manager's slot filling and timers) are off.
/// Completing one ends it for the area, as in the game: the one who completed it tells the area's machine, which ends
/// it the game's way (the group, sharing kill credit, completes it together).
/// </summary>
internal static class WorldQuests
{
    public const byte Kind = 25;
    private const byte OpState = 1, OpCompleted = 2;
    private const float SendEvery = 2f;
    private static readonly NetWriter W = new();
    private static float _nextSend;
    private static bool _applying;
    private static string _zonesScene = "";
    private static readonly Dictionary<uint, WorldQuestZone> Zones = new();

    private static WorldQuestManager Manager { get { try { return WorldQuestManager.Instance; } catch { return null; } } }

    /// <summary>Who runs this area's world quests: the host if here, else the lowest player here.</summary>
    private static byte Authority()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected || Dungeons.InDungeon) return s?.LocalId ?? 0;
        var place = Dungeons.Place;
        byte best = s.LocalId;
        foreach (var kv in s.Players)
        {
            if (kv.Key == s.LocalId || kv.Value.Scene != place) continue;
            if (kv.Key == ProtocolInfo.HostId) return kv.Key;
            if (kv.Key < best && s.LocalId != ProtocolInfo.HostId) best = kv.Key;
        }
        return best;
    }

    private static bool Following => CoopManager.Connected && GameInfo.InWorld && Authority() != CoopManager.Session.LocalId;

    private static bool AnyoneHere()
    {
        var s = CoopManager.Session;
        if (s == null) return false;
        foreach (var kv in s.Players) if (kv.Key != s.LocalId && kv.Value.Scene == Dungeons.Place) return true;
        return false;
    }

    private static void IndexZones()
    {
        var scene = Dungeons.Place;
        if (scene == _zonesScene && Zones.Count > 0) return;
        _zonesScene = scene;
        Zones.Clear();
        foreach (var z in UnityEngine.Object.FindObjectsOfType<WorldQuestZone>(true))
        {
            var p = z.transform.position;
            uint key = (uint)StableHash.Of($"{scene}|{z.name}|{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)},{Mathf.RoundToInt(p.z)}");
            while (Zones.ContainsKey(key)) key++;
            Zones[key] = z;
        }
    }

    private static int QuestId(RPGWorldQuest wq) { try { return wq != null && wq.quest != null ? wq.quest.ID : -1; } catch { return -1; } }

    // ---------------- the area's machine: sends ----------------

    public static void Update()
    {
        if (!CoopManager.Connected || !GameInfo.InWorld || Dungeons.InDungeon) return;
        if (Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + SendEvery;
        if (Following || !AnyoneHere()) return;
        var m = Manager;
        if (m?.entries == null) return;
        IndexZones();
        float now = Time.time;
        var entries = new List<(int id, byte state, float left, bool suspended)>();
        foreach (var kv in m.entries)
        {
            var e = kv.Value;
            int id = QuestId(e?.quest);
            if (id < 0) continue;
            entries.Add((id, (byte)(int)e.state, Mathf.Max(0f, e.endTime - now), e.suspended));
        }
        W.Reset(); W.U8(Kind); W.U8(OpState); W.I32(StableHash.Of(Dungeons.Place));
        W.U16((ushort)entries.Count);
        foreach (var (id, state, left, suspended) in entries) { W.I32(id); W.U8(state); W.F32(left); W.U8((byte)(suspended ? 1 : 0)); }
        W.U16((ushort)Zones.Count);
        foreach (var kv in Zones) { W.U32(kv.Key); W.I32(kv.Value != null ? QuestId(kv.Value.CurrentQuest) : -1); }
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void SendSoon() => _nextSend = Mathf.Min(_nextSend, Time.unscaledTime + 0.2f);

    // ---------------- the others: follow ----------------

    private static void OnState(byte from, ref NetReader r)
    {
        int scene = r.I32();
        int n = r.U16();
        var theirs = new Dictionary<int, (int state, float left, bool suspended)>();
        for (int i = 0; i < n; i++) { int id = r.I32(); int st = r.U8(); float left = r.F32(); bool sus = r.U8() == 1; theirs[id] = (st, left, sus); }
        n = r.U16();
        var zones = new Dictionary<uint, int>();
        for (int i = 0; i < n; i++) { uint key = r.U32(); zones[key] = r.I32(); }
        if (!Following || from != Authority() || scene != StableHash.Of(Dungeons.Place)) return;
        var m = Manager;
        if (m?.entries == null) return;
        IndexZones();
        var mine = new Dictionary<int, WorldQuestManager.Entry>();
        foreach (var kv in m.entries) { int id = QuestId(kv.Value?.quest); if (id >= 0) mine[id] = kv.Value; }
        const int Active = 1;
        _applying = true;
        try
        {
            // 1. quests that ended there: the game's end here (abandon, the zone lets go of it)
            foreach (var (id, e) in mine)
                if ((int)e.state == Active && theirs.TryGetValue(id, out var t) && t.state != Active)
                    try { m.Expire(e); } catch (Exception ex) { Plugin.L.LogWarning("World quest end: " + ex.Message); }
            // 2. what each zone offers
            foreach (var (key, questId) in zones)
            {
                if (!Zones.TryGetValue(key, out var z) || z == null) continue;
                if (QuestId(z.CurrentQuest) == questId) continue;
                RPGWorldQuest wq = null;
                if (questId >= 0) foreach (var e in mine.Values) if (QuestId(e.quest) == questId) { wq = e.quest; break; }
                z.CurrentQuest = wq;
                try { z.CurrentQuestChanged?.Invoke(wq); } catch { }
                z.waitingForCooldown = false;
            }
            // 3. quests that started there: the game's start here (auto-accept in its zone)
            foreach (var (id, e) in mine)
                if ((int)e.state != Active && theirs.TryGetValue(id, out var t) && t.state == Active)
                    try { m.Activate(e); } catch (Exception ex) { Plugin.L.LogWarning("World quest start: " + ex.Message); }
            // 4. their timers
            float now = Time.time;
            foreach (var (id, e) in mine)
            {
                if (!theirs.TryGetValue(id, out var t)) continue;
                e.state = (WorldQuestState)t.state;
                e.endTime = now + t.left;
                e.suspended = t.suspended;
            }
        }
        finally { _applying = false; }
    }

    /// <summary>Completed here: the area's machine ends it for the area.</summary>
    private static void Completed(RPGQuest quest)
    {
        if (!Following || quest == null) return;
        W.Reset(); W.U8(Kind); W.U8(OpCompleted); W.I32(StableHash.Of(Dungeons.Place)); W.I32(quest.ID);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnCompleted(byte from, ref NetReader r)
    {
        int scene = r.I32(), questId = r.I32();
        if (Following || scene != StableHash.Of(Dungeons.Place)) return;
        var m = Manager;
        if (m?.entries == null) return;
        foreach (var kv in m.entries)
        {
            var e = kv.Value;
            if (e == null || QuestId(e.quest) != questId || (int)e.state != 1) continue;
            Plugin.L.LogInfo($"World quest {questId} completed by {Groups.Name(from)}: ends for the area");
            try { m.OnQuestCompleted(e.quest.quest); } catch (Exception ex) { Plugin.L.LogWarning("World quest complete: " + ex.Message); }
            SendSoon();
            break;
        }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        switch (r.U8())
        {
            case OpState: OnState(from, ref r); break;
            case OpCompleted: OnCompleted(from, ref r); break;
        }
    }

    public static string Dev()
    {
        var m = Manager;
        if (m?.entries == null) return "no world quests here";
        IndexZones();
        var parts = new List<string> { $"authority {Groups.Name(Authority())}" };
        foreach (var kv in m.entries)
        {
            var e = kv.Value;
            if (e?.quest?.quest == null) continue;
            parts.Add($"{e.quest.quest.entryName}:{e.state} {Mathf.Max(0, e.endTime - Time.time):0}s{(e.suspended ? " susp" : "")}");
        }
        foreach (var kv in Zones) parts.Add($"zone {kv.Value?.name} @{kv.Value?.transform.position}: {kv.Value?.CurrentQuest?.quest?.entryName ?? "-"}");
        return string.Join(" | ", parts);
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldQuestManager), nameof(WorldQuestManager.Update))]
        private static bool ManagerTicks() => !Following;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldQuestManager), nameof(WorldQuestManager.TryFillVacantSlot))]
        private static bool FillsSlots() => !Following;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldQuestZone), nameof(WorldQuestZone.PickNewCurrent))]
        private static bool ZonePicks() => !Following;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldQuestZone), nameof(WorldQuestZone.Update))]
        private static bool ZoneTicks() => !Following;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldQuestManager), nameof(WorldQuestManager.OnQuestCompleted))]
        private static void QuestCompleted(RPGQuest __0) { try { if (!_applying) Completed(__0); } catch { } }

        // the area's machine: changes go out at once
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldQuestManager), nameof(WorldQuestManager.Activate))]
        private static void Activated() { if (!_applying) SendSoon(); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldQuestManager), nameof(WorldQuestManager.Transition))]
        private static void Transitioned() { if (!_applying) SendSoon(); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldQuestZone), nameof(WorldQuestZone.PickNewCurrent))]
        private static void Picked() { if (!_applying) SendSoon(); }
    }
}
