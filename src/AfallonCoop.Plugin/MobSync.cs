using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using AfallonCoop.Session;
using AfallonCoop.World;
using BLINK.RPGBuilder.AI;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Shared mobs (MODDING_PLAN §4.4, §4.10). Every machine near a spawner has the same mob entities, created by the game
/// from the same NPC record. The spawner's lease owner (decided by the host) runs them: its game spawns them and their
/// AI is on. Everyone else holds copies with AI and navigation off that follow the owner's stream (position, animator).
/// A change of owner only switches AI on or off on the entities that already exist, so nothing respawns or jumps.
///
/// The game decides who is "near" a spawner itself (GameState activates spawners within their PlayerDistanceMax and
/// deactivates them, destroying their mobs, 10 m further); active spawners are reported to the host as interest.
///
/// World bots ("adventurers") are the same thing with another source: an AdventurerSpawnZone, filled by the game's
/// AdventurerPopulationManager from its roster. The zone's lease owner fills it from its own bot world; everyone else
/// gets copies of those bots, dressed in the owner's equipment (item IDs), kept out of their own bot world simulation.
/// </summary>
internal static class MobSync
{
    private const byte KindState = 1, KindGone = 2, KindHit = 3, KindDied = 4, KindPlayerHit = 5, KindHeal = 10, KindResurrect = 11, KindRecruited = 14;
    private const float StreamInterval = 0.1f;   // 10 Hz
    private const float ScanInterval = 0.25f;
    private const int MaxPacket = 900;            // one unreliable datagram (LiteNetLib: 1020 incl. our headers)

    // ---------------- spawner identity ----------------

    private static string _scene = "";
    private static readonly Dictionary<uint, NPCSpawner> Spawners = new();
    private static readonly Dictionary<IntPtr, uint> KeyOf = new();
    private static readonly Dictionary<uint, AdventurerSpawnZone> Zones = new();
    private static readonly Dictionary<IntPtr, uint> ZoneKeyOf = new();

    /// <summary>Spawners have no saved ID in this game: scene + name + rounded position identifies them on every machine.</summary>
    private static void IndexSpawners()
    {
        var scene = Dungeons.Place; // a dungeon instance has its own spawner keys
        bool moved = scene != _scene;
        if (!moved && (Spawners.Count > 0 || Time.unscaledTime < _reindexAt)) return;
        _reindexAt = Time.unscaledTime + 2f; // a place without spawners (yet): looked at again now and then, not every frame
        if (moved) ReleaseInterest(); // left that place (another scene or dungeon instance)
        _scene = scene;
        Spawners.Clear(); KeyOf.Clear(); Zones.Clear(); ZoneKeyOf.Clear();
        foreach (var s in Object.FindObjectsOfType<NPCSpawner>(true))
        {
            uint key = NewKey(scene, "", s.name, s.transform.position);
            Spawners[key] = s;
            KeyOf[s.Pointer] = key;
        }
        foreach (var z in Object.FindObjectsOfType<AdventurerSpawnZone>(true))
        {
            uint key = NewKey(scene, "zone|", z.name, z.transform.position);
            Zones[key] = z;
            ZoneKeyOf[z.Pointer] = key;
        }
        if (moved || Spawners.Count > 0) Plugin.L.LogInfo($"Mob sync: {Spawners.Count} spawners, {Zones.Count} adventurer zones in '{scene}'");
    }

    private static float _reindexAt;

    private static uint NewKey(string scene, string kind, string name, Vector3 p)
    {
        uint key = (uint)StableHash.Of($"{scene}|{kind}{name}|{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.y)},{Mathf.RoundToInt(p.z)}");
        while (key == NoSpawner || Spawners.ContainsKey(key) || Zones.ContainsKey(key)) key++; // same name and spot: discovery order is the same everywhere
        return key;
    }

    private static bool TryKey(AdventurerSpawnZone z, out uint key)
    {
        key = 0;
        if (z == null) return false;
        IndexSpawners();
        return ZoneKeyOf.TryGetValue(z.Pointer, out key);
    }

    private static bool TryKey(NPCSpawner s, out uint key)
    {
        key = 0;
        if (s == null) return false;
        IndexSpawners();
        return KeyOf.TryGetValue(s.Pointer, out key);
    }

    private static CoopSession S => CoopManager.Connected ? CoopManager.Session : null;
    private static bool Active => S != null && GameInfo.InWorld;
    private static bool IOwn(uint key) => S != null && S.LeaseOwner(key) == S.LocalId;

    // ---------------- owner side ----------------

    private sealed class Owned
    {
        public uint NetId, Spawner; public MobCombatEntity E; public Animator Anim; public bool Bot;
        public uint SentGear;
        public uint Parent; // a pet: net id of the mob/bot it belongs to (0 = not a pet of a synced entity)
        public readonly AnimatorSampler Sampler = new();
        public readonly AnimDeltaEncoder Delta = new(UnityEngine.Random.value); // keyframes spread over the second
        public Vector3 SentPos; public float SentRotY; public byte SentFlags = 255; public float SentHp = -1, SentMaxHp = -1; public int SentLevel = -1;
        public ulong SentTarget = ulong.MaxValue;
        public long SentForm = long.MinValue, SentMount = long.MinValue;
        public int SentController;
    }

    private static readonly Dictionary<IntPtr, Owned> OwnedByPtr = new();
    private static uint _nextNetId;
    private static float _nextScan, _nextStream;
    private static readonly HashSet<uint> Interest = new();
    private static readonly NetWriter W = new();
    private static readonly List<AnimParam> TmpParams = new();
    private static readonly List<AnimLayer> TmpLayers = new();

    private static uint NewNetId() => ((uint)S.LocalId << 24) | (++_nextNetId & 0xFFFFFF);

    /// <summary>Spawner key 0: a mob outside any spawner (debug spawns); it belongs to whoever spawned it.</summary>
    private const uint NoSpawner = 0;

    /// <summary>Pets have no spawner: their source is their player (key = 0xFFFFFF00 | player id), who always runs them.</summary>
    private static uint PetKey(byte player) => 0xFFFFFF00u | player;
    private static bool IsPetKey(uint key, out byte player) { player = (byte)(key & 0xFF); return (key & 0xFFFFFF00u) == 0xFFFFFF00u; }
    private const float LooseCopyRange = 80f;

    public static void TrackLoose(CombatEntity e, bool bot = false)
    {
        var m = e?.TryCast<MobCombatEntity>();
        if (!Active || m == null || OwnedByPtr.ContainsKey(m.Pointer)) return;
        OwnedByPtr[m.Pointer] = new Owned { NetId = NewNetId(), Spawner = NoSpawner, Bot = bot, E = m, Anim = m.ThisAnimator ?? m.GetComponentInChildren<Animator>() };
    }

    /// <summary>
    /// Another machine's loose mob or bot becomes this machine's own, where it stands (a battleground's bots when the
    /// machine that ran the match leaves): same net id, so everyone's copies go on following it; AI and navigation on.
    /// </summary>
    public static MobCombatEntity Adopt(uint netId)
    {
        if (!Copies.TryGetValue(netId, out var c) || c.E == null || c.E.WasCollected) return null;
        Copies.Remove(netId);
        CopyPtrs.Remove(c.E.Pointer);
        OwnedByPtr[c.E.Pointer] = new Owned { NetId = netId, Spawner = NoSpawner, Bot = c.Bot, E = c.E, Anim = c.Anim };
        SetBrain(c.E, true);
        return c.E;
    }

    /// <summary>A loose copy now follows another owner (it is handed over there): the former owner's word on it no longer counts.</summary>
    public static void Reassign(uint netId, byte owner)
    {
        if (Copies.TryGetValue(netId, out var c)) { c.Owner = owner; c.HeardAt = Time.unscaledTime; }
    }

    /// <summary>Which player's machine runs this entity: the local player's (true, ours) or the given one's.</summary>
    public static bool TryGetCopyOwner(CombatEntity e, out byte owner)
    {
        owner = 0;
        if (e == null || !CopyPtrs.TryGetValue(e.Pointer, out var id) || !Copies.TryGetValue(id, out var c)) return false;
        owner = c.Owner;
        return true;
    }

    public static bool IsOwned(CombatEntity e) => e != null && OwnedByPtr.ContainsKey(e.Pointer);

    public static void Update(double now)
    {
        if (!Active)
        {
            ReleaseInterest(); // loading another place: its spawners pass to whoever is still there
            if (OwnedByPtr.Count > 0 || Copies.Count > 0) Reset();
            if (S == null) Held.Clear(); // the session is over
            return;
        }
        ReplayHeld();
        IndexSpawners();
        SendRecruited();
        if (Time.unscaledTime >= _nextScan)
        {
            _nextScan = Time.unscaledTime + ScanInterval;
            Prof.Run("mobs.scan", Scan);
        }
        if (Time.unscaledTime >= _nextStream)
        {
            _nextStream = Time.unscaledTime + StreamInterval;
            Prof.Run("mobs.stream", () => Stream(now));
        }
    }

    /// <summary>Report interest, pick up newly spawned mobs of owned spawners, report gone ones and combat.</summary>
    private static void Scan()
    {
        var s = S;
        foreach (var kv in Spawners)
        {
            var sp = kv.Value;
            bool near = sp != null && sp.IsActive;
            if (near ? Interest.Add(kv.Key) : Interest.Remove(kv.Key)) s.SetInterest(kv.Key, near);
            if (sp == null || !IOwn(kv.Key) || sp.CurrentNPCs == null) continue;
            bool fighting = false;
            foreach (var ce in sp.CurrentNPCs) fighting |= Own(ce, kv.Key, bot: false);
            s.ReportLeaseCombat(kv.Key, fighting);
        }
        foreach (var kv in Zones)
        {
            var z = kv.Value;
            bool near = z != null && z.IsActive;
            if (near ? Interest.Add(kv.Key) : Interest.Remove(kv.Key)) s.SetInterest(kv.Key, near);
            if (z == null || !IOwn(kv.Key) || z.CurrentAdventurers == null) continue;
            bool fighting = false;
            foreach (var ce in z.CurrentAdventurers) fighting |= Own(ce, kv.Key, bot: true);
            s.ReportLeaseCombat(kv.Key, fighting);
        }
        // The local player's pets (summons, the necromancer's skeletons): always run here, copies near the others.
        var me = GameInfo.Player;
        var pets = me != null ? me.GetCurrentPets() : null;
        if (pets != null) foreach (var pet in pets) Own(pet, PetKey(s.LocalId), bot: false);
        // Pets of the mobs and bots we run (a bot necromancer's skeletons): same source, linked to their master.
        foreach (var o in new List<Owned>(OwnedByPtr.Values))
        {
            if (o.Parent != 0 || IsPetKey(o.Spawner, out _) || o.E == null || o.E.WasCollected) continue;
            List<CombatEntity> list = null;
            try { var cp = o.E.GetCurrentPets(); if (cp != null && cp.Count > 0) { list = new(); foreach (var x in cp) list.Add(x); } } catch { }
            if (list == null) continue;
            foreach (var pet in list)
            {
                Own(pet, o.Spawner, bot: false);
                var pm = pet?.TryCast<MobCombatEntity>();
                if (pm != null && OwnedByPtr.TryGetValue(pm.Pointer, out var po)) po.Parent = o.NetId;
            }
        }

        Prof.Run("mobs.phase", UpdatePhasing);

        // A spawner takes its copies with it when it deactivates here; a zone's copies are not in its list (the game
        // would save them into this machine's bot world), so they go here.
        List<Copy> away = null;
        foreach (var c in Copies.Values)
        {
            if (Zones.TryGetValue(c.Spawner, out var z)) { if (z == null || !z.IsActive) (away ??= new()).Add(c); } // bots and their pets
            else if (IsPetKey(c.Spawner, out _) && me != null && c.E != null && !c.E.WasCollected && !Battlegrounds.InMatch
                     && Vector3.Distance(me.transform.position, c.E.transform.position) > LooseCopyRange + 10f) (away ??= new()).Add(c);
        }
        foreach (var c in Copies.Values)
        {
            if (away != null && away.Contains(c)) continue;
            bool pet = IsPetKey(c.Spawner, out var petOf);
            if (pet ? !Puppets.TryGetPosition(petOf, out _) // their player is elsewhere (another scene or instance)
                    : !c.Dead && Time.unscaledTime - c.HeardAt > OrphanAfter && !Battlegrounds.Contains(c.E)) // a match's adventurer waits for its next runner
                (away ??= new()).Add(c);
        }
        if (away != null) foreach (var c in away) DestroyCopy(c);

        List<IntPtr> gone = null;
        foreach (var kv in OwnedByPtr)
        {
            var o = kv.Value;
            if (o.E != null && !o.E.WasCollected && o.E.gameObject != null) continue;
            (gone ??= new()).Add(kv.Key);
            W.Reset(); W.U8(KindGone); W.U32(o.NetId);
            s.SendWorld(W.Span);
        }
        if (gone != null) foreach (var p in gone) { OwnedByPtr.Remove(p); Participants.Remove(p); ResurrectOffered.Remove(p); }
    }

    /// <summary>Track an entity of an owned spawner or zone; true when it is fighting.</summary>
    private static bool Own(CombatEntity ce, uint key, bool bot)
    {
        var m = ce?.TryCast<MobCombatEntity>();
        if (m == null || IsCopy(m)) return false;
        if (!OwnedByPtr.ContainsKey(m.Pointer))
            OwnedByPtr[m.Pointer] = new Owned { NetId = NewNetId(), Spawner = key, Bot = bot, E = m, Anim = m.ThisAnimator ?? m.GetComponentInChildren<Animator>() };
        try { return m.IsInCombat(); } catch { return false; }
    }

    private static readonly NetWriter Rec = new();
    private static uint _streamSeq;

    /// <summary>Owned mobs' state, as many per datagram as fit.</summary>
    private static void Stream(double now)
    {
        if (OwnedByPtr.Count == 0) return;
        Recs.Reset(); RecIndex.Clear();
        foreach (var o in OwnedByPtr.Values)
        {
            var e = o.E;
            if (e == null || e.WasCollected) continue;
            if (!WriteState(o, e, now, withAnimator: true)) continue; // nothing new about this mob
            if (Rec.Length > MaxPacket - 16)
            {
                WriteState(o, e, now, withAnimator: false); // an animator too big for one datagram: position only
                if (!_warnedBigAnimator) { _warnedBigAnimator = true; Plugin.L.LogWarning($"Mob sync: animator of '{e.name}' too big to stream, position only"); }
            }
            WhereWanted(o, e, out var at, out var range);
            RecIndex.Add((Recs.Length, Rec.Length, at, range));
            Recs.Bytes(Rec.Span);
        }
        if (RecIndex.Count == 0) return;

        // Each player gets the mobs near them only: their game has copies of nothing else, the rest would be dropped there.
        uint seq = ++_streamSeq; // one number per tick: the interpolator drops snapshots that are not newer
        bool all = Battlegrounds.InMatch; // a match's bots are everyone's, wherever they are
        foreach (var p in S.Players.Values)
        {
            if (p.Id == S.LocalId) continue;
            Vector3 pos = default;
            if (!all && !Puppets.TryGetPosition(p.Id, out pos)) continue; // in another place: none of these are there
            int countAt = 0; ushort count = 0;
            void Begin() { W.Reset(); W.U8(KindState); W.F64(now); W.U32(seq); countAt = W.Length; W.U16(0); count = 0; }
            void Flush() { if (count == 0) return; W.PatchU16(countAt, count); S.SendWorldStreamTo(p.Id, W.Span); }
            Begin();
            foreach (var (from, len, at, range) in RecIndex)
            {
                if (!all && (at - pos).sqrMagnitude > range * range) continue;
                if (W.Length + len > MaxPacket) { Flush(); Begin(); }
                W.Bytes(Recs.Span.Slice(from, len));
                count++;
            }
            Flush();
        }
    }

    /// <summary>This tick's records of the mobs we run, one after another, and where each is wanted.</summary>
    private static readonly NetWriter Recs = new();
    private static readonly List<(int from, int len, Vector3 at, float range)> RecIndex = new();

    /// <summary>
    /// Where another player wants a mob's stream: near its spawner (or bot zone), as near as the game there needs to be
    /// to make that spawner active, which is when their game has copies of its mobs (it lets them go 10 m further); a
    /// pet or loose mob: within the copy range of itself. With room to spare, so a player coming nearer already gets the
    /// stream (a keyframe with everything at least once a second) when their game brings the spawner to life.
    /// </summary>
    private static void WhereWanted(Owned o, MobCombatEntity e, out Vector3 at, out float range)
    {
        const float Spare = 10f + 30f;
        if (Spawners.TryGetValue(o.Spawner, out var sp) && sp != null)
        {
            at = sp.transform.position;
            range = sp.PlayerDistanceMax > 0f ? sp.PlayerDistanceMax + Spare : float.PositiveInfinity;
        }
        else if (Zones.TryGetValue(o.Spawner, out var z) && z != null)
        {
            at = z.transform.position;
            range = z.PlayerDistanceMax > 0f ? z.PlayerDistanceMax + Spare : float.PositiveInfinity;
        }
        else
        {
            at = e.transform.position;
            range = LooseCopyRange + Spare;
        }
    }

    private static bool _warnedBigAnimator;

    private static readonly List<AnimParam> SendParams = new();

    /// <summary>One mob's record into <see cref="Rec"/>; false when there is nothing new to say (idle, no keyframe due).</summary>
    private static bool WriteState(Owned o, MobCombatEntity e, double now, bool withAnimator)
    {
        Rec.Reset();
        var tr = e.transform;
        var p = tr.position;
        float rotY = tr.eulerAngles.y;
        var npc = e.GetNPCData();
        byte flags = 0;
        try { if (e.IsDead()) flags |= 1; if (e.IsInCombat()) flags |= 2; } catch { }
        TmpParams.Clear(); TmpLayers.Clear(); SendParams.Clear();
        bool key = false;
        _ctrlHash = 0;
        if (withAnimator && o.Anim != null)
        {
            _ctrlHash = o.Sampler.Sample(o.Anim, TmpParams, TmpLayers);
            key = o.Delta.Encode(now, TmpParams, SendParams);
        }
        float hp = Health(e), maxHp = MaxHealth(e);
        int level = 0;
        try { level = e.GetLevel(); } catch { }
        bool moved = (p - o.SentPos).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(rotY, o.SentRotY)) > 0.5f;
        bool changed = flags != o.SentFlags || Mathf.Abs(hp - o.SentHp) > 0.01f || Mathf.Abs(maxHp - o.SentMaxHp) > 0.01f || level != o.SentLevel;
        // A bot's look is its equipment: sent with every keyframe (copies made later get it too) and when it changes.
        bool gear = false;
        if (o.Bot)
        {
            ReadGear(e, GearArmor, GearWeapons, GearSlots);
            uint h = GearHash(GearArmor, GearWeapons) ^ GearHash(GearSlots, GearSlots);
            gear = key || h != o.SentGear;
            o.SentGear = h;
        }
        // Whom it attacks, so a copy has the same target (the game's party logic, target-of-target, facing).
        ulong target = TargetCode(e);
        bool sendTarget = key || target != o.SentTarget;
        o.SentTarget = target;
        // An animal form (a shapeshifting state): the copy takes the same form, the game's own way.
        long form = FormCode(e), mounted = MountCode(e); // and a mount: the same block
        bool sendForm = key || form != o.SentForm || mounted != o.SentMount;
        o.SentForm = form; o.SentMount = mounted;
        // The animator controller by name: the game picks it by weapon, race and form (a druid's bear), copies take the same.
        bool sendCtrl = _ctrlHash != 0 && (key || _ctrlHash != o.SentController);
        if (sendCtrl) o.SentController = _ctrlHash;
        if (!key && !moved && !changed && !gear && !sendTarget && !sendForm && !sendCtrl && SendParams.Count == 0 && withAnimator) return false;
        o.SentPos = p; o.SentRotY = rotY; o.SentFlags = flags; o.SentHp = hp; o.SentMaxHp = maxHp; o.SentLevel = level;
        if (key) flags |= 4;
        if (gear) flags |= 8;
        if (o.Parent != 0) flags |= 16;
        if (sendTarget) flags |= 32;
        if (sendForm) flags |= 64;
        if (sendCtrl) flags |= 128;
        Rec.U32(o.NetId); Rec.U32(o.Spawner); Rec.I32(npc != null ? npc.ID : -1); Rec.U8(flags);
        Rec.F32(hp); Rec.F32(maxHp); Rec.U8((byte)Mathf.Clamp(level, 0, 255));
        Rec.F32(p.x); Rec.F32(p.y); Rec.F32(p.z); Rec.F32(rotY);
        Rec.U8((byte)Math.Min(SendParams.Count, 255));
        for (int i = 0; i < SendParams.Count && i < 255; i++) { Rec.I32(SendParams[i].NameHash); Rec.U8((byte)SendParams[i].Type); Rec.F32(SendParams[i].Value); }
        Rec.U8((byte)Math.Min(TmpLayers.Count, 255));
        for (int i = 0; i < TmpLayers.Count && i < 255; i++) { Rec.I32(TmpLayers[i].StateHash); Rec.F32(TmpLayers[i].NormalizedTime); Rec.F32(TmpLayers[i].Weight); }
        if (gear) { WriteIds(GearArmor); WriteIds(GearSlots); WriteIds(GearWeapons); }
        if (o.Parent != 0) Rec.U32(o.Parent);
        if (sendTarget) { Rec.U8((byte)(target >> 32)); Rec.U32((uint)target); }
        if (sendForm) { Rec.I32((int)(form >> 8)); Rec.U8((byte)(form & 0xFF)); Rec.I32((int)(mounted >> 8)); Rec.U8((byte)(mounted & 0xFF)); }
        if (sendCtrl) Rec.I32(_ctrlHash);
        return true;
    }

    private static int _ctrlHash;

    /// <summary>A copy's animator gets its owner's controller (found by name among the loaded ones).</summary>
    private static void ApplyController(Copy c, int hash)
    {
        var a = c.Anim ?? c.E?.ThisAnimator;
        if (a == null || a.runtimeAnimatorController != null && StableHash.Of(a.runtimeAnimatorController.name) == hash) return;
        var ctrl = Puppets.FindController(hash);
        if (ctrl == null) return; // not loaded here yet: the parameters still drive the current one
        a.runtimeAnimatorController = ctrl;
        c.Follower.Reset();
    }

    /// <summary>The mount an entity rides: effect id and rank index packed (effect -1 = on foot).</summary>
    internal static long MountCode(CombatEntity e)
    {
        try { if (e.isMounted && e.mountEffect != null) return ((long)e.mountEffect.ID << 8) | (byte)e.mountEffectRank; }
        catch { }
        return -1L << 8;
    }

    /// <summary>A copy on (or off) its owner's mount, seen the game's way (see <see cref="MountVisual"/>).</summary>
    private static void ApplyMount(Copy c, int effectId, int rank)
    {
        var e = c.E;
        if (e == null || e.WasCollected) return;
        if (effectId >= 0 && !MountVisual.IsOn(e.gameObject, out _))
        {
            // the bot's own mount logic would get the copy off it (no navigation here): the owner decides
            var gm = e.GetComponent<AdventurerGroundMount>();
            if (gm != null && gm.enabled) gm.enabled = false;
        }
        Transform rider = null;
        try { rider = e.AIEntity?.npcPresetGameObject?.transform; } catch { }
        MountVisual.Set(e.gameObject, rider, effectId, rank);
    }

    /// <summary>The form an entity is in: effect id and rank index packed (effect -1 = its own shape).</summary>
    internal static long FormCode(CombatEntity e)
    {
        try
        {
            if (e.IsShapeshifted() && e.ShapeshiftedEffect != null) return ((long)e.ShapeshiftedEffect.ID << 8) | (byte)e.ShapeshiftedEffectRank;
        }
        catch { }
        return -1L << 8;
    }

    /// <summary>A copy into (or out of) its owner's form: the form's model and animator (see <see cref="FormVisual"/>).</summary>
    private static void ApplyForm(Copy c, int effectId, int rank)
    {
        var e = c.E;
        if (e == null || e.WasCollected) return;
        bool was = FormVisual.IsIn(e.gameObject, out var before);
        FormVisual.Set(e.gameObject, c.Anim ?? e.ThisAnimator, effectId, rank, e.InCombat, controller: false); // the controller comes by name
        FormVisual.IsIn(e.gameObject, out var after);
        if (was != (effectId >= 0)) try { e.AIEntity?.ConfigureFormAnimation(effectId >= 0); } catch { } // as the game's own shapeshifting tells the NPC
        // The entity's own form fields, as the game's shapeshifting sets them: its code then keeps the form's animator
        // (otherwise it puts its weapon controller back).
        try
        {
            var all = GameDatabase.Instance.GetEffects();
            bool on = effectId >= 0 && all.ContainsKey(effectId);
            e.Shapeshifted = on;
            e.ShapeshiftedEffect = on ? all[effectId] : null;
            e.ShapeshiftedEffectRank = on ? rank : 0;
            e.ShapeshiftingGameobject = on ? FormVisual.ModelOf(e.gameObject) : null;
        }
        catch (Exception ex) { Plugin.L.LogWarning("Copy form fields: " + ex.Message); }
        if (was != (effectId >= 0) || before != after) c.Follower.Reset(); // another animator controller: new parameters and states
    }

    private const byte TargetNone = 0, TargetPlayer = 1, TargetEntity = 2;

    private static void ApplyTarget(Copy c, byte kind, uint id)
    {
        if (c.E == null || c.E.WasCollected) return;
        var t = kind == TargetNone ? null : ResolveTarget(kind, id);
        CombatEntity cur = null;
        try { cur = c.E.GetTarget(); } catch { }
        if ((cur == null && t == null) || (cur != null && t != null && cur.Pointer == t.Pointer)) return;
        // The field GetTarget reads; SetTarget would drive the AI states a copy does not run.
        try { c.E.CurrentTarget = t; } catch (Exception e) { Plugin.L.LogWarning("Mob copy target: " + e.Message); }
    }

    /// <summary>A mob's target for the wire: kind in the high bits, then a player id or a synced net id.</summary>
    private static ulong TargetCode(MobCombatEntity e)
    {
        CombatEntity t = null;
        try { t = e.GetTarget(); } catch { }
        if (t == null || t.Dead) return 0;
        var me = GameInfo.Player;
        if (me != null && t.Pointer == me.Pointer) return ((ulong)TargetPlayer << 32) | S.LocalId;
        if (PlayerShells.TryGetPlayer(t, out var player)) return ((ulong)TargetPlayer << 32) | player;
        uint id = NetIdOf(t);
        return id != 0 ? ((ulong)TargetEntity << 32) | id : 0;
    }

    /// <summary>The target a copy's owner reported, as an entity here (the local player, a shell, a synced mob or bot).</summary>
    private static CombatEntity ResolveTarget(byte kind, uint id)
    {
        if (kind == TargetPlayer) return id == S.LocalId ? GameInfo.Player : PlayerShells.Get((byte)id);
        if (kind == TargetEntity) return EntityOf(id);
        return null;
    }

    private static void WriteIds(List<int> ids)
    {
        Rec.U8((byte)Math.Min(ids.Count, 255));
        for (int i = 0; i < ids.Count && i < 255; i++) Rec.I32(ids[i]);
    }

    private static void ReadIds(ref NetReader r, List<int> ids)
    {
        ids.Clear();
        int n = r.U8();
        for (int i = 0; i < n; i++) ids.Add(r.I32());
    }

    // ---------------- bot equipment ----------------

    private static readonly List<int> GearArmor = new(), GearWeapons = new(), GearSlots = new();

    /// <summary>
    /// Item IDs the entity wears (armor, with the slot each is in: <see cref="SlotCode"/>) and holds (weapons by slot,
    /// -1 = empty), as NPCEquipmentAppearance shows them.
    /// </summary>
    internal static void ReadGear(MobCombatEntity e, List<int> armor, List<int> weapons, List<int> slots = null, List<int> armorData = null, List<int> weaponData = null)
    {
        armor.Clear(); weapons.Clear(); slots?.Clear(); armorData?.Clear(); weaponData?.Clear();
        var a = e.equippedArmors;
        if (a != null) foreach (var x in a) if (x?.item != null) { armor.Add(x.item.ID); slots?.Add(SlotCode(x.ArmorSlot)); armorData?.Add(x.temporaryItemDataID); }
        var w = e.equippedWeapons;
        if (w != null) foreach (var x in w) { weapons.Add(x?.item != null ? x.item.ID : -1); weaponData?.Add(x?.item != null ? x.temporaryItemDataID : -1); }
    }

    // An item's slot is not always its own: of two rings (two trinkets) one is in the second slot. Sent by its name.
    private static readonly Dictionary<int, RPGBArmorSlot> SlotsByCode = new();

    internal static int SlotCode(RPGBArmorSlot slot) => slot != null ? StableHash.Of(slot.entryName ?? "") : 0;

    internal static RPGBArmorSlot SlotOf(int code)
    {
        if (code == 0) return null;
        if (SlotsByCode.Count == 0)
            foreach (var kv in GameDatabase.Instance.GetArmorSlots()) if (kv.Value != null) SlotsByCode[SlotCode(kv.Value)] = kv.Value;
        return SlotsByCode.TryGetValue(code, out var s) ? s : null;
    }

    /// <summary>A player's slot by the name their save keeps (ArmorEquippedEntry.armorSlotName).</summary>
    internal static int SlotCode(string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        foreach (var kv in GameDatabase.Instance.GetArmorSlots())
            if (kv.Value != null && (kv.Key == name || kv.Value.entryName == name || kv.Value.name == name)) return SlotCode(kv.Value);
        return 0;
    }

    private static uint GearHash(List<int> armor, List<int> weapons)
    {
        uint h = 2166136261;
        foreach (var id in armor) h = (h ^ (uint)id) * 16777619;
        h = (h ^ 0xFFFFu) * 16777619;
        foreach (var id in weapons) h = (h ^ (uint)id) * 16777619;
        return h;
    }

    /// <summary>Dress a bot copy in its owner's equipment, the way the game dresses bots: the lists, then a refresh.</summary>
    private static void ApplyGear(Copy c, List<int> armor, List<int> weapons, List<int> slots)
    {
        uint h = GearHash(armor, weapons) ^ GearHash(slots, slots);
        if (c.E == null || c.E.WasCollected || h == c.GearHash) return;
        c.GearHash = h;
        Dress(c.E, armor, weapons, armorSlots: slots);
    }

    /// <summary>A copy's items without the data made for a window (inspect): that data is gone, its ids must not stay.</summary>
    internal static void ForgetItemData(MobCombatEntity bot)
    {
        try
        {
            if (bot.equippedArmors != null) foreach (var a in bot.equippedArmors) if (a != null) a.temporaryItemDataID = -1;
            if (bot.equippedWeapons != null) foreach (var w in bot.equippedWeapons) if (w != null) w.temporaryItemDataID = -1;
        }
        catch (Exception e) { Plugin.L.LogWarning("Bot copy item data: " + e.Message); }
    }

    /// <summary>Put these items (database IDs) on a bot, as its owner's game had them.</summary>
    internal static void Dress(MobCombatEntity bot, List<int> armor, List<int> weapons, bool refreshLook = true, List<int> armorData = null, List<int> weaponData = null, List<int> armorSlots = null)
    {
        var items = GameDatabase.Instance.GetItems();
        RPGItem Item(int id) => id >= 0 && items.ContainsKey(id) ? items[id] : null;
        var a = bot.equippedArmors ??= new Il2CppSystem.Collections.Generic.List<BLINK.RPGBuilder.Data.EconomyData.EquippedArmor>();
        a.Clear();
        for (int i = 0; i < armor.Count; i++)
        {
            var it = Item(armor[i]);
            if (it == null) continue;
            int data = armorData != null && i < armorData.Count ? armorData[i] : -1;
            var slot = armorSlots != null && i < armorSlots.Count ? SlotOf(armorSlots[i]) : null;
            a.Add(new BLINK.RPGBuilder.Data.EconomyData.EquippedArmor { ArmorSlot = slot ?? it.ArmorSlot, item = it, temporaryItemDataID = data });
        }
        var w = bot.equippedWeapons ??= new Il2CppSystem.Collections.Generic.List<BLINK.RPGBuilder.Data.EconomyData.EquippedWeapon>();
        w.Clear();
        for (int i = 0; i < weapons.Count; i++)
            w.Add(new BLINK.RPGBuilder.Data.EconomyData.EquippedWeapon { item = Item(weapons[i]), temporaryItemDataID = weaponData != null && i < weaponData.Count ? weaponData[i] : -1 });
        if (!refreshLook) return;
        try { bot.EquipmentAppearance?.RefreshEquipment(); }
        catch (Exception e) { Plugin.L.LogWarning("Bot copy equipment: " + e.Message); }
    }

    // ---------------- copies ----------------

    private sealed class Copy
    {
        public uint NetId, Spawner; public byte Owner; public MobCombatEntity E; public Animator Anim;
        public IntPtr Ptr; // E's address, still known once the game has destroyed E
        public bool Bot; public uint GearHash, Parent;
        public readonly RemoteMotion Motion = new() { Delay = RenderDelay }; // 10 Hz stream: interval + jitter headroom
        public readonly AnimatorFollower Follower = new();
        public readonly AnimParamState Params = new(); public List<AnimLayer> Layers = new();
        public bool Dead, DeathPlayed;
        public float HeardAt = Time.unscaledTime;
    }

    /// <summary>
    /// Owners send every mob at least once a second (animator keyframes). A live copy unheard for this long is an orphan:
    /// its owner gave the spawner up, and the new owner, not having it yet, spawned fresh mobs instead.
    /// </summary>
    private const float OrphanAfter = 6f;

    private static readonly Dictionary<uint, Copy> Copies = new();
    private static readonly Dictionary<IntPtr, uint> CopyPtrs = new(); // a copy's entity address -> its net id
    private static bool _creatingCopy;

    /// <summary>A copy is being created: the game must not count it into this machine's bot world (PlayerShells' hooks).</summary>
    public static bool CreatingCopy => _creatingCopy;

    public static bool IsCopy(CombatEntity e) => e != null && CopyPtrs.ContainsKey(e.Pointer);

    /// <summary>Copies trail their owner's mob by this much (10 Hz stream + jitter); effects for them wait as long.</summary>
    public const double RenderDelay = 0.2;

    public static bool TryGetOwnedNetId(CombatEntity e, out uint netId)
    {
        netId = 0;
        if (e == null || !OwnedByPtr.TryGetValue(e.Pointer, out var o)) return false;
        netId = o.NetId;
        return true;
    }

    public static MobCombatEntity GetOwned(uint netId)
    {
        foreach (var o in OwnedByPtr.Values) if (o.NetId == netId && o.E != null && !o.E.WasCollected) return o.E;
        return null;
    }

    public static uint NetIdOfCopy(CombatEntity e) => e != null && CopyPtrs.TryGetValue(e.Pointer, out var id) ? id : 0;

    public static MobCombatEntity GetCopy(uint netId) =>
        Copies.TryGetValue(netId, out var c) && c.E != null && !c.E.WasCollected && !c.DeathPlayed ? c.E : null;

    /// <summary>Messages carrying items or gold that came while this player was loading: the sender already paid.</summary>
    private static readonly List<(byte from, byte[] data)> Held = new();

    private static void ReplayHeld()
    {
        if (Held.Count == 0) return;
        var held = Held.ToArray();
        Held.Clear();
        Plugin.L.LogInfo($"Mob sync: {held.Length} message(s) held while loading, handled now");
        foreach (var (from, data) in held) Receive(from, data);
    }

    public static void Receive(byte from, byte[] data)
    {
        if (!Active)
        {
            if (S != null && data.Length > 0 && data[0] is Trade.Kind or Mail.Kind or AuctionSync.Kind or GroupLoot.Kind or Housing.Kind) Held.Add((from, data));
            return;
        }
        ReplayHeld(); // in the order they came
        try
        {
            var r = new NetReader(data);
            switch (r.U8())
            {
                case KindState: ReceiveStates(from, ref r); break;
                case KindGone:
                {
                    uint id = r.U32();
                    if (Copies.TryGetValue(id, out var c) && c.Owner == from) DestroyCopy(c);
                    break;
                }
                case KindHit: ReceiveHit(from, ref r); break;
                case KindDied: ReceiveDied(from, ref r); break;
                case KindPlayerHit: ReceivePlayerHit(ref r); break;
                case KindHeal: ReceiveHeal(from, ref r); break;
                case KindResurrect: ReceiveResurrect(from, ref r); break;
                case KindRecruited: ReceiveRecruited(from, ref r); break;
                case WorldClock.Kind: WorldClock.Receive(from, ref r); break;
                case Trade.Kind: Trade.Receive(from, ref r); break;
                case Summon.Kind: Summon.Receive(from, ref r); break;
                case Inspect.Kind: Inspect.Receive(from, ref r); break;
                case Duel.Kind: Duel.Receive(from, ref r); break;
                case GroupLoot.Kind: GroupLoot.Receive(from, ref r); break;
                case MeterSync.Kind: MeterSync.Receive(from, ref r); break;
                case AuctionSync.Kind: AuctionSync.Receive(from, ref r); break;
                case Corruption.Kind: Corruption.Receive(from, ref r); break;
                case Challenges.Kind: Challenges.Receive(from, ref r); break;
                case Mail.Kind: Mail.Receive(from, ref r); break;
                case WorldRules.Kind: WorldRules.Receive(from, ref r); break;
                case WorldQuests.Kind: WorldQuests.Receive(from, ref r); break;
                case Housing.Kind: Housing.Receive(from, ref r); break;
                case BossSync.Kind: BossSync.Receive(from, ref r); break;
                case BossSync.KindHitEffect: BossSync.ReceiveHitEffect(from, ref r); break;
                case BossPhases.KindPhase: BossPhases.ReceivePhase(from, ref r); break;
                case BossPhases.KindForce: BossPhases.ReceiveForce(from, ref r); break;
                case BossPhases.KindChannel: BossPhases.ReceiveChannel(from, ref r); break;
                case BossPhases.KindHeroic: BossPhases.ReceiveHeroic(from, ref r); break;
                case FlightVisual.Kind: FlightVisual.Receive(from, ref r); break;
                case PlayerActions.Kind: PlayerActions.Receive(from, ref r); break;
                case QuestShare.Kind: QuestShare.Receive(from, ref r); break;
                case ShellStats.Kind: ShellStats.Receive(from, ref r); break;
                case Battlegrounds.Kind: Battlegrounds.Receive(from, ref r); break;
                case StateSync.KindToPlayer:
                case StateSync.KindToOwner:
                case StateSync.KindMobAdd:
                case StateSync.KindMobEnd:
                case StateSync.KindPlayerStates:
                case StateSync.KindMobStates:
                    r = new NetReader(data); StateSync.Receive(from, r.U8(), ref r); break;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Mob sync receive: " + e.Message); }
    }

    private static void ReceiveStates(byte from, ref NetReader r)
    {
        double senderTime = r.F64();
        uint seq = r.U32();
        int n = r.U16();
        double now = CoopManager.Now;
        for (int i = 0; i < n; i++)
        {
            uint netId = r.U32(), spawner = r.U32();
            int npcId = r.I32();
            byte flags = r.U8();
            float hp = r.F32(), maxHp = r.F32();
            int level = r.U8();
            float x = r.F32(), y = r.F32(), z = r.F32(), rotY = r.F32();
            var ps = new List<AnimParam>();
            int pc = r.U8();
            for (int k = 0; k < pc; k++) ps.Add(new AnimParam(r.I32(), (AnimParamType)r.U8(), r.F32()));
            var ls = new List<AnimLayer>();
            int lc = r.U8();
            for (int k = 0; k < lc; k++) ls.Add(new AnimLayer(r.I32(), r.F32(), r.F32()));
            bool gear = (flags & 8) != 0;
            if (gear) { ReadIds(ref r, GearArmor); ReadIds(ref r, GearSlots); ReadIds(ref r, GearWeapons); }
            uint parent = (flags & 16) != 0 ? r.U32() : 0;
            bool hasTarget = (flags & 32) != 0;
            byte targetKind = hasTarget ? r.U8() : (byte)0;
            uint targetId = hasTarget ? r.U32() : 0;
            bool hasForm = (flags & 64) != 0;
            int formEffect = hasForm ? r.I32() : -1;
            int formRank = hasForm ? r.U8() : 0;
            int mountEffect = hasForm ? r.I32() : -1;
            int mountRank = hasForm ? r.U8() : 0;
            int ctrlHash = (flags & 128) != 0 ? r.I32() : 0;

            if (IsPetKey(spawner, out var petOf)) { if (petOf != from) continue; }
            else if (spawner != NoSpawner && S.LeaseOwner(spawner) != from) continue; // stale stream from a former owner
            if (!Copies.TryGetValue(netId, out var c))
            {
                c = CreateCopy(from, netId, spawner, npcId, new Vector3(x, y, z), rotY, parent);
                if (c == null) continue;
            }
            c.Owner = from;
            c.HeardAt = Time.unscaledTime;
            c.Motion.Add(new PlayerStateMsg { Seq = seq, SenderTime = senderTime, PosX = x, PosY = y, PosZ = z, RotY = rotY, InWorld = true }, now);
            c.Params.Apply(ps, (flags & 4) != 0); c.Layers = ls;
            c.Dead = (flags & 1) != 0;
            if (!c.Dead && c.DeathPlayed && hp > 0f) Revive(c, hp, maxHp); // alive again on its owner (resurrected, respawned)
            ApplyHealthAndLevel(c, hp, maxHp, level);
            if (hasTarget) ApplyTarget(c, targetKind, targetId);
            if (hasForm) { ApplyForm(c, formEffect, formRank); ApplyMount(c, mountEffect, mountRank); }
            if (ctrlHash != 0) ApplyController(c, ctrlHash);
            if (c.Bot && gear) ApplyGear(c, GearArmor, GearWeapons, GearSlots);
            try { c.E.InCombat = (flags & 2) != 0; } catch { } // in combat as on the owner (weapons drawn, combat music, nameplates)
        }
    }

    private static Copy CreateCopy(byte owner, uint netId, uint spawnerKey, int npcId, Vector3 pos, float rotY, uint parent)
    {
        NPCSpawner sp = null;
        bool bot = false;
        CombatEntity petOwner = null;
        if (IsPetKey(spawnerKey, out var player))
        {
            if (SuppressedPets.TryGetValue((player, npcId), out var until) && Time.unscaledTime < until) return null; // handed over: ours now
            var me = GameInfo.Player;
            if (me == null || (!Battlegrounds.InMatch && Vector3.Distance(me.transform.position, pos) > LooseCopyRange)) return null;
            petOwner = PlayerShells.Get(player); // the game's pet of that player: their ally, a target for mobs
            if (petOwner == null) return null;
        }
        else if (spawnerKey == NoSpawner)
        {
            var me = GameInfo.Player;
            if (me == null || (!Battlegrounds.InMatch && Vector3.Distance(me.transform.position, pos) > LooseCopyRange)) return null; // a battleground's bots: all of them
            bot = parent == 0 && GameDatabase.Instance.GetNPCs().TryGetValue(npcId, out var rec) && (int)rec.npcType == 9; // an adventurer: dressed as on its owner
            if (!Puppets.TryGetPosition(owner, out _)) return null; // its owner is elsewhere (another scene or dungeon instance)
        }
        else if (Zones.TryGetValue(spawnerKey, out var zone))
        {
            if (zone == null || !zone.IsActive) return null; // not near it here
            bot = parent == 0;
        }
        else if (!Spawners.TryGetValue(spawnerKey, out sp) || sp == null || !sp.IsActive) return null; // not near it here
        if (parent != 0)
        {
            petOwner = GetCopy(parent); // the game's pet of that copy, as on the owner's machine
            if (petOwner == null) return null; // its master shows up first
            sp = null;
        }
        if (PhasedNpcs.ContainsKey(npcId)) return null; // gone for this player (a quest took it), still there for others
        var npcs = GameDatabase.Instance.GetNPCs();
        if (!npcs.ContainsKey(npcId)) return null;
        CombatEntity e;
        _creatingCopy = true;
        var record = petOwner != null ? ForeignPetRecord(npcs[npcId]) : npcs[npcId];
        try { e = CombatManager.Instance.GenerateNPCEntity(record, petOwner != null, false, petOwner, pos, Quaternion.Euler(0, rotY, 0), sp); }
        finally { _creatingCopy = false; }
        var m = e?.TryCast<MobCombatEntity>();
        if (m == null) return null;
        var c = new Copy { NetId = netId, Spawner = spawnerKey, Owner = owner, Bot = bot, Parent = parent, E = m, Ptr = m.Pointer, Anim = m.ThisAnimator ?? m.GetComponentInChildren<Animator>() };
        SetBrain(m, false);
        if (sp != null && sp.CurrentNPCs != null && !sp.CurrentNPCs.Contains(m)) sp.CurrentNPCs.Add(m); // the spawner cleans it up with its own
        Copies[netId] = c;
        CopyPtrs[m.Pointer] = netId;
        if (spawnerKey == NoSpawner && parent == 0) Corruption.DressCopy(m, npcId);
        Prof.Count("mobs.copy.created");
        return c;
    }

    /// <summary>AI and navigation on (owner) or off (copy). The entity itself stays as it is.</summary>
    private static void SetBrain(MobCombatEntity m, bool on)
    {
        var ai = m.GetComponent<AIEntity>();
        if (ai != null) ai.enabled = on;
        var agent = m.GetComponent<NavMeshAgent>();
        if (agent != null)
        {
            if (on) { agent.enabled = true; agent.Warp(m.transform.position); }
            else agent.enabled = false;
        }
        var anim = m.ThisAnimator ?? m.GetComponentInChildren<Animator>();
        if (anim != null && !on) anim.applyRootMotion = false;
    }

    private static void DestroyCopy(Copy c)
    {
        Copies.Remove(c.NetId);
        Forget(c);
        if (c.E != null && !c.E.WasCollected)
        {
            FormVisual.Clear(c.E.gameObject); MountVisual.Clear(c.E.gameObject);
            if (Spawners.TryGetValue(c.Spawner, out var sp) && sp != null) sp.CurrentNPCs?.Remove(c.E);
            Object.Destroy(c.E.gameObject);
        }
    }

    /// <summary>A copy is gone: nothing stays keyed by its address, which a new entity may get (it would pass for this copy).</summary>
    private static void Forget(Copy c)
    {
        if (CopyPtrs.TryGetValue(c.Ptr, out var id) && id == c.NetId) CopyPtrs.Remove(c.Ptr);
        Participants.Remove(c.Ptr); ResurrectOffered.Remove(c.Ptr);
    }

    public static void LateUpdate(double now)
    {
        if (Copies.Count == 0) return;
        List<Copy> dead = null;
        foreach (var c in Copies.Values)
        {
            if (c.E == null || c.E.WasCollected) { (dead ??= new()).Add(c); continue; }
            if (c.Motion.Sample(now, out var pos, out var rotY))
                c.E.transform.SetPositionAndRotation(new Vector3(pos.X, pos.Y, pos.Z), Quaternion.Euler(0, rotY, 0));
            if (c.Anim != null)
            {
                AnimatorFollower.TraceName = DevCastTrace.Matches(c.E) ? c.E.name : null;
                c.Follower.Apply(c.Anim, c.Params.Current, c.Layers, Time.deltaTime);
                AnimatorFollower.TraceName = null;
            }
        }
        if (dead != null) foreach (var c in dead) { Copies.Remove(c.NetId); Forget(c); }
    }

    // ---------------- combat (MODDING_PLAN §4.3) ----------------
    //
    // A mob's owner applies all damage to it; HP, level and death come from the owner. A hit by another player lands on
    // their copy at once (numbers, HP bar), cannot kill it there, and is forwarded to the owner, who applies it with
    // that player's shell as the caster (so threat and aggro go to the right player). When the mob dies, the owner
    // names everyone who damaged it; on those players' machines the copy dies through the game's own death, with XP,
    // loot and quest credit; for everyone else the same death gives nothing (two gates below).

    private static int _healthStat = -1;
    internal static int HealthStat
    {
        get
        {
            if (_healthStat < 0) try { _healthStat = GameDatabase.Instance.GetHealthStat().ID; } catch { }
            return _healthStat;
        }
    }

    internal static float Health(CombatEntity e)
    {
        try { return CombatUtilities.GetCurrentStatValue(e, HealthStat); } catch { return 0; }
    }

    internal static float MaxHealth(CombatEntity e)
    {
        try { return CombatUtilities.GetMaxStatValue(e, HealthStat); } catch { return 0; }
    }

    /// <summary>
    /// The owner's numbers, not just the current HP: each machine computes a mob's level (and with it max HP) for its own
    /// player, so the copy's own max would make the same HP show as a different share of the bar.
    /// </summary>
    private static void ApplyHealthAndLevel(Copy c, float hp, float maxHp, int level)
    {
        if (c.E == null || c.E.WasCollected || c.DeathPlayed) return;
        try
        {
            if (level > 0 && c.E.GetLevel() != level) c.E.SetLevel(level);
            if (maxHp > 0 && Mathf.Abs(MaxHealth(c.E) - maxHp) > 0.5f) CombatUtilities.SetMaxStatValue(c.E, HealthStat, maxHp);
            if (Mathf.Abs(Health(c.E) - hp) > 0.5f)
            {
                CombatUtilities.SetCurrentStatValue(c.E, HealthStat, Mathf.Max(1f, hp));
                // target frame, nameplate bar: they redraw on the game's stat event, which a direct set does not raise
                var stats = GameDatabase.Instance.GetStats();
                if (stats.ContainsKey(HealthStat)) CombatEvents.Instance?.OnStatValueChanged(c.E, stats[HealthStat], Health(c.E), MaxHealth(c.E));
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Mob sync health: " + e.Message); }
    }

    private static bool _reviving;

    /// <summary>A copy whose death was played stands up the game's way when its owner's entity lives again.</summary>
    private static void Revive(Copy c, float hp, float maxHp)
    {
        if (c.E == null || c.E.WasCollected) return;
        if (Battlegrounds.Revive(c.E)) { c.DeathPlayed = false; return; } // a battleground's defeat ends the battleground's way
        float percent = maxHp > 0f ? Mathf.Clamp(hp / maxHp * 100f, 1f, 100f) : 100f;
        _reviving = true;
        try { c.E.Resurrect(percent, null); c.DeathPlayed = false; Plugin.L.LogInfo($"Mob sync: {c.E.name} lives again"); }
        catch (Exception e) { Plugin.L.LogWarning("Mob sync revive: " + e.Message); }
        finally { _reviving = false; }
    }

    /// <summary>Who damaged each synced mob (owner side: tracked; copy side: named by the owner at death).</summary>
    private static readonly Dictionary<IntPtr, HashSet<byte>> Participants = new();
    private static bool _authorizedDeath;

    private static bool IsSynced(CombatEntity e) =>
        e != null && (CopyPtrs.ContainsKey(e.Pointer) || OwnedByPtr.ContainsKey(e.Pointer));

    private static bool IsPetEntity(CombatEntity e)
    {
        try { return e.IsPet(); } catch { return false; }
    }

    /// <summary>The local player took part, or a member of their group did and they were near (WoW-like sharing).</summary>
    private static bool LocalParticipated(CombatEntity e)
    {
        if (S == null || !Participants.TryGetValue(e.Pointer, out var p)) return false;
        if (p.Contains(S.LocalId)) return true;
        var at = e.transform.position;
        foreach (var who in p) if (Groups.SharesKill(who, at)) return true;
        return false;
    }

    private static void AddParticipant(CombatEntity mob, byte player)
    {
        if (!Participants.TryGetValue(mob.Pointer, out var set)) Participants[mob.Pointer] = set = new HashSet<byte>();
        set.Add(player);
    }

    /// <summary>The local player or something of theirs (a pet, a summon).</summary>
    internal static bool IsLocalSide(CombatEntity caster)
    {
        var me = GameInfo.Player;
        if (caster == null || me == null) return false;
        if (caster.Pointer == me.Pointer) return true;
        try { var owner = caster.GetOwnerEntity(); return owner != null && owner.Pointer == me.Pointer; } catch { return false; }
    }

    /// <summary>Every synced mob and bot here: the ones this machine runs and the copies of others'.</summary>
    public static IEnumerable<MobCombatEntity> AllSynced()
    {
        foreach (var o in OwnedByPtr.Values) if (o.E != null && !o.E.WasCollected) yield return o.E;
        foreach (var c in Copies.Values) if (c.E != null && !c.E.WasCollected) yield return c.E;
    }

    /// <summary>A synced mob or bot by net id, whoever runs it (this machine or a copy here).</summary>
    public static MobCombatEntity EntityOf(uint netId) => netId == 0 ? null : GetOwned(netId) ?? GetCopy(netId);

    /// <summary>Net id of a synced mob or bot (0 = not synced).</summary>
    public static uint SyncedId(CombatEntity e) => NetIdOf(e);

    private static uint NetIdOf(CombatEntity e)
    {
        if (e == null) return 0;
        if (OwnedByPtr.TryGetValue(e.Pointer, out var o)) return o.NetId;
        return CopyPtrs.TryGetValue(e.Pointer, out var id) ? id : 0;
    }

    /// <summary>Every hit on a mob or on a player shell passes here first. False = do not apply it locally.</summary>
    private static bool OnMobTakeDamage(MobCombatEntity mob, CombatCalculations.DamageResult dr, RPGAbility.RPGAbilityRankData rank, int stat)
    {
        if (!Active || dr == null) return true;
        var caster = dr.caster;

        // A shell is never hit here: the real player's machine applies it, with this machine's mob as the caster.
        if (PlayerShells.TryGetPlayer(mob, out var victim))
        {
            if (Duel.ForwardHit(victim, dr, stat)) return false; // my duel opponent: theirs to take
            if (Battlegrounds.ForwardHit(victim, mob, dr, stat))
            {
                // an enemy player in a battleground: shown here at once (numbers, bar), applied by them
                float hp = Health(mob);
                if (dr.DamageAmount >= hp) dr.DamageAmount = Mathf.Max(0f, hp - 1f);
                PlayerShells.Predict(victim);
                return true;
            }
            if (BossSync.ResolvedByVictim(caster, rank)) return false; // a mob's area hit: their own game hits them
            uint mobId = NetIdOf(caster);
            if (mobId != 0 && OwnedByPtr.ContainsKey(caster.Pointer))
            {
                W.Reset(); W.U8(KindPlayerHit); W.U8(victim); W.U32(mobId); W.F32(dr.DamageAmount); W.I32(stat);
                W.Bool(dr.IsAutoAttack); W.Bool(dr.IsDamageOverTime);
                S.SendWorld(W.Span);
            }
            return false;
        }

        if (CopyPtrs.ContainsKey(mob.Pointer))
        {
            // From the local player (caster id 0) or from a mob/bot this machine runs; anything else is someone else's.
            bool mine = IsLocalSide(caster);
            if (!TryGetOwnedNetId(caster, out var casterId) && !mine) return false;
            if (IsPlayerSideCopy(mob) && BossSync.ResolvedByVictim(caster, rank)) return false; // their owner's game hits them
            uint id = NetIdOf(mob);
            W.Reset(); W.U8(KindHit); W.U32(id); W.U32(casterId); W.F32(dr.DamageAmount); W.I32(stat);
            W.Bool(dr.IsAutoAttack); W.Bool(dr.IsDamageOverTime);
            S.SendWorld(W.Span);
            if (mine) AddParticipant(mob, S.LocalId);
            // Show the hit now, but only the owner may kill it.
            float hp = Health(mob);
            if (dr.DamageAmount >= hp) dr.DamageAmount = Mathf.Max(0f, hp - 1f);
            return true;
        }

        if (OwnedByPtr.ContainsKey(mob.Pointer))
        {
            if (IsLocalSide(caster)) AddParticipant(mob, S.LocalId);
            else if (PlayerShells.TryGetPlayer(caster, out var who)) AddParticipant(mob, who);
            else
            {
                CombatEntity owner = null;
                try { owner = caster?.GetOwnerEntity(); } catch { }
                if (PlayerShells.TryGetPlayer(owner, out var petOwner)) AddParticipant(mob, petOwner); // a remote player's pet
            }
        }
        return true;
    }

    /// <summary>A copy of another player's bot or pet (not a mob).</summary>
    private static bool IsPlayerSideCopy(MobCombatEntity m)
    {
        if (m == null || !CopyPtrs.ContainsKey(m.Pointer)) return false;
        if (IsPetEntity(m)) return true;
        try { return (int)m.GetNPCData().npcType == 9; } catch { return false; }
    }

    /// <summary>Owner: another player, or a mob/bot another machine runs, hit one of our mobs.</summary>
    private static void ReceiveHit(byte from, ref NetReader r)
    {
        uint id = r.U32(), casterId = r.U32();
        float amount = r.F32();
        int stat = r.I32();
        bool auto = r.Bool(), dot = r.Bool();
        Owned o = null;
        foreach (var x in OwnedByPtr.Values) if (x.NetId == id) { o = x; break; }
        if (o == null || o.E == null || o.E.WasCollected) return; // not ours (any more): the new owner never saw it
        CombatEntity caster = (casterId != 0 ? GetCopy(casterId) : null) ?? PlayerShells.Get(from); // their mob/pet, else them
        if (caster == null) { Plugin.L.LogWarning($"Mob sync: hit from #{from} ({casterId:x8}) without a caster here, dropped"); return; }
        var dr = new CombatCalculations.DamageResult
        {
            caster = caster, target = o.E, DamageAmount = amount, IsAutoAttack = auto, IsDamageOverTime = dot, DamageActionType = "",
        };
        o.E.TakeDamage(dr, null, stat);
    }

    /// <summary>The player this hit is for: a mob of another machine hit our character there.</summary>
    private static void ReceivePlayerHit(ref NetReader r)
    {
        byte victim = r.U8();
        uint mobId = r.U32();
        float amount = r.F32();
        int stat = r.I32();
        bool auto = r.Bool(), dot = r.Bool();
        if (victim != S.LocalId || !Copies.TryGetValue(mobId, out var c) || c.E == null || c.E.WasCollected) return;
        var me = GameInfo.Player;
        if (me == null) return;
        var dr = new CombatCalculations.DamageResult
        {
            caster = c.E, target = me, DamageAmount = amount, IsAutoAttack = auto, IsDamageOverTime = dot, DamageActionType = "",
        };
        me.TakeDamage(dr, null, stat);
    }

    // ---------------- heals (same rule: the healer's side computes, the target's owner applies) ----------------

    private const byte HealToPlayer = 0, HealToEntity = 1;

    /// <summary>Every heal passes here first. True = apply it here too (a shell or copy shows it at once; the owner's numbers follow).</summary>
    private static bool OnHeal(CombatEntity target, CombatCalculations.HealResult hr, int stat)
    {
        if (!Active || hr == null || target == null || _applyingHeal) return true;
        var caster = hr.caster;
        if (!TryGetOwnedNetId(caster, out var casterId) && !IsLocalSide(caster)) return true; // not ours to send
        if (PlayerShells.TryGetPlayer(target, out var player))
        {
            W.Reset(); W.U8(KindHeal); W.U8(HealToPlayer); W.U8(player);
        }
        else if (CopyPtrs.ContainsKey(target.Pointer))
        {
            W.Reset(); W.U8(KindHeal); W.U8(HealToEntity); W.U32(NetIdOf(target));
        }
        else return true;
        W.U32(casterId); W.F32(hr.HealAmount); W.I32(stat); W.Bool(hr.IsHealOverTime);
        S.SendWorld(W.Span);
        return true;
    }

    private static bool _applyingHeal;

    private static void ReceiveHeal(byte from, ref NetReader r)
    {
        byte to = r.U8();
        CombatEntity target = null;
        if (to == HealToPlayer) { if (r.U8() == S.LocalId) target = GameInfo.Player; }
        else target = GetOwned(r.U32());
        uint casterId = r.U32();
        float amount = r.F32();
        int stat = r.I32();
        bool hot = r.Bool();
        if (target == null) return;
        CombatEntity caster = (casterId != 0 ? GetCopy(casterId) : null) ?? PlayerShells.Get(from);
        var hr = new CombatCalculations.HealResult
        {
            caster = caster ?? target, target = target, HealAmount = amount, HealedStatID = stat, IsHealOverTime = hot, HealActionType = "",
        };
        _applyingHeal = true;
        try { target.Heal(hr, stat, null); }
        catch (Exception e) { Plugin.L.LogWarning("Heal sync: " + e.Message); }
        finally { _applyingHeal = false; }
    }

    // ---------------- resurrection (the dead one's machine decides: a player gets the game's offer) ----------------

    /// <summary>
    /// A resurrection aimed at a remote player's shell or a copy of someone's bot goes to its owner; nothing happens
    /// here (the shell stands up when its player accepts). False = handled.
    /// </summary>
    private static bool OnResurrect(MobCombatEntity target, float percent, CombatEntity caster)
    {
        if (!Active || target == null || !target.Dead) return true;
        uint casterId = 0;
        if (caster != null && !TryGetOwnedNetId(caster, out casterId) && !IsLocalSide(caster)) return true;
        if (PlayerShells.TryGetPlayer(target, out var player))
        {
            W.Reset(); W.U8(KindResurrect); W.U8(HealToPlayer); W.U8(player);
        }
        else if (CopyPtrs.ContainsKey(target.Pointer))
        {
            W.Reset(); W.U8(KindResurrect); W.U8(HealToEntity); W.U32(NetIdOf(target));
        }
        else return true;
        W.U32(casterId); W.F32(percent);
        S.SendWorld(W.Span);
        ResurrectOffered.Add(target.Pointer);
        Plugin.L.LogInfo($"Resurrect: {target.GetNPCData()?.entryDisplayName} -> its owner");
        return false;
    }

    /// <summary>
    /// Dead ones already offered a resurrection: one offer per death, so the casters go back to healing (the owner
    /// accepts or respawns; either way the entity is alive again and can be offered the next time).
    /// </summary>
    private static readonly HashSet<IntPtr> ResurrectOffered = new();

    private static void ReceiveResurrect(byte from, ref NetReader r)
    {
        byte to = r.U8();
        CombatEntity target = null;
        if (to == HealToPlayer) { if (r.U8() == S.LocalId) target = GameInfo.Player; }
        else target = GetOwned(r.U32());
        uint casterId = r.U32();
        float percent = r.F32();
        if (target == null || !target.Dead) return;
        CombatEntity caster = (casterId != 0 ? GetCopy(casterId) : null) ?? PlayerShells.Get(from);
        Plugin.L.LogInfo($"Resurrect from #{from} ({percent:0}%)");
        try { target.Resurrect(percent, caster); } // a player gets the game's offer; a bot stands up
        catch (Exception e) { Plugin.L.LogWarning("Resurrect sync: " + e.Message); }
    }

    /// <summary>Owner: one of our mobs died; tell everyone who earned it.</summary>
    private static void OnOwnedDeath(MobCombatEntity mob)
    {
        if (!OwnedByPtr.TryGetValue(mob.Pointer, out var o)) return;
        Participants.TryGetValue(mob.Pointer, out var set);
        W.Reset(); W.U8(KindDied); W.U32(o.NetId);
        W.U8((byte)(set?.Count ?? 0));
        if (set != null) foreach (var p in set) W.U8(p);
        S.SendWorld(W.Span);
    }

    /// <summary>A copy's owner reports its death: die here the game's way; rewards only if we took part.</summary>
    private static void ReceiveDied(byte from, ref NetReader r)
    {
        uint id = r.U32();
        int n = r.U8();
        var set = new HashSet<byte>();
        for (int i = 0; i < n; i++) set.Add(r.U8());
        if (!Copies.TryGetValue(id, out var c) || c.E == null || c.E.WasCollected || c.DeathPlayed) return;
        c.DeathPlayed = true;
        if (Battlegrounds.Defeated(c.E)) return; // a battleground's defeat: down until its owner brings it back
        Participants[c.E.Pointer] = set;
        try { CombatUtilities.SetCurrentStatValue(c.E, HealthStat, 0f); } catch { }
        _authorizedDeath = true;
        try { c.E.EntityDeath(); }
        catch (Exception e) { Plugin.L.LogWarning("Mob sync death: " + e.Message); }
        finally { _authorizedDeath = false; }
    }

    // ---------------- someone else's pets ----------------

    private static readonly Dictionary<int, RPGNpc> ForeignPetRecords = new();

    /// <summary>
    /// Someone else's pet or companion is no NPC to deal with: the same record (a quest companion is also its quest's
    /// giver) without its roles, so it offers no quests, trade or dialogue here. Same ID, same look.
    /// </summary>
    private static RPGNpc ForeignPetRecord(RPGNpc npc)
    {
        if (ForeignPetRecords.TryGetValue(npc.ID, out var r) && r != null) return r;
        r = Object.Instantiate(npc);
        r.isQuestGiver = r.isMerchant = r.isDialogue = r.isTradable = r.isFlightMaster = false;
        ForeignPetRecords[npc.ID] = r;
        return r;
    }

    /// <summary>(player, NPC id) whose pet copy must not come back for a while: that bot was handed over to this machine.</summary>
    private static readonly Dictionary<(byte, int), float> SuppressedPets = new();

    /// <summary>
    /// A bot companion of that player now runs here (handed over): its copy goes at once, and the player's last few
    /// states (sent before they let it go) do not bring it back.
    /// </summary>
    public static void ReplacePetCopy(byte player, int npcId)
    {
        SuppressedPets[(player, npcId)] = Time.unscaledTime + 5f;
        foreach (var c in new List<Copy>(Copies.Values))
            if (IsPetKey(c.Spawner, out var p) && p == player && c.E != null && !c.E.WasCollected && c.E.GetNPCData()?.ID == npcId) DestroyCopy(c);
    }

    /// <summary>Copies of a remote player's pets (their summons and bot companions) on this machine.</summary>
    public static List<MobCombatEntity> PetCopiesOf(byte player)
    {
        var list = new List<MobCombatEntity>();
        foreach (var c in Copies.Values)
            if (IsPetKey(c.Spawner, out var p) && p == player && c.E != null && !c.E.WasCollected) list.Add(c.E);
        return list;
    }

    /// <summary>The player whose pet this copy is (their summon or bot companion).</summary>
    public static bool TryGetPetOwner(CombatEntity e, out byte player)
    {
        player = 0;
        return e != null && Copies.TryGetValue(NetIdOf(e), out var c) && IsPetKey(c.Spawner, out player);
    }

    /// <summary>A copy of a pet that belongs to another player or to another machine's mob/bot.</summary>
    public static bool IsForeignPet(CombatEntity e) =>
        e != null && Copies.TryGetValue(NetIdOf(e), out var c) && (c.Parent != 0 || IsPetKey(c.Spawner, out _));

    // ---------------- phasing (WoW-like) ----------------
    //
    // Quests change the world per player: taking Darian's quest runs the game action "kill NPC #172" (the quest giver
    // leaves) and summons a companion that follows the player. In a shared world the giver stays for everyone else:
    // on the quest taker's machine it is only hidden (a copy is not shown; a giver this machine runs keeps existing,
    // inactive, so the others still see and talk to it). It comes back here when its spawner's requirements (the
    // same ones that keep the game from respawning it) are met for this player again.

    /// <summary>NPCs gone for this player, with the quest that took them (0 = not known yet).</summary>
    private static readonly Dictionary<int, int> PhasedNpcs = new();
    private static readonly Dictionary<int, float> PhasedAt = new();
    private static int _acceptedQuest; private static float _acceptedAt = -10f;
    private const float QuestLinkWindow = 2f; // the dialogue accepts the quest and runs its actions together

    private static int NpcIdOf(MobCombatEntity e)
    {
        try { var n = e.GetNPCData(); return n != null ? n.ID : -1; } catch { return -1; }
    }

    public static bool IsSyncedNpc(int npcId)
    {
        foreach (var o in OwnedByPtr.Values) if (o.E != null && !o.E.WasCollected && NpcIdOf(o.E) == npcId) return true;
        foreach (var c in Copies.Values) if (c.E != null && !c.E.WasCollected && NpcIdOf(c.E) == npcId) return true;
        return false;
    }

    public static void PhaseOut(int npcId)
    {
        int quest = Time.unscaledTime - _acceptedAt <= QuestLinkWindow ? _acceptedQuest : 0;
        PhasedNpcs[npcId] = quest; PhasedAt[npcId] = Time.unscaledTime;
        Plugin.L.LogInfo($"Phasing: NPC #{npcId} leaves this player's world (stays for the others){(quest != 0 ? $" until quest #{quest} ends" : "")}");
        UpdatePhasing();
    }

    /// <summary>A quest was accepted: it is what takes NPCs phased out just before (same dialogue).</summary>
    private static void OnQuestAccepted(int questId)
    {
        _acceptedQuest = questId; _acceptedAt = Time.unscaledTime;
        foreach (var id in new List<int>(PhasedNpcs.Keys))
            if (PhasedNpcs[id] == 0 && Time.unscaledTime - PhasedAt[id] <= QuestLinkWindow) PhasedNpcs[id] = questId;
    }

    /// <summary>The quest ended (turned in or abandoned): the NPCs it took come back for this player.</summary>
    private static void OnQuestEnded(int questId)
    {
        foreach (var id in new List<int>(PhasedNpcs.Keys))
            if (PhasedNpcs[id] == questId) { PhasedNpcs.Remove(id); Plugin.L.LogInfo($"Phasing: NPC #{id} is back for this player"); }
        UpdatePhasing();
    }

    private static void UpdatePhasing()
    {
        foreach (var o in OwnedByPtr.Values)
        {
            if (o.E == null || o.E.WasCollected) continue;
            bool hide = PhasedNpcs.ContainsKey(NpcIdOf(o.E));
            if (o.E.gameObject.activeSelf == hide) o.E.gameObject.SetActive(!hide);
        }
        if (PhasedNpcs.Count == 0) return;
        List<Copy> gone = null;
        foreach (var c in Copies.Values)
            if (c.E != null && !c.E.WasCollected && PhasedNpcs.ContainsKey(NpcIdOf(c.E))) (gone ??= new()).Add(c);
        if (gone != null) foreach (var c in gone) DestroyCopy(c);
    }

    // ---------------- ownership changes ----------------

    public static void OnLeaseChanged(uint key, byte owner, uint epoch)
    {
        if (!Active) return;
        IndexSpawners();
        byte me = S.LocalId;
        if (owner == me)
        {
            // Our copies become the real mobs; the spawner fills up what is missing.
            Zones.TryGetValue(key, out var zone);
            foreach (var c in new List<Copy>(Copies.Values))
            {
                if (c.Spawner != key || c.E == null || c.E.WasCollected) continue;
                Copies.Remove(c.NetId);
                CopyPtrs.Remove(c.E.Pointer);
                FormVisual.Clear(c.E.gameObject); // a form or mount shown for the former owner; ours are the game's own
                MountVisual.Clear(c.E.gameObject);
                SetBrain(c.E, true);
                if (zone != null && c.Bot) AdoptBot(c.E, zone);
                OwnedByPtr[c.E.Pointer] = new Owned { NetId = c.NetId, Spawner = key, Bot = c.Bot, Parent = c.Parent, E = c.E, Anim = c.Anim };
            }
            if (Spawners.TryGetValue(key, out var sp) && sp != null && sp.IsActive) sp.Initialize();
            if (zone != null && zone.IsActive) AdventurerPopulationManager.Instance?.FillZone(zone);
            Plugin.L.LogInfo($"Mob sync: now simulating spawner {key:x8}");
        }
        else
        {
            // Our mobs of this spawner become copies of the new owner's.
            foreach (var kv in new List<KeyValuePair<IntPtr, Owned>>(OwnedByPtr))
            {
                var o = kv.Value;
                if (o.Spawner != key) continue;
                OwnedByPtr.Remove(kv.Key);
                if (o.E == null || o.E.WasCollected || owner == LeaseRegistry.NoOwner) continue;
                SetBrain(o.E, false);
                if (o.Bot && Zones.TryGetValue(key, out var z) && z != null) z.CurrentAdventurers?.Remove(o.E); // no longer ours to save
                Copies[o.NetId] = new Copy { NetId = o.NetId, Spawner = key, Owner = owner, Bot = o.Bot, Parent = o.Parent, E = o.E, Ptr = kv.Key, Anim = o.Anim, GearHash = o.SentGear };
                CopyPtrs[kv.Key] = o.NetId;
            }
        }
    }

    /// <summary>A bot copy becomes this machine's bot: into the zone and the population the way SpawnAdventurer puts it.</summary>
    private static void AdoptBot(MobCombatEntity m, AdventurerSpawnZone zone)
    {
        try
        {
            if (zone.CurrentAdventurers != null && !zone.CurrentAdventurers.Contains(m)) zone.CurrentAdventurers.Add(m);
            m.SetAdventurerZone(zone);
            var pm = AdventurerPopulationManager.Instance;
            var npc = m.GetNPCData();
            if (pm == null || npc == null) return;
            pm.spawnedNpcIDs?.Add(npc.ID);
            if (pm.npcToZone != null) pm.npcToZone[npc.ID] = zone;
        }
        catch (Exception e) { Plugin.L.LogWarning("Bot adoption: " + e.Message); }
    }

    /// <summary>
    /// What a player ran without a lease (their pets and companions, debug spawns) leaves with them. Spawner and
    /// zone entities stay: their leases move to someone else, who takes the copies over.
    /// </summary>
    // ---------------- bots recruited by other players ----------------
    //
    // Recruiting takes a bot out of its world (the game: IsNpcAliveAsPlayerPet keeps zones from spawning it). Every
    // machine has its own bot world, so the others would still spawn that bot in their zones: each player says which
    // bots are theirs, and here those count as someone's pets too; one already standing in a zone we run leaves it.

    private static readonly Dictionary<byte, HashSet<int>> RecruitedBy = new();
    private static string _sentRecruited = "";
    private static float _nextRecruited, _resendRecruited;

    private static void SendRecruited()
    {
        if (Time.unscaledTime < _nextRecruited) return;
        _nextRecruited = Time.unscaledTime + 2f;
        var ids = new List<int>();
        var pets = GameInfo.Player?.GetCurrentPets();
        if (pets != null)
            foreach (var p in pets)
            {
                var npc = p?.GetNPCData();
                if (npc != null && (int)npc.npcType == 9) ids.Add(npc.ID);
            }
        ids.Sort();
        string sig = string.Join(",", ids);
        if (sig == _sentRecruited && Time.unscaledTime < _resendRecruited) return; // on change, and now and then for newcomers
        _sentRecruited = sig;
        _resendRecruited = Time.unscaledTime + 20f;
        W.Reset(); W.U8(KindRecruited); W.U8((byte)Math.Min(ids.Count, 255));
        for (int i = 0; i < ids.Count && i < 255; i++) W.I32(ids[i]);
        S.SendWorld(W.Span);
    }

    private static void ReceiveRecruited(byte from, ref NetReader r)
    {
        int n = r.U8();
        var set = new HashSet<int>();
        for (int i = 0; i < n; i++) set.Add(r.I32());
        RecruitedBy[from] = set;
        // a world bot this machine runs that is now that player's companion: it leaves our zone
        foreach (var o in new List<Owned>(OwnedByPtr.Values))
        {
            if (!o.Bot || o.Parent != 0 || o.E == null || o.E.WasCollected || o.E.IsPet()) continue;
            var npc = o.E.GetNPCData();
            if (npc == null || !set.Contains(npc.ID)) continue;
            Plugin.L.LogInfo($"Bot '{npc.entryName}' is #{from}'s companion now: leaves the zone here");
            try { AdventurerPopulationManager.Instance?.OnAdventurerDestroyed(npc.ID); } catch { }
            Object.Destroy(o.E.gameObject);
        }
    }

    public static bool RecruitedByOther(int npcId)
    {
        foreach (var kv in RecruitedBy) if (kv.Value.Contains(npcId)) return true;
        return false;
    }

    public static void OnPlayerLeft(byte player)
    {
        RecruitedBy.Remove(player);
        foreach (var c in new List<Copy>(Copies.Values))
            if (c.Owner == player && (c.Spawner == NoSpawner || IsPetKey(c.Spawner, out _))) DestroyCopy(c);
    }

    /// <summary>No longer near any spawner of the place we were in: the session moves their leases to others there.</summary>
    private static void ReleaseInterest()
    {
        if (Interest.Count == 0) return;
        var s = CoopManager.Connected ? CoopManager.Session : null;
        if (s != null) foreach (var key in Interest) s.SetInterest(key, false, leftPlace: true);
        Plugin.L.LogInfo($"Mob sync: left '{_scene}', released {Interest.Count} spawners");
        Interest.Clear();
    }

    public static void Reset()
    {
        foreach (var c in new List<Copy>(Copies.Values)) DestroyCopy(c);
        Copies.Clear(); CopyPtrs.Clear(); OwnedByPtr.Clear(); Interest.Clear(); Participants.Clear(); PhasedNpcs.Clear(); PhasedAt.Clear();
        Spawners.Clear(); KeyOf.Clear(); Zones.Clear(); ZoneKeyOf.Clear(); _scene = "";
    }

    /// <summary>Dev: the local player hits the nearest synced mob through the game's own damage path.</summary>
    public static string DevHit(float amount, string name = "")
    {
        var me = GameInfo.Player;
        MobCombatEntity best = null; float bestD = 40f;
        foreach (var m in Object.FindObjectsOfType<MobCombatEntity>())
        {
            if (m == null || !IsSynced(m) || PlayerShells.TryGetPlayer(m, out _) || IsPetEntity(m)) continue;
            if (name.Length > 0 && m.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            try { if (m.IsDead()) continue; } catch { continue; }
            float d = Vector3.Distance(m.transform.position, me.transform.position);
            if (d < bestD) { bestD = d; best = m; }
        }
        if (best == null) return "no synced mob within 40 m";
        float before = Health(best), max = MaxHealth(best);
        best.TakeDamage(new CombatCalculations.DamageResult { caster = me, target = best, DamageAmount = amount, DamageActionType = "" }, null, HealthStat);
        return $"hit '{best.name}' ({(IsCopy(best) ? "copy" : "own")}, {bestD:0} m) for {amount}: hp {before:0} -> {Health(best):0} / {max:0} (lvl {best.GetLevel()})";
    }

    /// <summary>Dev: the nearest mob this machine runs attacks a remote player's shell.</summary>
    public static string DevSic(byte player, string name = "")
    {
        var shell = PlayerShells.Get(player);
        if (shell == null) return $"no shell for #{player}";
        Owned best = null; float bestD = 40f;
        foreach (var o in OwnedByPtr.Values)
        {
            if (o.E == null || o.E.WasCollected || IsPetEntity(o.E)) continue;
            bool pick;
            if (name.Length > 0) pick = o.E.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            else { var ai = o.E.GetComponent<AIEntity>(); pick = false; try { pick = ai != null && ai.CanAggroTarget(shell); } catch { } }
            if (!pick) continue;
            float d = Vector3.Distance(o.E.transform.position, shell.transform.position);
            if (d < bestD) { bestD = d; best = o; }
        }
        if (best == null) return "no own hostile mob within 40 m of the shell";
        best.E.GetComponent<AIEntity>()?.AlterThreatTable(shell, 100);
        return $"'{best.E.name}' ({bestD:0} m) attacks #{player}";
    }

    /// <summary>Dev: a mob this machine runs goes for a bot (by name part, a copy of someone's bot or one of mine).</summary>
    public static string DevSicBot(string bot, string mob)
    {
        MobCombatEntity target = null;
        foreach (var e in AllSynced()) if (e != null && !e.Dead && e.name.IndexOf(bot, StringComparison.OrdinalIgnoreCase) >= 0) { target = e; break; }
        if (target == null) return "no such bot here";
        foreach (var o in OwnedByPtr.Values)
        {
            if (o.E == null || o.E.WasCollected || o.E.Dead || IsPetEntity(o.E) || o.E.name.IndexOf(mob, StringComparison.OrdinalIgnoreCase) < 0) continue;
            o.E.GetComponent<AIEntity>()?.AlterThreatTable(target, 1000);
            return $"'{o.E.name}' attacks '{target.name}' ({(IsCopy(target) ? "copy" : "mine")})";
        }
        return "no own mob of that name";
    }

    private static MobCombatEntity NearestSynced(float range = 40f)
    {
        var me = GameInfo.Player;
        MobCombatEntity best = null; float bestD = range;
        foreach (var m in Object.FindObjectsOfType<MobCombatEntity>())
        {
            if (m == null || !IsSynced(m) || PlayerShells.TryGetPlayer(m, out _) || IsPetEntity(m)) continue;
            try { if (m.IsDead()) continue; } catch { continue; }
            float d = Vector3.Distance(m.transform.position, me.transform.position);
            if (d < bestD) { bestD = d; best = m; }
        }
        return best;
    }

    /// <summary>Dev: the local player puts a status effect (by name) on the nearest synced mob.</summary>
    public static string DevDebuff(string name)
    {
        var mob = NearestSynced();
        if (mob == null) return "no synced mob within 40 m";
        RPGEffect effect = null;
        foreach (var kv in GameDatabase.Instance.GetEffects())
            if (kv.Value != null && (kv.Value.entryName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value.duration > 1f) { effect = kv.Value; break; }
        if (effect == null) return $"no lasting effect named like '{name}'";
        CombatManager.Instance.InitNewStateEffect(effect, 0, GameInfo.Player, effect.icon, mob);
        return $"'{effect.entryName}' (#{effect.ID}, {effect.effectType}, {effect.duration:0.#} s) on '{mob.name}' ({(IsCopy(mob) ? "copy" : "own")})";
    }

    /// <summary>Dev: the nearest mob this machine runs puts a status effect (by name) on a remote player's shell.</summary>
    public static string DevDebuffPlayer(byte player, string name)
    {
        var shell = PlayerShells.Get(player);
        if (shell == null) return $"no shell for #{player}";
        Owned caster = null; float bestD = 60f;
        foreach (var o in OwnedByPtr.Values)
        {
            if (o.E == null || o.E.WasCollected) continue;
            float d = Vector3.Distance(o.E.transform.position, shell.transform.position);
            if (d < bestD) { bestD = d; caster = o; }
        }
        if (caster == null) return "no own mob near the shell";
        RPGEffect effect = null;
        foreach (var kv in GameDatabase.Instance.GetEffects())
            if (kv.Value != null && (kv.Value.entryName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0 && kv.Value.duration > 1f) { effect = kv.Value; break; }
        if (effect == null) return $"no lasting effect named like '{name}'";
        CombatManager.Instance.InitNewStateEffect(effect, 0, caster.E, effect.icon, shell);
        return $"'{caster.E.name}' puts '{effect.entryName}' (#{effect.ID}, {effect.effectType}) on #{player}";
    }

    /// <summary>Dev: health, max health and level of synced mobs near the player, by net id (same id on every machine).</summary>
    public static string DevMobHp()
    {
        var me = GameInfo.Player;
        var rows = new List<string>();
        foreach (var o in OwnedByPtr.Values)
            if (o.E != null && !o.E.WasCollected && Vector3.Distance(o.E.transform.position, me.transform.position) < 40f)
                rows.Add($"{o.NetId:x8} {o.E.name} own  hp {Health(o.E):0}/{MaxHealth(o.E):0} lvl {o.E.GetLevel()}");
        foreach (var c in Copies.Values)
            if (c.E != null && !c.E.WasCollected && Vector3.Distance(c.E.transform.position, me.transform.position) < 40f)
                rows.Add($"{c.NetId:x8} {c.E.name} copy hp {Health(c.E):0}/{MaxHealth(c.E):0} lvl {c.E.GetLevel()}");
        rows.Sort();
        return string.Join(Environment.NewLine, rows);
    }

    /// <summary>Dev: every synced mob that has status effects, with them.</summary>
    public static string DevMobStates()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var m in Object.FindObjectsOfType<MobCombatEntity>())
        {
            if (m == null || !IsSynced(m)) continue;
            var states = m.GetStates();
            if (states == null || states.Count == 0) continue;
            sb.Append($"'{m.name}' ({(IsCopy(m) ? "copy" : "own")}):");
            foreach (var st in states) if (st != null) sb.Append($" [{st.stateName} #{st.stateEffectID} {st.stateCurDuration:0.0}/{st.stateMaxDuration:0.0}]");
            sb.AppendLine();
        }
        return sb.Length > 0 ? sb.ToString() : "no synced mob has states";
    }

    /// <summary>Dev: the local character dies through the game's own damage path (from the nearest mob, if any).</summary>
    public static string DevDie()
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        CombatEntity caster = NearestSynced(60f) ?? (CombatEntity)me;
        me.TakeDamage(new CombatCalculations.DamageResult { caster = caster, target = me, DamageAmount = Health(me) + 1000f, DamageActionType = "" }, null, HealthStat);
        return $"dead={me.IsDead()} (hit by '{caster.name}')";
    }

    /// <summary>Dev: the local player loses this share of max health (for heals by others).</summary>
    public static string DevHurt(float percent)
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        float amount = MaxHealth(me) * percent / 100f;
        CombatEntity caster = NearestSynced(60f) ?? (CombatEntity)me;
        me.TakeDamage(new CombatCalculations.DamageResult { caster = caster, target = me, DamageAmount = amount, DamageActionType = "" }, null, HealthStat);
        return $"hp {Health(me):0}/{MaxHealth(me):0}";
    }

    /// <summary>Dev: what the mobs this machine runs are fighting (target, distance), to see aggro and its loss.</summary>
    public static string DevTargets()
    {
        var rows = new List<string>();
        foreach (var o in OwnedByPtr.Values)
        {
            if (o.E == null || o.E.WasCollected) continue;
            CombatEntity t = null;
            try { t = o.E.GetTarget(); } catch { }
            bool fighting = false; try { fighting = o.E.IsInCombat(); } catch { }
            if (t == null && !fighting) continue;
            rows.Add($"'{o.E.name}' combat={fighting} target='{(t != null ? t.name : "-")}'{(t != null ? $" dead={t.IsDead()} {Vector3.Distance(o.E.transform.position, t.transform.position):0} m" : "")}");
        }
        foreach (var c in Copies.Values)
        {
            CombatEntity t = null;
            try { t = c.E?.GetTarget(); } catch { }
            if (t != null) rows.Add($"copy '{c.E.name}' (#{c.Owner}'s) target='{t.name}'");
        }
        return rows.Count > 0 ? string.Join(Environment.NewLine, rows) : "no mob is fighting";
    }

    /// <summary>Dev: respawn after death the way the death window's button does.</summary>
    public static string DevRespawn()
    {
        var popup = Object.FindObjectOfType<CharacterDeathPopUp>();
        if (popup == null) return "no death window";
        popup.ClickRespawn();
        return "respawn clicked";
    }

    /// <summary>Dev: the local player heals a remote player (their shell) through the game's heal path.</summary>
    public static string DevHealPlayer(byte player, float amount)
    {
        var shell = PlayerShells.Get(player);
        if (shell == null) return $"no shell for #{player}";
        var me = GameInfo.Player;
        shell.Heal(new CombatCalculations.HealResult { caster = me, target = shell, HealAmount = amount, HealedStatID = HealthStat, HealActionType = "" }, HealthStat, null);
        return $"healed #{player} for {amount}";
    }

    public static string DevMe()
    {
        var me = GameInfo.Player; var cd = GameInfo.Character;
        return me == null ? "not in world" : $"hp {Health(me):0} level {cd?.Level} xp {cd?.CurrentExperience}/{cd?.ExperienceNeeded}";
    }

    public static string Describe()
    {
        int active = 0, owned = 0, zNear = 0, zOwned = 0, bots = 0, botCopies = 0;
        foreach (var kv in Spawners) { if (kv.Value != null && kv.Value.IsActive) active++; if (IOwn(kv.Key)) owned++; }
        foreach (var kv in Zones) { if (kv.Value != null && kv.Value.IsActive) zNear++; if (IOwn(kv.Key)) zOwned++; }
        foreach (var o in OwnedByPtr.Values) if (o.Bot) bots++;
        foreach (var c in Copies.Values) if (c.Bot) botCopies++;
        return $"spawners={Spawners.Count} near={active} owned={owned} zones={Zones.Count} near={zNear} owned={zOwned} leases={S?.LeaseCount} " +
               $"ownMobs={OwnedByPtr.Count} (bots {bots}) copies={Copies.Count} (bots {botCopies}) shells={PlayerShells.Count}";
    }

    /// <summary>Dev: this machine's bots (and their pets) walk off the navmesh to the local player, a few metres apart.</summary>
    public static string DevBotsHere()
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        var rows = new List<string>();
        int i = 0;
        foreach (var o in OwnedByPtr.Values)
        {
            if (!o.Bot || o.E == null || o.E.WasCollected) continue;
            var at = me.transform.position + Quaternion.Euler(0, 72f * i++, 0) * Vector3.forward * 5f;
            float d = Vector3.Distance(o.E.transform.position, at);
            Warp(o.E, at);
            foreach (var p in OwnedByPtr.Values)
                if (p.Parent == o.NetId && p.E != null && !p.E.WasCollected) Warp(p.E, at + Vector3.right * 1.5f);
            rows.Add($"'{o.E.GetNPCData()?.entryName}' from {d:0} m");
        }
        return rows.Count > 0 ? "warped: " + string.Join(", ", rows) : "no own bots";
    }

    /// <summary>Dev: go to the n-th nearest adventurer zone (bots appear in zones near a player).</summary>
    public static string DevTpZone(int n)
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        IndexSpawners();
        var list = new List<(float d, AdventurerSpawnZone z)>();
        foreach (var z in Zones.Values) if (z != null) list.Add((Vector3.Distance(me.transform.position, z.transform.position), z));
        list.Sort((a, b) => a.d.CompareTo(b.d));
        if (list.Count == 0) return "no zones here";
        var pick = list[Math.Min(n, list.Count - 1)];
        GameInfo.TeleportPlayer(pick.z.transform.position + Vector3.up);
        return $"zone '{pick.z.name}' {pick.d:0} m away ({list.Count} zones)";
    }

    /// <summary>Dev: recruit the nearest own world bot of a role (0 damage, 1 tank, 2 healer) the game's way (interaction "Invite to group").</summary>
    public static string DevRecruit(int role, string name = null)
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        MobCombatEntity best = null;
        float bestD = float.MaxValue;
        foreach (var o in OwnedByPtr.Values)
        {
            if (!o.Bot || o.Parent != 0 || o.E == null || o.E.WasCollected || o.E.Dead || o.E.IsPet()) continue;
            if (name != null)
            {
                string cls = "";
                try { cls = o.E.ClassProgression?.Build?.Class?.entryName ?? ""; } catch { }
                if (cls.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0 && (o.E.GetNPCData()?.entryName ?? "").IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            }
            else if ((int)BLINK.RPGBuilder.Dungeons.DungeonFinderService.Role(o.E) != role) continue;
            float d = Vector3.Distance(o.E.transform.position, me.transform.position);
            if (d < bestD) { bestD = d; best = o.E; }
        }
        if (best == null) return "no own bot of that role (recruitnpc <id> recruits from a template)";
        var panel = UnityEngine.Object.FindObjectOfType<BLINK.RPGBuilder.UI.NPCInteractionsPanel>(true);
        if (panel == null) return "no interaction panel";
        Warp(best, me.transform.position + me.transform.forward * 3f);
        panel.entity = best;
        panel.InviteToGroup();
        return $"invited '{best.GetNPCData()?.entryName}' from {bestD:0} m; pets now {me.GetCurrentPets()?.Count}";
    }

    /// <summary>Dev: recruit an adventurer template as a companion, as the game's adventurer friends list does.</summary>
    public static string DevRecruitNpc(int npcId)
    {
        var me = GameInfo.Player;
        if (me == null) return "not in world";
        var db = GameDatabase.Instance;
        var npc = db.GetNPCs().TryGetValue(npcId, out var n) ? n : null;
        if (npc == null) return "no such npc";
        db.GetEffects().TryGetValue(npc.InviteEffectID, out var effect);
        var bot = CombatManager.Instance.RecruitAdventurer(me, npc, effect);
        return bot != null ? $"recruited '{npc.entryName}'; bots {Groups.CountBots()}" : $"recruit failed (invite effect {npc.InviteEffectID}: {(effect != null ? effect.entryName : "none")})";
    }

    private static void Warp(MobCombatEntity m, Vector3 at)
    {
        var agent = m.GetComponent<NavMeshAgent>();
        if (agent != null && agent.enabled) agent.Warp(at); else m.transform.position = at;
    }

    /// <summary>Dev: synced bots near the player: net id, name, level, hp, equipment item IDs (same on every machine).</summary>
    public static string DevBots()
    {
        var me = GameInfo.Player;
        var rows = new List<string>();
        void Row(uint id, MobCombatEntity e, string kind)
        {
            if (e == null || e.WasCollected || Vector3.Distance(e.transform.position, me.transform.position) > 80f) return;
            ReadGear(e, GearArmor, GearWeapons);
            rows.Add($"{id:x8} {kind} '{e.GetNPCData()?.entryName}' lvl {e.GetLevel()} hp {Health(e):0}/{MaxHealth(e):0} " +
                     $"combat {e.IsInCombat()} armor [{string.Join(",", GearArmor)}] weapons [{string.Join(",", GearWeapons)}]");
        }
        foreach (var o in OwnedByPtr.Values) if (o.Bot) Row(o.NetId, o.E, "own ");
        foreach (var c in Copies.Values) if (c.Bot) Row(c.NetId, c.E, "copy");
        rows.Sort();
        return rows.Count > 0 ? string.Join(Environment.NewLine, rows) : "no synced bots within 80 m";
    }

    // ---------------- game hooks ----------------

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>Only the lease owner's game spawns a spawner's mobs (until the lease is known: nobody does).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCSpawner), nameof(NPCSpawner.SpawnNPC))]
        private static bool OnlyOwnerSpawns(NPCSpawner __instance, ref bool __result)
        {
            try
            {
                if (S == null) return true;
                if (!GameInfo.InWorld) { __result = false; return false; } // loading: the lease owner starts it once in the world
                if (!TryKey(__instance, out var key) || IOwn(key)) return true;
                __result = false;
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Mob sync spawn gate: " + e.Message); return true; }
        }

        /// <summary>Only the zone's lease owner fills it with bots from its bot world (until the lease is known: nobody does).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerPopulationManager), nameof(AdventurerPopulationManager.FillZone))]
        private static bool OnlyOwnerFills(AdventurerSpawnZone __0) => MayFill(__0);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerPopulationManager), nameof(AdventurerPopulationManager.SpawnAdventurer))]
        private static bool OnlyOwnerSpawnsBots(AdventurerSpawnZone __1) => MayFill(__1); // a fill already running when the lease moved

        private static bool MayFill(AdventurerSpawnZone zone)
        {
            try { return S == null || (GameInfo.InWorld && (!TryKey(zone, out var key) || IOwn(key))); }
            catch (Exception e) { Plugin.L.LogWarning("Bot sync spawn gate: " + e.Message); return true; }
        }

        /// <summary>
        /// A game action that kills an NPC everyone shares (a quest giver leaving with the quest) only phases it out
        /// for this player; the rest of the actions run as usual.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(GameActionsManager), nameof(GameActionsManager.TriggerGameActions))]
        private static bool PhaseInsteadOfKill(GameActionsManager __instance, CombatEntity __0,
            Il2CppSystem.Collections.Generic.List<GameActionsData.GameAction> __1)
        {
            if (_runningActions || !Active || __1 == null) return true;
            try
            {
                bool IsSharedKill(GameActionsData.GameAction a) =>
                    a != null && a.type == GameActionsData.GameActionType.NPC && a.NPCAction == GameActionsData.NPCAction.Kill && IsSyncedNpc(a.NPCID);
                bool any = false;
                foreach (var a in __1) any |= IsSharedKill(a);
                if (!any) return true;
                var rest = new Il2CppSystem.Collections.Generic.List<GameActionsData.GameAction>();
                foreach (var a in __1)
                {
                    if (IsSharedKill(a)) PhaseOut(a.NPCID);
                    else rest.Add(a);
                }
                _runningActions = true;
                try { __instance.TriggerGameActions(__0, rest); }
                finally { _runningActions = false; }
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Phasing: " + e.Message); return true; }
        }

        private static bool _runningActions;

        /// <summary>Nobody talks to someone else's companion (it would offer its quest again).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.Interact))]
        private static bool NotWithForeignPets(MobCombatEntity __instance) => !Active || !IsForeignPet(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldEvents), nameof(WorldEvents.OnQuestAccepted))]
        private static void QuestAccepted(RPGQuest __0) { if (__0 != null) OnQuestAccepted(__0.ID); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldEvents), nameof(WorldEvents.OnQuestTurnedIn))]
        private static void QuestTurnedIn(RPGQuest __0) { if (__0 != null) OnQuestEnded(__0.ID); }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(WorldEvents), nameof(WorldEvents.OnQuestAbandoned))]
        private static void QuestAbandoned(RPGQuest __0) { if (__0 != null) OnQuestEnded(__0.ID); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CombatEntity), nameof(CombatEntity.Heal))]
        private static void Heal(CombatEntity __instance, CombatCalculations.HealResult __0, int __1)
        {
            try { OnHeal(__instance, __0, __1); }
            catch (Exception e) { Plugin.L.LogWarning("Heal sync: " + e.Message); }
        }

        /// <summary>A bot another player recruited counts as a player's pet here too: zones do not spawn it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AdventurerPopulationManager), nameof(AdventurerPopulationManager.IsNpcAliveAsPlayerPet))]
        private static void RecruitedElsewhere(int __0, ref bool __result)
        {
            if (!__result && Active && RecruitedByOther(__0)) __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.Resurrect))]
        private static bool Resurrect(MobCombatEntity __instance, float __0, CombatEntity __1)
        {
            if (_reviving) return true;
            try { return OnResurrect(__instance, __0, __1); }
            catch (Exception e) { Plugin.L.LogWarning("Resurrect sync: " + e.Message); return true; }
        }

        /// <summary>A dead remote player can be resurrected like the local one (the game allows players and bots).</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatEntity), nameof(CombatEntity.IsResurrectable))]
        private static void Resurrectable(CombatEntity __0, ref bool __result)
        {
            if (!Active || __0 == null) return;
            if (!__0.Dead) { ResurrectOffered.Remove(__0.Pointer); return; }
            if (ResurrectOffered.Contains(__0.Pointer)) { __result = false; return; }
            if (!__result && PlayerShells.TryGetPlayer(__0, out _)) __result = true;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.TakeDamage))]
        private static bool TakeDamage(MobCombatEntity __instance, CombatCalculations.DamageResult __0, RPGAbility.RPGAbilityRankData __1, int __2)
        {
            try { return OnMobTakeDamage(__instance, __0, __1, __2); }
            catch (Exception e) { Plugin.L.LogWarning("Mob sync damage: " + e.Message); return true; }
        }

        /// <summary>A copy dies only when its owner says so.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.EntityDeath))]
        private static bool CopyDiesOnlyWhenTold(MobCombatEntity __instance) => _authorizedDeath || !IsCopy(__instance);

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MobCombatEntity), nameof(MobCombatEntity.EntityDeath))]
        private static void AfterDeath(MobCombatEntity __instance)
        {
            try { if (Active) OnOwnedDeath(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Mob sync death report: " + e.Message); }
        }

        /// <summary>XP and loot of a synced mob: only for players who damaged it.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(EconomyUtilities), nameof(EconomyUtilities.ShouldRewardPlayer))]
        private static bool RewardOnlyParticipants(CombatEntity __0, ref bool __result)
        {
            if (!Active || !IsSynced(__0) || IsPetEntity(__0)) return true; // pets: the game's own rule
            __result = LocalParticipated(__0);
            return false;
        }

        /// <summary>Quest kill credit (an event on the mob, fired whoever killed it): only for participants.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PixelCrushers.RPGBuilderSupport.CombatEntityEvent), "OnEntityDied")]
        private static bool QuestCreditOnlyParticipants(CombatEntity __0)
        {
            if (!Active || !IsSynced(__0) || IsPetEntity(__0)) return true;
            return LocalParticipated(__0);
        }
    }
}
