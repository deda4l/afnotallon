using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Makes a puppet semi-transparent (ally in stealth). The game renders characters with opaque materials, so each
/// renderer temporarily gets transparent copies (built-in Standard shader, Fade mode, original texture and tint).
///
/// Plain transparency lets inner parts (eyes, teeth, mouth) show through the face, because transparent surfaces do not
/// write depth. So the copies DO write depth, and the parts are drawn from the largest to the smallest: the body and
/// face go first and hide whatever lies inside them, like on an opaque character. That needs a render queue per part,
/// hence one copy per renderer and slot rather than per shared material.
/// </summary>
internal static class Translucency
{
    private static Shader _shader;
    private static bool _shaderLooked;

    private sealed class Saved { public Renderer R; public Material[] Mats; public Material[] Ghosts; }

    /// <summary>Per puppet root: original materials of every renderer while translucent.</summary>
    private static readonly Dictionary<IntPtr, List<Saved>> Active = new();

    public static bool IsOn(GameObject root) => root != null && Active.ContainsKey(root.Pointer);

    private static Shader FadeShader()
    {
        if (_shaderLooked) return _shader;
        _shaderLooked = true;
        _shader = Shader.Find("Standard");
        if (_shader == null) Plugin.L.LogWarning("Standard shader not available; stealth translucency disabled");
        return _shader;
    }

    private static Material Ghost(Material src, float opacity, int queue)
    {
        var g = new Material(FadeShader()) { name = (src != null ? src.name : "none") + " (AfCoop ghost)" };
        Texture tex = null;
        if (src != null)
            foreach (var p in new[] { "_MainTex", "_BaseMap", "_BaseColorMap", "_Albedo" })
                if (src.HasProperty(p) && (tex = src.GetTexture(p)) != null) break;
        if (tex != null) g.mainTexture = tex;
        var color = src != null && src.HasProperty("_Color") ? src.GetColor("_Color") : Color.white;
        color.a = opacity;
        g.color = color;
        // Standard shader "Fade" mode as the material inspector sets it, but keeping depth writes (see class summary).
        g.SetFloat("_Mode", 2f);
        g.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        g.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        g.SetInt("_ZWrite", 1);
        g.DisableKeyword("_ALPHATEST_ON");
        g.EnableKeyword("_ALPHABLEND_ON");
        g.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        g.SetFloat("_Glossiness", 0.2f);
        g.renderQueue = queue;
        return g;
    }

    public static void Set(GameObject root, bool on)
    {
        if (root == null) return;
        on &= Plugin.CfgStealthTranslucent.Value && FadeShader() != null;
        bool isOn = Active.TryGetValue(root.Pointer, out var saved);
        if (on == isOn) return;
        if (on)
        {
            float opacity = Mathf.Clamp(Plugin.CfgStealthOpacity.Value, 0.1f, 0.9f);
            saved = new List<Saved>();
            var renderers = root.GetComponentsInChildren<Renderer>()
                .Where(r => r != null && r.enabled && r.TryCast<ParticleSystemRenderer>() == null && r.TryCast<TrailRenderer>() == null)
                .OrderByDescending(r => r.bounds.size.sqrMagnitude) // outer, larger parts first
                .ToList();
            int queue = 3000;
            foreach (var r in renderers)
            {
                var mats = r.sharedMaterials;
                var ghosts = new Material[mats.Length];
                for (int i = 0; i < mats.Length; i++) ghosts[i] = Ghost(mats[i], opacity, Math.Min(queue, 3999));
                queue++;
                saved.Add(new Saved { R = r, Mats = mats, Ghosts = ghosts });
                r.sharedMaterials = ghosts;
            }
            Active[root.Pointer] = saved;
        }
        else Restore(root.Pointer, saved);
    }

    private static void Restore(IntPtr key, List<Saved> saved)
    {
        foreach (var s in saved)
        {
            if (s.R != null) s.R.sharedMaterials = s.Mats;
            foreach (var g in s.Ghosts) if (g != null) Object.Destroy(g);
        }
        Active.Remove(key);
    }

    /// <summary>The puppet is being destroyed or rebuilt: drop its ghost materials.</summary>
    public static void Forget(GameObject root)
    {
        if (root != null && Active.TryGetValue(root.Pointer, out var saved)) Restore(root.Pointer, saved);
    }
}
