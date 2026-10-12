using System;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Status effects (buffs, debuffs, crowd control) across machines. Every new state is announced by the game
/// (CombatEvents.OnStateStarted), and is applied where the target really lives:
///  - on a remote player's shell (a mob's web, an ally's buff): applied to that player's character on their machine;
///  - on a mob copy, by the local player: applied to the real mob by its owner;
///  - on a mob this machine runs: mirrored onto everyone's copies (icons, auras), ended with it.
/// Copies never take damage or healing from their mirrored states (MobSync blocks non-local hits on copies).
/// </summary>
internal static class StateSync
{
    public const byte KindToPlayer = 6, KindToOwner = 7, KindMobAdd = 8, KindMobEnd = 9, KindPlayerStates = 12, KindMobStates = 13;

    private static bool _applying; // our own replays must not be captured again

    /// <summary>Dev: log each state message sent and received, with wall-clock milliseconds (copies on one PC compare).</summary>
    public static bool Trace;
    private static void T(string what)
    {
        if (Trace) Plugin.L.LogInfo($"[states {DateTime.Now:HH:mm:ss.fff} fps {1f / Mathf.Max(Time.smoothDeltaTime, 0.0001f):0}] {what}");
    }
    private static readonly NetWriter W = new();

    private static bool Active => CoopManager.Connected && CoopManager.Session.PlayerCount >= 2 && GameInfo.InWorld;

    private static RPGEffect Effect(int id)
    {
        var all = GameDatabase.Instance.GetEffects();
        return all.ContainsKey(id) ? all[id] : null;
    }

    /// <summary>What a state looks like on the wire: effect, rank, icon (by sprite name), stacks, seconds it has run.</summary>
    private struct Wire
    {
        public int Id, Rank, Icon;
        public byte Stack;
        public float Elapsed;

        public static Wire Of(BLINK.RPGBuilder.Combat.CombatData.CombatEntityStateEffect st) => new()
        {
            Id = st.stateEffectID, Rank = st.effectRankIndex, Icon = Sprites.Code(st.stateIcon),
            Stack = (byte)Math.Clamp(st.curStack, 0, 255), Elapsed = st.stateCurDuration,
        };

        public void Write(NetWriter w) { w.I32(Id); w.I32(Rank); w.I32(Icon); w.U8(Stack); w.F32(Elapsed); }
        public static Wire Read(ref NetReader r) => new() { Id = r.I32(), Rank = r.I32(), Icon = r.I32(), Stack = r.U8(), Elapsed = r.F32() };
    }

    /// <summary>
    /// A state on a real entity, asked for by another machine (a mob's debuff on me, my heal over time on a bot I run):
    /// through the game's own InitEntityState, which refreshes or stacks a state already there, shortens crowd control
    /// by resistances and starts the aggro, as when it is cast here.
    /// </summary>
    private static void ApplyReal(RPGEffect effect, int rank, int icon, CombatEntity caster, CombatEntity target)
    {
        if (effect == null || target == null || caster == null) return;
        try { CombatManager.Instance.StartCoroutine(CombatManager.Instance.InitEntityState(caster, target, effect, rank, Sprites.Find(icon) ?? effect.icon, 0f)); }
        catch (Exception e) { Plugin.L.LogWarning("State sync apply: " + e.Message); }
    }

    /// <summary>
    /// A state shown on a mirror (a copy, a shell): added once per effect, then kept to the owner's stacks and time. The
    /// game raises OnStateStarted itself when it adds one (buff bars, nameplate icons); a change of an existing one is
    /// announced as a refresh, as the game does when a state is cast again.
    /// </summary>
    private static void Mirror(CombatEntity target, Wire w)
    {
        var effect = Effect(w.Id);
        if (effect == null || target == null) return;
        _applying = true;
        try
        {
            int i = IndexOf(target, w.Id);
            bool added = i < 0;
            var icon = Sprites.Find(w.Icon);
            if (added)
            {
                CombatManager.Instance.InitNewStateEffect(effect, w.Rank, target, icon ?? effect.icon, target);
                i = IndexOf(target, w.Id);
                T($"shown +{w.Id} on {target.name}");
                if (i < 0) return;
            }
            var st = target.GetStates()[i];
            int stack = Math.Max(1, (int)w.Stack);
            bool newIcon = icon != null && st.stateIcon?.Pointer != icon.Pointer; // a sprite not found the first time
            if (!added && !newIcon && st.curStack == stack && Math.Abs(st.stateCurDuration - w.Elapsed) < 1.5f) return;
            if (added && stack <= 1 && w.Elapsed < 0.5f) return;
            if (newIcon) st.stateIcon = icon;
            st.curStack = stack;
            st.stateCurDuration = w.Elapsed;
            CombatEvents.Instance?.OnStateRefreshed(target, i);
        }
        catch (Exception e) { Plugin.L.LogWarning("State sync mirror: " + e.Message); }
        finally { _applying = false; }
    }

    private static int IndexOf(CombatEntity e, int effectId)
    {
        var states = e.GetStates();
        if (states != null) for (int i = 0; i < states.Count; i++) if (states[i] != null && states[i].stateEffectID == effectId) return i;
        return -1;
    }

    private static void End(MobCombatEntity e, int index)
    {
        T($"ended -{e.GetStates()[index]?.stateEffectID} on {e.name}");
        _applying = true;
        try { e.EndStateEffect(index); }
        catch (Exception ex) { Plugin.L.LogWarning("State sync end: " + ex.Message); }
        finally { _applying = false; }
    }

    /// <summary>
    /// The game announces every new state (InitNewStateEffect and the main path of InitEntityState, which builds the
    /// state itself, both raise it). On a mirror cast from here, the state stays as a prediction (shown at once, its
    /// caster made the mirror itself so its pulses act on nobody) and goes to where the target lives; on a mob we run,
    /// the copies follow; on me, my list goes out at once.
    /// </summary>
    private static void OnStarted(CombatEntity target, int index)
    {
        if (_applying || !Active || target == null) return;
        var states = target.GetStates();
        if (states == null || index < 0 || index >= states.Count || states[index] == null) return;
        var st = states[index];
        if (PlayerShells.TryGetPlayer(target, out _) || MobSync.IsCopy(target))
        {
            Forward(st.stateEffectID, st.effectRankIndex, Sprites.Code(st.stateIcon), st.casterEntity, target);
            st.casterEntity = target;
        }
        else if (MobSync.TryGetOwnedNetId(target, out var id)) SendMobState(id, Wire.Of(st));
        else if (target.Pointer == GameInfo.Player?.Pointer) _sentStates = "";
    }

    /// <summary>A state cast here on a mirror goes to the machine where the target lives.</summary>
    private static void Forward(int effectId, int rank, int icon, CombatEntity caster, CombatEntity target)
    {
        var s = CoopManager.Session;
        if (PlayerShells.TryGetPlayer(target, out var player))
        {
            MobSync.TryGetOwnedNetId(caster, out var mobId); // 0 when the caster is a player
            W.Reset(); W.U8(KindToPlayer); W.U8(player); W.I32(effectId); W.I32(rank); W.U32(mobId); W.I32(icon);
            s.SendWorld(W.Span);
        }
        else if (MobSync.IsCopy(target))
        {
            // From the local player (0) or a mob/bot we run; otherwise it comes with the owner's mirror.
            if (!MobSync.TryGetOwnedNetId(caster, out var casterId) && !MobSync.IsLocalSide(caster)) return;
            W.Reset(); W.U8(KindToOwner); W.U32(MobSync.NetIdOfCopy(target)); W.U32(casterId); W.I32(effectId); W.I32(rank); W.I32(icon);
            s.SendWorld(W.Span);
        }
    }

    private static void SendMobState(uint id, Wire w)
    {
        T($"send #{id} +{w.Id} x{w.Stack} {w.Elapsed:0.0}s");
        W.Reset(); W.U8(KindMobAdd); W.U32(id); w.Write(W);
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>
    /// The game cast a state again on a target that already had it (more time, one more stack). On a mirror, the real
    /// target gets it cast again where it lives; on a mob we run, the copies follow; on me, my list goes out at once.
    /// </summary>
    private static void OnRefreshed(CombatEntity target, int index)
    {
        if (_applying || !Active || target == null) return;
        var states = target.GetStates();
        if (states == null || index < 0 || index >= states.Count || states[index] == null) return;
        var st = states[index];
        if (PlayerShells.TryGetPlayer(target, out _) || MobSync.IsCopy(target))
            Forward(st.stateEffectID, st.effectRankIndex, Sprites.Code(st.stateIcon), GameInfo.Player, target);
        else if (MobSync.TryGetOwnedNetId(target, out var id)) SendMobState(id, Wire.Of(st));
        else if (target.Pointer == GameInfo.Player?.Pointer) _sentStates = "";
    }

    private static void BeforeEndState(MobCombatEntity mob, int index)
    {
        if (_applying || !Active || !MobSync.TryGetOwnedNetId(mob, out var id)) return;
        var states = mob.GetStates();
        if (states == null || index < 0 || index >= states.Count || states[index] == null) return;
        T($"send #{id} -{states[index].stateEffectID}");
        W.Reset(); W.U8(KindMobEnd); W.U32(id); W.I32(states[index].stateEffectID);
        CoopManager.Session.SendWorld(W.Span);
    }

    // ---------------- a player's own states, shown on their shell elsewhere ----------------
    //
    // A player's buffs and debuffs live on their machine. Everyone else shows them on that player's shell (the visuals
    // follow the puppet): the owner sends the list of its states when it changes (and now and then, for late comers),
    // the others add what is missing and end what is gone. Mirrored with the shell as its own caster, so a mirrored
    // damage or heal over time never reaches anyone (MobSync forwards only hits from mobs and heals from our side).

    /// <summary>
    /// Kinds of state shown on a shell: what a buff or debuff looks like (and its icon on the target frame). Not the
    /// ones that act when they start (summons, forms, mounts, teleports, stealth, flight...): those have their own sync.
    /// </summary>
    private static bool Mirrored(RPGEffect e)
    {
        switch (e.effectType)
        {
            case RPGEffect.EFFECT_TYPE.DamageOverTime:
            case RPGEffect.EFFECT_TYPE.HealOverTime:
            case RPGEffect.EFFECT_TYPE.Stat:
            case RPGEffect.EFFECT_TYPE.Stun:
            case RPGEffect.EFFECT_TYPE.Sleep:
            case RPGEffect.EFFECT_TYPE.Immune:
            case RPGEffect.EFFECT_TYPE.Silence:
            case RPGEffect.EFFECT_TYPE.Blocking:
            case RPGEffect.EFFECT_TYPE.Root:
            case RPGEffect.EFFECT_TYPE.Empty:
                return true;
            default:
                return false;
        }
    }

    private static string _sentStates = "";
    private static float _nextStates, _nextCheck;

    public static void Update()
    {
        var me = GameInfo.Player;
        if (!Active || me == null || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 0.25f;
        KeepForms();
        SendMobStates();
        var list = new System.Collections.Generic.List<Wire>();
        var states = me.GetStates();
        if (states != null) foreach (var st in states) if (st != null && st.stateEffect != null && Mirrored(st.stateEffect)) list.Add(Wire.Of(st));
        list.Sort((a, b) => a.Id.CompareTo(b.Id));
        long form = MobSync.FormCode(me); // an animal form travels with the states: shown on the puppet elsewhere
        long mounted = MobSync.MountCode(me); // and a mount
        var sig = new System.Text.StringBuilder();
        foreach (var w in list) sig.Append(w.Id).Append(':').Append(w.Rank).Append(':').Append(w.Stack).Append(',');
        sig.Append('|').Append(form).Append('|').Append(mounted);
        if (sig.ToString() == _sentStates && Time.unscaledTime < _nextStates) return;
        _sentStates = sig.ToString();
        T("send my list " + _sentStates);
        _nextStates = Time.unscaledTime + 5f;
        W.Reset(); W.U8(KindPlayerStates); W.U8((byte)Math.Min(list.Count, 255));
        for (int i = 0; i < list.Count && i < 255; i++) list[i].Write(W);
        W.I32((int)(form >> 8)); W.U8((byte)(form & 0xFF));
        W.I32((int)(mounted >> 8)); W.U8((byte)(mounted & 0xFF));
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>Remote players' forms (effect id, rank; effect -1 = own shape), kept on their puppets.</summary>
    private static readonly System.Collections.Generic.Dictionary<byte, (int effect, int rank)> Forms = new();

    private static readonly System.Collections.Generic.Dictionary<byte, (int effect, int rank)> Mounts = new();

    /// <summary>That id goes to the next player who joins: not in this one's form or on their mount.</summary>
    public static void OnPlayerLeft(byte player) { Forms.Remove(player); Mounts.Remove(player); }

    /// <summary>Puppets are rebuilt (gear, scene, coming into view): their form and mount are put back each time.</summary>
    public static void KeepForms()
    {
        foreach (var kv in Mounts)
            if (Puppets.TryGetVisual(kv.Key, out var go, out _))
                MountVisual.Set(go, Puppets.RiderOf(kv.Key), kv.Value.effect, kv.Value.rank);
        foreach (var kv in Forms)
            if (Puppets.TryGetVisual(kv.Key, out var go, out var anim))
                FormVisual.Set(go, anim, kv.Value.effect, kv.Value.rank, Puppets.IsArmed(kv.Key), controller: false); // the puppet follows the owner's controller by name
        FormVisual.Prune();
    }

    private static System.Collections.Generic.List<Wire> ReadList(ref NetReader r)
    {
        int n = r.U8();
        var want = new System.Collections.Generic.List<Wire>(n);
        for (int i = 0; i < n; i++) want.Add(Wire.Read(ref r));
        return want;
    }

    private static void ReceivePlayerStates(byte from, ref NetReader r)
    {
        var want = ReadList(ref r);
        T($"got list of #{from}: {want.Count}");
        if (r.Remaining >= 5) Forms[from] = (r.I32(), r.U8());
        if (r.Remaining >= 5) Mounts[from] = (r.I32(), r.U8());
        var shell = PlayerShells.Get(from);
        if (shell != null) Reconcile(shell, want); // not near: comes again with the next list
    }

    // ---------------- states of the mobs and bots we run, for copies made later ----------------
    //
    // New and ended states go out as they happen (KindMobAdd/KindMobEnd); a copy that appears later (someone comes
    // near) would miss what was already there, so every few seconds the list of each entity that has states goes out
    // and copies reconcile with it.

    private static float _nextMobStates;
    private static readonly System.Collections.Generic.HashSet<uint> HadStates = new();

    private static void SendMobStates()
    {
        if (Time.unscaledTime < _nextMobStates) return;
        _nextMobStates = Time.unscaledTime + 3f;
        var now = new System.Collections.Generic.HashSet<uint>();
        foreach (var m in MobSync.AllSynced())
        {
            if (!MobSync.TryGetOwnedNetId(m, out var id)) continue;
            var states = m.GetStates();
            var list = new System.Collections.Generic.List<Wire>();
            if (states != null) foreach (var st in states) if (st != null && st.stateEffect != null && list.Count < 255) list.Add(Wire.Of(st));
            if (list.Count == 0 && !HadStates.Contains(id)) continue; // nothing on it, and nothing to clear
            if (list.Count > 0) now.Add(id);
            W.Reset(); W.U8(KindMobStates); W.U32(id); W.U8((byte)list.Count);
            foreach (var w in list) w.Write(W);
            CoopManager.Session.SendWorld(W.Span);
        }
        HadStates.Clear(); HadStates.UnionWith(now);
    }

    private static void ReceiveMobStates(ref NetReader r)
    {
        var copy = MobSync.GetCopy(r.U32());
        var want = ReadList(ref r);
        if (copy != null) Reconcile(copy, want);
    }

    /// <summary>Make a mirror's states the given list: end the extra ones (and doubles), add or update the rest.</summary>
    private static void Reconcile(MobCombatEntity e, System.Collections.Generic.List<Wire> want)
    {
        var states = e.GetStates();
        if (states != null)
            for (int i = states.Count - 1; i >= 0; i--)
            {
                var st = states[i];
                if (st == null) continue;
                if (want.Exists(w => w.Id == st.stateEffectID) && IndexOf(e, st.stateEffectID) == i) continue;
                if (st.stateCurDuration < 1f && !want.Exists(w => w.Id == st.stateEffectID)) continue; // just cast here: the owner's word is on its way
                End(e, i);
            }
        foreach (var w in want) Mirror(e, w);
    }

    /// <summary>World channel messages of kinds 6..9, 12 and 13 (dispatched by MobSync).</summary>
    public static void Receive(byte from, byte kind, ref NetReader r)
    {
        var s = CoopManager.Session;
        switch (kind)
        {
            case KindPlayerStates: ReceivePlayerStates(from, ref r); break;
            case KindMobStates: ReceiveMobStates(ref r); break;
            case KindToPlayer:
            {
                byte player = r.U8();
                int effectId = r.I32(), rank = r.I32();
                uint mobId = r.U32();
                int icon = r.I32();
                if (player != s.LocalId) return;
                var me = GameInfo.Player;
                if (me == null) return;
                CombatEntity caster = mobId != 0 ? MobSync.GetCopy(mobId) : null;
                ApplyReal(Effect(effectId), rank, icon, caster ?? (CombatEntity)PlayerShells.Get(from) ?? me, me);
                break;
            }
            case KindToOwner:
            {
                var mob = MobSync.GetOwned(r.U32());
                uint casterId = r.U32();
                int effectId = r.I32(), rank = r.I32(), icon = r.I32();
                if (mob == null) return;
                ApplyReal(Effect(effectId), rank, icon, (casterId != 0 ? MobSync.GetCopy(casterId) : null) ?? PlayerShells.Get(from), mob);
                break;
            }
            case KindMobAdd:
            {
                var copy = MobSync.GetCopy(r.U32());
                var w = Wire.Read(ref r);
                T($"got +{w.Id} x{w.Stack} on {(copy != null ? copy.name : "no copy")}");
                if (copy != null) Mirror(copy, w);
                break;
            }
            case KindMobEnd:
            {
                var copy = MobSync.GetCopy(r.U32());
                int effectId = r.I32();
                T($"got -{effectId} on {(copy != null ? copy.name : "no copy")}");
                if (copy == null) return;
                int i = IndexOf(copy, effectId);
                if (i >= 0) End(copy, i);
                break;
            }
        }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatEvents), nameof(CombatEvents.OnStateStarted))]
        private static void Started(CombatEntity __0, int __1)
        {
            try { OnStarted(__0, __1); }
            catch (Exception e) { Plugin.L.LogWarning("State sync start: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatEvents), nameof(CombatEvents.OnStateRefreshed))]
        private static void Refreshed(CombatEntity __0, int __1)
        {
            try { OnRefreshed(__0, __1); }
            catch (Exception e) { Plugin.L.LogWarning("State sync refresh: " + e.Message); }
        }

        /// <summary>
        /// A mirrored state (on a shell or a copy, its own caster) pulses for nobody: the real one where the target lives
        /// pulses there, and its visuals come here through FxSync. Pulsed here too, each tick showed twice on every machine.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.ExecuteHOTPulse))]
        private static bool HotPulse(CombatEntity __0, CombatEntity __1) => !IsMirrorPulse(__0, __1);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.ExecuteDOTPulse))]
        private static bool DotPulse(CombatEntity __0, CombatEntity __1) => !IsMirrorPulse(__0, __1);

        private static bool IsMirrorPulse(CombatEntity caster, CombatEntity target)
        {
            try { return Active && target != null && caster != null && caster.Pointer == target.Pointer && (PlayerShells.TryGetPlayer(target, out _) || MobSync.IsCopy(target)); }
            catch { return false; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.EndStateEffect))]
        private static void EndState(MobCombatEntity __instance, int __0)
        {
            try { BeforeEndState(__instance, __0); }
            catch (Exception e) { Plugin.L.LogWarning("State sync end: " + e.Message); }
        }
    }
}

/// <summary>
/// Sprites by name across machines (a state's icon is the sprite of the ability that cast it, not something its effect
/// knows): sent as a hash of the name, found again among the sprites loaded here.
/// </summary>
internal static class Sprites
{
    private static readonly System.Collections.Generic.Dictionary<int, Sprite> ByCode = new();
    private static float _nextScan = -1f;

    public static int Code(Sprite s)
    {
        if (s == null) return 0;
        try
        {
            int c = StableHash.Of(s.name);
            ByCode[c] = s;
            return c;
        }
        catch { return 0; }
    }

    public static Sprite Find(int code)
    {
        if (code == 0) return null;
        if (ByCode.TryGetValue(code, out var s) && s != null) return s;
        if (Time.unscaledTime < _nextScan) return null; // all loaded sprites, now and then for a name not met yet
        _nextScan = Time.unscaledTime + 5f;
        try
        {
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sp != null) ByCode[StableHash.Of(sp.name)] = sp;
        }
        catch (Exception e) { Plugin.L.LogWarning("Sprites: " + e.Message); }
        return ByCode.TryGetValue(code, out s) ? s : null;
    }
}
