using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Things one player does with another, from the portrait's right-click menu:
///  - Follow: this character runs after that player by itself (the game's own movement, as if "forward" were held,
///    turning towards them) until a movement key is pressed or they are out of reach.
///  - Ride (also in the F menu): on a player in an animal form (a druid's), this character climbs on its back the way
///    the game seats a rider on its dragon (no own movement, no gravity, no knockback, the rider pose) and is carried
///    wherever the animal runs. Jump (space), Get off, the form ending or the animal going away gets the rider off. Every
///    machine shows the rider on that animal's back as it sees the animal (no lag between the two).
/// </summary>
internal static class PlayerActions
{
    public const byte Kind = 34;
    private const byte OpMount = 1, OpDismount = 2, OpSeat = 3;
    private static readonly NetWriter W = new();

    // ---------------- the menu ----------------

    private static GameObject _followButton, _rideButton;
    private static byte _menuPlayer;

    /// <summary>"Follow" (any player here) and "Ride" (a player in an animal form) on that player's menu, or hidden (null).</summary>
    public static void MenuButtons(BLINK.RPGBuilder.UI.TargetContextMenu menu, byte? player)
    {
        if (_followButton == null && menu.inviteButton != null)
        {
            var template = menu.inviteButton.GetComponent<UnityEngine.UI.Button>();
            var parent = menu.inviteButton.transform.parent;
            _followButton = UiKit.CloneButton(template, parent, "Follow", () => { Follow(_menuPlayer); menu.Hide(); }).gameObject;
            _rideButton = UiKit.CloneButton(template, parent, "Ride", () => { Ride(_menuPlayer); menu.Hide(); }).gameObject;
        }
        if (player is { } p) _menuPlayer = p;
        bool here = player is { } q && PlayerShells.Get(q) != null;
        if (_followButton != null) { _followButton.SetActive(here); if (here) _followButton.GetComponentInChildren<TMPro.TMP_Text>(true).text = _following == _menuPlayer && _followOn ? "Stop following" : "Follow"; }
        bool ride = here && (CanRide(_menuPlayer) || _riding == _menuPlayer && _rideOn);
        if (_rideButton != null) { _rideButton.SetActive(ride); if (ride) _rideButton.GetComponentInChildren<TMPro.TMP_Text>(true).text = _riding == _menuPlayer && _rideOn ? "Get off" : "Ride"; }
    }

    // ---------------- follow ----------------

    private static bool _followOn;
    private static byte _following;
    private const float FollowGap = 3f, FollowReach = 60f;
    private static float _nextPath;
    private static Vector3 _corner;

    private static void Follow(byte player)
    {
        if (_followOn && _following == player) { StopFollow(null); return; }
        if (PlayerShells.Get(player) == null) return;
        if (_rideOn) GetOff();
        _following = player; _followOn = true;
        Hud.Notify($"Following {Groups.Name(player)}");
    }

    private static void StopFollow(string why)
    {
        if (!_followOn) return;
        _followOn = false;
        Hud.Notify(why ?? $"You stop following {Groups.Name(_following)}");
    }

    /// <summary>The motor's input this frame: "forward" towards the one followed, while no key is pressed.</summary>
    private static void FollowInput(ref Vector3 input)
    {
        var me = GameInfo.Player;
        var shell = PlayerShells.Get(_following);
        if (me == null || me.Dead || shell == null || !CoopManager.Connected) { StopFollow($"You stop following {Groups.Name(_following)}"); return; }
        if (input.sqrMagnitude > 0.01f) { StopFollow(null); return; } // the player moves by themselves
        var from = me.transform.position;
        var goal = shell.transform.position;
        var flat = goal - from; flat.y = 0f;
        float d = flat.magnitude;
        if (d > FollowReach) { StopFollow($"{Groups.Name(_following)} is too far away"); return; }
        if (d <= FollowGap) return;
        // round trees and walls: the next corner of the navmesh path to them (as the game's AI walks), else straight
        var next = goal;
        if (Time.unscaledTime >= _nextPath)
        {
            _nextPath = Time.unscaledTime + 0.25f;
            _corner = goal;
            try
            {
                var path = new UnityEngine.AI.NavMeshPath();
                if (UnityEngine.AI.NavMesh.CalculatePath(from, goal, UnityEngine.AI.NavMesh.AllAreas, path) && path.corners.Length > 1)
                {
                    var corners = path.corners;
                    for (int i = 1; i < corners.Length; i++) { var c = corners[i] - from; c.y = 0f; if (c.magnitude > 0.6f) { _corner = corners[i]; break; } }
                }
            }
            catch { }
        }
        next = _corner;
        var to = next - from; to.y = 0f;
        if (to.sqrMagnitude < 0.01f) to = flat;
        var t = me.transform;
        t.rotation = Quaternion.RotateTowards(t.rotation, Quaternion.LookRotation(to.normalized), 540f * Time.deltaTime);
        input = Vector3.forward;
    }

    // ---------------- ride ----------------
    //
    // As the game seats a rider: on its dragon, the player itself under the dragon's rider bone; on a mount, the rider on
    // the mount's RiderReParentName bone at RiderPosition/RiderRotation. An animal form has no seat of its own: the seat
    // of the game's mount with that same model, else its spine. Everyone hangs the rider (the player, or their puppet)
    // under that bone of the animal as they see it, so the two move as one; nothing else is sent.

    private static bool _rideOn;
    private static float _walkUntil; // dev: walking ahead until then
    private static byte _riding;
    private static bool _oldCanMove, _oldFlying, _oldMotionImmune, _oldRootMotion;
    private static Transform _oldParent;
    private static bool _wasPersistent;
    private static readonly List<Behaviour> Paused = new();
    private static readonly List<(Rigidbody body, bool kinematic, bool gravity)> Bodies = new();

    /// <summary>As the game pauses its rider for a flight: its movement components off, its body kinematic.</summary>
    private static void PauseRider(GameObject go)
    {
        Paused.Clear(); Bodies.Clear();
        void Off(Behaviour b) { if (b != null && b.enabled) { b.enabled = false; Paused.Add(b); } }
        Off(go.GetComponent<JohnStairs.RCC.Character.Motor.RPGMotor>());
        Off(go.GetComponent<JohnStairs.RCC.Character.Motor.RPGController>());
        Off(go.GetComponent<BLINK.Controller.RPGBThirdPersonController>());
        Off(go.GetComponent<UnityEngine.AI.NavMeshAgent>());
        var cc = go.GetComponent<CharacterController>();
        if (cc != null && cc.enabled) { cc.enabled = false; Paused.Add(null); _cc = cc; } // the movement capsule only: still hit by mobs
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>())
        {
            Bodies.Add((rb, rb.isKinematic, rb.useGravity));
            rb.isKinematic = true; rb.useGravity = false;
        }
    }

    private static CharacterController _cc;

    private static void ResumeRider()
    {
        foreach (var b in Paused) if (b != null) b.enabled = true;
        if (_cc != null) { _cc.enabled = true; _cc = null; }
        foreach (var (rb, k, g) in Bodies) if (rb != null) { rb.isKinematic = k; rb.useGravity = g; }
        Paused.Clear(); Bodies.Clear();
    }
    private static string _pose = "";
    private static readonly Dictionary<byte, byte> Rides = new();      // others: rider -> the player carrying them
    private static readonly Dictionary<byte, Transform> SeatedOn = new(); // others: rider -> the bone their puppet hangs on

    /// <summary>A puppet hanging on an animal: its stream does not move it.</summary>
    public static bool Seated(byte player) => SeatedOn.TryGetValue(player, out var t) && t != null;

    /// <summary>The animal model of that player as seen here (their form's model), or null.</summary>
    private static GameObject AnimalOf(byte player)
    {
        var s = CoopManager.Session;
        if (s == null) return null;
        if (player == s.LocalId)
        {
            var me = GameInfo.Player;
            try { return me != null && me.IsShapeshifted() && me.ShapeshiftedEffect != null && Rideable(me.ShapeshiftedEffect.ID) ? me.ShapeshiftingGameobject : null; } catch { return null; }
        }
        return Puppets.TryGetVisual(player, out var go, out _) && FormVisual.IsIn(go, out var effect) && Rideable(effect) ? FormVisual.ModelOf(go) : null;
    }

    /// <summary>The forms one can ride: the bear and the cat (the others are no mount: a moonkin, a tree).</summary>
    private static bool Rideable(int effect)
    {
        try
        {
            var effects = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects();
            var name = effects.ContainsKey(effect) ? effects[effect].entryName ?? "" : "";
            return name.IndexOf("Bear", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Cat", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    private static bool CanRide(byte player) => AnimalOf(player) != null && !Rides.ContainsValue(player) && !Rides.ContainsKey(player);

    /// <summary>The seat on that animal: bone, local position and rotation under it.</summary>
    private static bool Seat(GameObject animal, out Transform bone, out Vector3 local, out Quaternion rot)
    {
        bone = null; local = Vector3.zero; rot = Quaternion.identity;
        if (animal == null) return false;
        // the spine bone in the middle of its back; the rider's hips go on it, as high over it as its back is (the bone
        // runs inside the body)
        var spines = new List<Transform>();
        foreach (var t in animal.GetComponentsInChildren<Transform>())
            if (t.name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0) spines.Add(t);
        var mid = animal.transform.position;
        if (spines.Count > 0) { mid = Vector3.zero; foreach (var t in spines) mid += t.position; mid /= spines.Count; }
        Transform spine = null;
        foreach (var t in spines) if (spine == null || (t.position - mid).sqrMagnitude < (spine.position - mid).sqrMagnitude) spine = t;
        bone = spine ?? animal.transform;
        local = bone.InverseTransformPoint(bone.position + Vector3.up * BackOverSpine(animal) * animal.transform.lossyScale.y);
        rot = Quaternion.Inverse(bone.rotation) * Quaternion.Euler(0f, animal.transform.eulerAngles.y, 0f);
        return true;
    }

    /// <summary>How high the back is over the middle spine bone (at the model's own scale): the bear's, the cat's.</summary>
    private static float BackOverSpine(GameObject animal)
    {
        foreach (var r in animal.GetComponentsInChildren<Renderer>(true)) // the model is named as it is shown (a puppet's form: by the mod)
            if (r != null && r.name.IndexOf("Bear", StringComparison.OrdinalIgnoreCase) >= 0) return BearBack;
        return CatBack;
    }

    internal static float BearBack = 0.25f, CatBack = 0.30f; // dev: pa back <bear> <cat>

    private static string RiderPose()
    {
        if (_pose.Length > 0) return _pose;
        try
        {
            // the game's mounts: the rider's pose astride
            foreach (var kv in BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects())
            {
                var e = kv.Value;
                if (e == null || (int)e.effectType != 21 || e.ranks == null) continue;
                foreach (var r in e.ranks) if (r != null && r.MountPrefab != null && !string.IsNullOrEmpty(r.MountAnimationParameter)) return _pose = r.MountAnimationParameter;
            }
            // else the dragon rider's
            foreach (var kv in BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetNPCs())
            {
                var n = kv.Value?.GetFlightNetwork();
                if (n != null && !string.IsNullOrEmpty(n.RiderAnimationParameter)) return _pose = n.RiderAnimationParameter;
            }
        }
        catch { }
        return _pose;
    }

    private static bool HasBool(Animator a, string name)
    {
        if (a == null || string.IsNullOrEmpty(name)) return false;
        foreach (var p in a.parameters) if (p.type == AnimatorControllerParameterType.Bool && p.name == name) return true;
        return false;
    }

    private static void Hang(Transform rider, Transform bone, Vector3 local, Quaternion rot)
    {
        var own = Seats.TryGetValue(rider.Pointer, out var was) ? was.Scale : rider.localScale; // (hung again: its own size, not the seat's)
        var size = rider.parent != null && was == null ? Vector3.Scale(own, rider.parent.lossyScale) : own;
        rider.SetParent(bone, false);
        rider.localPosition = local;
        rider.localRotation = rot;
        var b = bone.lossyScale; // the bone's scale is the animal's, not the rider's: kept out
        rider.localScale = new Vector3(size.x / b.x, size.y / b.y, size.z / b.z);
        var st = new SeatState { Rider = rider, Bone = bone, Local = local, Rot = rot, Since = Time.time, Scale = own };
        // the riding pose must not walk or turn the rider (a puppet's animator moves its root otherwise)
        st.Anim = rider.GetComponentInChildren<Animator>();
        if (st.Anim != null) { st.RootMotion = was?.RootMotion ?? st.Anim.applyRootMotion; st.Anim.applyRootMotion = false; }
        if (was != null) st.Shown.AddRange(was.Shown);
        else foreach (var r in rider.GetComponentsInChildren<Renderer>()) if (r != null && r.enabled) st.Shown.Add(r);
        Seats[rider.Pointer] = st;
    }

    /// <summary>
    /// The local player in a form keeps what is under it hidden (it took the rider in, sitting on its model): the
    /// rider's renderers out of its lists again, so it stops hiding them.
    /// </summary>
    private static void Unwatch(List<Renderer> rider)
    {
        try
        {
            if (GameInfo.Player?.TryCast<PlayerCombatEntity>() is not { } me || rider.Count == 0) return;
            var mine = new HashSet<IntPtr>();
            foreach (var r in rider) if (r != null) mine.Add(r.Pointer);
            foreach (var list in new[] { me._shapeshiftHiddenRenderers, me._shapeshiftExtraHiddenRenderers })
                if (list != null) for (int i = list.Count - 1; i >= 0; i--) if (list[i] != null && mine.Contains(list[i].Pointer)) list.RemoveAt(i);
            var watched = me._shapeshiftWatchedRenderers;
            if (watched != null)
            {
                var keep = new List<Renderer>();
                foreach (var r in watched) if (r == null || !mine.Contains(r.Pointer)) keep.Add(r);
                if (keep.Count != watched.Length) me._shapeshiftWatchedRenderers = keep.ToArray();
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Ride unwatch: " + e.Message); }
    }

    /// <summary>Off its seat: under that parent again, its own size as before.</summary>
    private static void Unhang(Transform rider, Transform parent)
    {
        var own = Seats.TryGetValue(rider.Pointer, out var st) ? st.Scale : rider.localScale;
        Seats.Remove(rider.Pointer);
        rider.SetParent(parent, true);
        rider.localScale = own;
        if (st?.Anim != null) st.Anim.applyRootMotion = st.RootMotion;
        if (st != null)
        {
            Unwatch(new List<Renderer>(rider.GetComponentsInChildren<Renderer>(true)));
            foreach (var r in st.Shown) if (r != null && !r.enabled) r.enabled = true;
        }
    }

    // riders and the point on the animal's back their seat is (the bone, a point under it)
    private sealed class SeatState { public Transform Rider, Bone; public Vector3 Local, Scale = Vector3.one; public Quaternion Rot = Quaternion.identity; public Animator Anim; public bool RootMotion; public float Since;
        public readonly List<Renderer> Shown = new(); } // what of the rider was shown when it sat down (the animal's owner hides what is under its form)
    private static readonly Dictionary<IntPtr, SeatState> Seats = new();

    /// <summary>
    /// A rider stays as it sat down: shown (the animal's owner in its form hides what is under its model) and not
    /// moved by its own animation (a puppet's animator would walk its root off the seat).
    /// </summary>
    private static void Settle()
    {
        List<IntPtr> gone = null;
        foreach (var kv in Seats)
        {
            var st = kv.Value;
            if (st.Rider == null || st.Bone == null || st.Rider.parent != st.Bone) { (gone ??= new()).Add(kv.Key); continue; }
            foreach (var r in st.Shown) if (r != null && !r.enabled) r.enabled = true; // shown as it was, under someone's form
            var anim = st.Anim != null ? st.Anim : st.Rider.GetComponentInChildren<Animator>();
            if (anim != null && anim.applyRootMotion) anim.applyRootMotion = false;
        }
        if (gone != null) foreach (var k in gone) Seats.Remove(k);
    }

    private static void Ride(byte player)
    {
        if (_rideOn && _riding == player) { GetOff(); return; }
        var me = GameInfo.Player;
        if (me == null || me.Dead || !Seat(AnimalOf(player), out var bone, out var local, out var rot)) { Hud.Notify($"{Groups.Name(player)} is not in bear or cat form"); return; }
        if (_rideOn) GetOff();
        StopFollow(null);
        var ce = me.controllerEssentials;
        if (ce != null) { _oldCanMove = ce.canMove; _oldFlying = ce.isFlying; ce.canMove = false; ce.isFlying = true; }
        _oldMotionImmune = me.IsMotionImmune(); me.SetMotionImmune(true);
        var anim = me.ThisAnimator;
        if (anim != null)
        {
            _oldRootMotion = anim.applyRootMotion; anim.applyRootMotion = false;
            if (HasBool(anim, RiderPose())) anim.SetBool(RiderPose(), true);
        }
        _oldParent = me.transform.parent;
        _wasPersistent = me.gameObject.scene.name == "DontDestroyOnLoad";
        PauseRider(me.gameObject);
        Hang(me.transform, bone, local, rot);
        _riding = player; _rideOn = true;
        Send(OpMount, player);
        SendSeat(); // the others put my puppet just where I sit
        Hud.Notify($"You ride {Groups.Name(player)}");
    }

    private static void GetOff(string why = null)
    {
        if (!_rideOn) return;
        _rideOn = false;
        var me = GameInfo.Player;
        if (me != null)
        {
            var t = me.transform;
            var at = t.position;
            Unhang(t, _oldParent);
            if (_wasPersistent && t.parent == null) UnityEngine.Object.DontDestroyOnLoad(me.gameObject); // as the dragon ride gives it back
            t.rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
            t.position = at + t.right * 1.2f; // down beside the animal
            ResumeRider();
            var ce = me.controllerEssentials;
            if (ce != null) { ce.canMove = _oldCanMove; ce.isFlying = _oldFlying; }
            me.SetMotionImmune(_oldMotionImmune);
            var anim = me.ThisAnimator;
            if (anim != null) { anim.applyRootMotion = _oldRootMotion; if (HasBool(anim, RiderPose())) anim.SetBool(RiderPose(), false); }
        }
        Send(OpDismount, _riding);
        Hud.Notify(why ?? $"You get off {Groups.Name(_riding)}");
    }

    private static readonly Dictionary<byte, List<Collider>> ShellSolids = new();

    /// <summary>A seated rider's shell has no solid body: on the animal's back it would push the animal (the druid here).</summary>
    private static void ShellSolid(byte rider, bool solid)
    {
        if (!solid)
        {
            var shell = PlayerShells.Get(rider);
            if (shell == null || ShellSolids.ContainsKey(rider)) return;
            var off = new List<Collider>();
            foreach (var c in shell.GetComponentsInChildren<Collider>()) if (c.enabled && !c.isTrigger) { c.enabled = false; off.Add(c); }
            ShellSolids[rider] = off;
        }
        else if (ShellSolids.Remove(rider, out var list))
            foreach (var c in list) if (c != null) c.enabled = true;
    }

    /// <summary>A rider's puppet off its animal here, standing where it was.</summary>
    private static void Unseat(byte rider)
    {
        ShellSolid(rider, true);
        if (!SeatedOn.Remove(rider)) return;
        if (!Puppets.TryGetVisual(rider, out var go, out _) || go == null) return;
        var t = go.transform;
        Unhang(t, null);
        t.rotation = Quaternion.Euler(0f, t.eulerAngles.y, 0f);
    }

    /// <summary>
    /// Something is about to be destroyed (an animal form's model, a puppet, the scene): whoever hangs under it comes
    /// off first, so nobody is destroyed with it.
    /// </summary>
    public static void BeforeDestroy(GameObject g)
    {
        if (g == null) return;
        try
        {
            var me = GameInfo.Player;
            if (_rideOn && me != null && me.transform.IsChildOf(g.transform)) GetOff($"{Groups.Name(_riding)} is no longer in an animal form");
            var off = new List<byte>();
            foreach (var kv in SeatedOn) if (kv.Value != null && kv.Value.IsChildOf(g.transform)) off.Add(kv.Key);
            foreach (var id in off) Unseat(id);
        }
        catch (Exception e) { Plugin.L.LogWarning("Ride: " + e.Message); }
    }

    private static void AllOff()
    {
        if (_rideOn) GetOff();
        foreach (var id in new List<byte>(SeatedOn.Keys)) Unseat(id);
    }

    private static void Send(byte op, byte player)
    {
        if (!CoopManager.Connected) return;
        W.Reset(); W.U8(Kind); W.U8(op); W.U8(player);
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>My seat as it is: the animal's bone I sit on and where under it (the same model everywhere, so the same seat).</summary>
    private static void SendSeat()
    {
        var me = GameInfo.Player;
        if (!_rideOn || me == null || me.transform.parent == null || !CoopManager.Connected) return;
        var t = me.transform;
        W.Reset(); W.U8(Kind); W.U8(OpSeat); W.U8(_riding); W.Str(t.parent.name);
        W.F32(t.localPosition.x); W.F32(t.localPosition.y); W.F32(t.localPosition.z);
        W.F32(t.localRotation.x); W.F32(t.localRotation.y); W.F32(t.localRotation.z); W.F32(t.localRotation.w);
        CoopManager.Session.SendWorld(W.Span);
        _nextSeat = Time.unscaledTime + 5f;
    }

    private static float _nextSeat;
    // others: the seat their rider sent (bone name, local position and rotation), and whether their puppet sits on it here
    private static readonly Dictionary<byte, (string bone, Vector3 pos, Quaternion rot)> GivenSeats = new();
    private static readonly HashSet<byte> GivenApplied = new();

    private static Transform FindBone(Transform root, string name)
    {
        if (root == null) return null;
        foreach (var t in root.GetComponentsInChildren<Transform>()) if (t.name == name) return t;
        return null;
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8(), player = r.U8();
        if (op == OpSeat)
        {
            string bone = r.Str();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var rot = new Quaternion(r.F32(), r.F32(), r.F32(), r.F32());
            Rides[from] = player;
            if (!GivenSeats.TryGetValue(from, out var old) || old.bone != bone || (old.pos - pos).sqrMagnitude > 1e-6f || Quaternion.Angle(old.rot, rot) > 0.1f)
            { GivenSeats[from] = (bone, pos, rot); GivenApplied.Remove(from); }
            return;
        }
        if (op == OpMount) { Rides[from] = player; if (player == CoopManager.Session?.LocalId) Hud.Notify($"{Groups.Name(from)} rides you"); }
        else if (op == OpDismount) { Rides.Remove(from); GivenSeats.Remove(from); GivenApplied.Remove(from); Unseat(from); }
    }

    public static void Update()
    {
        if (_rideOn)
        {
            var me = GameInfo.Player;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (me == null || me.Dead || !CoopManager.Connected) GetOff();
            else if (kb != null && kb.spaceKey.wasPressedThisFrame && !ChatOpen()) GetOff(); // jump off
            else if (AnimalOf(_riding) == null) GetOff($"{Groups.Name(_riding)} is no longer in an animal form");
        }
        var s = CoopManager.Session;
        if (Rides.Count == 0 && SeatedOn.Count == 0) return;
        var gone = new List<byte>();
        foreach (var kv in Rides) if (s == null || !s.Players.ContainsKey(kv.Key) || !s.Players.ContainsKey(kv.Value)) gone.Add(kv.Key);
        foreach (var id in gone) { Rides.Remove(id); GivenSeats.Remove(id); GivenApplied.Remove(id); Unseat(id); }
        // riders hung on their animals here, as soon as both are here
        if (_rideOn && Time.unscaledTime >= _nextSeat && _nextSeat > 0f) SendSeat(); // for whoever came later
        foreach (var kv in Rides)
        {
            // the rider's own seat, once it has sent it: just there, as on its screen
            if (GivenSeats.TryGetValue(kv.Key, out var given) && !GivenApplied.Contains(kv.Key)
                && Puppets.TryGetVisual(kv.Key, out var gr, out _) && gr != null)
            {
                var gb = FindBone(AnimalOf(kv.Value)?.transform, given.bone);
                if (gb != null)
                {
                    Hang(gr.transform, gb, given.pos, given.rot);
                    SeatedOn[kv.Key] = gr.transform;
                    ShellSolid(kv.Key, false);
                    GivenApplied.Add(kv.Key);
                    continue;
                }
            }
            if (Seated(kv.Key)) continue;
            if (!Puppets.TryGetVisual(kv.Key, out var rider, out _) || rider == null) continue;
            if (!Seat(AnimalOf(kv.Value), out var bone, out var local, out var rot)) continue;
            Hang(rider.transform, bone, local, rot);
            SeatedOn[kv.Key] = rider.transform;
            ShellSolid(kv.Key, false);
        }
    }

    private static bool ChatOpen()
    {
        try { var f = UnityEngine.EventSystems.EventSystem.current?.currentSelectedGameObject; return f != null && f.GetComponent<TMPro.TMP_InputField>() != null; } catch { return false; }
    }

    /// <summary>A rider's shell (targeting, nameplate) where the rider sits.</summary>
    public static void LateUpdate()
    {
        Settle();
        foreach (var kv in SeatedOn)
        {
            var shell = PlayerShells.Get(kv.Key);
            if (shell != null && kv.Value != null) shell.transform.position = kv.Value.position;
        }
    }

    public static void Hook()
    {
        try { GameEvents.add_StartNewGameSceneLoad((Il2CppSystem.Action)(Action)AllOff); } // as the dragon ride: off before the scene goes
        catch (Exception e) { Plugin.L.LogWarning("Ride scene hook: " + e.Message); }
    }

    public static void Install(Harmony h) { h.PatchAll(typeof(Patches)); Hook(); }

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>My character's movement input: following, or a key that ends a ride.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(JohnStairs.RCC.Character.Motor.RPGMotor), nameof(JohnStairs.RCC.Character.Motor.RPGMotor.SetInputDirection))]
        private static void Input(JohnStairs.RCC.Character.Motor.RPGMotor __instance, ref Vector3 __0)
        {
            if (!_followOn && !_rideOn && Time.time >= _walkUntil) return;
            try
            {
                var me = GameInfo.Player;
                if (me == null || __instance.gameObject != me.gameObject && !__instance.transform.IsChildOf(me.transform) && !me.transform.IsChildOf(__instance.transform)) return;
                if (_rideOn) { if (__0.sqrMagnitude > 0.01f) GetOff(); else __0 = Vector3.zero; return; }
                if (Time.time < _walkUntil) { __0 = Vector3.forward; return; } // dev: walk ahead
                FollowInput(ref __0);
            }
            catch (Exception e) { Plugin.L.LogWarning("Follow: " + e.Message); }
        }

        // ---- nobody is destroyed with the animal they ride ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(FormVisual), nameof(FormVisual.Clear))]
        private static void FormCleared(GameObject __0) => BeforeDestroy(FormVisual.ModelOf(__0));

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PlayerCombatEntity), nameof(PlayerCombatEntity.ResetShapeshifting))]
        private static void MyFormEnds(PlayerCombatEntity __instance) { try { BeforeDestroy(__instance.ShapeshiftingGameobject); } catch { } }

        // ---- "Ride" in the interaction menu (F) on a player in an animal form ----

        private static readonly Dictionary<IntPtr, Action> Options = new();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void RideOption(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                Options.Clear();
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player)) return;
                bool off = _rideOn && _riding == player;
                if (!off && !CanRide(player)) return;
                var go = UnityEngine.Object.Instantiate(__instance.TextOptionPrefab, __instance.TextOptionsParent);
                var option = go.GetComponent<NPCInteractionTextOption>();
                option.Initialize(off ? "Get off" : "Ride", __instance);
                option.Type = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.Trade;
                __instance.optionSlots?.Add(option);
                var panel = __instance;
                Options[option.Pointer] = () => { Ride(player); try { panel.Hide(); } catch { } };
            }
            catch (Exception e) { Plugin.L.LogWarning("Ride option: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCInteractionTextOption), nameof(NPCInteractionTextOption.Click))]
        private static bool OptionClick(NPCInteractionTextOption __instance)
        {
            if (!Options.TryGetValue(__instance.Pointer, out var act)) return true;
            try { act(); } catch (Exception e) { Plugin.L.LogWarning("Ride option: " + e.Message); }
            return false;
        }
    }

    public static string Dev(string arg)
    {
        var a = arg.Split(' ');
        if (a[0] == "follow") { Follow(byte.Parse(a[1])); return "following " + a[1]; }
        if (a[0] == "ride") { Ride(byte.Parse(a[1])); return _rideOn ? "riding " + a[1] : "not riding"; }
        if (a[0] == "off") { GetOff(); StopFollow(null); return "off"; }
        if (a[0] == "seat")
        {
            var animal = AnimalOf(byte.Parse(a[1]));
            if (animal == null) return "no animal";
            Seat(animal, out var bone, out var local, out var rot);
            var sb = new System.Text.StringBuilder($"animal '{animal.name}' at {animal.transform.position} scale {animal.transform.lossyScale} bone '{bone?.name}' at {bone?.position} local {local}; renderers:");
            foreach (var r in animal.GetComponentsInChildren<Renderer>()) sb.Append($" {r.name}[{r.enabled}] {r.bounds.min.y:0.0}-{r.bounds.max.y:0.0};");
            sb.Append(" bones:");
            var fwd = animal.transform.forward;
            foreach (var t in animal.GetComponentsInChildren<Transform>())
            {
                var n = t.name.ToLowerInvariant();
                if (n.Contains("spine") || n.Contains("pelvis") || n.Contains("hip") || n.Contains("neck") || n.Contains("root") || n.Contains("head") || n.Contains("tail"))
                    sb.Append($" {t.name}@f{Vector3.Dot(t.position - animal.transform.position, fwd):0.00}/y{t.position.y - animal.transform.position.y:0.00}");
            }
            sb.Append(" spines:");
            foreach (var t in animal.GetComponentsInChildren<Transform>()) if (t.name.IndexOf("spine", StringComparison.OrdinalIgnoreCase) >= 0) sb.Append($" {t.name}@{t.position.y:0.00}");
            return sb.ToString();
        }
        var me = GameInfo.Player;
        if (a[0] == "back") { var ci = System.Globalization.CultureInfo.InvariantCulture; BearBack = float.Parse(a[1], ci); CatBack = float.Parse(a[2], ci); return $"bear {BearBack} cat {CatBack} (get on again)"; }
        if (a[0] == "walk") { _walkUntil = Time.time + float.Parse(a[1], System.Globalization.CultureInfo.InvariantCulture); return "walking"; }
        if (a[0] == "puppets")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var (id, tr) in Puppets.Visible())
            {
                int on = 0, all = 0, en = 0, act = 0; string offAt = "";
                foreach (var r in tr.GetComponentsInChildren<Renderer>(true))
                {
                    all++; if (r.enabled) en++; if (r.gameObject.activeInHierarchy) act++; if (r.enabled && r.gameObject.activeInHierarchy) on++;
                    if (r.enabled && !r.gameObject.activeInHierarchy && offAt.Length == 0) for (var p = r.transform; p != null && p != tr.parent; p = p.parent) if (!p.gameObject.activeSelf) { offAt = p.name; break; }
                }
                sb.Append($"enabled {en} active {act} off-at '{offAt}' ");
                sb.Append($"{id}: '{tr.name}' at {tr.position} scale {tr.lossyScale} local {tr.localScale} parent '{tr.parent?.name}' active {tr.gameObject.activeInHierarchy} renderers {on}/{all}; ");
            }
            return sb.Length > 0 ? sb.ToString() : "no visible puppets";
        }
        if (a[0] == "where")
        {
            var sb = new System.Text.StringBuilder($"pose '{RiderPose()}' me {me?.transform.position} scale {me?.transform.lossyScale.y:0.000} parent '{me?.transform.parent?.name}'; ");
            foreach (var kv in Seats)
            {
                var (rider, bone, local) = (kv.Value.Rider, kv.Value.Bone, kv.Value.Local);
                var an = rider != null ? rider.GetComponentInChildren<Animator>() : null;
                var hips = an != null && an.isHuman ? an.GetBoneTransform(HumanBodyBones.Hips) : null;
                if (an != null && an.isHuman && hips != null)
                {
                    var lu = an.GetBoneTransform(HumanBodyBones.LeftUpperLeg); var ll = an.GetBoneTransform(HumanBodyBones.LeftLowerLeg); var hd = an.GetBoneTransform(HumanBodyBones.Head);
                    sb.Append($"[upperLeg.y {lu?.position.y - hips.position.y:0.00} knee.y {ll?.position.y - hips.position.y:0.00} knee.side {(ll != null ? Vector3.Dot(ll.position - hips.position, rider.right) : 0f):0.00} head.y {hd?.position.y - hips.position.y:0.00} thigh {(lu != null && ll != null ? Vector3.Distance(lu.position, ll.position) : 0f):0.00}] ");
                }
                sb.Append($"local {rider?.localPosition.x:0.000},{rider?.localPosition.y:0.000},{rider?.localPosition.z:0.000} on '{bone?.name}' ");
                sb.Append($"seat '{rider?.name}' anim '{an?.name}' human {an?.isHuman} rider.y {rider?.position.y:0.00} hips.y {hips?.position.y:0.00} seat.y {(bone != null ? bone.TransformPoint(local).y : 0f):0.00}; ");
            }
            foreach (var kv in SeatedOn) { var sh = PlayerShells.Get(kv.Key); sb.Append($"rider {kv.Key} puppet {kv.Value?.position} shell {sh?.transform.position}; "); }
            return sb.ToString();
        }
        return $"follow {_followOn}:{_following} ride {_rideOn}:{_riding} rides [{string.Join(",", Rides)}] me {me?.transform.position} canRide1 {CanRide(1)} canRide0 {CanRide(0)}";
    }
}
