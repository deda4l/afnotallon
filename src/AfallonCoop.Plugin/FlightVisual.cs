using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Travel;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Flights on the dragon (the game's flight paths): the one flying tells the others its route's network and each state
/// its dragon plays (takeoff, flying, landing); the others show the same dragon under that player's character (the
/// network's own prefab, controller, avatar and scale), its rider bone on the character, heading where it flies, playing
/// those states. The character itself moves with its own stream, seated (the rider animation is the player's).
/// </summary>
internal static class FlightVisual
{
    public const byte Kind = 33;
    private const byte OpStart = 1, OpState = 2, OpStop = 3;
    private static readonly NetWriter W = new();

    // ---------------- mine ----------------

    private static FlightPathRide _ride;
    private static string _network = "", _state = "";
    private static float _nextResend;

    private static bool MyRide(FlightPathRide r)
    {
        var me = GameInfo.Player;
        return r != null && me != null && r.player != null && r.player.Pointer == me.Pointer;
    }

    private static void Send(byte op, string text)
    {
        if (!CoopManager.Connected) return;
        W.Reset(); W.U8(Kind); W.U8(op); W.Str(text ?? "");
        CoopManager.Session.SendWorld(W.Span);
    }

    // ---------------- theirs ----------------

    private sealed class Dragon
    {
        public GameObject Go; public Animator Anim; public Transform Seat; public Vector3 Offset; public string State = "";
    }

    private static readonly Dictionary<byte, Dragon> Dragons = new();

    /// <summary>The route network by its ID: the flight masters' own (a database asset reached through their NPC records).</summary>
    private static FlightPathNetwork FindNetwork(string id)
    {
        foreach (var n in Resources.FindObjectsOfTypeAll<FlightPathNetwork>()) if (n != null && n.NetworkID == id) return n;
        foreach (var kv in BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetNPCs())
        {
            FlightPathNetwork n = null;
            try { n = kv.Value?.GetFlightNetwork(); } catch { }
            if (n != null && n.NetworkID == id) return n;
        }
        return null;
    }

    private static Transform FindBone(Transform root, string name)
    {
        if (root == null || string.IsNullOrEmpty(name)) return null;
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++) { var t = FindBone(root.GetChild(i), name); if (t != null) return t; }
        return null;
    }

    private static void Show(byte player, string networkId)
    {
        if (Dragons.ContainsKey(player)) return;
        var net = FindNetwork(networkId);
        if (net?.DragonPrefab == null) { Plugin.L.LogWarning($"Flight: no route network '{networkId}' here"); return; }
        var go = Object.Instantiate(net.DragonPrefab);
        go.name = "AfCoop_Dragon_" + player;
        go.transform.localScale = net.DragonScale;
        foreach (var c in go.GetComponentsInChildren<Collider>(true)) c.enabled = false; // a picture: nothing to bump into
        foreach (var rb in go.GetComponentsInChildren<Rigidbody>(true)) rb.isKinematic = true;
        var anim = go.GetComponent<Animator>() ?? go.AddComponent<Animator>(); // as the game sets its dragon up
        if (anim != null)
        {
            if (net.DragonController != null) anim.runtimeAnimatorController = net.DragonController;
            if (net.DragonAvatar != null) anim.avatar = net.DragonAvatar;
            anim.applyRootMotion = false;
            anim.enabled = true;
            anim.cullingMode = AnimatorCullingMode.AlwaysAnimate; // seen from afar too
            anim.Rebind();
        }
        var d = new Dragon { Go = go, Anim = anim, Seat = FindBone(go.transform, net.RiderBoneName) ?? go.transform, Offset = net.RiderOffset };
        Dragons[player] = d;
        Play(d, net.FlyingState); // until the rider's own state comes
        Plugin.L.LogInfo($"Flight: {Groups.Name(player)} flies ({networkId})");
    }

    private static void Hide(byte player)
    {
        if (!Dragons.Remove(player, out var d)) return;
        if (d.Go != null) Object.Destroy(d.Go);
        Plugin.L.LogInfo($"Flight: {Groups.Name(player)} landed");
    }

    private static void Play(Dragon d, string state)
    {
        if (d.Anim == null || string.IsNullOrEmpty(state) || d.State == state) return;
        d.State = state;
        try { if (d.Anim.HasState(0, Animator.StringToHash(state))) d.Anim.CrossFadeInFixedTime(state, 0.18f); } catch { }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8(); string text = r.Str();
        switch (op)
        {
            case OpStart:
                Show(from, text);
                break;
            case OpState:
                if (Dragons.TryGetValue(from, out var d)) Play(d, text);
                break;
            case OpStop:
                Hide(from);
                break;

        }
    }

    public static void Update()
    {
        // mine: the others who came later learn it
        if (_ride != null && Time.unscaledTime >= _nextResend)
        {
            _nextResend = Time.unscaledTime + 3f;
            if (_ride.WasCollected || !_ride.running) { _ride = null; Send(OpStop, ""); }
            else { Send(OpStart, _network); if (_state.Length > 0) Send(OpState, _state); }
        }
        if (Dragons.Count == 0) return;
        var s = CoopManager.Session;
        var gone = new List<byte>();
        foreach (var kv in Dragons) if (s == null || !s.Players.ContainsKey(kv.Key) || kv.Value.Go == null) gone.Add(kv.Key);
        foreach (var id in gone) Hide(id);
    }

    /// <summary>Each dragon under its rider, after the riders moved.</summary>
    public static void LateUpdate()
    {
        foreach (var kv in Dragons)
        {
            var d = kv.Value;
            if (d.Go == null) continue;
            bool visible = Puppets.TryGetVisual(kv.Key, out var rider, out _);
            if (d.Go.activeSelf != visible) d.Go.SetActive(visible);
            if (!visible || rider == null) continue;
            var t = d.Go.transform;
            t.rotation = Quaternion.Euler(0f, rider.transform.eulerAngles.y, 0f);
            var seat = d.Seat.position + d.Seat.rotation * d.Offset;
            t.position += rider.transform.position - seat;
        }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlightPathRide), nameof(FlightPathRide.Begin))]
        private static void Began(FlightPathRide __instance, FlightPathNetwork __0)
        {
            try
            {
                if (!MyRide(__instance) || __0 == null) return;
                _ride = __instance; _network = __0.NetworkID ?? ""; _state = "";
                _nextResend = Time.unscaledTime + 3f;
                Send(OpStart, _network);
            }
            catch (Exception e) { Plugin.L.LogWarning("Flight start: " + e.Message); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlightPathRide), nameof(FlightPathRide.PlayDragon))]
        private static void Played(FlightPathRide __instance, string __0)
        {
            try { if (__instance == _ride && __0 != null && __0 != _state) { _state = __0; Send(OpState, __0); } } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(FlightPathRide), nameof(FlightPathRide.Restore))]
        private static void Ended(FlightPathRide __instance)
        {
            try { if (__instance == _ride) { _ride = null; Send(OpStop, ""); } } catch { }
        }
    }
}
