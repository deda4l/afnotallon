using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.LogicMono;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Ability objects the owner's game spawns into the world (projectiles, ground AoE) and moves with physics or homing.
/// Their logic (hits, damage) stays with the owner; everyone else gets a visual-only copy of the same prefab, created
/// from the ability record (ability ID + rank), with colliders/physics/hit logic removed, following the owner's object
/// position until the owner's object is gone.
/// </summary>
internal static class VisualMirror
{
    // Payload kinds share FxSync's kind byte space.
    public const byte KindSpawn = 30, KindMove = 31, KindEnd = 32;
    private enum Src : byte { Projectile = 1, Ground = 2 }

    private const float MoveInterval = 0.05f; // 20 Hz

    /// <summary>Dev: log projectile spawns and ends (positions) on both sides.</summary>
    public static bool Trace;
    private static void T(string s) { if (Trace) Plugin.L.LogInfo($"[mirror {DateTime.Now:HH:mm:ss.fff}] {s}"); }
    private static readonly NetWriter W = new();

    // ---------------- owner ----------------

    private sealed class Tracked
    {
        public ushort Id; public GameObject Go; public Transform Tr; public Vector3 Pos; public Quaternion Rot;
        public Vector3 Last; // where it was in the last frame it lived: the end goes there (a hit between two sends)
    }

    private static readonly List<Tracked> Live = new();
    private static ushort _nextId;
    private static float _nextMove;

    private static void Track(GameObject go, Src src, RPGAbility ability, RPGAbility.RPGAbilityRankData rank, float lifetime)
    {
        if (go == null || ability == null || rank == null || !go.scene.IsValid()) return; // a prefab asset, not an instance
        int rankIndex = ability.ranks != null ? ability.ranks.IndexOf(rank) : -1;
        if (rankIndex < 0) return;
        var t = new Tracked { Id = ++_nextId, Go = go, Tr = go.transform, Pos = go.transform.position, Rot = go.transform.rotation, Last = go.transform.position };
        Live.Add(t);
        W.Reset(); W.U8(KindSpawn); W.U16(t.Id); W.U8((byte)src); W.I32(ability.ID); W.I32(rankIndex);
        WritePose(t.Pos, t.Rot);
        var s = t.Tr.lossyScale; W.F32(s.x); W.F32(s.y); W.F32(s.z);
        W.F32(lifetime);
        CoopManager.Session.SendFx(W.Span);
        T($"spawn #{t.Id} '{go.name}' at {t.Pos} ability {ability.ID}");
        Prof.Count("mirror.spawn");
    }

    private static void WritePose(Vector3 p, Quaternion r)
    {
        W.F32(p.x); W.F32(p.y); W.F32(p.z); W.F32(r.x); W.F32(r.y); W.F32(r.z); W.F32(r.w);
    }

    /// <summary>Owner side: stream positions of live objects, report the ones that are gone.</summary>
    public static void Update()
    {
        if (Live.Count == 0) return;
        var session = CoopManager.Session;
        if (session == null || !CoopManager.Connected) { Live.Clear(); return; }
        // Every frame: an object gone ends at once, where it last was (positions go out at 20 Hz only).
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var t = Live[i];
            if (t.Go == null || !t.Go.activeInHierarchy)
            {
                Live.RemoveAt(i);
                var end = new NetWriter(); end.U8(KindEnd); end.U16(t.Id);
                end.F32(t.Last.x); end.F32(t.Last.y); end.F32(t.Last.z);
                T($"end #{t.Id} at {t.Last} (from {t.Pos} last sent)");
                session.SendFx(end.Span);
                continue;
            }
            t.Last = t.Tr.position;
        }
        if (Live.Count == 0 || Time.unscaledTime < _nextMove) return;
        _nextMove = Time.unscaledTime + MoveInterval;

        W.Reset(); W.U8(KindMove);
        int countAt = W.Length; W.U16(0);
        ushort moved = 0;
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            var t = Live[i];
            var p = t.Tr.position; var r = t.Tr.rotation;
            if ((p - t.Pos).sqrMagnitude < 0.0001f && Quaternion.Angle(r, t.Rot) < 0.5f) continue;
            t.Pos = p; t.Rot = r;
            W.U16(t.Id); WritePose(p, r);
            moved++;
        }
        if (moved == 0) return;
        W.PatchU16(countAt, moved);
        session.SendFx(W.Span, reliable: false);
    }

    /// <summary>Spawned by the local character or by a mob this machine runs.</summary>
    private static bool IsOurs(CombatEntity caster) =>
        FxSync.IsLocal(caster) || (CoopManager.Connected && CoopManager.Session.PlayerCount >= 2 && MobSync.TryGetOwnedNetId(caster, out _));

    private const float MirrorRange = 120f;

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(ProjectileHitDetection), nameof(ProjectileHitDetection.InitProjectile))]
        private static void Projectile(ProjectileHitDetection __instance, CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2)
        {
            try { if (IsOurs(__0) && !BossSync.ExecutedByAll(__0, __2)) Track(__instance.gameObject, Src.Projectile, __1, __2, 0f); } // a mob's: everyone fires it
            catch (Exception e) { Plugin.L.LogWarning("Mirror projectile: " + e.Message); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CombatManager), nameof(CombatManager.EXECUTE_GROUND_ABILITY))]
        private static void Ground(GameObject __result, CombatEntity __0, RPGAbility __1, RPGAbility.RPGAbilityRankData __2)
        {
            try { if (IsOurs(__0) && !BossSync.ExecutedByAll(__0, __2)) Track(__result, Src.Ground, __1, __2, __2 != null ? __2.groundVisualEffectDuration : 0f); }
            catch (Exception e) { Plugin.L.LogWarning("Mirror ground: " + e.Message); }
        }
    }

    // ---------------- everyone else ----------------

    private sealed class Mirror
    {
        public GameObject Go;
        public Vector3 FromPos, ToPos; public Quaternion FromRot, ToRot; public float MoveStart;
        public float EndAt = -1f; // ended: finishing the last stretch, then fades
    }

    private static readonly Dictionary<(byte, ushort), Mirror> Mirrors = new();

    /// <summary>Called from FxSync replay (already delayed like the puppet's motion).</summary>
    public static void Play(byte owner, byte kind, ref NetReader r)
    {
        switch (kind)
        {
            case KindSpawn: Spawn(owner, ref r); break;
            case KindMove:
            {
                int n = r.U16();
                for (int i = 0; i < n; i++)
                {
                    ushort id = r.U16();
                    var p = ReadPos(ref r); var q = ReadRot(ref r);
                    if (!Mirrors.TryGetValue((owner, id), out var m) || m.Go == null || m.EndAt >= 0f) continue; // a late step after the end
                    var tr = m.Go.transform;
                    m.FromPos = tr.position; m.FromRot = tr.rotation; m.ToPos = p; m.ToRot = q; m.MoveStart = Time.unscaledTime;
                }
                break;
            }
            case KindEnd:
            {
                ushort id = r.U16();
                if (!Mirrors.TryGetValue((owner, id), out var m)) break;
                if (r.Remaining >= 12 && m.Go != null)
                {
                    // the last stretch to where it ended (a hit happens between two position sends), then it fades
                    m.FromPos = m.Go.transform.position; m.FromRot = m.Go.transform.rotation;
                    m.ToPos = ReadPos(ref r); m.ToRot = m.FromRot; m.MoveStart = Time.unscaledTime;
                    m.EndAt = Time.unscaledTime + MoveInterval;
                    T($"got end #{id} from {m.FromPos} to {m.ToPos}");
                }
                else { Fade(m.Go); Mirrors.Remove((owner, id)); }
                break;
            }
        }
    }

    private static Vector3 ReadPos(ref NetReader r) => new(r.F32(), r.F32(), r.F32());
    private static Quaternion ReadRot(ref NetReader r) => new(r.F32(), r.F32(), r.F32(), r.F32());

    private static void Spawn(byte owner, ref NetReader r)
    {
        ushort id = r.U16();
        var src = (Src)r.U8();
        int abilityId = r.I32(), rankIndex = r.I32();
        var pos = ReadPos(ref r); var rot = ReadRot(ref r);
        var scale = ReadPos(ref r);
        float lifetime = r.F32();

        var me = GameInfo.Player;
        if (me == null || Vector3.Distance(me.transform.position, pos) > MirrorRange) return; // elsewhere: nothing to see
        var abilities = GameDatabase.Instance.GetAbilities();
        if (!abilities.ContainsKey(abilityId)) { FxSync.Missing++; return; }
        var ranks = abilities[abilityId].ranks;
        if (ranks == null || rankIndex < 0 || rankIndex >= ranks.Count) { FxSync.Missing++; return; }
        var rank = ranks[rankIndex];
        var prefab = src == Src.Projectile ? rank.projectileEffect : rank.groundVisualEffect;
        if (prefab == null) { FxSync.Missing++; return; }

        var go = Object.Instantiate(prefab, pos, rot);
        go.name = $"AfCoop_Mirror_{owner}_{id}_{prefab.name}";
        go.transform.localScale = scale;
        StripLogic(go);
        // Safety net in case the end message is lost with a disconnect.
        Object.Destroy(go, lifetime > 0f ? lifetime + 1f : 30f);
        T($"got spawn #{id} '{prefab.name}' at {pos}");
        Mirrors[(owner, id)] = new Mirror { Go = go, FromPos = pos, ToPos = pos, FromRot = rot, ToRot = rot, MoveStart = Time.unscaledTime };
        FxSync.Played++;
        Prof.Count("mirror.recv.spawn");
    }

    /// <summary>Keep looks, drop everything that could touch the world: colliders, physics, hit/damage scripts.</summary>
    private static void StripLogic(GameObject go)
    {
        foreach (var c in go.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            if (c.TryCast<Collider>() != null || c.TryCast<Rigidbody>() != null) { Object.Destroy(c); continue; }
            var ns = c.GetIl2CppType().Namespace ?? "";
            if (ns.StartsWith("BLINK.RPGBuilder.LogicMono") || ns.StartsWith("BLINK.RPGBuilder.Combat")) Object.Destroy(c);
        }
    }

    /// <summary>Stop emitting and let particles already in the air finish, instead of popping out.</summary>
    private static void Fade(GameObject go)
    {
        if (go == null) return;
        foreach (var ps in go.GetComponentsInChildren<ParticleSystem>()) ps.Stop(true, ParticleSystemStopBehavior.StopEmitting);
        foreach (var rd in go.GetComponentsInChildren<MeshRenderer>()) rd.enabled = false;
        Object.Destroy(go, 1.5f);
    }

    public static void LateUpdate()
    {
        if (Mirrors.Count == 0) return;
        float now = Time.unscaledTime;
        List<(byte, ushort)> dead = null;
        foreach (var kv in Mirrors)
        {
            var m = kv.Value;
            if (m.Go == null) { (dead ??= new()).Add(kv.Key); continue; }
            if (m.EndAt >= 0f && now >= m.EndAt)
            {
                // At its end the owner's object is gone at once (a hit): so is the copy, its glow and trail with it.
                Object.Destroy(m.Go);
                (dead ??= new()).Add(kv.Key);
                continue;
            }
            float k = Mathf.Clamp01((now - m.MoveStart) / MoveInterval);
            var tr = m.Go.transform;
            tr.position = Vector3.Lerp(m.FromPos, m.ToPos, k);
            tr.rotation = Quaternion.Slerp(m.FromRot, m.ToRot, k);
        }
        if (dead != null) foreach (var key in dead) Mirrors.Remove(key);
    }

    public static void Clear()
    {
        foreach (var m in Mirrors.Values) if (m.Go != null) Object.Destroy(m.Go);
        Mirrors.Clear();
        Live.Clear();
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));
}
