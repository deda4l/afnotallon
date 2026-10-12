using System.Collections.Generic;
using AfallonCoop.Protocol;
using AfallonCoop.Session;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>Samples the local character (position, yaw, animator) and sends it at the configured rate.</summary>
internal static class PlayerSync
{
    private static uint _seq;
    private static double _nextSend;
    private static double _nextInfo;
    private static Vector3 _lastPos;
    private static double _lastPosTime;
    private static bool _wasInWorld;
    private static string _infoSignature = "";


    public static void Reset()
    {
        _seq = 0; _nextSend = 0; _nextInfo = 0; _infoSignature = ""; _wasInWorld = false; Delta.Reset(); _lastController = 0;
    }

    public static void Update(CoopSession s, double now)
    {
        if (now >= _nextInfo)
        {
            _nextInfo = now + 1;
            SendInfoIfChanged(s);
        }

        int rate = Mathf.Clamp(Plugin.CfgSendRate.Value, 5, 30);
        if (now < _nextSend) return;
        _nextSend = now + 1.0 / rate;

        var anim = GameInfo.PlayerAnimator;
        bool inWorld = anim != null;
        if (!inWorld)
        {
            if (_wasInWorld) s.SendLocalState(new PlayerStateMsg { Seq = ++_seq, SenderTime = now, InWorld = false });
            _wasInWorld = false;
            return;
        }

        var tr = anim.transform;
        var pos = tr.position;
        var vel = Vector3.zero;
        if (_wasInWorld && now > _lastPosTime)
        {
            vel = (pos - _lastPos) / (float)(now - _lastPosTime);
            if (vel.sqrMagnitude > 30f * 30f) vel = Vector3.zero; // teleport, not movement
        }
        _lastPos = pos;
        _lastPosTime = now;
        _wasInWorld = true;

        var m = new PlayerStateMsg
        {
            Seq = ++_seq,
            SenderTime = now,
            SceneHash = StableHash.Of(Dungeons.Place),
            PosX = pos.x, PosY = pos.y, PosZ = pos.z,
            RotY = tr.eulerAngles.y,
            VelX = vel.x, VelY = vel.y, VelZ = vel.z,
            InWorld = true,
            Flags = (GameInfo.PlayerArmed ? StateFlags.Armed : 0) | (PauseController.LocalMenuOpen ? StateFlags.InMenu : 0)
                    | (GameInfo.PlayerStealthed ? StateFlags.Stealth : 0) | (GameInfo.PlayerDead ? StateFlags.Dead : 0),
        };
        SampleAnimator(anim, m, now);
        ModelParts.FillActive(tr, m.ActiveParts);
        FillVitals(m, now);
        s.SendLocalState(m);
    }

    // ---- vitals: health for the group frames of the others ----

    private static List<int> _vitalStats;
    private static readonly Dictionary<int, (float cur, float max)> SentVitals = new();
    private static double _nextVitals;

    private static void FillVitals(PlayerStateMsg m, double now)
    {
        var me = GameInfo.Player;
        if (me == null) return;
        // Only health: the game's group frames show nothing else (portrait, health, role).
        _vitalStats ??= new List<int> { GameDatabase.Instance.GetHealthStat().ID };
        bool due = now >= _nextVitals, changed = false;
        foreach (var id in _vitalStats)
        {
            float cur, max;
            try { cur = CombatUtilities.GetCurrentStatValue(me, id); max = CombatUtilities.GetMaxStatValue(me, id); }
            catch { continue; }
            if (max <= 0) continue; // a stat this character does not use
            if (!SentVitals.TryGetValue(id, out var was) || Mathf.Abs(was.cur - cur) > 0.5f || Mathf.Abs(was.max - max) > 0.5f) changed = true;
            m.Vitals.Add(new Vital(id, cur, max));
        }
        if (!due && !changed) { m.Vitals.Clear(); return; }
        _nextVitals = now + 1.0;
        foreach (var v in m.Vitals) SentVitals[v.Stat] = (v.Current, v.Max);
    }

    private static readonly AnimatorSampler Sampler = new();
    private static readonly AnimDeltaEncoder Delta = new();
    private static readonly List<AnimParam> AllParams = new();
    private static int _lastController;

    private static void SampleAnimator(Animator anim, PlayerStateMsg m, double now)
    {
        AllParams.Clear();
        m.ControllerHash = Sampler.Sample(anim, AllParams, m.Layers);
        if (m.ControllerHash != _lastController) { _lastController = m.ControllerHash; Delta.Reset(); } // new parameter set
        m.AnimKeyframe = Delta.Encode(now, AllParams, m.Params);
    }

    private static void SendInfoIfChanged(CoopSession s)
    {
        var cd = GameInfo.Character;
        bool inWorld = GameInfo.InWorld;
        string charName = inWorld && cd != null ? cd.CharacterName : "";
        int cls = inWorld && cd != null ? cd.ClassID : -1;
        int race = inWorld && cd != null ? cd.RaceID : -1;
        string gender = inWorld && cd != null ? cd.Gender : "";
        int level = inWorld && cd != null ? cd.Level : 0;
        string scene = inWorld ? Dungeons.Place : "";
        var visuals = new PlayerInfo();
        if (inWorld) GameInfo.FillVisuals(visuals);
        visuals.RaceId = race; visuals.Gender = gender ?? "";
        byte role = 0;
        // the role chosen in the Dungeon Finder window (DungeonFinderService.Role answers by class, SelectedRole only once queued)
        try { if (inWorld) role = (byte)BLINK.RPGBuilder.Dungeons.DungeonFinderService.Instance.Data.PreferredRole; } catch { } // what the finder window saves (SavePreferredRole)
        string sig = $"{charName}|{cls}|{level}|{role}|{scene}|{visuals.VisualSignature}|{visuals.Materials}|{visuals.FxPrewarm.Count}";
        if (sig == _infoSignature) return;
        _infoSignature = sig;
        Prof.Count("info.sent");
        s.UpdateLocalInfo(p =>
        {
            p.Role = role;
            p.CharacterName = charName; p.ClassId = cls; p.RaceId = race; p.Gender = gender ?? ""; p.Level = level; p.Scene = scene;
            p.Appearance = visuals.Appearance; p.Armor = visuals.Armor; p.Weapons = visuals.Weapons; p.Materials = visuals.Materials;
            p.FxPrewarm = visuals.FxPrewarm;
        });
    }
}
