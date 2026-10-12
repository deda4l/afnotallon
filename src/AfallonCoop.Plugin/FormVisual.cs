using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// An animal form (a shapeshifting effect) shown on a model that is not the real entity: a bot copy or a player's
/// puppet. These do not run the game's own shapeshifting (it needs a class progression and changes stats, abilities
/// and the action bar), so this repeats only its visual steps, as MobCombatEntity.InitShapeshifting does them: hide
/// the own model, put the rank's form model under the animator (its position and scale), give the animator the form's
/// controller (the combat one while fighting), avatar and settings, rebind. Root motion stays off: the network moves it.
/// </summary>
internal static class FormVisual
{
    private sealed class State
    {
        public int Effect, Rank; public bool Combat, OwnController;
        public GameObject Model;
        public Animator Anim;
        public RuntimeAnimatorController Controller; public Avatar Avatar;
        public AnimatorUpdateMode UpdateMode; public AnimatorCullingMode CullingMode;
        public readonly List<Renderer> Hidden = new();
    }

    private static readonly Dictionary<IntPtr, State> On = new();

    /// <summary>Dev: the animator a form drives on this model, and its controller now.</summary>
    public static string Describe(GameObject root) =>
        root != null && On.TryGetValue(root.Pointer, out var s)
            ? $"anim='{s.Anim?.name}' ctrl='{s.Anim?.runtimeAnimatorController?.name}' model={(s.Model != null)}"
            : "none";

    /// <summary>The form model shown on this root (null = none).</summary>
    public static GameObject ModelOf(GameObject root) => root != null && On.TryGetValue(root.Pointer, out var s) ? s.Model : null;

    public static bool IsIn(GameObject root, out int effect)
    {
        effect = -1;
        if (root == null || !On.TryGetValue(root.Pointer, out var s)) return false;
        effect = s.Effect;
        return true;
    }

    /// <summary>Put this model into that form (effect id, rank index); -1 = back to its own shape.</summary>
    /// <param name="controller">False when something else already follows the owner's animator controller (puppets do,
    /// by name): then only the model and avatar change here.</param>
    public static void Set(GameObject root, Animator anim, int effectId, int rank, bool combat, bool controller = true)
    {
        if (root == null) return;
        On.TryGetValue(root.Pointer, out var cur);
        if (effectId < 0) { if (cur != null) Clear(root); return; }
        if (cur != null && cur.Effect == effectId && cur.Rank == rank)
        {
            if (cur.OwnController && (cur.Combat != combat || cur.Anim != null && cur.Anim.runtimeAnimatorController != Controller(cur, combat)))
                SetController(cur, combat); // the game may put its weapon controller back (stance changes): the form's again
            HideOwn(root, cur); // the own model may have been rebuilt meanwhile (gear change)
            return;
        }
        if (cur != null) Clear(root);

        var all = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects();
        if (!all.ContainsKey(effectId)) return;
        var ranks = all[effectId].ranks;
        if (ranks == null || rank < 0 || rank >= ranks.Count) return;
        var r = ranks[rank];
        if (r?.shapeshiftingModel == null) return;
        anim ??= root.GetComponentInChildren<Animator>();
        var parent = anim != null ? anim.transform : root.transform;

        var s = new State { Effect = effectId, Rank = rank, Anim = anim, OwnController = controller };
        HideOwn(root, s);

        try
        {
            s.Model = Object.Instantiate(r.shapeshiftingModel, parent);
            s.Model.name = "AfCoop_Form";
            s.Model.transform.localPosition = r.shapeshiftingmodelPosition;
            s.Model.transform.localRotation = Quaternion.identity;
            if (r.shapeshiftingmodelScale != Vector3.zero) s.Model.transform.localScale = r.shapeshiftingmodelScale;
            foreach (var a in s.Model.GetComponentsInChildren<Animator>(true)) if (a != anim) Object.Destroy(a); // one animator drives it
            if (anim != null)
            {
                s.Controller = anim.runtimeAnimatorController; s.Avatar = anim.avatar;
                s.UpdateMode = anim.updateMode; s.CullingMode = anim.cullingMode;
                if (r.shapeshiftingAnimatorAvatar != null) anim.avatar = r.shapeshiftingAnimatorAvatar;
                anim.updateMode = r.shapeshiftingAnimatorUpdateMode;
                anim.cullingMode = r.shapeshiftingAnimatorCullingMode;
                anim.applyRootMotion = false;
                On[root.Pointer] = s;
                SetController(s, combat);
            }
            else On[root.Pointer] = s;
        }
        catch (Exception e)
        {
            Plugin.L.LogWarning("Form visual: " + e.Message);
            On[root.Pointer] = s;
            Clear(root);
        }
    }

    private static void HideOwn(GameObject root, State s)
    {
        foreach (var rend in root.GetComponentsInChildren<Renderer>(false))
        {
            if (rend == null || !rend.enabled) continue;
            if (s.Model != null && rend.transform.IsChildOf(s.Model.transform)) continue;
            rend.enabled = false;
            s.Hidden.Add(rend);
        }
    }

    private static RuntimeAnimatorController Controller(State s, bool combat)
    {
        var r = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects()[s.Effect].ranks[s.Rank];
        return combat && r.shapeshiftingAnimatorControllerCombat != null ? r.shapeshiftingAnimatorControllerCombat : r.shapeshiftingAnimatorController;
    }

    private static void SetController(State s, bool combat)
    {
        s.Combat = combat;
        if (s.Anim == null) return;
        if (!s.OwnController) { s.Anim.Rebind(); return; }
        var all = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetEffects();
        var r = all[s.Effect].ranks[s.Rank];
        var ctrl = combat && r.shapeshiftingAnimatorControllerCombat != null ? r.shapeshiftingAnimatorControllerCombat : r.shapeshiftingAnimatorController;
        if (ctrl != null && s.Anim.runtimeAnimatorController != ctrl) s.Anim.runtimeAnimatorController = ctrl;
        s.Anim.Rebind();
    }

    /// <summary>Back to its own shape (the form's model goes, the own renderers and animator settings come back).</summary>
    public static void Clear(GameObject root)
    {
        if (root == null || !On.TryGetValue(root.Pointer, out var s)) return;
        On.Remove(root.Pointer);
        if (s.Model != null) Object.Destroy(s.Model);
        foreach (var rend in s.Hidden) if (rend != null) rend.enabled = true;
        if (s.Anim != null)
        {
            if (s.Controller != null && s.OwnController) s.Anim.runtimeAnimatorController = s.Controller;
            s.Anim.avatar = s.Avatar;
            s.Anim.updateMode = s.UpdateMode; s.Anim.cullingMode = s.CullingMode;
            s.Anim.applyRootMotion = false;
            s.Anim.Rebind();
        }
    }

    /// <summary>Forget models that no longer exist (scene change, despawned copies).</summary>
    public static void Prune()
    {
        List<IntPtr> gone = null;
        foreach (var kv in On) if (kv.Value.Anim == null && kv.Value.Model == null) (gone ??= new()).Add(kv.Key);
        if (gone != null) foreach (var k in gone) On.Remove(k);
    }
}
