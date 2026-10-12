using System.Collections.Generic;
using BLINK.RPGBuilder.AI;
using UnityEngine;
using UnityEngine.UI;

namespace AfallonCoop.Plugin;

/// <summary>
/// The game's dungeon invitation window as a group ready check: its five role icons (tank, healer, three damage) are
/// the group's seats; each member's seat gets the game's own check mark when they are ready (bots always are), and
/// the accept button reads "Ready" (everyone travels once all are).
/// </summary>
internal static class ReadyPanel
{
    private const string PanelPath = "RPG_Builder_ESSENTIALS(Clone)/Game_UI_Canvas/--- PANELS ---/Dungeon Invitation";
    private const string CheckSprite = "QuestTracker_Checkmark_Complete"; // the quest tracker's tick for a completed objective
    private const string MarkName = "AfCoop_Ready";

    private static GameObject _panel;
    private static Sprite _check;
    private static string _acceptLabel, _description;
    private static bool _shown;

    private static AdventurerRole SlotRole(int i) => i == 0 ? AdventurerRole.Tank : i == 1 ? AdventurerRole.Healer : AdventurerRole.Damage;

    /// <summary>Draw the ready state (null = not a group invitation: the window as the game has it).</summary>
    public static void Render(List<GroupFinder.Seat> roster)
    {
        if (roster == null) { if (_shown) Restore(); return; }
        if (_panel == null) _panel = GameObject.Find(PanelPath);
        if (_panel == null) return;
        _shown = true;

        // seat each member on an icon of their role, in order; anyone left over takes a free icon
        var seats = new GroupFinder.Seat?[5];
        var rest = new List<GroupFinder.Seat>();
        foreach (var m in roster)
        {
            int slot = -1;
            for (int i = 0; i < 5 && slot < 0; i++) if (seats[i] == null && SlotRole(i) == m.Role) slot = i;
            if (slot >= 0) seats[slot] = m; else rest.Add(m);
        }
        foreach (var m in rest) for (int i = 0; i < 5; i++) if (seats[i] == null) { seats[i] = m; break; }

        int ready = 0, total = 0;
        for (int i = 0; i < 5; i++)
        {
            var icon = _panel.transform.Find("Group role " + i);
            if (icon == null) continue;
            var seat = seats[i];
            if (seat != null) { total++; if (seat.Value.Ready) ready++; }
            SetMark(icon, seat != null && seat.Value.Ready);
        }
        SetText("Accept", ref _acceptLabel, "Ready");
        SetText("Party description", ref _description, $"Ready: {ready} / {total}");
    }

    private static void SetMark(Transform icon, bool on)
    {
        var mark = icon.Find(MarkName);
        if (mark == null)
        {
            if (!on) return;
            if (_check == null)
                foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                    if (sp != null && sp.name == CheckSprite) { _check = sp; break; }
            var go = new GameObject(MarkName);
            go.transform.SetParent(icon, false);
            var rt = go.AddComponent<RectTransform>();
            rt.anchorMin = rt.anchorMax = new Vector2(1f, 0f); // bottom-right corner of the role icon
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.anchoredPosition = new Vector2(-4f, 6f);
            rt.sizeDelta = new Vector2(26f, 21f); // the sprite's 22x18, a bit larger on a 46 px icon
            var img = go.AddComponent<Image>();
            img.sprite = _check;
            img.raycastTarget = false;
            mark = go.transform;
        }
        if (mark.gameObject.activeSelf != on) mark.gameObject.SetActive(on);
    }

    private static void SetText(string child, ref string original, string text)
    {
        var t = _panel.transform.Find(child)?.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (t == null) return;
        original ??= t.text;
        if (t.text != text) t.text = text;
    }

    private static void Restore()
    {
        _shown = false;
        if (_panel == null) return;
        for (int i = 0; i < 5; i++)
        {
            var mark = _panel.transform.Find("Group role " + i)?.Find(MarkName);
            if (mark != null) mark.gameObject.SetActive(false);
        }
        var a = _panel.transform.Find("Accept")?.GetComponentInChildren<TMPro.TMP_Text>(true);
        if (a != null && _acceptLabel != null) a.text = _acceptLabel;
        _acceptLabel = null; _description = null; // the description is the game's own, rewritten by it
    }
}
