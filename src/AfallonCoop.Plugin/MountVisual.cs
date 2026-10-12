using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// A mount (a Mount effect) shown under a rider that is not the real entity: a bot copy or a player's puppet. The game's
/// CombatEntity.InitMount also changes stats, applies nested effects and (for bots) swaps controllers; this repeats only
/// its visual steps: the rank's mount prefab under the rider (its position and scale), an animator with the rank's
/// controller and avatar, the rider's model on the mount's seat (RiderReParentName, RiderPosition/Rotation). The
/// sitting pose comes with the rider's own streamed animator; the mount's legs are driven here from how fast it moves.
/// Nothing physical is kept on it: the network moves the rider.
/// </summary>
internal static class MountVisual
{
    private sealed class State
    {
        public int Effect, Rank;
        public GameObject Root, Mount;
        public Animator MountAnim;
        public Transform Rider, RiderParent;
        public Vector3 RiderPos, RiderScale; public Quaternion RiderRot;
        public Vector3 LastPos; public float Speed, Yaw, Turn;
    }

    private static readonly Dictionary<IntPtr, State> On = new();

    /// <summary>Put this rider on that mount (effect id, rank); -1 = on foot. <paramref name="rider"/>: the model moved onto the seat.</summary>
    public static void Set(GameObject root, Transform rider, int effectId, int rank)
    {
        if (root == null) return;
        On.TryGetValue(root.Pointer, out var cur);
        if (effectId < 0) { if (cur != null) Clear(root); return; }
        if (cur != null && cur.Effect == effectId && cur.Rank == rank) return;
        if (cur != null) Clear(root);

        var all = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects();
        if (!all.ContainsKey(effectId)) return;
        var ranks = all[effectId].ranks;
        if (ranks == null || rank < 0 || rank >= ranks.Count) return;
        var r = ranks[rank];
        if (r?.MountPrefab == null) return;

        var s = new State { Effect = effectId, Rank = rank, Root = root, LastPos = root.transform.position };
        try
        {
            s.Mount = Object.Instantiate(r.MountPrefab, root.transform);
            s.Mount.name = "AfCoop_Mount";
            s.Mount.transform.localPosition = r.MountPosition;
            s.Mount.transform.localEulerAngles = Vector3.zero;
            if (r.MountScale != Vector3.zero) s.Mount.transform.localScale = r.MountScale;
            // a picture of a mount: no physics, no navigation, no scripts of its own
            foreach (var c in s.Mount.GetComponentsInChildren<Collider>(true)) Object.Destroy(c);
            foreach (var b in s.Mount.GetComponentsInChildren<Rigidbody>(true)) Object.Destroy(b);
            foreach (var a in s.Mount.GetComponentsInChildren<UnityEngine.AI.NavMeshAgent>(true)) Object.Destroy(a);
            s.MountAnim = s.Mount.GetComponent<Animator>() ?? s.Mount.AddComponent<Animator>();
            if (r.MountAnimatorController != null) s.MountAnim.runtimeAnimatorController = r.MountAnimatorController;
            if (r.MountAvatar != null) s.MountAnim.avatar = r.MountAvatar;
            s.MountAnim.applyRootMotion = false;
            s.MountAnim.cullingMode = AnimatorCullingMode.AlwaysAnimate;

            if (rider != null)
            {
                var seat = !string.IsNullOrEmpty(r.RiderReParentName) ? RPGBuilderUtilities.GetChildByName(s.Mount, r.RiderReParentName) : null;
                s.Rider = rider; s.RiderParent = rider.parent;
                s.RiderPos = rider.localPosition; s.RiderRot = rider.localRotation; s.RiderScale = rider.localScale;
                rider.SetParent(seat != null ? seat.transform : s.Mount.transform, true);
                rider.localPosition = r.RiderPosition;
                rider.localEulerAngles = r.RiderRotation;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Mount visual: " + e.Message); }
        On[root.Pointer] = s;
    }

    /// <summary>Off the mount: the rider back where it was, the mount gone.</summary>
    public static void Clear(GameObject root)
    {
        if (root == null || !On.TryGetValue(root.Pointer, out var s)) return;
        On.Remove(root.Pointer);
        Restore(s);
    }

    private static void Restore(State s)
    {
        try
        {
            if (s.Rider != null)
            {
                s.Rider.SetParent(s.RiderParent, false);
                s.Rider.localPosition = s.RiderPos; s.Rider.localRotation = s.RiderRot; s.Rider.localScale = s.RiderScale;
            }
        }
        catch { }
        if (s.Mount != null) Object.Destroy(s.Mount);
    }

    public static bool IsOn(GameObject root, out int effect)
    {
        effect = -1;
        if (root == null || !On.TryGetValue(root.Pointer, out var s)) return false;
        effect = s.Effect;
        return true;
    }

    /// <summary>For the dev bridge: the mount's animator (controller, avatar, parameters and their values).</summary>
    public static string Describe(GameObject root)
    {
        if (root == null || !On.TryGetValue(root.Pointer, out var s)) return "-";
        var a = s.MountAnim;
        if (a == null) return "no animator";
        var ps = new List<string>();
        foreach (var p in a.parameters)
            ps.Add(p.type == AnimatorControllerParameterType.Float ? $"{p.name}={a.GetFloat(p.nameHash):0.##}"
                 : p.type == AnimatorControllerParameterType.Bool ? $"{p.name}={a.GetBool(p.nameHash)}" : $"{p.name}:{p.type}");
        var st = a.isInitialized ? a.GetCurrentAnimatorStateInfo(0) : default;
        return $"ctrl='{a.runtimeAnimatorController?.name}' avatar='{a.avatar?.name}' enabled={a.enabled} init={a.isInitialized} state={st.shortNameHash} speed={s.Speed:0.#} [{string.Join(", ", ps)}]";
    }

    /// <summary>Each frame: the mount's legs follow how fast its rider moves (as the game's AnimateMount does for bots).</summary>
    public static void Tick()
    {
        List<IntPtr> gone = null;
        float dt = Mathf.Max(Time.deltaTime, 0.0001f);
        foreach (var kv in On)
        {
            var s = kv.Value;
            if (s.Root == null || s.Mount == null) { (gone ??= new()).Add(kv.Key); continue; }
            var pos = s.Root.transform.position;
            var delta = pos - s.LastPos; delta.y = 0f;
            s.LastPos = pos;
            float speed = delta.magnitude / dt;
            s.Speed = Mathf.Lerp(s.Speed, speed, Mathf.Clamp01(dt * 8f)); // network steps are uneven: smooth
            var a = s.MountAnim;
            if (a == null || a.runtimeAnimatorController == null) continue;
            // as the game's AdventurerGroundMount.AnimateMount: local direction (unit), speed (m/s), turning (-1..1)
            var dir = s.Speed > 0.05f && delta.sqrMagnitude > 1e-8f ? s.Root.transform.InverseTransformDirection(delta.normalized) : Vector3.zero;
            float yaw = s.Root.transform.eulerAngles.y;
            float turn = Mathf.Clamp(Mathf.DeltaAngle(s.Yaw, yaw) / dt / 180f, -1f, 1f);
            s.Yaw = yaw;
            s.Turn = Mathf.Lerp(s.Turn, turn, Mathf.Clamp01(dt * 4f)); // the rider turns in network steps: smooth, or the mount twitches
            foreach (var p in a.parameters)
            {
                switch (p.name)
                {
                    case "Local Movement X": a.SetFloat(p.nameHash, dir.x); break;
                    case "Local Movement Z": a.SetFloat(p.nameHash, dir.z); break;
                    case "Movement Speed": a.SetFloat(p.nameHash, s.Speed); break;
                    case "Turning Direction": a.SetFloat(p.nameHash, s.Turn); break;
                    case "Grounded": a.SetBool(p.nameHash, true); break;
                    case "MoveSpeedModifier": a.SetFloat(p.nameHash, 1f); break;
                }
            }
        }
        if (gone != null) foreach (var k in gone) { if (On.TryGetValue(k, out var s)) Restore(s); On.Remove(k); }
    }
}
