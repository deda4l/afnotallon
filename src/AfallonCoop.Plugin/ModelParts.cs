using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Parts of a character model that the game switches on and off by rules (ActiveRequirement: e.g. the stealth aura).
/// Those rules are always evaluated against the local player, so a puppet cannot run them; the owner reports which of
/// its parts are on, by the StableHash of their path under the model root, and the puppet mirrors that.
/// </summary>
internal static class ModelParts
{
    /// <summary>Paths (hash → object) of rule-driven parts under a model root.</summary>
    public static Dictionary<int, GameObject> Collect(Transform root)
    {
        var map = new Dictionary<int, GameObject>();
        foreach (var r in root.GetComponentsInChildren<ActiveRequirement>(true))
        {
            var t = r.TargetObject;
            if (t == null || !t.transform.IsChildOf(root)) continue;
            map[StableHash.Of(RelPath(root, t.transform))] = t;
        }
        return map;
    }

    private static string RelPath(Transform root, Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null && p != root; p = p.parent) s = p.name + "/" + s;
        return s;
    }

    // ---------------- owner ----------------

    private static IntPtr _ownerRoot;
    private static Dictionary<int, GameObject> _owner = new();

    public static void FillActive(Transform root, List<int> into)
    {
        into.Clear();
        if (root == null) return;
        if (root.Pointer != _ownerRoot) { _owner = Collect(root); _ownerRoot = root.Pointer; }
        foreach (var kv in _owner)
            if (kv.Value != null && kv.Value.activeSelf) into.Add(kv.Key);
    }

    // ---------------- puppet ----------------

    public static void Apply(Dictionary<int, GameObject> parts, List<int> active)
    {
        if (parts == null || parts.Count == 0) return;
        foreach (var kv in parts)
        {
            if (kv.Value == null) continue;
            bool on = active.Contains(kv.Key);
            if (kv.Value.activeSelf != on) kv.Value.SetActive(on);
        }
    }
}
