using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.World;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The world's rules are the host's: its Heroic mode (the game's tier, kept in the character's save: mobs' health,
/// damage and affixes, essence, heroic loot) and its world modifiers (the game's sliders: XP rate, drop rate, NPC and
/// adventurer stats). The host sends them; a client's game reads the host's values instead of its character's own
/// (the client's save keeps its own, for playing alone), and its Heroic console and sliders say the host sets them.
/// </summary>
internal static class WorldRules
{
    public const byte Kind = 24;
    private const float SendEvery = 3f;
    private static readonly NetWriter W = new();
    private static float _nextSend;
    private static bool _have, _heroic;
    private static readonly Dictionary<string, float> Modifiers = new();
    private static bool _reading; // the host's own values, for sending

    private static bool Following => _have && CoopManager.Connected && CoopManager.Session is { IsHost: false };

    public static void Update()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected) WorldRandom.Reset();
        if (s == null) return;
        if (!s.IsHost) { if (!CoopManager.Connected) Reset(); return; }
        if (!GameInfo.InWorld || Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + SendEvery;
        Send();
    }

    private static void Send()
    {
        var s = CoopManager.Session;
        var values = new List<(string id, float v)>();
        bool heroic;
        _reading = true;
        try
        {
            heroic = HeroicTierManager.IsActive;
            WorldModifierManager.EnsureRegistered();
            foreach (var m in WorldModifierManager.modifiers)
                if (m?.id != null) { float v = WorldModifierManager.GetMultiplier(m.id); if (Mathf.Abs(v - WorldModifierManager.DefaultValue) > 0.0001f) values.Add((m.id, v)); } // the rest: the default
        }
        catch (Exception e) { Plugin.L.LogWarning("World rules: " + e.Message); return; }
        finally { _reading = false; }
        W.Reset(); W.U8(Kind); W.U8((byte)(heroic ? 1 : 0)); W.U16((ushort)values.Count);
        foreach (var (id, v) in values) { W.Str(id); W.F32(v); }
        W.I32(WorldRandom.HostSeed);
        s.SendWorld(W.Span);
    }

    public static void Receive(byte from, ref NetReader r)
    {
        bool heroic = r.U8() == 1;
        int n = r.U16();
        var values = new Dictionary<string, float>();
        for (int i = 0; i < n; i++) { var id = r.Str(); values[id] = r.F32(); }
        int seed = r.I32();
        var s = CoopManager.Session;
        if (s == null || s.IsHost || from != ProtocolInfo.HostId) return;
        WorldRandom.FromHost(seed);
        bool first = !_have;
        bool heroicChanged = first || heroic != _heroic;
        var changed = new List<string>();
        foreach (var kv in values) if (!Modifiers.TryGetValue(kv.Key, out var old) || Mathf.Abs(old - kv.Value) > 0.0001f) changed.Add(kv.Key);
        foreach (var kv in Modifiers) if (!values.ContainsKey(kv.Key)) changed.Add(kv.Key);
        Modifiers.Clear();
        foreach (var kv in values) Modifiers[kv.Key] = kv.Value;
        _heroic = heroic;
        _have = true;
        if (heroicChanged) OnHeroicChanged(first);
        foreach (var id in changed) try { WorldModifierManager.ModifierChanged?.Invoke(id, values.TryGetValue(id, out var v) ? v : WorldModifierManager.DefaultValue); } catch { }
        if (first) Plugin.L.LogInfo($"World rules from the host: heroic {heroic}, {values.Count} modifiers");
    }

    private static void OnHeroicChanged(bool first)
    {
        try { HeroicTierManager.SyncTierBuff(); } catch (Exception e) { Plugin.L.LogWarning("Heroic buff: " + e.Message); }
        try { HeroicTierManager.TierChanged?.Invoke(_heroic); } catch { }
        try { foreach (var c in UnityEngine.Object.FindObjectsOfType<HeroicConsole>()) c.RefreshActiveVisual(_heroic); } catch { }
        if (!first || _heroic) Hud.Notify(_heroic ? "Heroic mode is on (the host's world)" : "Heroic mode is off (the host's world)");
    }

    private static void Reset()
    {
        if (!_have) return;
        _have = false;
        Modifiers.Clear();
        try { HeroicTierManager.SyncTierBuff(); } catch { }
    }

    public static string Dev() =>
        !_have ? (CoopManager.Session?.IsHost == true ? "host: sending its own rules" : "no rules from the host yet")
               : $"heroic {_heroic}; " + string.Join(", ", System.Linq.Enumerable.Select(Modifiers, kv => $"{kv.Key}={kv.Value:0.##}"));

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroicTierManager), nameof(HeroicTierManager.IsActive), MethodType.Getter)]
        private static void HeroicIsTheHosts(ref bool __result) { if (Following && !_reading) __result = _heroic; }

        /// <summary>The host switched Heroic mode: the others at once.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(HeroicTierManager), nameof(HeroicTierManager.SetActiveInternal))]
        private static void HostSwitched() { if (CoopManager.Session?.IsHost == true) _nextSend = 0f; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(HeroicConsole), nameof(HeroicConsole.TryInteract))]
        private static bool ConsoleIsTheHosts()
        {
            if (!Following) return true;
            Hud.Notify("The host sets Heroic mode for the world");
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldModifierManager), nameof(WorldModifierManager.GetMultiplier))]
        private static bool ModifierIsTheHosts(string __0, ref float __result)
        {
            if (!Following || _reading || __0 == null) return true;
            __result = Modifiers.TryGetValue(__0, out var v) ? v : WorldModifierManager.DefaultValue; // not sent: the default
            return false;
        }

        /// <summary>The sliders show the host's values, locked.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldModifierManager), nameof(WorldModifierManager.IsLocked), MethodType.Getter)]
        private static void LockedForClients(ref bool __result) { if (Following && GameInfo.InWorld) __result = true; }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(WorldModifierManager), nameof(WorldModifierManager.SetMultiplier))]
        private static bool NoChangeOnClients() => !(Following && GameInfo.InWorld);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldModifierManager), nameof(WorldModifierManager.SetMultiplier))]
        private static void HostChanged() { if (CoopManager.Session?.IsHost == true) _nextSend = Mathf.Min(_nextSend, Time.unscaledTime + 0.5f); }
    }
}
