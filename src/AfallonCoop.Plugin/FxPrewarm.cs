using System;
using System.Collections.Generic;
using System.Text;
using BLINK.RPGBuilder.Managers;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// The first time a visual effect is shown, the game loads its prefab (a visible hitch). Each player sends the IDs of
/// the effects their known abilities can produce; the others load those prefabs ahead of time, one per frame.
/// </summary>
internal static class FxPrewarm
{
    // ---------------- owner: which effects can my character show ----------------

    private static string _abilitiesSig = "";
    private static List<int> _ids = new();

    /// <summary>IDs of the abilities this character knows (database IDs, unique).</summary>
    public static List<int> LocalAbilityIds()
    {
        var cd = GameInfo.Character;
        if (cd == null || cd.Abilities == null) return _ids;
        var sb = new StringBuilder();
        foreach (var a in cd.Abilities) if (a != null && a.known) sb.Append(a.ID).Append(',');
        var sig = sb.ToString();
        if (sig == _abilitiesSig) return _ids;
        _abilitiesSig = sig;
        var list = new List<int>();
        foreach (var a in cd.Abilities) if (a != null && a.known) list.Add(a.ID);
        _ids = list;
        Plugin.L.LogInfo($"Prewarm: I know {_ids.Count} abilities");
        return _ids;
    }

    // ---------------- others: load their effects ahead of time ----------------

    private static readonly HashSet<int> Done = new();
    private static readonly HashSet<string> Keys = new();
    private static readonly Queue<int> Todo = new();

    public static void Enqueue(IEnumerable<int> ids)
    {
        foreach (var id in ids)
            if (Done.Add(id)) Todo.Enqueue(id);
    }

    public static void Update()
    {
        if (Todo.Count == 0 || !GameInfo.InWorld) return;
        int abilityId = Todo.Dequeue();
        Prof.Run("fx.prewarm", () =>
        {
            foreach (var t in EntryIndex.AbilityEffects(abilityId))
            {
                if (t.PrefabKeys == null) continue;
                foreach (var key in t.PrefabKeys)
                {
                    if (string.IsNullOrEmpty(key) || !Keys.Add(key)) continue;
                    try { SoftAssets.Load<GameObject>(key); Prof.Count("fx.prewarmed"); }
                    catch (Exception e) { Plugin.L.LogWarning($"Prewarm '{key}' failed: {e.Message}"); }
                }
            }
        });
    }
}
