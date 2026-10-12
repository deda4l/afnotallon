using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Reads an animator the owner's game drives (parameters + current state and weight per layer), for sending.
/// One instance per sampled animator: it caches the parameter list per controller.
/// </summary>
internal sealed class AnimatorSampler
{
    private RuntimeAnimatorController _cached;
    private readonly List<(int hash, AnimParamType type)> _params = new();

    /// <summary>Fills params and layers; returns the StableHash of the controller name (0 = none).</summary>
    public int Sample(Animator anim, List<AnimParam> outParams, List<AnimLayer> outLayers)
    {
        var ctrl = anim != null ? anim.runtimeAnimatorController : null;
        if (ctrl == null) return 0;
        if (_cached == null || _cached.Pointer != ctrl.Pointer)
        {
            _cached = ctrl;
            _params.Clear();
            foreach (var p in anim.parameters)
            {
                var t = (int)p.type;
                if (t == (int)AnimParamType.Float || t == (int)AnimParamType.Int || t == (int)AnimParamType.Bool)
                    _params.Add((p.nameHash, (AnimParamType)t));
            }
        }
        foreach (var (hash, type) in _params)
        {
            float v = type switch
            {
                AnimParamType.Float => anim.GetFloat(hash),
                AnimParamType.Int => anim.GetInteger(hash),
                _ => anim.GetBool(hash) ? 1f : 0f,
            };
            outParams.Add(new AnimParam(hash, type, v));
        }
        for (int i = 0; i < anim.layerCount; i++)
        {
            // While blending, report where we are going so the remote copy starts the same transition early.
            var info = anim.IsInTransition(i) ? anim.GetNextAnimatorStateInfo(i) : anim.GetCurrentAnimatorStateInfo(i);
            outLayers.Add(new AnimLayer(info.fullPathHash, info.normalizedTime, i == 0 ? 1f : anim.GetLayerWeight(i)));
        }
        return StableHash.Of(ctrl.name);
    }
}

/// <summary>
/// Makes a copy's animator follow sampled owner data: parameters drive the same transitions; layer weights blend;
/// a layer that stays in a different state (attacks, triggers) is cross-faded to the owner's state at the same point.
/// </summary>
internal sealed class AnimatorFollower
{
    private const float ForceStateAfter = 0.2f;
    private HashSet<int> _paramHashes;
    private float[] _mismatchSince;
    private IntPtr _controller;

    /// <summary>Dev (cast trace): the copy being followed, to log forced state changes.</summary>
    public static string TraceName;

    public void Reset() { _paramHashes = null; _mismatchSince = null; _controller = IntPtr.Zero; }

    public void Apply(Animator a, List<AnimParam> ps, List<AnimLayer> ls, float dt)
    {
        if (a == null || a.runtimeAnimatorController == null) return;
        if (a.runtimeAnimatorController.Pointer != _controller) { Reset(); _controller = a.runtimeAnimatorController.Pointer; }
        if (_paramHashes == null)
        {
            _paramHashes = new HashSet<int>();
            foreach (var par in a.parameters) _paramHashes.Add(par.nameHash);
        }
        foreach (var par in ps)
        {
            if (!_paramHashes.Contains(par.NameHash)) continue;
            switch (par.Type)
            {
                case AnimParamType.Float: a.SetFloat(par.NameHash, par.Value); break;
                case AnimParamType.Int: a.SetInteger(par.NameHash, (int)par.Value); break;
                case AnimParamType.Bool: a.SetBool(par.NameHash, par.Value > 0.5f); break;
            }
        }

        int layers = Math.Min(a.layerCount, ls.Count);
        if (_mismatchSince == null || _mismatchSince.Length != layers) _mismatchSince = new float[layers];
        for (int i = 0; i < layers; i++)
        {
            if (i > 0)
            {
                float w = a.GetLayerWeight(i), want = ls[i].Weight;
                if (Math.Abs(w - want) > 0.001f) a.SetLayerWeight(i, Mathf.MoveTowards(w, want, dt * 8f)); // ~0.12 s blend
            }
            int target = ls[i].StateHash;
            int cur = a.GetCurrentAnimatorStateInfo(i).fullPathHash;
            int next = a.IsInTransition(i) ? a.GetNextAnimatorStateInfo(i).fullPathHash : 0;
            if (target == 0 || cur == target || next == target) { _mismatchSince[i] = 0; continue; }
            // Parameters usually drive the same transition; only force a crossfade when they don't (attacks, triggers).
            _mismatchSince[i] += dt;
            if (_mismatchSince[i] < ForceStateAfter) continue;
            _mismatchSince[i] = 0;
            // Join the owner's state at the same point in it, so short actions (swings) are not replayed from the start late.
            if (TraceName != null)
                Plugin.L.LogInfo($"Anim {Time.time:0.00} '{TraceName}' L{i} forced {cur} -> {target} at {ls[i].NormalizedTime:0.00} (has={a.HasState(i, target)}, next={next})");
            if (a.HasState(i, target)) a.CrossFade(target, 0.1f, i, ls[i].NormalizedTime % 1f);
        }
    }

    /// <summary>For diagnostics: per layer, does the copy play the owner's state.</summary>
    public static string Compare(Animator a, List<AnimLayer> ls)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Math.Min(a.layerCount, ls.Count); i++)
            sb.Append($" L{i}:{(a.GetCurrentAnimatorStateInfo(i).fullPathHash == ls[i].StateHash ? "ok" : "diff")}");
        return sb.ToString();
    }
}
