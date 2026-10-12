using Il2CppInterop.Runtime.InteropTypes.Arrays;
using System;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;
using BLINK.RPGBuilder.Managers;
using UnityEngine;
using UnityEngine.InputSystem;

namespace AfallonCoop.Plugin;

/// <summary>Hotkeys: F7 teleport to the next ally in this scene, F8 write a diagnostics dump.</summary>
internal static class DevTools
{
    /// <summary>UI objects whose name contains the text: path, screen rect, components.</summary>
    internal static string UiFind(string name)
    {
        var sb = new StringBuilder();
        foreach (var rt in UnityEngine.Object.FindObjectsOfType<RectTransform>(true))
        {
            if (rt == null || rt.name.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var corners = new Il2CppStructArray<Vector3>(4);
            rt.GetWorldCorners(corners);
            sb.AppendLine($"{PathOf(rt)} active={rt.gameObject.activeInHierarchy} rect=({corners[0].x:0},{corners[0].y:0})-({corners[2].x:0},{corners[2].y:0}) [{string.Join(",", rt.GetComponents<Component>().Select(c => c.GetIl2CppType().Name))}]");
        }
        return sb.Length > 0 ? sb.ToString() : "none";
    }

    /// <summary>UI elements under a point of the screen (x, y as fractions of the screen, y from the bottom).</summary>
    internal static string UiAt(float fx, float fy)
    {
        var es = UnityEngine.EventSystems.EventSystem.current;
        if (es == null) return "no event system";
        var data = new UnityEngine.EventSystems.PointerEventData(es) { position = new Vector2(Screen.width * fx, Screen.height * fy) };
        var hits = new Il2CppSystem.Collections.Generic.List<UnityEngine.EventSystems.RaycastResult>();
        es.RaycastAll(data, hits);
        var sb = new StringBuilder();
        foreach (var h in hits) sb.AppendLine($"{PathOf(h.gameObject.transform)} [{string.Join(",", h.gameObject.GetComponents<Component>().Select(c => c.GetIl2CppType().Name))}]");
        return sb.Length > 0 ? sb.ToString() : $"nothing at {data.position}";
    }

    private static int _teleportIndex;

    private static bool Pressed(Func<Keyboard, UnityEngine.InputSystem.Controls.KeyControl> key)
    {
        try
        {
            var kb = Keyboard.current;
            return kb != null && key(kb).wasPressedThisFrame;
        }
        catch { return false; }
    }

    public static void Update()
    {
        if (Pressed(k => k.f8Key)) Dump();
        if (Pressed(k => k.f7Key)) TeleportToNextAlly();
        if (Pressed(k => k.f6Key)) Hud.Notify(SpawnNpc(""));
    }

    /// <summary>Debug: spawn NPCs a few meters in front of the player, the way the game's spawners do it.</summary>
    internal static string SpawnNpc(string arg)
    {
        var player = GameInfo.Player;
        if (player == null) return "not in the world";
        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int count = 1;
        if (parts.Length > 0 && int.TryParse(parts[^1], out var c)) { count = Math.Clamp(c, 1, 10); parts = parts[..^1]; }
        string want = parts.Length > 0 ? string.Join(" ", parts) : "Orbweaver hatchling";

        RPGNpc npc = null;
        foreach (var kv in GameDatabase.Instance.GetNPCs())
        {
            var n = kv.Value;
            if (n == null) continue;
            if (int.TryParse(want, out var id) ? kv.Key == id
                : (n.entryName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0
                  || (n.entryDisplayName ?? "").IndexOf(want, StringComparison.OrdinalIgnoreCase) >= 0)
            { npc = n; break; }
        }
        if (npc == null) return $"no NPC matching '{want}'";
        var tr = player.transform;
        for (int i = 0; i < count; i++)
        {
            var pos = tr.position + tr.forward * 6f + tr.right * ((i - (count - 1) / 2f) * 2f);
            var e = CombatManager.Instance.GenerateNPCEntity(npc, false, false, null, pos, Quaternion.LookRotation(-tr.forward), null);
            MobSync.TrackLoose(e); // in co-op the others see it too
        }
        return $"spawned {count} x {npc.entryName} (#{npc.ID})";
    }

    internal static void TeleportToNextAlly()
    {
        var s = CoopManager.Session;
        if (s == null || !GameInfo.InWorld) return;
        var ids = s.Players.Keys.Where(id => id != s.LocalId).OrderBy(i => i).ToList();
        for (int n = 0; n < ids.Count; n++)
        {
            var id = ids[(_teleportIndex + n) % ids.Count];
            if (!Puppets.TryGetPosition(id, out var pos)) continue;
            _teleportIndex = (_teleportIndex + n + 1) % ids.Count;
            GameInfo.TeleportPlayer(pos + Vector3.right * 1.5f);
            Hud.Notify($"Teleported to {s.Players[id].Name}");
            return;
        }
        Hud.Notify("No ally in this zone to teleport to.");
    }

    public static void Dump()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== AfallonCoop dump {DateTime.Now:s} scene='{GameInfo.SceneName}' build={GameInfo.BuildGuid}");
        try
        {
            var mm = MainMenuManager.Instance;
            if (mm != null && mm.HomeCG != null)
            {
                sb.AppendLine("--- Main menu HomeCG");
                Tree(mm.HomeCG.transform, sb, 0, 4);
            }
        }
        catch (Exception e) { sb.AppendLine("menu: " + e.Message); }

        var player = GameInfo.Player;
        if (player != null)
        {
            sb.AppendLine($"--- Player '{player.name}' pos={player.transform.position}");
            Tree(player.transform, sb, 0, 3);
            var anim = GameInfo.PlayerAnimator;
            if (anim != null)
            {
                sb.AppendLine($"--- Animator on '{anim.name}' controller='{anim.runtimeAnimatorController?.name}' layers={anim.layerCount} rootMotion={anim.applyRootMotion} culling={anim.cullingMode}");
                foreach (var p in anim.parameters) sb.AppendLine($"  param {p.name} ({p.type}) hash={p.nameHash}");
                for (int i = 0; i < anim.layerCount; i++)
                {
                    var st = anim.GetCurrentAnimatorStateInfo(i);
                    sb.AppendLine($"  layer {i} '{anim.GetLayerName(i)}' state={st.fullPathHash} t={st.normalizedTime:0.00}");
                }
            }
        }
        foreach (var (id, tr) in Puppets.Visible())
        {
            sb.AppendLine($"--- Puppet #{id} '{tr.name}' active={tr.gameObject.activeInHierarchy} pos={tr.position} scale={tr.lossyScale}");
            foreach (var r in tr.GetComponentsInChildren<Renderer>(true))
            {
                string mesh = "";
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr != null) mesh = $" mesh={(smr.sharedMesh != null ? smr.sharedMesh.name : "null")} root={(smr.rootBone != null ? smr.rootBone.name : "null")} bones={smr.bones.Length}";
                sb.AppendLine($"  renderer {r.GetIl2CppType().Name} '{r.name}' enabled={r.enabled} active={r.gameObject.activeInHierarchy} bounds={r.bounds.center}/{r.bounds.size}{mesh} mats={string.Join("+", r.sharedMaterials.Where(m => m != null).Select(m => m.name))}");
            }
        }
        if (player != null)
        {
            sb.AppendLine("--- Player renderers");
            foreach (var r in player.GetComponentsInChildren<Renderer>(true))
            {
                string mesh = "";
                var smr = r.TryCast<SkinnedMeshRenderer>();
                if (smr != null) mesh = $" mesh={(smr.sharedMesh != null ? smr.sharedMesh.name : "null")} root={(smr.rootBone != null ? smr.rootBone.name : "null")}";
                sb.AppendLine($"  renderer {r.GetIl2CppType().Name} '{r.name}' enabled={r.enabled} active={r.gameObject.activeInHierarchy} bounds={r.bounds.center}/{r.bounds.size}{mesh} mats={string.Join("+", r.sharedMaterials.Where(m => m != null).Select(m => m.name))}");
            }
        }
        var path = Path.Combine(Paths.BepInExRootPath, "AfCoop_dump.txt");
        File.AppendAllText(path, sb.ToString());
        Hud.Notify("Diagnostics written to BepInEx/AfCoop_dump.txt");
    }

    internal static string PathOf(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    private static System.Collections.Generic.HashSet<int> _snapshot = new();

    /// <summary>Remember every live transform, so <see cref="SnapshotDiff"/> can list what was spawned since.</summary>
    internal static void Snapshot()
    {
        _snapshot = new System.Collections.Generic.HashSet<int>();
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true)) _snapshot.Add(t.GetInstanceID());
    }

    internal static string SnapshotDiff()
    {
        var sb = new StringBuilder();
        int n = 0;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>(true))
        {
            if (_snapshot.Contains(t.GetInstanceID())) continue;
            if (t.parent != null && !_snapshot.Contains(t.parent.GetInstanceID())) continue; // only roots of new subtrees
            if (n++ < 80)
            {
                var comps = string.Join(",", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name));
                sb.AppendLine($"{PathOf(t)} [{(t.gameObject.activeInHierarchy ? "on" : "off")}] {{{comps}}} children={t.GetComponentsInChildren<Transform>(true).Length - 1}");
            }
        }
        sb.AppendLine($"{n} new subtrees");
        return sb.ToString();
    }

    internal static void Tree(Transform t, StringBuilder sb, int depth, int maxDepth)
    {
        var comps = string.Join(", ", t.GetComponents<Component>().Where(c => c != null).Select(c => c.GetIl2CppType().Name));
        sb.AppendLine($"{new string(' ', depth * 2)}{t.name} [{(t.gameObject.activeSelf ? "on" : "off")}] {{{comps}}}");
        if (depth >= maxDepth) return;
        for (int i = 0; i < t.childCount; i++) Tree(t.GetChild(i), sb, depth + 1, maxDepth);
    }
}
