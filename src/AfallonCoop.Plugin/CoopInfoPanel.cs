using System;
using System.Linq;
using AfallonCoop.Transport;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Esc menu > CO-OP INFO: how others join this game (Steam friends: from their game list; LAN: the addresses, with a
/// copy button), who is here (the host can ban them, and lift a ban), and whether the status line in the corner shows.
/// </summary>
internal static class CoopInfoPanel
{
    private static GameObject _panel;
    private static GameObject _rowTemplate;
    private static Transform _parent;
    private static float _y, _w;

    private static readonly Color TextColor = new(0.93f, 0.85f, 0.70f, 1f);
    private static readonly Color DimColor = new(0.70f, 0.64f, 0.55f, 1f);

    public static void Open(Transform parent, GameObject rowTemplate)
    {
        Close();
        _parent = parent;
        _rowTemplate = rowTemplate;
        _panel = new GameObject("AfCoop_InfoPanel");
        _panel.transform.SetParent(parent, false);
        var img = _panel.AddComponent<Image>();
        img.color = new Color(0.07f, 0.045f, 0.04f, 0.97f);
        var r = UiKit.Rect(img);
        r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
        r.pivot = new Vector2(0.5f, 0.5f);
        r.anchoredPosition = Vector2.zero;
        Build();
    }

    public static void Close()
    {
        if (_panel != null) Object.Destroy(_panel);
        _panel = null;
    }

    private static void Build()
    {
        var size = UiKit.Rect(_rowTemplate.GetComponent<RectTransform>()).sizeDelta;
        _w = Math.Max(size.x * 2f + 80f, 640f);
        _y = -24f;
        var s = CoopManager.Session;
        Line("CO-OP INFO", 32, TextColor, 48);
        if (s == null) { Line("Not in a co-op game.", 20, DimColor); }
        else if (!s.IsHost) { Line("Connected to " + Groups.Name(Protocol.ProtocolInfo.HostId) + $", ping {s.PingToHost} ms", 18, DimColor); }
        else if (CoopManager.Mode == CoopMode.HostSteam)
        {
            Line("Your Steam friends see this game in CO-OP > JOIN GAME.", 18, DimColor);
        }
        else
        {
            Line($"Your addresses (UDP port {Plugin.CfgPort.Value}; friends: CO-OP > JOIN GAME):", 18, DimColor);
            foreach (var a in CoopManager.LocalAddresses().Take(3))
            {
                string full = a + ":" + Plugin.CfgPort.Value;
                Line(full, 26, TextColor);
                Btn("COPY " + a, () => Copy(full, "address"));
            }
        }
        if (s != null)
        {
            _y -= 6;
            Line($"Players {s.PlayerCount}/{Protocol.ProtocolInfo.MaxPlayers}: " +
                 string.Join(", ", s.Players.Values.OrderBy(p => p.Id).Select(p => string.IsNullOrEmpty(p.CharacterName) ? p.Name : p.CharacterName)), 18, DimColor);
            if (s.IsHost) Bans(s);
        }
        _y -= 6;
        bool debug = Plugin.CfgShowDebug.Value;
        Btn((debug ? "[X]" : "[  ]") + " SHOW DEBUG", () => { Plugin.CfgShowDebug.Value = !debug; Reopen(); });
        Btn("CLOSE", Close);
        UiKit.Rect(_panel.GetComponent<Image>()).sizeDelta = new Vector2(_w, -_y + 20f);
    }

    private static void Reopen()
    {
        if (_parent != null && _rowTemplate != null) Open(_parent, _rowTemplate);
    }

    /// <summary>Host: a BAN row per other player (they leave and cannot come back), an UNBAN row per banned one.</summary>
    private static void Bans(AfallonCoop.Session.CoopSession s)
    {
        foreach (var p in s.Players.Values.Where(p => p.Id != s.LocalId).OrderBy(p => p.Id).ToList())
        {
            var player = p;
            Btn("BAN " + (string.IsNullOrEmpty(p.CharacterName) ? p.Name : p.CharacterName).ToUpperInvariant(), () => { BanList.Ban(player); Reopen(); });
        }
        if (BanList.All.Count == 0) return;
        Line("Banned:", 18, DimColor);
        float rowH = UiKit.Rect(_rowTemplate.GetComponent<RectTransform>()).sizeDelta.y;
        float h = Math.Min(BanList.All.Count, 3) * (rowH + 6f);
        var list = UiKit.ScrollList(_panel.transform, _y, _w - 60f, h);
        foreach (var kv in BanList.All.ToList())
        {
            var key = kv.Key;
            UiKit.Row(_rowTemplate, list, "UNBAN " + kv.Value.ToUpperInvariant(), () => { BanList.Unban(key); Reopen(); });
        }
        _y -= h + 8f;
    }

    private static void Copy(string text, string what)
    {
        GUIUtility.systemCopyBuffer = text ?? "";
        Hud.Notify($"Copied the {what}: {text}");
    }

    private static void Line(string t, float size, Color color, float h = 0)
    {
        var txt = UiKit.Text(_panel.transform, t, size, color, TextAlignmentOptions.Center);
        float height = h > 0 ? h : size * 1.4f;
        UiKit.Place(txt, 0, _y, _w - 40, height);
        _y -= height + 2;
    }

    private static void Btn(string text, Action onClick)
    {
        var go = UiKit.Row(_rowTemplate, _panel.transform, text, onClick);
        var rt = go.GetComponent<RectTransform>();
        var size = rt.sizeDelta;
        UiKit.Place(rt, 0, _y, size.x, size.y);
        _y -= size.y + 8;
    }
}
