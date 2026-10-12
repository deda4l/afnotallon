using System;
using AfallonCoop.Protocol;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// One time of day and one weather for everyone: the host's. The game keeps its world time in COZY (CozyTimeBridge owns
/// it and mirrors it into the save); the host sends its hour and minute and the current weather profile, clients set
/// the same through the game's own calls (CozyTimeBridge.SetWorldTime, the ecosystem's SetWeather with a transition).
/// Weather only between players in the same scene (each scene has its own sky); time everywhere.
/// </summary>
internal static class WorldClock
{
    public const byte Kind = 15;
    private const float SendEvery = 5f;
    private const int MaxDriftMinutes = 2;
    private const float WeatherTransition = 10f;

    private static float _nextSend;
    private static string _hostWeather = "";
    private static int _hostWeatherScene;
    private static float _nextWeatherCheck;
    private static readonly NetWriter W = new();

    private static DistantLands.Cozy.CozyWeather Cozy
    {
        get { try { return DistantLands.Cozy.CozyWeather.instance; } catch { return null; } }
    }

    /// <summary>
    /// The sky as seen: the heaviest profile of COZY's blend ("Clear coalwaywoods"), not the forecast's name ("Clear"):
    /// a machine's own forecast can start blending in another profile while the name stays.
    /// </summary>
    private static string WeatherName()
    {
        try
        {
            var cz = Cozy;
            string best = ""; float bestW = -1f;
            if (cz?.currentWeatherProfiles != null)
                foreach (var w in cz.currentWeatherProfiles)
                    if (w?.profile != null && w.weight > bestW) { bestW = w.weight; best = w.profile.name; }
            return best.Length > 0 ? best : cz?.currentWeather?.name ?? "";
        }
        catch { return ""; }
    }

    /// <summary>The blend is the host's profile alone (anything else weighs next to nothing).</summary>
    private static bool SameSky(string profile)
    {
        try
        {
            var cz = Cozy;
            if (cz?.currentWeatherProfiles == null) return true;
            bool has = false;
            foreach (var w in cz.currentWeatherProfiles)
            {
                if (w?.profile == null) continue;
                if (w.profile.name == profile) { if (w.weight > 0.5f) has = true; }
                else if (w.weight > 0.05f) return false;
            }
            return has;
        }
        catch { return true; }
    }

    public static void Update()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected || !GameInfo.InWorld) return;
        if (s.IsHost)
        {
            if (Time.unscaledTime < _nextSend || s.PlayerCount < 2) return;
            _nextSend = Time.unscaledTime + SendEvery;
            if (!CozyTimeBridge.TryGetCurrentTime(out int h, out int m)) return;
            W.Reset(); W.U8(Kind); W.U8((byte)h); W.U8((byte)m);
            W.I32(StableHash.Of(Dungeons.Place)); W.Str(WeatherName());
            // Each ecosystem (the scene's and its biomes') runs its own forecast: its weather and time to the next change.
            var ecos = Ecosystems();
            W.U8((byte)Math.Min(ecos.Length, 32));
            for (int i = 0; i < ecos.Length && i < 32; i++)
            {
                var e = ecos[i];
                W.Str(Key(e)); W.Str(e.currentWeather != null ? e.currentWeather.name : "");
                var ws = e.weightedWeatherProfiles;
                int n = 0;
                if (ws != null) foreach (var w in ws) if (w?.profile != null && w.weight > 0.001f && n < 8) n++; // only what shows: the rest fades out there
                W.U8((byte)n);
                int k = 0;
                if (ws != null) foreach (var w in ws) if (w?.profile != null && w.weight > 0.001f && k < n) { W.Str(w.profile.name); W.F32(w.weight); k++; }
            }
            s.SendWorld(W.Span);
        }
        else if (Time.unscaledTime >= _nextWeatherCheck)
        {
            _nextWeatherCheck = Time.unscaledTime + SendEvery;
            KeepWeather(); // this machine's own forecast would drift away from the host's sky
        }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        int h = r.U8(), m = r.U8();
        _hostWeatherScene = r.I32();
        _hostWeather = r.Str();
        if (r.Remaining > 0)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                string key = r.Str(), weather = r.Str();
                int count = r.U8();
                var weights = new System.Collections.Generic.List<(string, float)>(count);
                for (int j = 0; j < count; j++) weights.Add((r.Str(), r.F32()));
                HostEcos[key] = (weather, weights);
            }
        }
        var s = CoopManager.Session;
        if (s == null || s.IsHost || from != ProtocolInfo.HostId || !GameInfo.InWorld) return;
        try
        {
            if (CozyTimeBridge.TryGetCurrentTime(out int ch, out int cm))
            {
                int diff = Math.Abs((ch * 60 + cm) - (h * 60 + m));
                diff = Math.Min(diff, 24 * 60 - diff); // across midnight
                if (diff > MaxDriftMinutes) CozyTimeBridge.SetWorldTime(h, m);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("World time: " + e.Message); }
        KeepWeather();
    }

    /// <summary>The host's ecosystems: weather and blend weights, by <see cref="Key"/>.</summary>
    private static readonly System.Collections.Generic.Dictionary<string, (string weather, System.Collections.Generic.List<(string profile, float weight)> weights)> HostEcos = new();
    private static readonly System.Collections.Generic.HashSet<IntPtr> Taken = new(); // ecosystems whose own forecast is stopped here

    /// <summary>An ecosystem by name and place (two biomes can share a name).</summary>
    private static string Key(DistantLands.Cozy.CozyEcosystem e)
    {
        var p = e.transform.position;
        return $"{e.gameObject.name}@{Mathf.RoundToInt(p.x)},{Mathf.RoundToInt(p.z)}";
    }

    private static DistantLands.Cozy.CozyEcosystem[] Ecosystems()
    {
        try { return UnityEngine.Object.FindObjectsOfType<DistantLands.Cozy.CozyEcosystem>(); }
        catch { return Array.Empty<DistantLands.Cozy.CozyEcosystem>(); }
    }

    private static readonly System.Collections.Generic.Dictionary<string, DistantLands.Cozy.Data.WeatherProfile> Profiles = new();
    private static float _profilesAt = -100f;

    /// <summary>A weather profile by name (all loaded ones looked through once, then remembered).</summary>
    private static DistantLands.Cozy.Data.WeatherProfile Profile(string name)
    {
        if (Profiles.TryGetValue(name, out var known) && (known != null || Time.unscaledTime < _profilesAt + 10f)) return known;
        _profilesAt = Time.unscaledTime;
        foreach (var p in Resources.FindObjectsOfTypeAll<DistantLands.Cozy.Data.WeatherProfile>()) if (p != null) Profiles[p.name] = p;
        if (!Profiles.ContainsKey(name)) Profiles[name] = null; // not loaded: looked for again in a while
        return Profiles[name];
    }

    private static void KeepWeather()
    {
        if (StableHash.Of(Dungeons.Place) != _hostWeatherScene) return;
        if (HostEcos.Count > 0) return; // FollowEcosystems, every frame
        if (string.IsNullOrEmpty(_hostWeather)) return;
        var cozy = Cozy;
        if (cozy == null || SameSky(_hostWeather)) return;
        try
        {
            foreach (var p in Resources.FindObjectsOfTypeAll<DistantLands.Cozy.Data.WeatherProfile>())
            {
                if (p == null || p.name != _hostWeather) continue;
                cozy.SetWeather(p, WeatherTransition);
                Plugin.L.LogInfo($"Weather: '{_hostWeather}' (the host's)");
                return;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Weather: " + e.Message); }
    }

    /// <summary>
    /// Every ecosystem here follows the host's of the same key. Its own forecast is stopped (its timer never runs out,
    /// its transitions are dropped) and its blend weights move each frame toward the host's: a sky that changes as the
    /// host's does. (The game's own SetWeather starts a transition per profile; two in a row fight and can zero the blend.)
    /// </summary>
    private static DistantLands.Cozy.CozyEcosystem[] _ecos = Array.Empty<DistantLands.Cozy.CozyEcosystem>();
    private static float _nextEcoScan;
    private const float WeightPerSecond = 0.25f; // a full change in about 4 s

    public static void FollowEcosystems()
    {
        var s = CoopManager.Session;
        if (s == null || s.IsHost || HostEcos.Count == 0 || !GameInfo.InWorld) return;
        if (StableHash.Of(Dungeons.Place) != _hostWeatherScene) { Release(); return; } // the host's sky is another scene's
        if (Time.unscaledTime >= _nextEcoScan) { _nextEcoScan = Time.unscaledTime + 5f; _ecos = Ecosystems(); }
        // just arrived (logged in, another scene): the host's sky at once, no transition from this machine's own
        string place = Dungeons.Place;
        if (place != _followedPlace) { _followedPlace = place; _snap = true; Taken.Clear(); _ecos = Ecosystems(); _nextEcoScan = Time.unscaledTime + 5f; }
        float step = _snap ? 1f : WeightPerSecond * Time.deltaTime;
        bool followed = false;
        try
        {
            foreach (var e in _ecos)
            {
                if (e == null || !HostEcos.TryGetValue(Key(e), out var want)) continue;
                if (Taken.Add(e.Pointer)) e.StopAllCoroutines();
                followed = true;
                e.weatherTimer = 1e6f;
                if (!string.IsNullOrEmpty(want.weather) && (e.currentWeather == null || e.currentWeather.name != want.weather))
                {
                    var p = Profile(want.weather);
                    if (p != null) { e.currentWeather = p; e.weatherChangeCheck = p; }
                }
                var list = e.weightedWeatherProfiles;
                if (list == null) continue;
                foreach (var (name, weight) in want.weights)
                {
                    DistantLands.Cozy.CozyEcosystem.WeightedWeather entry = null;
                    foreach (var w in list) if (w?.profile != null && w.profile.name == name) { entry = w; break; }
                    if (entry == null)
                    {
                        var p = Profile(name);
                        if (p == null) continue;
                        entry = new DistantLands.Cozy.CozyEcosystem.WeightedWeather { profile = p, weight = 0f };
                        list.Add(entry);
                    }
                    entry.weight = Mathf.MoveTowards(entry.weight, weight, step);
                    entry.transitioning = !Mathf.Approximately(entry.weight, weight);
                }
                foreach (var w in list)
                {
                    if (w?.profile == null || want.weights.Exists(x => x.profile == w.profile.name)) continue;
                    w.weight = Mathf.MoveTowards(w.weight, 0f, step);
                    w.transitioning = w.weight > 0f;
                }
            }
            if (followed && _snap) { _snap = false; Plugin.L.LogInfo("Weather: the host's sky, set at once on arrival"); }
        }
        catch (Exception ex) { Plugin.L.LogWarning("Weather follow: " + ex.Message); }
    }

    private static string _followedPlace = "";
    private static bool _snap;

    /// <summary>Not following the host here (any more): the ecosystems stopped here run their own forecast again.</summary>
    private static void Release()
    {
        if (Taken.Count == 0) return;
        try { foreach (var e in _ecos) if (e != null && Taken.Contains(e.Pointer)) e.weatherTimer = 0f; } // next forecast now
        catch (Exception ex) { Plugin.L.LogWarning("Weather release: " + ex.Message); }
        Taken.Clear();
        _followedPlace = ""; // back with the host: their sky at once again
    }

    public static void Reset() { Release(); _followedPlace = ""; HostEcos.Clear(); _ecos = Array.Empty<DistantLands.Cozy.CozyEcosystem>(); }
}
