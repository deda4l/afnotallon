using System;
using AfallonCoop.Protocol;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Moving to a group member, from their frame's menu:
///  - "Summon": the member is asked (the game's confirmation popup) and, if they accept, comes to the summoner;
///  - "Travel to": one goes to the member at once.
/// The destination is the member's place (scene, with the group's dungeon instance) and spot, sent by whoever is there.
/// Same scene: a teleport; another scene: the game loads it and the character is put on the spot once there
/// (<see cref="Dungeons.GoTo"/>).
/// </summary>
internal static class Summon
{
    public const byte Kind = 17;
    private const byte OpSummon = 1, OpWhere = 2, OpHere = 3;
    private static readonly NetWriter W = new();

    /// <summary>A player of this session (a group member, a friend): the summoned one still says yes or no.</summary>
    private static bool InMyGroup(byte player)
    {
        var s = CoopManager.Session;
        return s != null && player != s.LocalId && s.Players.ContainsKey(player);
    }

    private static void Header(byte op, byte to) { W.Reset(); W.U8(Kind); W.U8(op); W.U8(to); }

    private static void WriteHere()
    {
        var me = GameInfo.Player;
        var p = me != null ? me.transform.position : Vector3.zero;
        W.Str(Dungeons.Place); W.F32(p.x); W.F32(p.y); W.F32(p.z);
    }

    /// <summary>Ask a member to come here.</summary>
    public static void SummonPlayer(byte player)
    {
        if (!InMyGroup(player) || GameInfo.Player == null) return;
        Header(OpSummon, player); WriteHere();
        CoopManager.Session.SendWorld(W.Span);
        Hud.Notify($"Summoning {Groups.Name(player)}...");
    }

    /// <summary>Go to a member: ask where they are, go there when they answer.</summary>
    public static void TravelTo(byte player)
    {
        if (!InMyGroup(player)) return;
        Header(OpWhere, player);
        CoopManager.Session.SendWorld(W.Span);
        _travel = (player, Time.unscaledTime);
    }

    /// <summary>"Travel to" asked of this player: only their answer, soon, moves me (a late one would pull me out mid-fight).</summary>
    private static (byte player, float at)? _travel;

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8(), to = r.U8();
        var s = CoopManager.Session;
        if (s == null || to != s.LocalId || !InMyGroup(from)) return;
        switch (op)
        {
            case OpSummon:
            {
                string place = r.Str();
                var pos = new Vector3(r.F32(), r.F32(), r.F32());
                string who = Groups.Name(from);
                _pending = (place, pos, who);
                Groups.Ask($"{who} summons you. Go there?", () => Go(place, pos, who), () => Hud.Notify($"You declined {who}'s summon"));
                break;
            }
            case OpWhere:
                if (GameInfo.Player == null) return;
                Header(OpHere, from); WriteHere();
                s.SendWorld(W.Span);
                break;
            case OpHere:
            {
                string place = r.Str();
                var pos = new Vector3(r.F32(), r.F32(), r.F32());
                if (_travel is not { } t || t.player != from || Time.unscaledTime - t.at > 10f) return;
                _travel = null;
                Go(place, pos, Groups.Name(from));
                break;
            }
        }
    }

    private static (string place, Vector3 pos, string who)? _pending; // the last summon shown (dev: answer without the popup)

    public static string DevAnswer()
    {
        if (_pending is not { } p) return "no summon";
        _pending = null;
        Go(p.place, p.pos, p.who);
        return "going to " + p.who;
    }

    private static void Go(string place, Vector3 pos, string who)
    {
        _pending = null;
        if (string.IsNullOrEmpty(place)) { Hud.Notify($"{who} is not in the world"); return; }
        Plugin.L.LogInfo($"Summon: to {who} at '{place}' {pos}");
        if (!Dungeons.GoTo(place, pos)) Hud.Notify($"Cannot get to {who} from here");
    }
}
