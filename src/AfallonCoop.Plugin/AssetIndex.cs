using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Templates;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// ID → asset tables for game database entries (animation / visual effect / sound templates, node sockets),
/// built once when the player enters the world, so the network can refer to assets by their database ID.
/// Peers are guaranteed to run the same game build (checked at handshake), so IDs mean the same thing everywhere.
/// IDs that turn out not to be unique within a type are referred to by name instead.
/// </summary>
internal static class AssetIndex
{
    public static readonly Index<AnimationTemplate> Animations = new();
    public static readonly Index<VisualEffectTemplate> Effects = new();
    public static readonly Index<SoundTemplate> Sounds = new();
    public static readonly Index<RPGBNodeSocket> Sockets = new();

    public static bool Built { get; private set; }

    public static void BuildIfNeeded()
    {
        if (Built) return;
        Built = true;
        Prof.Run("assetindex.build", () =>
        {
            Animations.Build(); Effects.Build(); Sounds.Build(); Sockets.Build();
        });
        Plugin.L.LogInfo($"Asset index: {Animations.Summary} animations, {Effects.Summary} effects, {Sounds.Summary} sounds, {Sockets.Summary} sockets");
    }

    public sealed class Index<T> where T : RPGBuilderDatabaseEntry
    {
        private readonly Dictionary<int, T> _byId = new();
        private readonly Dictionary<string, T> _byName = new();
        private readonly HashSet<int> _ambiguous = new();
        private float _rescannedAt = -100f;

        public string Summary => $"{_byId.Count}" + (_ambiguous.Count > 0 ? $" ({_ambiguous.Count} ids shared, by name)" : "");

        public void Build()
        {
            _byId.Clear(); _byName.Clear(); _ambiguous.Clear();
            Prof.Count("scan." + typeof(T).Name);
            foreach (var a in Resources.FindObjectsOfTypeAll<T>()) Add(a);
        }

        private void Add(T a)
        {
            if (a == null) return;
            _byName[a.name] = a;
            if (_ambiguous.Contains(a.ID)) return;
            if (_byId.TryGetValue(a.ID, out var other) && other.Pointer != a.Pointer)
            {
                _byId.Remove(a.ID);
                _ambiguous.Add(a.ID);
                return;
            }
            _byId[a.ID] = a;
        }

        /// <summary>Wire form: (true, id) normally, (false, name) when the id is not unique.</summary>
        public void Write(NetWriter w, T asset)
        {
            if (asset != null && !_ambiguous.Contains(asset.ID) && _byId.ContainsKey(asset.ID))
            {
                w.Bool(true); w.I32(asset.ID);
            }
            else
            {
                w.Bool(false); w.Str(asset != null ? asset.name : "");
            }
        }

        public T Read(ref NetReader r)
        {
            if (r.Bool()) return ById(r.I32());
            var name = r.Str();
            if (name.Length == 0) return null;
            if (_byName.TryGetValue(name, out var a) && a != null) return a;
            Rescan();
            return _byName.TryGetValue(name, out a) ? a : null;
        }

        public T ById(int id)
        {
            if (_byId.TryGetValue(id, out var a) && a != null) return a;
            Rescan();
            return _byId.TryGetValue(id, out a) ? a : null;
        }

        /// <summary>Assets loaded after the index was built (rare): one extra pass, at most every 10 s.</summary>
        private void Rescan()
        {
            if (Time.unscaledTime - _rescannedAt < 10f) return;
            _rescannedAt = Time.unscaledTime;
            Prof.Count("rescan." + typeof(T).Name);
            foreach (var a in Resources.FindObjectsOfTypeAll<T>()) if (a != null && !_byName.ContainsKey(a.name)) Add(a);
        }
    }
}
