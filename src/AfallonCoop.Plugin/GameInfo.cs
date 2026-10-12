using System;
using System.IO;
using System.Linq;
using BepInEx;
using BLINK.RPGBuilder.Characters;
using BLINK.RPGBuilder.Combat;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Managers;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AfallonCoop.Plugin;

/// <summary>Read-only access to game state the mod needs. All game-specific lookups live here.</summary>
internal static class GameInfo
{
    private static string _build;

    /// <summary>Unity build guid from boot.config: changes with every game update, so peers on different versions are refused.</summary>
    public static string BuildGuid
    {
        get
        {
            if (_build != null) return _build;
            try
            {
                var boot = Path.Combine(Paths.GameRootPath, "Afallon_Data", "boot.config");
                _build = File.ReadAllLines(boot).Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith("build-guid="))?.Substring("build-guid=".Length) ?? "";
            }
            catch { _build = ""; }
            return _build;
        }
    }

    public static string SceneName
    {
        get { try { return SceneManager.GetActiveScene().name ?? ""; } catch { return ""; } }
    }

    /// <summary>The local player's combat entity, or null when not in the world (menus, loading).</summary>
    public static PlayerCombatEntity Player
    {
        get
        {
            try
            {
                var p = GameState.playerEntity;
                return p != null && p.gameObject != null ? p : null;
            }
            catch { return null; }
        }
    }

    public static bool InWorld => Player != null;

    public static Animator PlayerAnimator
    {
        get
        {
            var p = Player;
            if (p == null) return null;
            try
            {
                var ess = p.controllerEssentials;
                if (ess != null && ess.anim != null) return ess.anim;
            }
            catch { /* fall through */ }
            return p.GetComponentInChildren<Animator>();
        }
    }

    public static CharacterData Character
    {
        get
        {
            try { return BLINK.RPGBuilder.Characters.Character.Instance?.CharacterData; }
            catch { return null; }
        }
    }

    /// <summary>Local look + gear, as sent to other players.</summary>
    public static void FillVisuals(PlayerInfo info)
    {
        var cd = Character;
        var p = Player;
        info.Appearance = "";
        info.Armor.Clear();
        info.Weapons.Clear();
        if (cd == null || p == null) return;
        try
        {
            var mod = p.GetComponentInChildren<CharacterAppearanceModifier>(true);
            if (mod != null) info.Appearance = mod.Serialize() ?? "";
        }
        catch (Exception e) { Plugin.L.LogWarning("Appearance serialize failed: " + e.Message); }
        try
        {
            var items = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetItems();
            if (cd.ArmorPiecesEquipped != null)
                foreach (var a in cd.ArmorPiecesEquipped)
                    if (a != null && a.itemID > 0 && !HiddenByChoice(p, items.ContainsKey(a.itemID) ? items[a.itemID] : null)) info.Armor.Add(a.itemID);
            if (cd.WeaponsEquipped != null)
                foreach (var w in cd.WeaponsEquipped)
                    info.Weapons.Add(w != null ? w.itemID : 0);
        }
        catch (Exception e) { Plugin.L.LogWarning("Equipment read failed: " + e.Message); }
        try { info.FxPrewarm = FxPrewarm.LocalAbilityIds(); }
        catch (Exception e) { Plugin.L.LogWarning("Prewarm ids failed: " + e.Message); }
        try { info.Materials = MaterialSnapshot.Capture(PlayerAnimator != null ? PlayerAnimator.transform : p.transform); }
        catch (Exception e) { Plugin.L.LogWarning("Material snapshot failed: " + e.Message); }
    }

    /// <summary>
    /// A helmet or cape the player chose not to show (the game's "show helm/cloak" settings): others do not see it
    /// either. Asked of the game itself (is that piece's visual shown now), for those two slots only.
    /// </summary>
    private static bool HiddenByChoice(PlayerCombatEntity p, RPGItem item)
    {
        if (item == null || p?.appearance == null) return false;
        try
        {
            bool helmOrCape = string.Equals(item.ArmorSlot?.entryName, "HELMET", StringComparison.OrdinalIgnoreCase)
                              || BLINK.RPGBuilder.Characters.PlayerAppearance.IsCape(item);
            return helmOrCape && !p.appearance.IsArmorVisualVisible(item);
        }
        catch { return false; }
    }

    public static bool PlayerArmed
    {
        get
        {
            try { var a = Player?.appearance; return a != null && a.LastArmed; }
            catch { return false; }
        }
    }

    public static bool PlayerDead
    {
        get { try { var p = Player; return p != null && p.IsDead(); } catch { return false; } }
    }

    public static bool PlayerStealthed
    {
        get { try { return Player != null && Player.IsStealth(); } catch { return false; } }
    }

    public static RPGItem Item(int id)
    {
        try
        {
            var items = GameDatabase.Instance.GetItems();
            return items.ContainsKey(id) ? items[id] : null;
        }
        catch { return null; }
    }

    /// <summary>Addressable key of the character prefab for a race + gender ("Male"/"Female").</summary>
    public static string RacePrefabKey(int raceId, string gender, out string restControllerKey, out string combatControllerKey)
    {
        restControllerKey = combatControllerKey = null;
        try
        {
            var races = GameDatabase.Instance.GetRaces();
            if (!races.ContainsKey(raceId)) return null;
            var race = races[raceId];
            RPGRace.RaceGenderData pick = null;
            foreach (var g in race.Genders)
            {
                if (g == null || g.Gender == null) continue;
                pick ??= g;
                if (string.Equals(g.Gender.entryName, gender, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.Gender.name, gender, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(g.Gender.entryDisplayName, gender, StringComparison.OrdinalIgnoreCase))
                { pick = g; break; }
            }
            if (pick == null) return null;
            restControllerKey = pick.RestAnimatorControllerKey;
            combatControllerKey = pick.CombatAnimatorControllerKey;
            return pick.PrefabKey;
        }
        catch (Exception e) { Plugin.L.LogWarning("Race lookup failed: " + e.Message); return null; }
    }

    public static void TeleportPlayer(Vector3 pos)
    {
        var p = Player;
        if (p == null) return;
        try { p.controllerEssentials.TeleportToTarget(pos); }
        catch (Exception e)
        {
            Plugin.L.LogWarning("TeleportToTarget failed, moving transform directly: " + e.Message);
            p.transform.position = pos;
        }
    }
}
