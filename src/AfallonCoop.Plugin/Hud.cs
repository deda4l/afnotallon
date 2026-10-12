using System.Collections.Generic;
using System.Linq;
using AfallonCoop.Session;
using TMPro;
using UnityEngine;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>Overlay: session status, notifications, and name labels over remote players.</summary>
internal static class Hud
{
    private static GameObject _root;
    private static TextMeshProUGUI _status;
    private static TextMeshProUGUI _feed;
    private static readonly Dictionary<byte, TextMeshProUGUI> Labels = new();
    private static readonly List<(float until, string text)> Feed = new();
    private static string _lastStatus = "";
    private static TextMeshProUGUI _pause;
    private static readonly Dictionary<byte, (float until, string text)> Bubbles = new();

    /// <summary>Show a chat line over a player's head for a few seconds.</summary>
    public static void Bubble(byte id, string text)
    {
        if (text.Length > 90) text = text.Substring(0, 87) + "...";
        Bubbles[id] = (Time.unscaledTime + 6f, text);
    }

    /// <summary>A co-op notice: in the world, a line in the game's chat (as its own notices); in the menus, the feed here.</summary>
    public static void Notify(string text)
    {
        Plugin.L.LogInfo("[HUD] " + text);
        if (GameInfo.InWorld)
        {
            try { UIEvents.Instance.OnShowChatTextMessage(text); return; }
            catch (System.Exception e) { Plugin.L.LogWarning("Notice to chat: " + e.Message); }
        }
        Feed.Add((Time.unscaledTime + 8f, text));
        if (Feed.Count > 6) Feed.RemoveAt(0);
    }

    private static bool Ensure()
    {
        if (_root != null) return true;
        if (UiKit.Font == null)
        {
            var any = Object.FindObjectOfType<TextMeshProUGUI>();
            if (any == null) return false;
            UiKit.Font = any.font;
        }
        _root = new GameObject("AfCoop_HUD");
        Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = -10; // name labels sit under the game's windows
        var scaler = _root.AddComponent<UnityEngine.UI.CanvasScaler>();
        scaler.uiScaleMode = UnityEngine.UI.CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;

        _status = UiKit.Text(_root.transform, "", 18, new Color(0.95f, 0.88f, 0.7f, 1f), TextAlignmentOptions.TopLeft);
        Anchor(_status, new Vector2(0, 1), new Vector2(14, -10), new Vector2(700, 60));
        _feed = UiKit.Text(_root.transform, "", 18, new Color(1f, 1f, 1f, 0.95f), TextAlignmentOptions.TopLeft);
        Anchor(_feed, new Vector2(0, 1), new Vector2(14, -64), new Vector2(700, 200));
        _pause = UiKit.Text(_root.transform, "", 64, new Color(1f, 0.9f, 0.6f, 1f), TextAlignmentOptions.Center);
        _pause.outlineWidth = 0.2f;
        Anchor(_pause, new Vector2(0.5f, 0.5f), new Vector2(0, 120), new Vector2(1400, 260));
        return true;
    }

    private static void Anchor(Component c, Vector2 corner, Vector2 pos, Vector2 size)
    {
        var r = UiKit.Rect(c);
        r.anchorMin = r.anchorMax = corner;
        r.pivot = corner;
        r.anchoredPosition = pos;
        r.sizeDelta = size;
    }

    public static void Update()
    {
        if (!CoopManager.Active && Feed.Count == 0) { if (_root != null) _root.SetActive(false); return; }
        if (!Ensure()) return;
        _root.SetActive(true);

        string status = "";
        var s = CoopManager.Session;
        if (s != null && s.State != SessionState.Closed && Plugin.CfgShowDebug.Value)
        {
            string role = s.IsHost ? "Host" : $"Client, ping {s.PingToHost} ms";
            status = $"<b>CO-OP</b>  {role}  ·  {s.PlayerCount} player{(s.PlayerCount == 1 ? "" : "s")}";
#if DEBUG
            if (GameInfo.InWorld && s.PlayerCount > 1) status += "\n<size=15>F6: spawn test mob   F7: teleport to next ally   F8: diagnostics dump</size>";
#endif
            if (s.PlayerCount > 1) status += "\n<size=15>" + TrafficView.Line() + "</size>";
        }
        if (status != _lastStatus) { _status.text = status; _lastStatus = status; }

        var pauseText = PauseController.OverlayText();
        if (_pause.text != pauseText) _pause.text = pauseText;

        float now = Time.unscaledTime;
        Feed.RemoveAll(f => f.until < now);
        _feed.text = string.Join("\n", Feed.Select(f => f.text));
    }

    private static float _nameplateDistance = 40f;
    private static float _nextDistanceLookup;

    /// <summary>The visible distance the game gives its adventurer bots' nameplates (read from a live one).</summary>
    private static float NameplateDistance()
    {
        if (Time.unscaledTime < _nextDistanceLookup) return _nameplateDistance;
        _nextDistanceLookup = Time.unscaledTime + 5f;
        try
        {
            var plates = Object.FindObjectOfType<BLINK.RPGBuilder.UI.ScreenSpaceNameplates>();
            if (plates?.AllNameplates == null) return _nameplateDistance;
            foreach (var n in plates.AllNameplates)
            {
                var npc = n?.Entity?.GetNPCData();
                if (npc != null && (int)npc.npcType == 9 && n.VisibleDistance > 1f) { _nameplateDistance = n.VisibleDistance; break; }
            }
        }
        catch { }
        return _nameplateDistance;
    }

    public static void LateUpdate()
    {
        if (_root == null || !_root.activeSelf) return;
        var cam = Camera.main;
        var seen = new HashSet<byte>();
        var s = CoopManager.Session;
        if (cam != null && s != null)
        {
            foreach (var (id, tr) in Puppets.Visible())
            {
                var sp = cam.WorldToScreenPoint(tr.position + Vector3.up * 2.15f);
                if (sp.z <= 0) continue;
                // Like the game's nameplates of its bot players: gone beyond their visible distance, fading before it.
                var me = GameInfo.Player;
                float dist = me != null ? Vector3.Distance(me.transform.position, tr.position) : 0f;
                float max = NameplateDistance();
                float alpha = Mathf.Clamp01((max - dist) / (max * 0.25f));
                if (alpha <= 0f) continue;
                if (!Labels.TryGetValue(id, out var label) || label == null)
                {
                    label = UiKit.Text(_root.transform, "", 17, new Color(0.55f, 0.85f, 1f, 1f), TextAlignmentOptions.Center);
                    UiKit.Rect(label).sizeDelta = new Vector2(420, 90);
                    label.alignment = TextAlignmentOptions.Bottom;
                    Labels[id] = label;
                }
                var info = s.Players.TryGetValue(id, out var p) ? p : null;
                string name = info == null ? $"#{id}" : (string.IsNullOrEmpty(info.CharacterName) ? info.Name : $"{info.CharacterName}\n<size=13>{info.Name}</size>");
                if (Bubbles.TryGetValue(id, out var b) && b.until > Time.unscaledTime)
                    name = $"<color=#FFFFFF><size=16>\"{b.text}\"</size></color>\n" + name;
                if (label.text != name) label.text = name;
                label.transform.position = new Vector3(sp.x, sp.y, 0); // overlay canvas: world == screen pixels
                label.alpha = alpha;
                label.gameObject.SetActive(true);
                ShowStateIcons(id, new Vector2(sp.x, sp.y + label.preferredHeight + 4f), alpha);
                seen.Add(id);
            }
        }
        foreach (var kv in Labels)
            if (kv.Value != null && !seen.Contains(kv.Key)) kv.Value.gameObject.SetActive(false);
        foreach (var kv in StateIcons)
            if (!seen.Contains(kv.Key)) foreach (var img in kv.Value) if (img != null) img.gameObject.SetActive(false);
    }

    // Like the small state icons over the game's nameplates: a player's buffs and debuffs (mirrored on their shell here).
    private const int MaxStateIcons = 8;
    private const float StateIconSize = 18f;
    private static readonly Dictionary<byte, List<UnityEngine.UI.Image>> StateIcons = new();

    private static void ShowStateIcons(byte id, Vector2 bottomCenter, float alpha)
    {
        if (!StateIcons.TryGetValue(id, out var icons))
        {
            StateIcons[id] = icons = new List<UnityEngine.UI.Image>();
            for (int i = 0; i < MaxStateIcons; i++)
            {
                var go = new GameObject("AfCoop_StateIcon");
                go.transform.SetParent(_root.transform, false);
                var img = go.AddComponent<UnityEngine.UI.Image>();
                img.raycastTarget = false;
                img.rectTransform.sizeDelta = new Vector2(StateIconSize, StateIconSize);
                go.SetActive(false);
                icons.Add(img);
            }
        }
        var sprites = new List<Sprite>();
        var shell = PlayerShells.Get(id);
        var states = shell != null ? shell.GetStates() : null;
        if (states != null)
            foreach (var st in states)
            {
                if (st?.stateEffect == null || !st.stateEffect.showUIState) continue; // as the game's nameplates
                var icon = st.stateIcon ?? st.stateEffect.icon;
                if (icon != null && sprites.Count < MaxStateIcons) sprites.Add(icon);
            }
        float width = sprites.Count * (StateIconSize + 2f) - 2f;
        for (int i = 0; i < icons.Count; i++)
        {
            var img = icons[i];
            if (img == null) continue;
            bool on = i < sprites.Count;
            if (img.gameObject.activeSelf != on) img.gameObject.SetActive(on);
            if (!on) continue;
            if (img.sprite != sprites[i]) img.sprite = sprites[i];
            img.color = new Color(1f, 1f, 1f, alpha);
            img.transform.position = new Vector3(bottomCenter.x - width / 2f + i * (StateIconSize + 2f) + StateIconSize / 2f, bottomCenter.y + StateIconSize / 2f, 0f);
        }
    }
}
