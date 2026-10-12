using System;
using System.Collections.Generic;
using System.Linq;
using AfallonCoop.Protocol;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// What the sync costs, for the debug line: the transport counts bytes per message type; the world and fx channels
/// (most of the sync) are split further by what the payload is (mobs, states, trade...), counted where this machine
/// sends them and where it reads them.
/// </summary>
internal static class TrafficView
{
    private const double Window = 5.0;
    private static readonly Queue<(double t, bool incoming, string what, int bytes)> Log = new();
    private static string _line = "";
    private static float _nextLine;

    public static void Wire(AfallonCoop.Session.CoopSession s)
    {
        s.PayloadSent += (fx, kind, bytes) => Count(false, Label(fx, kind), bytes);
        s.WorldReceived += (_, blob) => { if (blob.Length > 0) Count(true, Label(false, blob[0]), blob.Length); };
        s.FxReceived += (_, blob) => { if (blob.Length > 0) Count(true, Label(true, blob[0]), blob.Length); };
    }

    private static void Count(bool incoming, string what, int bytes)
    {
        double now = Time.realtimeSinceStartupAsDouble;
        Log.Enqueue((now, incoming, what, bytes));
        while (Log.Count > 0 && now - Log.Peek().t > Window) Log.Dequeue();
    }

    /// <summary>The sender's kind byte, by the module that owns it.</summary>
    private static string Label(bool fx, byte kind)
    {
        if (fx) return kind is VisualMirror.KindSpawn or VisualMirror.KindMove or VisualMirror.KindEnd ? "projectiles" : "effects";
        return kind switch
        {
            1 or 2 or 3 or 4 or 5 => "mobs",
            10 or 11 => "heals",
            14 => "bots",
            StateSync.KindPlayerStates => "states: players",
            StateSync.KindMobStates => "states: mobs",
            6 or 7 or 8 or 9 => "states: changes",
            WorldClock.Kind => "clock",
            Trade.Kind => "trade",
            Summon.Kind => "summon",
            Inspect.Kind => "inspect",
            Duel.Kind => "duel",
            GroupLoot.Kind => "loot",
            MeterSync.Kind => "meter",
            AuctionSync.Kind => "auction",
            Corruption.Kind => "corruption",
            Challenges.Kind => "challenges",
            Mail.Kind => "mail",
            WorldRules.Kind => "rules",
            WorldQuests.Kind => "world quests",
            Housing.Kind => "houses",
            BossSync.Kind => "telegraphs",
            BossSync.KindHitEffect => "hit effects",
            FlightVisual.Kind => "flights",
            PlayerActions.Kind => "ride",
            QuestShare.Kind => "quest share",
            ShellStats.Kind => "shell stats",
            Battlegrounds.Kind => "battleground",
            BossPhases.KindPhase or BossPhases.KindForce or BossPhases.KindChannel or BossPhases.KindHeroic => "boss",
            _ => "world " + kind,
        };
    }

    /// <summary>Dev: every world and fx category of the last seconds, each way: bytes and messages per second, average size.</summary>
    public static string Report()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var g in Log.GroupBy(e => (e.incoming, e.what)).OrderByDescending(g => g.Sum(e => e.bytes)))
            sb.AppendLine($"  {(g.Key.incoming ? "in " : "out")} {g.Key.what,-16} {g.Sum(e => e.bytes) / Window / 1024,6:0.00} KB/s {g.Count() / Window,6:0.0} msg/s  avg {g.Sum(e => e.bytes) / g.Count(),5} B");
        return sb.ToString();
    }

    /// <summary>"up 2.0 KB/s, down 15.8 KB/s · mobs 14.1 · players 1.4 · ..." (once a second).</summary>
    public static string Line()
    {
        if (Time.unscaledTime < _nextLine) return _line;
        _nextLine = Time.unscaledTime + 1f;
        var net = CoopManager.Net;
        if (net == null) return _line = "";
        double now = Time.realtimeSinceStartupAsDouble;
        while (Log.Count > 0 && now - Log.Peek().t > Window) Log.Dequeue();
        var rates = net.Stats.Rates();
        double up = rates.Where(r => !r.incoming).Sum(r => r.bytesPerSecond), down = rates.Where(r => r.incoming).Sum(r => r.bytesPerSecond);
        // the categories: transport types, with the world and fx ones replaced by what is in them
        var cats = new Dictionary<string, double>();
        foreach (var r in rates)
        {
            var t = (MsgType)r.type;
            if (t is MsgType.World or MsgType.WorldStream or MsgType.Fx or MsgType.FxStream) continue;
            string name = t == MsgType.PlayerState ? "players" : t.ToString().ToLowerInvariant();
            cats[name] = cats.TryGetValue(name, out var v) ? v + r.bytesPerSecond : r.bytesPerSecond;
        }
        foreach (var g in Log.GroupBy(e => e.what))
            cats[g.Key] = (cats.TryGetValue(g.Key, out var v) ? v : 0) + g.Sum(e => e.bytes) / Window;
        var top = cats.OrderByDescending(kv => kv.Value).Take(4).Where(kv => kv.Value >= 10).Select(kv => $"{kv.Key} {kv.Value / 1024:0.0}");
        return _line = $"traffic up {up / 1024:0.0} KB/s, down {down / 1024:0.0} KB/s  ·  " + string.Join("  ·  ", top);
    }
}
