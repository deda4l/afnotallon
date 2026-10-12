#if BATTLEGROUNDS // the game ships no battleground yet (no scene, finder tab off): build with -p:Battlegrounds=true
using System;
using BLINK.RPGBuilder.PvP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Dev only: the game ships the battleground's code and definition but not its scene (Stonefield Basin is not in the
/// build), so a match cannot be played yet. For testing, `bg testarena` points the battleground at a scene that
/// exists and puts the arena the game looks for there on arrival: the BattlegroundScene with two team spawns and five
/// bases around a fixed spot (the same on every machine). Everything else is the game's own match.
/// </summary>
internal static class BattlegroundTestArena
{
    private static int _sceneId = -1;
    private static Vector3 _center;

    public static string Configure(string arg)
    {
        var parts = arg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 5) return "bg testarena <scene id> <team size> <x> <y> <z> [winning score]";
        var svc = BattlegroundService.Instance;
        var def = svc?.Settings?.Battlegrounds?[0];
        if (def == null) return "no battleground";
        int sceneId = int.Parse(parts[0]);
        var scenes = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetGameScenes();
        if (!scenes.TryGetValue(sceneId, out var gs)) return "no such scene";
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        _center = new Vector3(float.Parse(parts[2], inv), float.Parse(parts[3], inv), float.Parse(parts[4], inv));
        _sceneId = sceneId;
        def.Scene = gs;
        def.TeamSize = Mathf.Clamp(int.Parse(parts[1]), 2, 15);
        def.PreparationSeconds = 20f;
        if (parts.Length > 5) def.WinningScore = int.Parse(parts[5]);
        return $"test arena: '{def.DisplayName}' plays in '{gs.entryName}' around {_center}, {def.TeamSize} vs {def.TeamSize}, to {def.WinningScore}";
    }

    private static Vector3 Snap(Vector3 p) => NavMesh.SamplePosition(p, out var hit, 25f, -1) ? hit.position : p;

    private static void Build(BattlegroundDefinition def)
    {
        var root = new GameObject("AfCoop_TestArena");
        var c = _center;
        Transform Point(string name, Vector3 at, Transform parent = null)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent ?? root.transform, false);
            go.transform.position = Snap(at);
            return go.transform;
        }
        var arena = root.AddComponent<BattlegroundScene>();
        arena.Definition = def;
        arena.BlueSpawn = Point("Blue spawn", c + new Vector3(-45f, 0f, 0f));
        arena.RedSpawn = Point("Red spawn", c + new Vector3(45f, 0f, 0f));
        var offsets = new[] { new Vector3(0, 0, 0), new Vector3(-25, 0, 25), new Vector3(-25, 0, -25), new Vector3(25, 0, 25), new Vector3(25, 0, -25) };
        var names = new[] { "Mill", "Farm", "Stables", "Mine", "Smithy" };
        var objs = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<BattlegroundObjective>(5);
        for (int i = 0; i < 5; i++)
        {
            var t = Point(names[i], c + offsets[i]);
            var o = t.gameObject.AddComponent<BattlegroundObjective>();
            o.DisplayName = names[i];
            o.Index = i;
            o.Graveyard = Point("Graveyard", t.position + new Vector3(0, 0, 6f), t);
            o.TeamCloth = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Renderer>(0);
            o.State = new BattlegroundNodeState();
            var marker = GameObject.CreatePrimitive(PrimitiveType.Cylinder); // something to see where the base is
            Object.Destroy(marker.GetComponent<Collider>());
            marker.transform.SetParent(t, false);
            marker.transform.localScale = new Vector3(0.6f, 3f, 0.6f);
            marker.transform.localPosition = new Vector3(0, 3f, 0);
            objs[i] = o;
        }
        arena.Objectives = objs;
        arena.PlayBounds = new Bounds(c, new Vector3(400f, 200f, 400f));
        arena.StartingGates = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<GameObject>(0);
        Plugin.L.LogInfo($"Test arena built around {c}: blue {arena.BlueSpawn.position}, red {arena.RedSpawn.position}");
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>Arriving in the test scene for a match: the arena is there before the game looks for it (a frame later).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BattlegroundService), "SceneReady")]
        private static void Arrived(BattlegroundService __instance)
        {
            try
            {
                if (_sceneId < 0 || __instance.Data?.Journey == null || __instance.Definition == null) return;
                var gs = RPGBuilderUtilities.GetGameSceneFromName(UnityEngine.SceneManagement.SceneManager.GetActiveScene().name);
                if (gs == null || gs.ID != _sceneId || __instance.Data.Journey.BattlegroundSceneID != _sceneId) return;
                if (Object.FindObjectOfType<BattlegroundScene>() != null) return;
                Build(__instance.Definition);
            }
            catch (Exception e) { Plugin.L.LogWarning("Test arena: " + e); }
        }
    }
}
#endif
