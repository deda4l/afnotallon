using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// A player's stats on their shell elsewhere: armor, resistances, dodge, block and the rest. The game computes a hit
/// on the attacker's machine against the target's stats (CombatCalculations reads the target's armor, resistances,
/// avoidance), and for a remote player the target there is their shell: with the template's stats a plate-armored
/// warrior would take a hit like a naked one, from a mob or from another player. Health comes with the player's state
/// (<see cref="PlayerShells"/>); every other stat comes here: the owner sends its values when they change (and now
/// and then for late comers), the others put them on the shell and keep them there (the game recomputes a shell's
/// stats from its own template and states now and then).
/// </summary>
internal static class ShellStats
{
    public const byte Kind = 39;
    private const float SendEvery = 0.25f, FullEvery = 5f, EnforceEvery = 0.2f;

    private static readonly NetWriter W = new();
    private static readonly Dictionary<int, (float cur, float min, float max)> Sent = new();
    private static float _nextSend, _nextFull, _nextEnforce;

    /// <summary>The last values each player sent, by stat.</summary>
    private static readonly Dictionary<byte, Dictionary<int, (float cur, float min, float max)>> Of = new();

    public static void Update()
    {
        if (!CoopManager.Connected || !GameInfo.InWorld || CoopManager.Session.PlayerCount < 2) { Sent.Clear(); return; }
        float now = Time.unscaledTime;
        if (now >= _nextSend) { _nextSend = now + SendEvery; Send(now >= _nextFull); }
        if (now >= _nextEnforce) { _nextEnforce = now + EnforceEvery; Enforce(); }
    }

    private static void Send(bool full)
    {
        var me = GameInfo.Player;
        if (me == null) return;
        if (full) { _nextFull = Time.unscaledTime + FullEvery; Sent.Clear(); }
        int health = MobSync.HealthStat;
        W.Reset(); W.U8(Kind);
        int countAt = W.Length; W.U16(0);
        ushort n = 0;
        try
        {
            foreach (var kv in me.GetStats())
            {
                var st = kv.Value;
                if (kv.Key == health || st == null) continue;
                var v = (st.currentValue, st.currentMinValue, st.currentMaxValue);
                if (Sent.TryGetValue(kv.Key, out var was) && Same(was, v)) continue;
                Sent[kv.Key] = v;
                W.I32(kv.Key); W.F32(v.currentValue); W.F32(v.currentMinValue); W.F32(v.currentMaxValue);
                n++;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Shell stats: " + e.Message); return; }
        if (n == 0) return;
        W.PatchU16(countAt, n);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static bool Same((float cur, float min, float max) a, (float cur, float min, float max) b) =>
        Mathf.Abs(a.cur - b.cur) < 0.001f && Mathf.Abs(a.min - b.min) < 0.001f && Mathf.Abs(a.max - b.max) < 0.001f;

    public static void Receive(byte from, ref NetReader r)
    {
        int n = r.U16();
        if (!Of.TryGetValue(from, out var map)) Of[from] = map = new();
        for (int i = 0; i < n; i++) map[r.I32()] = (r.F32(), r.F32(), r.F32());
        var shell = PlayerShells.Get(from);
        if (shell != null) Apply(shell, map);
    }

    /// <summary>Each shell keeps its player's values (put back when the game recomputed them).</summary>
    private static void Enforce()
    {
        foreach (var kv in Of)
        {
            var shell = PlayerShells.Get(kv.Key);
            if (shell != null) Apply(shell, kv.Value);
        }
    }

    private static void Apply(CombatEntity shell, Dictionary<int, (float cur, float min, float max)> map)
    {
        Il2CppSystem.Collections.Generic.Dictionary<int, BLINK.RPGBuilder.Combat.CombatData.CombatEntityStat> stats;
        try { stats = shell.GetStats(); } catch { return; }
        if (stats == null) return;
        foreach (var kv in map)
        {
            if (!stats.ContainsKey(kv.Key)) continue; // a stat the shell's template lacks
            var st = stats[kv.Key];
            if (st == null || Same((st.currentValue, st.currentMinValue, st.currentMaxValue), kv.Value)) continue;
            st.currentMinValue = kv.Value.min;
            st.currentMaxValue = kv.Value.max;
            st.currentValue = kv.Value.cur;
        }
    }

    public static void OnPlayerLeft(byte player) => Of.Remove(player);

    public static void Reset() { Of.Clear(); Sent.Clear(); }

    public static string Describe(byte player)
    {
        var shell = PlayerShells.Get(player);
        if (!Of.TryGetValue(player, out var map)) return "nothing received";
        int differ = 0, missing = 0;
        try
        {
            var stats = shell?.GetStats();
            foreach (var kv in map)
            {
                if (stats == null || !stats.ContainsKey(kv.Key)) { missing++; continue; }
                var st = stats[kv.Key];
                if (!Same((st.currentValue, st.currentMinValue, st.currentMaxValue), kv.Value)) differ++;
            }
        }
        catch { }
        return $"#{player}: {map.Count} stats received, shell {(shell != null ? "here" : "absent")}, {differ} differ, {missing} missing on the shell";
    }
}
