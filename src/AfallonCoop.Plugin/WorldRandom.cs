using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using HarmonyLib;
using UnityEngine;
using Random = UnityEngine.Random;

namespace AfallonCoop.Plugin;

/// <summary>
/// The world's random events where the game rolls them in the scene (its RandomActivators: where the moving camp, the
/// meteor strike, the bandit roadblocks, the demon invasion appear) are the host's: the host picks a seed for the
/// session, and each machine runs the game's own roll with Unity's random seeded by it and by that activator's place
/// in the scene, so everyone rolls the same, whenever their part of the world loads. What rolled before the seed was
/// known (the host's world before hosting, a client's before the host's rules came) rolls again with it.
/// </summary>
internal static class WorldRandom
{
    private static int _seed;
    private static bool _have;
    private static readonly List<RandomActivator> Rolled = new();

    /// <summary>The seed the host sends (picked when it first needs one).</summary>
    public static int HostSeed
    {
        get
        {
            if (!_have) { _seed = new System.Random().Next(); _have = true; RollAgain(); }
            return _seed;
        }
    }

    public static void FromHost(int seed)
    {
        if (_have && seed == _seed) return;
        _seed = seed; _have = true;
        RollAgain();
    }

    /// <summary>Out of the session: what is there stays; the next session's seed rolls it again.</summary>
    public static void Reset() => _have = false;

    private static void RollAgain()
    {
        int n = 0;
        Rolled.RemoveAll(a => a == null || a.WasCollected);
        foreach (var a in Rolled.ToArray())
        {
            try { a.Start(); n++; } // the game's own roll, seeded by the patch
            catch (Exception e) { Plugin.L.LogWarning("World random: " + e.Message); }
        }
        Plugin.L.LogInfo($"World random: seed {_seed}, {n} rolled again");
    }

    private static string Path(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    public static string Dev()
    {
        var sb = new System.Text.StringBuilder($"seed {(_have ? _seed.ToString() : "none")}; ");
        foreach (var a in Rolled)
        {
            if (a == null || a.WasCollected || a.gameObjects == null) continue;
            sb.Append(a.name).Append(':');
            for (int i = 0; i < a.gameObjects.Count; i++) if (a.gameObjects[i] != null && a.gameObjects[i].activeSelf) sb.Append(' ').Append(a.gameObjects[i].name.Trim());
            sb.Append("; ");
        }
        return sb.ToString();
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RandomActivator), nameof(RandomActivator.Start))]
        private static void Seed(RandomActivator __instance, out Random.State? __state)
        {
            __state = null;
            if (!_have || !CoopManager.Connected) return;
            __state = Random.state;
            Random.InitState(_seed ^ StableHash.Of(__instance.gameObject.scene.name + "/" + Path(__instance.transform)));
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RandomActivator), nameof(RandomActivator.Start))]
        private static void Done(RandomActivator __instance, Random.State? __state)
        {
            if (__state.HasValue) Random.state = __state.Value;
            if (!Rolled.Contains(__instance)) Rolled.Add(__instance);
        }
    }
}
