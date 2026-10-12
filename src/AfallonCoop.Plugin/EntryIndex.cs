using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.Templates;

namespace AfallonCoop.Plugin;

/// <summary>
/// Visual/animation/sound entries live inside database records: ability rank N → VisualEffectEntries[i], and the same
/// for status effects. Those records have real, unique database IDs (GameDatabase keys them by ID), while the template
/// assets themselves do not (their ID field is unset in this game). So an entry is referenced as
/// (source, record ID, rank, list, index) and the receiver plays the very same entry object from its own database.
/// Built once; entry objects are never moved (IL2CPP's GC is non-moving), so their pointers are stable keys.
/// </summary>
internal static class EntryIndex
{
    public enum Source : byte { Ability = 1, Effect = 2 }
    public enum ListKind : byte { Vfx = 1, Anim = 2, Sound = 3 }

    public struct Ref
    {
        public Source Src; public int Id; public ushort Rank; public ListKind List; public ushort Index;
        public void Write(NetWriter w) { w.U8((byte)Src); w.I32(Id); w.U16(Rank); w.U8((byte)List); w.U16(Index); }
        public static Ref Read(ref NetReader r) => new()
            { Src = (Source)r.U8(), Id = r.I32(), Rank = r.U16(), List = (ListKind)r.U8(), Index = r.U16() };
    }

    private static readonly Dictionary<IntPtr, Ref> ByEntry = new();
    public static bool Built { get; private set; }
    public static int Count => ByEntry.Count;

    public static void BuildIfNeeded()
    {
        if (Built) return;
        Built = true;
        Prof.Run("entryindex.build", () =>
        {
            try
            {
                var db = GameDatabase.Instance;
                foreach (var kv in db.GetAbilities())
                {
                    var ranks = kv.Value?.ranks;
                    if (ranks == null) continue;
                    for (int r = 0; r < ranks.Count; r++)
                    {
                        var rank = ranks[r];
                        if (rank == null) continue;
                        AddAll(Source.Ability, kv.Key, r, ListKind.Vfx, rank.VisualEffectEntries);
                        AddAll(Source.Ability, kv.Key, r, ListKind.Anim, rank.AnimationEntries);
                        AddAll(Source.Ability, kv.Key, r, ListKind.Sound, rank.SoundEntries);
                    }
                }
                foreach (var kv in db.GetEffects())
                {
                    var ranks = kv.Value?.ranks;
                    if (ranks == null) continue;
                    for (int r = 0; r < ranks.Count; r++)
                    {
                        var rank = ranks[r];
                        if (rank == null) continue;
                        AddAll(Source.Effect, kv.Key, r, ListKind.Vfx, rank.VisualEffectEntries);
                        AddAll(Source.Effect, kv.Key, r, ListKind.Anim, rank.AnimationEntries);
                        AddAll(Source.Effect, kv.Key, r, ListKind.Sound, rank.SoundEntries);
                    }
                }
            }
            catch (Exception e) { Plugin.L.LogWarning("Entry index failed: " + e.Message); }
        });
        Plugin.L.LogInfo($"Entry index: {ByEntry.Count} ability/effect visual, animation and sound entries");
    }

    private static void AddAll<T>(Source src, int id, int rank, ListKind list, Il2CppSystem.Collections.Generic.List<T> entries)
        where T : Il2CppSystem.Object
    {
        if (entries == null) return;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e != null) ByEntry[e.Pointer] = new Ref { Src = src, Id = id, Rank = (ushort)rank, List = list, Index = (ushort)i };
        }
    }

    public static bool TryGet(Il2CppSystem.Object entry, out Ref r)
    {
        r = default;
        return entry != null && ByEntry.TryGetValue(entry.Pointer, out r);
    }

    /// <summary>The same entry object in this machine's database.</summary>
    public static Il2CppSystem.Object Resolve(Ref r)
    {
        try
        {
            var db = GameDatabase.Instance;
            if (r.Src == Source.Ability)
            {
                var all = db.GetAbilities();
                if (!all.ContainsKey(r.Id)) return null;
                var ranks = all[r.Id].ranks;
                if (ranks == null || r.Rank >= ranks.Count) return null;
                var rank = ranks[r.Rank];
                return Pick(rank.VisualEffectEntries, rank.AnimationEntries, rank.SoundEntries, r);
            }
            else
            {
                var all = db.GetEffects();
                if (!all.ContainsKey(r.Id)) return null;
                var ranks = all[r.Id].ranks;
                if (ranks == null || r.Rank >= ranks.Count) return null;
                var rank = ranks[r.Rank];
                return Pick(rank.VisualEffectEntries, rank.AnimationEntries, rank.SoundEntries, r);
            }
        }
        catch { return null; }
    }

    private static Il2CppSystem.Object Pick(
        Il2CppSystem.Collections.Generic.List<VisualEffectEntry> v,
        Il2CppSystem.Collections.Generic.List<AnimationEntry> a,
        Il2CppSystem.Collections.Generic.List<SoundEntry> s, Ref r)
    {
        switch (r.List)
        {
            case ListKind.Vfx: return v != null && r.Index < v.Count ? v[r.Index] : null;
            case ListKind.Anim: return a != null && r.Index < a.Count ? a[r.Index] : null;
            case ListKind.Sound: return s != null && r.Index < s.Count ? s[r.Index] : null;
        }
        return null;
    }

    /// <summary>Visual effect templates an ability can show (all ranks), for preloading.</summary>
    public static IEnumerable<VisualEffectTemplate> AbilityEffects(int abilityId)
    {
        var all = GameDatabase.Instance.GetAbilities();
        if (!all.ContainsKey(abilityId)) yield break;
        var ranks = all[abilityId].ranks;
        if (ranks == null) yield break;
        foreach (var rank in ranks)
        {
            if (rank?.VisualEffectEntries == null) continue;
            foreach (var v in rank.VisualEffectEntries)
                if (v?.Template != null) yield return v.Template;
        }
    }
}
