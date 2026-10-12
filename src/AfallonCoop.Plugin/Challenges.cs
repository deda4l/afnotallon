using System;
using System.Text;
using BLINK.RPGBuilder.Challenges;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Challenge stones (the game's challenge scenes). The scene runs on its own clock from the moment it loads: its
/// Timer counts down, its animators bring the waves, the reward chests and the way out at set times. A group's
/// players load it at different moments, so whoever comes in asks the others there how long it has been running and
/// moves its own clock forward to theirs (once, on arrival). The mobs are the room owner's as everywhere; the
/// objectives' counters are each player's own, as in the game.
/// </summary>
internal static class Challenges
{
    public const byte Kind = 36;
    private const byte OpAsk = 1, OpClock = 2;
    private const float AskAfter = 2f, Slack = 1f, Step = 0.5f;
    private static readonly AfallonCoop.Protocol.NetWriter W = new();

    private static string _place = "";
    private static float _since = -1f; // when this challenge's clock started here (moved back by the skips)
    private static float _askAt = -1f;

    private static bool InChallenge
    {
        get { try { return ChallengeStoneParty.IsInside; } catch { return false; } }
    }

    /// <summary>How long this challenge has been running here.</summary>
    private static float Elapsed => _since < 0f ? -1f : Time.time - _since;

    private static bool FromHere(byte from)
    {
        var s = CoopManager.Session;
        return s != null && Dungeons.InDungeon && s.InSameGroup(s.LocalId, from)
            && s.Players.TryGetValue(from, out var p) && p.Scene == Dungeons.Place;
    }

    private static bool AnyoneHere()
    {
        var s = CoopManager.Session;
        if (s?.MyGroup == null) return false;
        foreach (var id in s.MyGroup.Players) if (id != s.LocalId && FromHere(id)) return true;
        return false;
    }

    public static void Update()
    {
        if (!GameInfo.InWorld) return;
        var place = Dungeons.Place;
        if (place == _place) { if (_askAt >= 0f && Time.unscaledTime >= _askAt) Ask(); return; }
        _place = place;
        bool inside = InChallenge;
        _since = inside ? Time.time : -1f;
        _askAt = inside ? Time.unscaledTime + AskAfter : -1f;
    }

    private static void Ask()
    {
        _askAt = -1f;
        if (!CoopManager.Connected || !AnyoneHere()) return;
        W.Reset(); W.U8(Kind); W.U8(OpAsk);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnAsk(byte from)
    {
        if (!FromHere(from) || Elapsed < 0f) return;
        W.Reset(); W.U8(Kind); W.U8(OpClock); W.U8(from); W.F32(Elapsed);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnClock(byte from, byte to, float theirs)
    {
        if (to != CoopManager.Session?.LocalId || !FromHere(from) || Elapsed < 0f) return;
        float behind = theirs - Elapsed;
        if (behind <= Slack) return;
        Skip(behind);
        Plugin.L.LogInfo($"Challenge: clock moved {behind:0.0}s forward to {Groups.Name(from)}'s");
    }

    /// <summary>The scene's own clocks moved forward: its timers and its animators (as if that time had passed here).</summary>
    private static void Skip(float seconds)
    {
        _since -= seconds;
        foreach (var t in Resources.FindObjectsOfTypeAll<Timer>())
            if (t != null && Here(t.gameObject)) t.remainingTime -= seconds;
        foreach (var an in Resources.FindObjectsOfTypeAll<Animator>())
        {
            if (an == null || !Here(an.gameObject) || !an.isActiveAndEnabled || an.runtimeAnimatorController == null
                || an.GetComponentInParent<BLINK.RPGBuilder.Combat.CombatEntity>() != null) continue;
            // in steps, so each state's exit and every keyed switch on the way are passed as they would be
            for (float left = seconds; left > 0f; left -= Step) an.Update(Mathf.Min(Step, left));
        }
    }

    public static void Receive(byte from, ref AfallonCoop.Protocol.NetReader r)
    {
        switch (r.U8())
        {
            case OpAsk: OnAsk(from); break;
            case OpClock: { byte to = r.U8(); OnClock(from, to, r.F32()); break; }
        }
    }

    public static string Dev(string arg = "")
    {
        if (arg.StartsWith("skip ")) { Skip(float.Parse(arg.Substring(5), System.Globalization.CultureInfo.InvariantCulture)); return $"elapsed {Elapsed:0.0}s"; }
        if (arg == "clock") return $"inside={InChallenge} place='{_place}' elapsed={Elapsed:0.0}s";
        if (arg == "types") return Types();
        if (arg.StartsWith("targets ")) return Targets(arg.Substring(8));
        _under = null;
        if (arg.StartsWith("world ")) { var a = arg.Substring(6); int sp = a.IndexOf(' '); _under = sp < 0 ? a : a.Substring(0, sp); arg = sp < 0 ? "logic" : a.Substring(sp + 1); } // world <path part, no spaces> [logic | tree <part> [depth]]: the same for any objects under that path
        try
        {
            if (arg == "logic") return Logic();
            if (arg.StartsWith("tree ")) return Tree(arg.Substring(5));
        }
        finally { _under = null; }
        var sb = new StringBuilder();
        sb.Append("scenes:");
        for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++) sb.Append($" '{UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).name}'");
        sb.Append('\n');
        try { sb.Append($"inside={ChallengeStoneParty.IsInside} waiting={ChallengeStoneParty.Waiting?.Count ?? 0}\n"); } catch (Exception e) { sb.Append("party: " + e.Message + "\n"); }
        foreach (var h in Resources.FindObjectsOfTypeAll<ChallengeStoneHUD>())
        {
            if (h == null || !h.gameObject.scene.IsValid()) continue;
            sb.Append($"HUD '{h.name}' active={h.gameObject.activeInHierarchy} kind={h.Kind} title='{h.Title}' objective='{h.Objective}' duration={h.Duration} boss={h.BossID}");
            if (h.TimerSource != null) sb.Append($" timer '{Path(h.TimerSource.transform)}' remaining={h.TimerSource.RemainingTime:0.0}");
            if (h.Milestones != null) sb.Append(" milestones=" + string.Join("/", h.Milestones));
            if (h.CounterSources != null) foreach (var c in h.CounterSources) if (c != null) sb.Append($"\n  counter '{Path(c.transform)}' = '{c.text}'");
            if (h.CompletionSources != null) foreach (var g in h.CompletionSources) if (g != null) sb.Append($"\n  completion '{Path(g.transform)}' active={g.activeInHierarchy}");
            sb.Append('\n');
        }
        // the stones and whatever else names itself a challenge
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null || !t.gameObject.scene.IsValid() || t.name.IndexOf("challenge", StringComparison.OrdinalIgnoreCase) < 0) continue;
            sb.Append($"obj '{Path(t)}' at {t.position} active={t.gameObject.activeInHierarchy} [");
            foreach (var c in t.GetComponents<Component>()) if (c != null) sb.Append(c.GetIl2CppType().Name).Append(' ');
            sb.Append("]\n");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "nothing";
    }

    /// <summary>Every behaviour type in the loaded scenes (not the game's managers), with how many.</summary>
    private static string Types()
    {
        var n = new System.Collections.Generic.SortedDictionary<string, int>();
        foreach (var b in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
        {
            if (b == null || !Seen(b.gameObject)) continue;
            var t = b.GetIl2CppType().FullName;
            n[t] = n.TryGetValue(t, out var c) ? c + 1 : 1;
        }
        var sb = new StringBuilder();
        foreach (var kv in n) sb.Append(kv.Key).Append(' ').Append(kv.Value).Append("; ");
        return sb.ToString();
    }

    /// <summary>Objects whose path has that part [depth]: their components and children.</summary>
    private static string Tree(string arg)
    {
        var a = arg.Split(' ');
        int depth = a.Length > 1 && int.TryParse(a[^1], out var d) ? d : 2;
        string part = a.Length > 1 && int.TryParse(a[^1], out _) ? string.Join(" ", a[..^1]) : arg;
        var sb = new StringBuilder();
        int shown = 0;
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
        {
            if (t == null || !Seen(t.gameObject) || Path(t).IndexOf(part, StringComparison.OrdinalIgnoreCase) < 0) continue;
            if (t.parent != null && Path(t.parent).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0) continue; // its parent is listed
            Walk(t, depth, 0, sb);
            if (++shown >= 8) break;
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "nothing";
    }

    private static void Walk(Transform t, int depth, int level, StringBuilder sb)
    {
        sb.Append(' ', level * 2).Append(level == 0 ? Path(t) : t.name).Append(t.gameObject.activeSelf ? "" : " (off)").Append(" [");
        foreach (var c in t.GetComponents<Component>()) if (c != null && c.GetIl2CppType().Name is not ("Transform" or "RectTransform" or "CanvasRenderer")) sb.Append(c.GetIl2CppType().Name).Append(' ');
        sb.Append("]\n");
        if (level >= depth) { if (t.childCount > 0) sb.Append(' ', level * 2 + 2).Append($"... {t.childCount} children\n"); return; }
        for (int i = 0; i < t.childCount; i++) Walk(t.GetChild(i), depth, level + 1, sb);
    }

    /// <summary>What switches things in the challenge scene: requirement-driven objects, animators, interactables.</summary>
    private static string Logic()
    {
        var sb = new StringBuilder();
        foreach (var a in Resources.FindObjectsOfTypeAll<ActiveRequirement>())
        {
            if (a == null || !Seen(a.gameObject)) continue;
            sb.Append($"active-req '{Path(a.transform)}' -> '{(a.TargetObject != null ? Path(a.TargetObject.transform) : "-")}' on={a.TargetObject?.activeSelf} src={a.Source} every={a.CheckEveryNFrames}: ");
            Reqs(a.Source == ActiveRequirement.RequirementSource.Template ? a.ActivationRequirement?.Requirements : a.RequirementGroups, sb);
            sb.Append('\n');
        }
        foreach (var a in Resources.FindObjectsOfTypeAll<DisableRequirement>())
        {
            if (a == null || !Seen(a.gameObject)) continue;
            sb.Append($"disable-req '{Path(a.transform)}' -> '{(a.TargetObject != null ? Path(a.TargetObject.transform) : "-")}': ");
            Reqs(a.ActivationRequirement?.Requirements, sb);
            sb.Append('\n');
        }
        foreach (var an in Resources.FindObjectsOfTypeAll<Animator>())
        {
            if (an == null || !Seen(an.gameObject) || an.GetComponent<BLINK.RPGBuilder.Combat.CombatEntity>() != null) continue;
            var c = an.runtimeAnimatorController;
            sb.Append($"animator '{Path(an.transform)}' enabled={an.enabled} ctrl='{c?.name}'");
            if (c != null) foreach (var clip in c.animationClips) if (clip != null) sb.Append($" clip '{clip.name}' {clip.length:0.#}s events={clip.events.Length}");
            try { var st = an.GetCurrentAnimatorStateInfo(0); sb.Append($" t={st.normalizedTime:0.00}"); } catch { }
            sb.Append('\n');
        }
        foreach (var ra in Resources.FindObjectsOfTypeAll<RandomActivator>())
        {
            if (ra == null || !Seen(ra.gameObject)) continue;
            sb.Append($"random '{Path(ra.transform)}' enables {ra.numberToEnable} of:");
            if (ra.gameObjects != null) foreach (var g in ra.gameObjects) if (g != null) sb.Append($" '{g.name}'{(g.activeSelf ? "*" : "")}");
            sb.Append('\n');
        }
        foreach (var io in Resources.FindObjectsOfTypeAll<BLINK.RPGBuilder.World.InteractableObject>())
        {
            if (io == null || !Seen(io.gameObject) || io.name.StartsWith("Exploding mushroom (")) continue;
            sb.Append($"interactable '{Path(io.transform)}' active={io.gameObject.activeInHierarchy}\n");
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "nothing";
    }

    /// <summary>Every switch in the loaded scenes (requirement, timed, trigger, random activators) aimed at an object with that path part.</summary>
    private static string Targets(string part)
    {
        var sb = new StringBuilder();
        bool Hit(GameObject g) => g != null && Path(g.transform).IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
        string P(GameObject g) => g != null ? Path(g.transform) : "-";
        foreach (var a in Resources.FindObjectsOfTypeAll<ActiveRequirement>())
            if (a != null && Hit(a.TargetObject) && a.gameObject.scene.IsValid()) { sb.Append($"active-req '{P(a.gameObject)}' -> '{P(a.TargetObject)}': "); Reqs(a.Source == ActiveRequirement.RequirementSource.Template ? a.ActivationRequirement?.Requirements : a.RequirementGroups, sb); sb.Append('\n'); }
        foreach (var a in Resources.FindObjectsOfTypeAll<TimedActiveRequirement>())
            if (a != null && Hit(a.TargetObject) && a.gameObject.scene.IsValid()) { sb.Append($"timed-req '{P(a.gameObject)}' -> '{P(a.TargetObject)}' for {a.ActivationDurationSeconds}s: "); Reqs(a.ActivationRequirement?.Requirements, sb); sb.Append('\n'); }
        foreach (var a in Resources.FindObjectsOfTypeAll<DisableRequirement>())
            if (a != null && Hit(a.TargetObject) && a.gameObject.scene.IsValid()) { sb.Append($"disable-req '{P(a.gameObject)}' -> '{P(a.TargetObject)}': "); Reqs(a.ActivationRequirement?.Requirements, sb); sb.Append('\n'); }
        foreach (var a in Resources.FindObjectsOfTypeAll<EnhancedInteractableObject>())
            if (a != null && Hit(a.TargetObject) && a.gameObject.scene.IsValid()) { sb.Append($"enhanced '{P(a.gameObject)}' -> '{P(a.TargetObject)}' on: "); Reqs(a.ActivationRequirements?.Requirements, sb); sb.Append(" off: "); Reqs(a.DeactivationRequirements?.Requirements, sb); sb.Append('\n'); }
        foreach (var a in Resources.FindObjectsOfTypeAll<EnableOnTrigger>())
            if (a != null && Hit(a.targetObject) && a.gameObject.scene.IsValid()) sb.Append($"on-trigger '{P(a.gameObject)}' tag '{a.triggerTag}' -> '{P(a.targetObject)}'\n");
        foreach (var a in Resources.FindObjectsOfTypeAll<RandomActivator>())
        {
            if (a == null || !a.gameObject.scene.IsValid() || a.gameObjects == null) continue;
            foreach (var g in a.gameObjects) if (Hit(g)) { sb.Append($"random '{P(a.gameObject)}' ({a.numberToEnable} of {a.gameObjects.Count}) -> '{P(g)}'\n"); break; }
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : "nothing aims there";
    }

    private static void Reqs(Il2CppSystem.Collections.Generic.List<RequirementsData.RequirementGroup> groups, StringBuilder sb)
    {
        if (groups == null) { sb.Append("none"); return; }
        foreach (var g in groups)
        {
            if (g?.Requirements == null) continue;
            sb.Append('{');
            foreach (var r in g.Requirements)
                if (r != null) sb.Append($"{r.type} {r.condition} npc={r.NPCID} effect={r.EffectID} quest={r.QuestID} scene={r.GameSceneID} item={r.ItemID} state={r.State} cmp={r.Comparison} val={r.Value} ent={r.Entity}; ");
            sb.Append("} ");
        }
    }

    private static string _under; // dev: inspect objects under this path instead of the challenge scene
    private static bool Seen(GameObject g) => _under == null ? Here(g) : g.scene.IsValid() && Path(g.transform).IndexOf(_under.Replace('_', ' '), StringComparison.OrdinalIgnoreCase) >= 0;

    private static bool Here(GameObject g) => g.scene.IsValid() && g.scene.name is not ("Coalway outdoors" or "DontDestroyOnLoad"); // the challenge scene, loaded over the world

    private static string Path(Transform t)
    {
        var s = t.name;
        for (var p = t.parent; p != null; p = p.parent) s = p.name + "/" + s;
        return s;
    }
}
