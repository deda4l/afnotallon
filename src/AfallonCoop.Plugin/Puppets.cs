using System;
using System.Collections.Generic;
using System.Linq;
using AfallonCoop.Protocol;
using AfallonCoop.Session;
using BLINK.RPGBuilder.Characters;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Visual stand-ins for remote players: the remote character's own race/gender prefab with their appearance and gear,
/// stripped down to visual components and driven by interpolated network snapshots.
/// Fallback (prefab unavailable): a stripped copy of the local character's model.
/// </summary>
internal static class Puppets
{
    private sealed class Puppet
    {
        public byte Id;
        public readonly RemoteMotion Motion = new();
        public GameObject Go;
        public Animator Anim;
        public PlayerAppearance Appearance;
        public readonly AnimatorFollower Follow = new();
        public readonly AnimParamState AnimParams = new();
        public bool Visible;
        public string VisualSig;
        public int ControllerHash;
        public bool? Armed;
        public float RetryAt;
        public int DressAtFrame = -1;
        public PlayerInfo DressInfo;
        public string MaterialsApplied;
        public int MaterialsAtFrame = -1;
        public Dictionary<int, GameObject> Parts;
    }

    private static readonly Dictionary<byte, Puppet> All = new();
    private static string _scene = "";
    private static int _sceneHash;
    private static GameObject _holder;

    public static double RenderDelay => 0.12;

    public static bool TryGetVisual(byte id, out GameObject go, out Animator anim)
    {
        go = null; anim = null;
        if (!All.TryGetValue(id, out var p) || !p.Visible || p.Go == null) return false;
        go = p.Go; anim = p.Anim;
        return true;
    }

    /// <summary>Controller and clips playing per layer, for diagnosing pose problems.</summary>
    public static string AnimatorReport(Animator a)
    {
        if (a == null) return "no animator";
        var rc = a.runtimeAnimatorController;
        var sb = new System.Text.StringBuilder();
        sb.Append($"ctrl='{rc?.name}' type={rc?.GetIl2CppType().Name} id={rc?.GetInstanceID()}");
        for (int i = 0; i < a.layerCount; i++)
        {
            sb.Append($" | L{i} w={a.GetLayerWeight(i):0.00} state={a.GetCurrentAnimatorStateInfo(i).fullPathHash}");
            foreach (var ci in a.GetCurrentAnimatorClipInfo(i))
                sb.Append($" [{(ci.clip != null ? ci.clip.name : "NULL")} {ci.weight:0.00}]");
        }
        return sb.ToString();
    }

    public static IEnumerable<(byte id, Transform tr)> Visible()
    {
        foreach (var p in All.Values)
            if (p.Visible && p.Go != null) yield return (p.Id, p.Go.transform);
    }

    private static readonly Dictionary<byte, Dictionary<int, Vital>> LastVitals = new();

    /// <summary>The remote player's last reported vitality stats (health, mana...), by stat id.</summary>
    private static readonly Dictionary<byte, float> VitalsHeard = new();

    /// <summary>When that player's vitals last came (unscaled time; -1 never).</summary>
    public static float VitalsHeardAt(byte id) => VitalsHeard.TryGetValue(id, out var t) ? t : -1f;

    public static IReadOnlyDictionary<int, Vital> Vitals(byte id) => LastVitals.TryGetValue(id, out var v) ? v : null;

    /// <summary>The part of a puppet that sits on a mount (the game re-parents the character's armature the same way).</summary>
    public static Transform RiderOf(byte id)
    {
        if (!All.TryGetValue(id, out var p) || p.Appearance == null) return null;
        try { return p.Appearance.armatureParentGO != null ? p.Appearance.armatureParentGO.transform : null; } catch { return null; }
    }

    /// <summary>The remote player has weapons drawn (combat stance), as last reported.</summary>
    public static bool IsArmed(byte id) =>
        All.TryGetValue(id, out var p) && p.Motion.Latest != null && p.Motion.Latest.Flags.HasFlag(StateFlags.Armed);

    /// <summary>The remote player's character is dead (as last reported).</summary>
    public static bool IsDead(byte id) =>
        All.TryGetValue(id, out var p) && p.Motion.Latest != null && p.Motion.Latest.Flags.HasFlag(StateFlags.Dead);

    /// <summary>The time on that player's own clock of the pose shown of them now (NaN if none).</summary>
    public static double ViewTime(byte id, double now) => All.TryGetValue(id, out var p) ? p.Motion.ViewTime(now) : double.NaN;

    public static bool TryGetPosition(byte id, out Vector3 pos)
    {
        pos = default;
        if (!All.TryGetValue(id, out var p) || p.Motion.Latest == null || !p.Motion.Latest.InWorld) return false;
        var l = p.Motion.Latest;
        if (l.SceneHash != _sceneHash) return false;
        pos = new Vector3(l.PosX, l.PosY, l.PosZ);
        return true;
    }

    /// <summary>Animator state of a puppet vs. what the owner last sent, for diagnostics.</summary>
    public static string Describe(byte id)
    {
        if (!All.TryGetValue(id, out var p) || p.Anim == null || p.Motion.Latest == null) return "none";
        var a = p.Anim;
        var m = p.Motion.Latest;
        var sb = new System.Text.StringBuilder();
        sb.Append($" model='{p.Go.name}' ctrl='{a.runtimeAnimatorController?.name}' match={p.ControllerHash == m.ControllerHash} armed={m.Flags.HasFlag(StateFlags.Armed)}");
        for (int i = 0; i < Math.Min(a.layerCount, m.Layers.Count); i++)
            sb.Append($" L{i}:{(a.GetCurrentAnimatorStateInfo(i).fullPathHash == m.Layers[i].StateHash ? "ok" : "diff")}");
        return sb.ToString();
    }

    public static void OnState(PlayerStateMsg m, double now)
    {
        if (m.Vitals.Count > 0)
        {
            if (!LastVitals.TryGetValue(m.Id, out var v)) LastVitals[m.Id] = v = new Dictionary<int, Vital>();
            foreach (var x in m.Vitals) v[x.Stat] = x;
            VitalsHeard[m.Id] = Time.unscaledTime;
        }
        if (!All.TryGetValue(m.Id, out var p)) All[m.Id] = p = new Puppet { Id = m.Id };
        p.Motion.Add(m, now);
        p.AnimParams.Apply(m.Params, m.AnimKeyframe);
    }

    /// <summary>Character, appearance or gear changed: rebuild the model on the next frame.</summary>
    public static void OnInfo(PlayerInfo info)
    {
        if (All.TryGetValue(info.Id, out var q) && q.Go != null && q.MaterialsApplied != null && q.MaterialsApplied != info.Materials)
            q.MaterialsAtFrame = Time.frameCount + 1;
        if (All.TryGetValue(info.Id, out var p) && p.Go != null && p.VisualSig != info.VisualSignature)
        {
            Plugin.L.LogInfo($"Player #{info.Id} changed look/gear, rebuilding model");
            Destroy(p);
        }
    }

    public static void Remove(byte id)
    {
        if (All.TryGetValue(id, out var p)) { Destroy(p); All.Remove(id); }
        LastVitals.Remove(id); VitalsHeard.Remove(id); // the id goes to the next player who joins
    }

    public static void Clear()
    {
        foreach (var p in All.Values) Destroy(p);
        All.Clear();
    }

    private static void Destroy(Puppet p)
    {
        if (p.Go != null) PlayerActions.BeforeDestroy(p.Go);
        if (p.Go != null) { MaterialSnapshot.Forget(p.Go.transform); Translucency.Forget(p.Go); Object.Destroy(p.Go); }
        p.Go = null; p.Anim = null; p.Appearance = null; p.Visible = false; p.VisualSig = null; p.Armed = null; p.ControllerHash = 0;
    }

    public static void Update(double now)
    {
        var scene = Dungeons.Place;
        if (scene != _scene)
        {
            // Scene objects are destroyed with the scene; forget our references.
            foreach (var p in All.Values) { p.Go = null; p.Anim = null; p.Appearance = null; p.Visible = false; p.VisualSig = null; }
            _holder = null;
            _scene = scene;
            _sceneHash = StableHash.Of(scene);
        }
    }

    public static void LateUpdate(double now)
    {
        bool localInWorld = GameInfo.InWorld;
        var session = CoopManager.Session;
        foreach (var p in All.Values)
        {
            var latest = p.Motion.Latest;
            bool show = localInWorld && latest != null && latest.InWorld && latest.SceneHash == _sceneHash;
            if (!show)
            {
                if (p.Go != null && p.Visible) p.Go.SetActive(false);
                p.Visible = false;
                continue;
            }
            if (p.Go == null)
            {
                if (Time.unscaledTime < p.RetryAt) continue;
                if (session == null || !session.Players.TryGetValue(p.Id, out var info) || !Spawn(p, info))
                {
                    p.RetryAt = Time.unscaledTime + 2f;
                    continue;
                }
            }
            if (!p.Visible) { p.Go.SetActive(true); p.Visible = true; }

            if (!PlayerActions.Seated(p.Id) && p.Motion.Sample(now, out var pos, out var rotY)) // a rider hangs on its animal
            {
                // The interpolator already smooths; large jumps (teleports, respawns) simply snap.
                var tr = p.Go.transform;
                tr.position = new Vector3(pos.X, pos.Y, pos.Z);
                tr.rotation = Quaternion.Euler(0f, rotY, 0f);
            }
            if (p.DressAtFrame >= 0 && Time.frameCount >= p.DressAtFrame) { Dress(p); p.MaterialsAtFrame = Time.frameCount + 3; }
            if (p.MaterialsAtFrame >= 0 && Time.frameCount >= p.MaterialsAtFrame && session != null
                && !Translucency.IsOn(p.Go) // ghost copies are swapped in; apply real materials once they are back
                && session.Players.TryGetValue(p.Id, out var cur) && cur.Materials != p.MaterialsApplied)
            {
                Prof.Count("materials.apply");
                int n = MaterialSnapshot.Apply(p.Go.transform.GetComponentInChildren<Animator>()?.transform ?? p.Go.transform, cur.Materials);
                p.MaterialsApplied = cur.Materials;
                Plugin.L.LogInfo($"Applied {n} material sets to #{p.Id}");
            }
            ApplyGearState(p, latest);
            ModelParts.Apply(p.Parts, latest.ActiveParts);
            Translucency.Set(p.Go, latest.Flags.HasFlag(StateFlags.Stealth));
            ApplyController(p, latest);
            ApplyAnimator(p, latest, Time.deltaTime);
        }
    }

    // ---------------- animation ----------------

    private static readonly Dictionary<int, RuntimeAnimatorController> Controllers = new();
    private static float _controllersScannedAt = -100f;

    internal static RuntimeAnimatorController FindController(int hash)
    {
        if (Controllers.TryGetValue(hash, out var c) && c != null) return c;
        if (Time.unscaledTime - _controllersScannedAt < 10f) return null;
        _controllersScannedAt = Time.unscaledTime;
        Prof.Count("scan.Controller");
        foreach (var rc in Resources.FindObjectsOfTypeAll<RuntimeAnimatorController>())
            if (rc != null) Controllers[StableHash.Of(rc.name)] = rc;
        return Controllers.TryGetValue(hash, out c) ? c : null;
    }

    private static void ApplyController(Puppet p, PlayerStateMsg m)
    {
        if (p.Anim == null || m.ControllerHash == 0 || p.ControllerHash == m.ControllerHash) return;
        var ctrl = FindController(m.ControllerHash);
        if (ctrl == null) return; // not loaded on this machine yet; params still drive the base controller
        p.Anim.runtimeAnimatorController = ctrl;
        p.ControllerHash = m.ControllerHash;
        p.Follow.Reset();
    }

    private static void ApplyGearState(Puppet p, PlayerStateMsg m)
    {
        if (p.Appearance == null) return;
        bool armed = m.Flags.HasFlag(StateFlags.Armed);
        if (p.Armed == armed) return;
        p.Armed = armed;
        p.MaterialsAtFrame = Time.frameCount + 2; // stowing/drawing can recompose materials
        p.MaterialsApplied = null;
        try { p.Appearance.UpdateWeaponStates(armed); }
        catch (Exception e) { Plugin.L.LogWarning("UpdateWeaponStates failed: " + e.Message); p.Appearance = null; }
    }

    private static void ApplyAnimator(Puppet p, PlayerStateMsg m, float dt) => p.Follow.Apply(p.Anim, p.AnimParams.Current, m.Layers, dt);

    // ---------------- building ----------------

    private static readonly HashSet<string> KeepEngineTypes = new()
    {
        "UnityEngine.Transform", "UnityEngine.Animator",
        "UnityEngine.SkinnedMeshRenderer", "UnityEngine.MeshRenderer", "UnityEngine.MeshFilter", "UnityEngine.LODGroup",
        "UnityEngine.ParticleSystem", "UnityEngine.ParticleSystemRenderer", "UnityEngine.TrailRenderer",
    };

    /// <summary>Game scripts that only deal with looks: kept so the game itself can dress the model.</summary>
    private static readonly HashSet<string> KeepVisualScripts = new()
    {
        "PlayerAppearance", "CharacterAppearanceModifier", "NHPieceCoverage", "NHBodyPart", "BodyPart",
        "NodeSockets", "JawKeeper", "WeaponVisualSockets", "StowedWith",
        "NHAutoCulling", "AutoBodyCulling", "ArmorPieceCoverage",
    };

    private static bool Spawn(Puppet p, PlayerInfo info)
    {
        if (_holder == null)
        {
            _holder = new GameObject("AfCoop_PuppetHolder");
            _holder.SetActive(false);
        }
        if (info.RaceId < 0) return false; // character info not here yet; retry shortly instead of a placeholder
        GameObject go = null;
        try { go = BuildFromRace(p, info); }
        catch (Exception e) { Plugin.L.LogWarning($"Model for #{p.Id} from race prefab failed: {e.Message}"); }
        if (go == null)
        {
            go = BuildFromLocalCopy();
            if (go == null) return false;
            Plugin.L.LogInfo($"Player #{p.Id}: using a copy of the local model (fallback)");
        }

        go.name = $"AfCoop_Player_{p.Id}";
        p.Go = go;
        p.Anim = go.GetComponentInChildren<Animator>();
        if (p.Anim != null)
        {
            p.Anim.applyRootMotion = false;
            p.Anim.cullingMode = AnimatorCullingMode.AlwaysAnimate;
        }
        p.Follow.Reset();
        p.ControllerHash = p.Anim != null && p.Anim.runtimeAnimatorController != null ? StableHash.Of(p.Anim.runtimeAnimatorController.name) : 0;
        p.VisualSig = info.VisualSignature;
        p.Visible = false;
        go.SetActive(false);
        return true;
    }

    private static GameObject BuildFromRace(Puppet p, PlayerInfo info)
    {
        if (info.RaceId < 0) return null;
        var key = GameInfo.RacePrefabKey(info.RaceId, info.Gender, out _, out _);
        if (string.IsNullOrEmpty(key)) { Plugin.L.LogWarning($"No prefab key for race {info.RaceId} '{info.Gender}'"); return null; }
        var prefab = SoftAssets.Load<GameObject>(key);
        if (prefab == null) { Plugin.L.LogWarning($"Prefab '{key}' did not load"); return null; }

        var go = Object.Instantiate(prefab, _holder.transform);
        // Rule-driven parts (stealth aura...) follow the owner's state; record them before their rule scripts go.
        var animRoot = go.GetComponentInChildren<Animator>(true);
        p.Parts = ModelParts.Collect(animRoot != null ? animRoot.transform : go.transform);
        foreach (var part in p.Parts.Values) part.SetActive(false);
        int removed = Strip(go, keepVisualScripts: true);

        var mod = go.GetComponentInChildren<CharacterAppearanceModifier>(true);
        if (mod != null)
        {
            mod.UsePlayerSaveData = false; // never read the local player's save
            if (!string.IsNullOrEmpty(info.Appearance)) mod.DefaultSelections = info.Appearance;
        }

        // A player-model PlayerAppearance takes race/gender (weapon grip transforms), body scale, animator override and
        // cosmetic settings from the *local* player. NPC mode makes it use its own ModelRaceID/ModelGender instead.
        p.Appearance = go.GetComponent<PlayerAppearance>();
        if (p.Appearance != null)
        {
            p.Appearance.IsNPCModel = true;
            p.Appearance.ModelRaceID = info.RaceId;
            p.Appearance.ModelGender = info.Gender;
        }

        go.transform.SetParent(null, false);
        go.SetActive(true); // Awake/OnEnable of the kept visual scripts run now; their Start runs next frame
        // Start() of the appearance scripts re-applies defaults and resets materials, so dress after it ran.
        p.DressInfo = info.Clone();
        p.DressAtFrame = Time.frameCount + 2;
        Plugin.L.LogInfo($"Built model for #{p.Id} '{info.CharacterName}' from '{key}' (removed {removed} components, armor {info.Armor.Count}, weapons {info.Weapons.Count(w => w > 0)})");
        return go;
    }

    /// <summary>Appearance options, armor (body overlay materials + pieces) and weapons, via the game's own code.</summary>
    private static void Dress(Puppet p)
    {
        var info = p.DressInfo;
        p.DressAtFrame = -1;
        if (p.Go == null || info == null) return;
        var mod = p.Go.GetComponentInChildren<CharacterAppearanceModifier>(true);
        if (mod != null && !string.IsNullOrEmpty(info.Appearance))
        {
            try { mod.ApplySerialized(info.Appearance); }
            catch (Exception e) { Plugin.L.LogWarning("ApplySerialized failed: " + e.Message); }
        }
        var look = p.Appearance;
        if (look == null) return;
        try { look.ModelRaceID = info.RaceId; look.ModelGender = info.Gender; } catch { /* cosmetic */ }
        foreach (var id in info.Armor)
        {
            var item = GameInfo.Item(id);
            if (item == null) continue;
            try { look.ShowArmor(item, null); }
            catch (Exception e) { Plugin.L.LogWarning($"ShowArmor({id}) failed: {e.Message}"); }
        }
        for (int i = 0; i < info.Weapons.Count; i++)
        {
            var item = GameInfo.Item(info.Weapons[i]);
            if (item == null) continue;
            try { look.ShowWeapon(item, i + 1, null, false); }
            catch (Exception e) { Plugin.L.LogWarning($"ShowWeapon({info.Weapons[i]}) failed: {e.Message}"); }
        }
        try { look.ReapplyEquippedBodyCulling(); } catch { /* optional */ }
        p.Armed = null; // re-apply armed/stowed from the next snapshot
        p.MaterialsApplied = null;
    }

    private static GameObject BuildFromLocalCopy()
    {
        var src = GameInfo.PlayerAnimator;
        if (src == null) return null;
        var go = Object.Instantiate(src.gameObject, _holder.transform);
        Strip(go, keepVisualScripts: false);
        go.transform.SetParent(null, false);
        return go;
    }

    private static int Strip(GameObject go, bool keepVisualScripts)
    {
        int removed = 0;
        for (int pass = 0; pass < 6; pass++)
        {
            int before = removed;
            foreach (var c in go.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                Il2CppSystem.Type t;
                try { t = c.GetIl2CppType(); } catch { continue; }
                if (KeepEngineTypes.Contains(t.FullName)) continue;
                if (keepVisualScripts && KeepVisualScripts.Contains(t.Name)) continue;
                try { Object.DestroyImmediate(c); removed++; } catch { /* a dependency still needs it; next pass */ }
            }
            if (removed == before) break;
        }
        return removed;
    }
}
