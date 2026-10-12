using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Templates;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Combat visuals of the local character, mirrored on everyone else's copy of it: ability animations,
/// visual effects (casting hands, swings, impacts on self) and sounds.
///
/// We transmit what the owner's game decided (after combos, weapon variants, random picks, talents), not "ability X was
/// used": replaying an ability from the database on the receiver would have to repeat all of that selection logic
/// without a real combat entity behind the model. Templates travel as database IDs (see <see cref="AssetIndex"/>).
/// </summary>
internal static class FxSync
{
    private enum Kind : byte
    {
        Vfx = 1, Anim = 2, AnimTemplate = 3, Sound = 4, SoundTemplate = 5, DbEntry = 10,
        HandCastStart = 20, HandCastStop = 21, HandInstant = 22,
        /// <summary>A mob's area ability, executed by each player's game on its copy (see <see cref="BossSync"/>).</summary>
        MobAbility = 50,
        /// <summary>Prefix: what follows happened on a mob the sender owns (u32 net id), not on their character.</summary>
        MobSource = 40,
        /// <summary>Prefix: what follows happened on another player (u8 id), e.g. a heal of the sender's bot landing on them.</summary>
        OnPlayer = 41,
    }

    /// <summary>Entries that belong to an ability/effect record go by database reference; others by template.</summary>
    private static bool TrySendDbEntry(Il2CppSystem.Object entry)
    {
        EntryIndex.BuildIfNeeded();
        if (!EntryIndex.TryGet(entry, out var r)) return false;
        Begin(Kind.DbEntry); r.Write(W);
        Send();
        return true;
    }

    public static int Sent, Played, Missing;

    public static void Install(Harmony h)
    {
        h.PatchAll(typeof(Patches));
        Plugin.L.LogInfo("Fx sync installed");
    }

    internal static bool IsLocal(CombatEntity e)
    {
        if (e == null || !CoopManager.Connected || CoopManager.Session.PlayerCount < 2) return false;
        var p = GameInfo.Player;
        return p != null && e.Pointer == p.Pointer;
    }

    /// <summary>Net id of the owned mob the effect being captured belongs to; 0 = the local character.</summary>
    private static uint _srcMob;
    /// <summary>The remote player the effect being captured landed on (their shell here); -1 = none.</summary>
    private static int _onPlayer = -1;

    /// <summary>
    /// Visual effects and sounds also when they land on another player's shell: only this machine plays them (the shell
    /// is this machine's stand-in for them), so that player and everyone else get them through us. Not animations: a
    /// shell's animator is nobody's character.
    /// </summary>
    private static bool SourceOrPlayer(CombatEntity e)
    {
        if (_replaying) return false;
        if (Source(e)) return true;
        if (e == null || !CoopManager.Connected || CoopManager.Session.PlayerCount < 2 || !PlayerShells.TryGetPlayer(e, out var p)) return false;
        _onPlayer = p;
        return true;
    }

    /// <summary>Is this an entity whose effects we send: the local character, or a mob this machine runs.</summary>
    private static bool Source(CombatEntity e)
    {
        _srcMob = 0;
        _onPlayer = -1;
        if (_replaying) return false;
        if (IsLocal(e)) return true;
        if (e == null || !CoopManager.Connected || CoopManager.Session.PlayerCount < 2) return false;
        return MobSync.TryGetOwnedNetId(e, out _srcMob);
    }

    private static void Begin(Kind kind)
    {
        W.Reset();
        if (_srcMob != 0) { W.U8((byte)Kind.MobSource); W.U32(_srcMob); }
        else if (_onPlayer >= 0) { W.U8((byte)Kind.OnPlayer); W.U8((byte)_onPlayer); }
        W.U8((byte)kind);
    }

    // ---------------- capture (owner side) ----------------

    private static readonly NetWriter W = new();

    private static void Send()
    {
        CoopManager.Session.SendFx(W.Span);
        Prof.Count("fx.sent");
        Sent++;
    }

    private static void SendVfx(VisualEffectEntry v)
    {
        if (v == null || v.Template == null) return;
        if (TrySendDbEntry(v)) return;
        AssetIndex.BuildIfNeeded();
        Begin(Kind.Vfx);
        AssetIndex.Effects.Write(W, v.Template);
        W.Bool(v.UseNodeSocket && v.NodeSocket != null);
        if (v.UseNodeSocket && v.NodeSocket != null) AssetIndex.Sockets.Write(W, v.NodeSocket);
        W.Bool(v.ParentedToCaster);
        W.F32(v.PositionOffset.x); W.F32(v.PositionOffset.y); W.F32(v.PositionOffset.z);
        W.F32(v.Scale.x); W.F32(v.Scale.y); W.F32(v.Scale.z);
        W.Bool(v.Endless); W.F32(v.Duration); W.F32(v.Delay);
        Send();
    }

    private static void SendAnim(AnimationEntry a)
    {
        if (a == null || a.Template == null) return;
        if (TrySendDbEntry(a)) return;
        AssetIndex.BuildIfNeeded();
        Begin(Kind.Anim);
        AssetIndex.Animations.Write(W, a.Template);
        W.F32(a.Delay); W.Bool(a.ShowWeapons); W.F32(a.ShowWeaponsDuration);
        W.Bool(a.ModifySpeed); W.Str(a.SpeedParameterName ?? ""); W.F32(a.ModifierSpeed);
        Send();
    }

    private static void SendSound(SoundEntry s)
    {
        if (s == null || s.Template == null) return;
        if (TrySendDbEntry(s)) return;
        AssetIndex.BuildIfNeeded();
        Begin(Kind.Sound);
        AssetIndex.Sounds.Write(W, s.Template);
        W.F32(s.Delay); W.Bool(s.Parented);
        Send();
    }

    private static void SendAnimTemplate(AnimationTemplate t)
    {
        if (t == null) return;
        AssetIndex.BuildIfNeeded();
        Begin(Kind.AnimTemplate); AssetIndex.Animations.Write(W, t);
        Send();
    }

    private static void SendSoundTemplate(SoundTemplate t)
    {
        if (t == null) return;
        AssetIndex.BuildIfNeeded();
        Begin(Kind.SoundTemplate); AssetIndex.Sounds.Write(W, t);
        Send();
    }

    /// <summary>Hand glow while casting: CastingHandAuras reacts to combat events of real entities only.</summary>
    private static void SendHand(Kind kind, RPGAbility ability, RPGAbility.RPGAbilityRankData rank, float castDuration)
    {
        Begin(kind);
        if (kind != Kind.HandCastStop)
        {
            int rankIndex = ability?.ranks != null && rank != null ? ability.ranks.IndexOf(rank) : -1;
            W.I32(ability != null ? ability.ID : -1); W.I32(rankIndex); W.F32(castDuration);
        }
        Send();
    }

    /// <summary>An owned mob's spatial ability: the others execute it on its copy when their copy gets there.</summary>
    public static void SendMobAbility(CombatEntity mob, int abilityId, int rankIndex, byte targetKind, uint targetId)
    {
        if (!Source(mob) || _srcMob == 0) return;
        Begin(Kind.MobAbility); W.I32(abilityId); W.I32(rankIndex); W.U8(targetKind); W.U32(targetId);
        Send();
    }

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CastingHandAuras), nameof(CastingHandAuras.OnStartedCasting))]
        private static void HandStart(CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2, float __3)
        { try { if (Source(__0)) SendHand(Kind.HandCastStart, __1, __2, __3); } catch (Exception e) { Log(e); } } // mine or a bot/mob I run

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CastingHandAuras), nameof(CastingHandAuras.OnStoppedCasting))]
        private static void HandStop(CombatEntity __0)
        { try { if (Source(__0)) SendHand(Kind.HandCastStop, null, null, 0f); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CastingHandAuras), nameof(CastingHandAuras.OnInstantAbilityUsed))]
        private static void HandInstant(CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2)
        { try { if (Source(__0)) SendHand(Kind.HandInstant, __1, __2, 0f); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerVisualEffect), typeof(CombatEntity), typeof(VisualEffectEntry))]
        private static void Vfx(CombatEntity __0, VisualEffectEntry __1)
        { try { if (SourceOrPlayer(__0)) SendVfx(__1); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerVisualEffectsList))]
        private static void VfxList(CombatEntity __0, Il2CppSystem.Collections.Generic.List<VisualEffectEntry> __1, ActivationType __2)
        {
            try
            {
                if (__1 == null || !SourceOrPlayer(__0)) return;
                foreach (var v in __1) if (v != null && v.ActivationType == __2) SendVfx(v);
            }
            catch (Exception e) { Log(e); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerAnimation), typeof(CombatEntity), typeof(AnimationEntry))]
        private static void Anim(CombatEntity __0, AnimationEntry __1)
        { try { if (Source(__0)) SendAnim(__1); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerAnimationsList))]
        private static void AnimList(CombatEntity __0, Il2CppSystem.Collections.Generic.List<AnimationEntry> __1, ActivationType __2)
        {
            try
            {
                if (__1 == null || !Source(__0)) return;
                foreach (var a in __1) if (a != null && a.ActivationType == __2) SendAnim(a);
            }
            catch (Exception e) { Log(e); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerAnimationTemplate))]
        private static void AnimTemplate(CombatEntity __0, AnimationTemplate __1)
        { try { if (Source(__0)) SendAnimTemplate(__1); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerSound), typeof(CombatEntity), typeof(SoundEntry), typeof(Transform))]
        private static void Sound(CombatEntity __0, SoundEntry __1)
        { try { if (SourceOrPlayer(__0)) SendSound(__1); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerSound), typeof(CombatEntity), typeof(SoundTemplate), typeof(Transform))]
        private static void SoundT(CombatEntity __0, SoundTemplate __1)
        { try { if (SourceOrPlayer(__0)) SendSoundTemplate(__1); } catch (Exception e) { Log(e); } }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(GameEvents), nameof(GameEvents.OnTriggerSoundsList))]
        private static void SoundList(CombatEntity __0, Il2CppSystem.Collections.Generic.List<SoundEntry> __1, ActivationType __2)
        {
            try
            {
                if (__1 == null || !SourceOrPlayer(__0)) return;
                foreach (var s in __1) if (s != null && s.ActivationType == __2) SendSound(s);
            }
            catch (Exception e) { Log(e); }
        }
    }

    private static float _lastLog;
    private static void Log(Exception e)
    {
        if (Time.unscaledTime - _lastLog < 2f) return;
        _lastLog = Time.unscaledTime;
        Plugin.L.LogWarning("Fx: " + e.Message);
    }

    // ---------------- replay (receiving side) ----------------

    private struct Pending { public float At; public byte Id; public byte[] Data; }
    private static readonly List<Pending> Queue = new();

    /// <summary>Queue an effect; it plays when the puppet's (delayed) motion reaches the moment it happened.</summary>
    public static void Receive(byte id, byte[] data)
    {
        float delay = data.Length > 0 && data[0] == (byte)Kind.MobSource ? (float)MobSync.RenderDelay : (float)Puppets.RenderDelay;
        Queue.Add(new Pending { At = Time.unscaledTime + delay, Id = id, Data = data });
    }

    public static void Update()
    {
        if (Queue.Count == 0) return;
        float now = Time.unscaledTime;
        for (int i = 0; i < Queue.Count; i++)
        {
            if (Queue[i].At > now) continue;
            var p = Queue[i];
            Queue.RemoveAt(i--);
            _replaying = true;
            try { Prof.Run("fx.play1", () => Play(p.Id, p.Data)); }
            catch (Exception e) { Log(e); }
            finally { _replaying = false; }
        }
    }

    /// <summary>
    /// Playing what someone sent: the game's calls used for it are the ones captured above (an effect on this player's
    /// own character would go out again as theirs, and everyone would see it twice).
    /// </summary>
    private static bool _replaying;

    private static CastingHandAuras _hands;
    private static readonly Dictionary<long, List<GameObject>> HandAuras = new(); // by player id, or by a bot copy's object

    /// <summary>Same rules as the game's CastingHandAuras (cast longer than 0.05 s, human rig, both hands), on a puppet or a bot's copy.</summary>
    private static void PlayHand(long id, Kind kind, ref NetReader r, Animator anim)
    {
        if (HandAuras.TryGetValue(id, out var old))
        {
            if (kind != Kind.HandInstant) { foreach (var g in old) if (g != null) Object.Destroy(g); HandAuras.Remove(id); }
        }
        if (kind == Kind.HandCastStop) { Played++; return; }
        int abilityId = r.I32(), rankIndex = r.I32();
        float castDuration = r.F32();
        if (kind == Kind.HandCastStart && castDuration <= 0.05f) return;
        if (anim == null || !anim.isHuman) return;
        if (_hands == null) _hands = Object.FindObjectOfType<CastingHandAuras>();
        if (_hands == null) { Missing++; return; }
        var abilities = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetAbilities();
        if (!abilities.ContainsKey(abilityId)) { Missing++; return; }
        var ability = abilities[abilityId];
        var rank = ability.ranks != null && rankIndex >= 0 && rankIndex < ability.ranks.Count ? ability.ranks[rankIndex] : null;
        var prefab = _hands.AuraPrefabFor(ability, rank);
        if (prefab == null) return; // ability without a hand glow
        var spawned = new Il2CppSystem.Collections.Generic.List<GameObject>();
        _hands.SpawnOnBone(anim, HumanBodyBones.LeftHand, prefab, spawned);
        _hands.SpawnOnBone(anim, HumanBodyBones.RightHand, prefab, spawned);
        if (kind == Kind.HandInstant)
        {
            foreach (var g in spawned) if (g != null) Object.Destroy(g, _hands.instantAuraDuration);
        }
        else
        {
            var list = new List<GameObject>();
            foreach (var g in spawned) list.Add(g);
            HandAuras[id] = list;
        }
        Played++;
    }

    private static void Play(byte id, byte[] data)
    {
        var r = new NetReader(data);
        var kind = (Kind)r.U8();
        CombatEntity entity = null; // a mob copy: played through the game's entity overloads (its own sockets)
        GameObject go; Animator anim;
        bool visible, onPlayer = false;
        if (kind == Kind.MobSource)
        {
            var copy = MobSync.GetCopy(r.U32());
            if (copy == null) return; // not near that mob here
            entity = copy; go = copy.gameObject; anim = copy.ThisAnimator ?? copy.GetComponentInChildren<Animator>();
            visible = true;
            kind = (Kind)r.U8();
        }
        else if (kind == Kind.OnPlayer)
        {
            byte on = r.U8();
            onPlayer = true;
            kind = (Kind)r.U8();
            if (kind is not (Kind.Vfx or Kind.Sound or Kind.SoundTemplate or Kind.DbEntry)) return;
            var me = GameInfo.Player;
            if (on == CoopManager.Session.LocalId)
            {
                if (me == null) return;
                entity = me; go = me.gameObject; anim = null; visible = true; // on this player's own character
            }
            else visible = Puppets.TryGetVisual(on, out go, out anim);
        }
        else visible = Puppets.TryGetVisual(id, out go, out anim);
        if (kind is (Kind)VisualMirror.KindMove or (Kind)VisualMirror.KindEnd or (Kind)VisualMirror.KindSpawn)
        {
            // Mirrors also apply while the owner is out of sight (a mob's projectile, nothing left hanging).
            VisualMirror.Play(id, (byte)kind, ref r);
            return;
        }
        if (!visible) return;
        var ge = GameEvents.Instance;
        if (ge == null) return;
        AssetIndex.BuildIfNeeded();
        Prof.Count("fx.recv." + kind);
        switch (kind)
        {
            case Kind.MobAbility:
                if (entity != null) { int ab = r.I32(), rk = r.I32(); byte tk = r.U8(); uint tid = r.U32(); BossSync.Execute(entity, ab, rk, tk, tid); }
                break;
            case Kind.HandCastStart:
            case Kind.HandInstant:
            case Kind.HandCastStop:
                if (entity != null && kind != Kind.HandInstant)
                {
                    // a mob's cast: the game's own cast event on its copy (its hands' glow too)
                    if (kind == Kind.HandCastStop) BossSync.CastStopped(entity);
                    else { int ab = r.I32(), rk = r.I32(); float cast = r.F32(); BossSync.CastStarted(entity, ab, rk, cast); }
                    break;
                }
                if (!onPlayer) PlayHand(entity == null ? id : entity.Pointer.ToInt64(), kind, ref r, anim); // a copy has no real casts here
                break;
            case Kind.DbEntry:
            {
                var rf = EntryIndex.Ref.Read(ref r);
                var entry = EntryIndex.Resolve(rf);
                if (entry == null) { Missing++; return; }
                Prof.Count("fx.recv.db." + rf.List);
                switch (rf.List)
                {
                    case EntryIndex.ListKind.Vfx: PlayVfx(ge, entity, go, entry.Cast<VisualEffectEntry>()); break;
                    case EntryIndex.ListKind.Anim: if (!onPlayer) PlayAnim(ge, entity, anim, entry.Cast<AnimationEntry>()); break;
                    case EntryIndex.ListKind.Sound: PlaySound(ge, entity, go, entry.Cast<SoundEntry>()); break;
                }
                break;
            }
            case Kind.Vfx:
            {
                var t = AssetIndex.Effects.Read(ref r);
                var e = new VisualEffectEntry { Template = t, ActivationType = ActivationType.Start };
                if (r.Bool())
                {
                    e.NodeSocket = AssetIndex.Sockets.Read(ref r);
                    e.UseNodeSocket = e.NodeSocket != null;
                }
                e.ParentedToCaster = r.Bool();
                e.PositionOffset = new Vector3(r.F32(), r.F32(), r.F32());
                e.Scale = new Vector3(r.F32(), r.F32(), r.F32());
                e.Endless = r.Bool(); e.Duration = r.F32(); e.Delay = r.F32();
                if (t == null) { Missing++; return; }
                PlayVfx(ge, entity, go, e);
                break;
            }
            case Kind.Anim:
            {
                var t = AssetIndex.Animations.Read(ref r);
                var e = new AnimationEntry
                {
                    Template = t, ActivationType = ActivationType.Start,
                    Delay = r.F32(), ShowWeapons = r.Bool(), ShowWeaponsDuration = r.F32(),
                    ModifySpeed = r.Bool(), SpeedParameterName = r.Str(), ModifierSpeed = r.F32(),
                };
                if (t == null || anim == null) { Missing++; return; }
                PlayAnim(ge, entity, anim, e);
                break;
            }
            case Kind.AnimTemplate:
            {
                var t = AssetIndex.Animations.Read(ref r);
                if (t == null || anim == null) { Missing++; return; }
                if (entity != null) ge.OnTriggerAnimationTemplate(entity, t);
                else ge.OnTriggerAnimation(anim, new AnimationEntry { Template = t, ActivationType = ActivationType.Start });
                break;
            }
            case Kind.Sound:
            {
                var t = AssetIndex.Sounds.Read(ref r);
                var e = new SoundEntry { Template = t, ActivationType = ActivationType.Start, Delay = r.F32(), Parented = r.Bool() };
                if (t == null) { Missing++; return; }
                PlaySound(ge, entity, go, e);
                break;
            }
            case Kind.SoundTemplate:
            {
                var t = AssetIndex.Sounds.Read(ref r);
                if (t == null) { Missing++; return; }
                if (entity != null) ge.OnTriggerSound(entity, t, go.transform);
                else ge.OnTriggerSound(go, t, go.transform);
                break;
            }
        }
        Played++;
    }

    private static void PlayVfx(GameEvents ge, CombatEntity entity, GameObject go, VisualEffectEntry e)
    {
        if (entity != null) ge.OnTriggerVisualEffect(entity, e); else ge.OnTriggerVisualEffect(go, e);
    }

    private static void PlayAnim(GameEvents ge, CombatEntity entity, Animator anim, AnimationEntry e)
    {
        if (entity != null) ge.OnTriggerAnimation(entity, e); else if (anim != null) ge.OnTriggerAnimation(anim, e);
    }

    private static void PlaySound(GameEvents ge, CombatEntity entity, GameObject go, SoundEntry e)
    {
        if (entity != null) ge.OnTriggerSound(entity, e, go.transform); else ge.OnTriggerSound(go, e, go.transform);
    }
}
