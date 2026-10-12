using System;
using System.Collections.Generic;
using BLINK.RPGBuilder.Combat;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;

namespace AfallonCoop.Plugin;

/// <summary>
/// The other players of the local player's group, and their bot companions, in the game's own companion frames:
/// after the game fills the frames with the local companions it fills the next free ones with the players' shells
/// (which carry the player's name, class icon, level, health and mana) and their bots' copies.
/// </summary>
internal static class GroupFrames
{
    private static void Fill(CompanionDisplayManager ui)
    {
        var slots = ui.companionSlots;
        if (slots == null) return;
        int used = ui.activeCompanions != null ? ui.activeCompanions.Count : 0;
        try { var w = BLINK.RPGBuilder.Challenges.ChallengeStoneParty.Waiting; if (w != null) used += w.Count; } catch { }
        for (int i = 0; i < slots.Count; i++) Overlay(slots[i], null); // the game re-inits frames: our marks go
        FarSlots.Clear();
        var s = CoopManager.Session;
        var g = s.MyGroup;
        foreach (var mem in g.Members)
        {
            if (mem.Online && mem.Id == s.LocalId) continue;
            var shell = mem.Online ? PlayerShells.Get(mem.Id) : null;
            if (shell != null)
            {
                if (used >= slots.Count) break;
                var slot = slots[used++];
                ui.InitSlot(slot, shell);
                ShowPlayer(slot, mem.Id);
                foreach (var pet in MobSync.PetCopiesOf(mem.Id))
                {
                    bool bot = false; try { bot = (int)pet.GetNPCData().npcType == 9; } catch { }
                    if (!bot || used >= slots.Count) continue;
                    ui.InitSlot(slots[used++], pet);
                }
            }
            else
            {
                // disconnected (place kept) or not around here: a greyed frame from what we know
                if (used >= slots.Count) break;
                var vit = mem.Online ? Puppets.Vitals(mem.Id) : null;
                Vital? hp = null;
                if (vit != null) foreach (var v in vit.Values) { hp = v; break; }
                var far = slots[used++];
                // far: their portrait, greyed (WoW-like); disconnected: marked
                Manual(far, mem.Online ? Groups.Name(mem.Id) : mem.Name, hp, mem.Online ? null : "OFFLINE", mem.Online ? ClassIcon(mem.Id) : null);
                if (mem.Online && far.thisCG != null) FarSlots[far.thisCG.Pointer] = mem.Id;
            }
        }
    }

    // A far member's frame has no entity for the game's own click (CompanionSlotClickHandler polls the mouse over its
    // frame each frame and acts on the frame's entity): a right click on it opens the target menu for that member
    // (summon, travel to). Frames are reused: which member a frame stands for is set at each fill.
    private static readonly Dictionary<IntPtr, byte> FarSlots = new();

    private static UnityEngine.Sprite ClassIcon(byte player)
    {
        try
        {
            var s = CoopManager.Session;
            if (s == null || !s.Players.TryGetValue(player, out var info)) return null;
            var classes = GameDatabase.Instance.GetClasses();
            if (!classes.ContainsKey(info.ClassId)) return null;
            var c = classes[info.ClassId];
            return c.icon != null ? c.icon : c.entryIcon;
        }
        catch { return null; }
    }

    private static void FarClick(CompanionSlotClickHandler h)
    {
        var slot = h.slot;
        if (slot?.thisCG == null || !FarSlots.TryGetValue(slot.thisCG.Pointer, out var player)) return;
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || !mouse.rightButton.wasPressedThisFrame || h.rectTransform == null) return;
        var canvas = h.rectTransform.GetComponentInParent<UnityEngine.Canvas>();
        var cam = canvas == null || canvas.rootCanvas.renderMode == UnityEngine.RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
        if (!UnityEngine.RectTransformUtility.RectangleContainsScreenPoint(h.rectTransform, mouse.position.ReadValue(), cam)) return;
        Groups.OpenFarMenu(player);
    }

    /// <summary>A frame for a member with no entity here: name, last health, dimmed, with a status mark (WoW-like).</summary>
    private static void Manual(CompanionDisplayManager.CompanionUISlot slot, string name, Vital? hp, string status, UnityEngine.Sprite icon = null)
    {
        slot.AssignedEntity = null;
        if (slot.companionIcon != null && icon != null) { slot.companionIcon.sprite = icon; slot.companionIcon.enabled = true; }
        if (slot.thisCG != null) { slot.thisCG.alpha = 0.55f; slot.thisCG.gameObject.SetActive(true); }
        if (slot.companionNameText != null) slot.companionNameText.text = name;
        if (slot.companionLevelText != null) slot.companionLevelText.text = "";
        if (slot.StatDisplays != null)
            foreach (var d in slot.StatDisplays)
            {
                if (d == null) continue;
                if (d.statBarImage != null) d.statBarImage.fillAmount = hp.HasValue && hp.Value.Max > 0 ? hp.Value.Current / hp.Value.Max : 0f;
                if (d.statValueText != null) d.statValueText.text = status == "OFFLINE" ? "Offline" : hp.HasValue ? $"{hp.Value.Current:0} / {hp.Value.Max:0}" : "";
            }
        Overlay(slot, status);
    }

    private static readonly Dictionary<IntPtr, TMPro.TextMeshProUGUI> Marks = new();

    /// <summary>A status mark over the portrait ("OFFLINE" in red, like WoW's disconnect icon), or none.</summary>
    private static void Overlay(CompanionDisplayManager.CompanionUISlot slot, string status)
    {
        if (slot?.thisCG == null) return;
        var key = slot.thisCG.Pointer;
        Marks.TryGetValue(key, out var mark);
        if (status == null) { if (mark != null) mark.gameObject.SetActive(false); return; }
        if (mark == null)
        {
            var parent = slot.companionIcon != null ? slot.companionIcon.transform : slot.thisCG.transform;
            mark = UiKit.Text(parent, "", 16, new UnityEngine.Color(1f, 0.3f, 0.25f, 1f), TMPro.TextAlignmentOptions.Center);
            mark.fontStyle = TMPro.FontStyles.Bold;
            mark.outlineWidth = 0.25f;
            var r = UiKit.Rect(mark); r.anchorMin = UnityEngine.Vector2.zero; r.anchorMax = UnityEngine.Vector2.one; r.offsetMin = r.offsetMax = UnityEngine.Vector2.zero;
            Marks[key] = mark;
        }
        mark.text = status;
        mark.color = status == "OFFLINE" ? new UnityEngine.Color(1f, 0.3f, 0.25f, 1f) : new UnityEngine.Color(0.9f, 0.85f, 0.7f, 1f);
        mark.gameObject.SetActive(true);
    }

    private static CompanionDisplayManager _ui;

    /// <summary>The group changed: rebuild the frames now rather than on the game's roster timer.</summary>
    private static readonly Dictionary<byte, string> Shown = new(); // what a member's frame shows (role, level, name)

    /// <summary>A member's info changed: their frame is redrawn when what it shows changed (a role chosen in the finder).</summary>
    public static void OnInfo(PlayerInfo p)
    {
        string sig = $"{p.Role}|{p.Level}|{p.CharacterName}";
        if (Shown.TryGetValue(p.Id, out var old) && old == sig) return;
        Shown[p.Id] = sig;
        if (CoopManager.Session?.MyGroup?.Member(p.Id) != null) RefreshAll();
    }

    public static void RefreshAll()
    {
        try
        {
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<CompanionDisplayManager>();
            _ui?.CheckForCompanions();
        }
        catch { _ui = null; }
    }

    /// <summary>Redraw the frame showing this entity now (its health changed).</summary>
    public static void Refresh(CombatEntity e)
    {
        try
        {
            if (_ui == null) _ui = UnityEngine.Object.FindObjectOfType<CompanionDisplayManager>();
            if (_ui?.companionSlots == null) return;
            foreach (var slot in _ui.companionSlots)
                if (slot?.AssignedEntity != null && slot.AssignedEntity.Pointer == e.Pointer) { _ui.UpdateSlotStats(slot); return; }
        }
        catch { _ui = null; }
    }

    /// <summary>Dev: what each companion frame shows.</summary>
    public static string Describe()
    {
        var ui = UnityEngine.Object.FindObjectOfType<CompanionDisplayManager>();
        if (ui == null || ui.companionSlots == null) return "no companion frames";
        var rows = new List<string>();
        int i = 0;
        foreach (var slot in ui.companionSlots)
        {
            var e = slot?.AssignedEntity;
            float alpha = slot?.thisCG != null ? slot.thisCG.alpha : -1;
            rows.Add($"{i++}: {(e != null ? $"'{slot.companionNameText?.text}' lvl '{slot.companionLevelText?.text}' entity '{e.name}'" : "-")} alpha {alpha:0.0}");
        }
        return string.Join(Environment.NewLine, rows);
    }

    /// <summary>The game writes the shell's template (a bot's name); the frame is the player's.</summary>
    private static void ShowPlayer(CompanionDisplayManager.CompanionUISlot slot, byte player)
    {
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var info)) return;
        string name = string.IsNullOrEmpty(info.CharacterName) ? info.Name : info.CharacterName;
        if (s.MyGroup?.Leader == player) name += " *";
        if (slot.companionNameText != null) slot.companionNameText.text = name;
        if (slot.companionLevelText != null && info.Level > 0) slot.companionLevelText.text = info.Level.ToString();
        var badge = slot.roleBadge;
        if (badge != null && badge.Icon != null)
        {
            var sprite = info.Role switch { 1 => badge.Tank, 2 => badge.Healer, _ => badge.Damage };
            if (sprite != null) { badge.Icon.sprite = sprite; badge.Icon.enabled = true; }
        }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(CompanionSlotClickHandler), "Update")]
        private static void FarFrameClick(CompanionSlotClickHandler __instance)
        {
            try { if (FarSlots.Count > 0) FarClick(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Far frame menu: " + e.Message); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(CompanionDisplayManager), nameof(CompanionDisplayManager.CheckForCompanions))]
        private static void AddGroup(CompanionDisplayManager __instance)
        {
            try { if (CoopManager.Connected && CoopManager.Session.MyGroup != null) Fill(__instance); }
            catch (Exception e) { Plugin.L.LogWarning("Group frames: " + e.Message); }
        }
    }
}
