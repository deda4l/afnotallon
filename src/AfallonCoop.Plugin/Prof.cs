using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>Cheap always-on profiler for the mod's own code: per-section totals/max, counters and frame hitches.</summary>
internal static class Prof
{
    private sealed class Stat { public int Count; public double TotalMs; public double MaxMs; }

    private static readonly Dictionary<string, Stat> Stats = new();
    private static readonly Dictionary<string, int> Counters = new();
    private static readonly Stopwatch Sw = Stopwatch.StartNew();
    private static int _hitches;
    private static double _worstFrameMs;
    private static double _frameModMs;
    private static double _worstFrameModMs;

    /// <summary>
    /// Time a subsystem's per-frame work. Its exceptions stay inside it (logged, throttled per label), so one failing
    /// subsystem cannot stop the others that run after it in the same frame.
    /// </summary>
    public static void Run(string label, Action a)
    {
        double t0 = Sw.Elapsed.TotalMilliseconds;
        try { a(); }
        catch (Exception e)
        {
            Count("error." + label);
            if (!LastError.TryGetValue(label, out var at) || Sw.Elapsed.TotalSeconds - at > 10)
            {
                LastError[label] = Sw.Elapsed.TotalSeconds;
                Plugin.L.LogError($"[{label}] {e}");
            }
        }
        finally { Add(label, Sw.Elapsed.TotalMilliseconds - t0); }
    }

    private static readonly Dictionary<string, double> LastError = new();

    public static T Run<T>(string label, Func<T> f)
    {
        double t0 = Sw.Elapsed.TotalMilliseconds;
        try { return f(); }
        finally { Add(label, Sw.Elapsed.TotalMilliseconds - t0); }
    }

    public static void Count(string label, int n = 1)
    {
        Counters.TryGetValue(label, out var c);
        Counters[label] = c + n;
    }

    private static void Add(string label, double ms)
    {
        if (!Stats.TryGetValue(label, out var s)) Stats[label] = s = new Stat();
        s.Count++; s.TotalMs += ms; if (ms > s.MaxMs) s.MaxMs = ms;
        _frameModMs += ms;
        if (ms > 8) Plugin.L.LogWarning($"[prof] {label} took {ms:0.0} ms");
    }

    /// <summary>Call once per frame (end of LateUpdate).</summary>
    public static void EndFrame()
    {
        double frameMs = Time.unscaledDeltaTime * 1000.0;
        if (frameMs > _worstFrameMs) _worstFrameMs = frameMs;
        if (_frameModMs > _worstFrameModMs) _worstFrameModMs = _frameModMs;
        if (frameMs > 60) _hitches++;
        _frameModMs = 0;
    }

    public static string Report(bool reset)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"frames: hitches(>60ms)={_hitches} worstFrame={_worstFrameMs:0}ms worstModPerFrame={_worstFrameModMs:0.0}ms fps={1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f):0}");
        foreach (var kv in Stats.OrderByDescending(k => k.Value.TotalMs))
            sb.AppendLine($"  {kv.Key}: n={kv.Value.Count} total={kv.Value.TotalMs:0.0}ms avg={kv.Value.TotalMs / Math.Max(1, kv.Value.Count):0.000}ms max={kv.Value.MaxMs:0.0}ms");
        foreach (var kv in Counters.OrderBy(k => k.Key)) sb.AppendLine($"  #{kv.Key} = {kv.Value}");
        if (reset) { Stats.Clear(); Counters.Clear(); _hitches = 0; _worstFrameMs = 0; _worstFrameModMs = 0; }
        return sb.ToString();
    }
}
