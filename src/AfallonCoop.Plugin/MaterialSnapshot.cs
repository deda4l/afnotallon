using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// "What materials does each visible renderer of my character use", as text. The game layers armor onto the body
/// by appending materials, and skin/hair options swap them; copying the final result is simpler and more faithful
/// than re-running all of that logic without a real player behind the model.
/// Format: "path=matA+matB;path2=matC" where path is child names (with #n for repeated sibling names).
///
/// Character models have ~4000 transforms, and every name read crosses into IL2CPP, so paths are never computed for
/// the whole hierarchy: the sender caches the path per renderer, the receiver resolves only the paths it is sent,
/// walking down from the root, and caches the result per model.
/// </summary>
internal static class MaterialSnapshot
{
    // ---------------- sender ----------------

    private static IntPtr _captureRoot;
    private static readonly Dictionary<IntPtr, string> PathCache = new();

    public static string Capture(Transform root)
    {
        if (root.Pointer != _captureRoot) { PathCache.Clear(); _captureRoot = root.Pointer; }
        var sb = new StringBuilder();
        foreach (var r in root.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled) continue;
            var t = r.GetIl2CppType().Name;
            if (t != "SkinnedMeshRenderer" && t != "MeshRenderer") continue;
            var mats = r.sharedMaterials;
            if (mats == null || mats.Length == 0) continue;
            if (!PathCache.TryGetValue(r.Pointer, out var path))
                PathCache[r.Pointer] = path = PathOf(r.transform, root);
            sb.Append(path).Append('=');
            for (int i = 0; i < mats.Length; i++)
            {
                if (i > 0) sb.Append('+');
                sb.Append(Clean(mats[i] != null ? mats[i].name : ""));
            }
            sb.Append(';');
        }
        return sb.ToString();
    }

    // ---------------- receiver ----------------

    /// <summary>Resolved renderers per model root (cleared when the model is destroyed and rebuilt).</summary>
    private static readonly Dictionary<IntPtr, Dictionary<string, Renderer>> Resolved = new();

    public static void Forget(Transform root)
    {
        if (root != null) Resolved.Remove(root.Pointer);
    }

    public static int Apply(Transform root, string snapshot)
    {
        if (string.IsNullOrEmpty(snapshot)) return 0;
        if (!Resolved.TryGetValue(root.Pointer, out var cache)) Resolved[root.Pointer] = cache = new();

        int applied = 0, missing = 0;
        foreach (var entry in snapshot.Split(';'))
        {
            int eq = entry.IndexOf('=');
            if (eq <= 0) continue;
            var path = entry.Substring(0, eq);
            if (!cache.TryGetValue(path, out var renderer))
                cache[path] = renderer = Resolve(root, path); // null is cached too: not part of this model
            if (renderer == null) continue;

            var names = entry.Substring(eq + 1).Split('+');
            var current = renderer.sharedMaterials;
            if (current != null && current.Length == names.Length)
            {
                bool same = true;
                for (int i = 0; i < names.Length && same; i++)
                    same = Clean(current[i] != null ? current[i].name : "") == names[i];
                if (same) continue; // nothing to do: avoid touching the renderer at all
            }

            var mats = new Material[names.Length];
            bool ok = true;
            for (int i = 0; i < names.Length && ok; i++)
            {
                mats[i] = Find(names[i]);
                ok = mats[i] != null || names[i].Length == 0;
            }
            if (!ok) { missing++; continue; }
            renderer.sharedMaterials = mats;
            applied++;
        }
        if (missing > 0) Plugin.L.LogInfo($"Materials: {missing} renderers kept their own (material not loaded here)");
        return applied;
    }

    private static Renderer Resolve(Transform root, string path)
    {
        var t = root;
        foreach (var part in path.Split('/'))
        {
            string name = part;
            int dup = 0, hash = part.LastIndexOf('#');
            if (hash > 0 && int.TryParse(part.Substring(hash + 1), out var d)) { name = part.Substring(0, hash); dup = d; }
            Transform found = null;
            int seen = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name != name) continue;
                if (seen++ == dup) { found = c; break; }
            }
            if (found == null) return null;
            t = found;
        }
        return t.GetComponent<Renderer>();
    }

    // ---------------- shared ----------------

    private static readonly Dictionary<string, Material> Cache = new();
    private static float _scannedAt = -100f;

    private static Material Find(string name)
    {
        if (name.Length == 0) return null;
        if (Cache.TryGetValue(name, out var m) && m != null) return m;
        if (Time.unscaledTime - _scannedAt < 10f) return null;
        _scannedAt = Time.unscaledTime;
        Prof.Count("scan.Material");
        foreach (var mat in Resources.FindObjectsOfTypeAll<Material>())
            if (mat != null) Cache[Clean(mat.name)] = mat;
        return Cache.TryGetValue(name, out m) ? m : null;
    }

    private static string Clean(string n)
    {
        n ??= "";
        if (n.EndsWith(" (Instance)")) n = n.Substring(0, n.Length - " (Instance)".Length);
        return n.Replace(";", "_").Replace("=", "_").Replace("+", "_");
    }

    private static string PathOf(Transform t, Transform root)
    {
        var parts = new List<string>();
        while (t != null && t.Pointer != root.Pointer)
        {
            var name = t.name;
            int dup = 0;
            var parent = t.parent;
            if (parent != null)
            {
                int idx = t.GetSiblingIndex();
                for (int i = 0; i < idx; i++)
                    if (parent.GetChild(i).name == name) dup++;
            }
            parts.Add(dup == 0 ? name : $"{name}#{dup}");
            t = parent;
        }
        parts.Reverse();
        return string.Join("/", parts);
    }
}
