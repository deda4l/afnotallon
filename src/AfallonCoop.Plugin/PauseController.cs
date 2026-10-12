using System;
using AfallonCoop.Protocol;
using AfallonCoop.Session;
using BLINK.RPGBuilder.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Co-op pause. With 2+ players the world stops only when the host pauses it (F9 or the button in the Esc menu)
/// or when every player has the menu open (or is outside the world). One player's menu no longer freezes the rest.
/// The host's session decides; everyone applies it through Time.timeScale.
/// </summary>
internal static class PauseController
{
    public static bool LocalMenuOpen { get; private set; }

    private static OptionsPanel _options;
    private static float _nextFind;
    private static bool _forced;
    private static Button _escButton, _infoButton;
    private static IntPtr _escButtonFor;
    private static Transform _escRow;

    public static bool Coop => CoopManager.Connected && CoopManager.Session.PlayerCount >= 2;

    private static bool OptionsOpen()
    {
        try
        {
            if (_options == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 1f;
                _options = Object.FindObjectOfType<OptionsPanel>();
            }
            return _options != null && _options.IsOpen();
        }
        catch { _options = null; return false; }
    }

    public static void Update()
    {
        var s = CoopManager.Session;
        bool optionsOpen = OptionsOpen();
        LocalMenuOpen = !GameInfo.InWorld || optionsOpen;
        if (s == null || !CoopManager.Connected) return;
        s.SetLocalMenuOpen(LocalMenuOpen);

        if (s.IsHost && Pressed(k => k.f9Key)) ToggleHostPause();
        if (optionsOpen) EnsureEscButton();
        UpdateEscButtonLabel();
        if (!optionsOpen) CoopInfoPanel.Close();
    }

    public static void ToggleHostPause()
    {
        var s = CoopManager.Session;
        if (s == null || !s.IsHost) return;
        s.SetHostPause(!s.HostPauseRequested);
        Hud.Notify(s.HostPauseRequested ? "You paused the game for everyone (F9 to resume)" : "Resumed");
    }

    /// <summary>Runs after the game's own Update, so our decision wins over its local pause.</summary>
    public static void LateUpdate()
    {
        var s = CoopManager.Session;
        bool apply = s != null && CoopManager.Connected && (s.PlayerCount >= 2 || s.Paused);
        if (!apply)
        {
            if (_forced) { Time.timeScale = 1f; _forced = false; }
            return;
        }
        if (s.Paused)
        {
            if (Time.timeScale != 0f) Time.timeScale = 0f;
            _forced = true;
        }
        else
        {
            // The game pauses itself when its menu opens; in co-op the world keeps running for the others.
            if (Time.timeScale == 0f) Time.timeScale = 1f;
            _forced = false;
        }
    }

    public static string OverlayText()
    {
        var s = CoopManager.Session;
        if (s == null || !s.Paused) return "";
        return s.PauseReason switch
        {
            PauseReason.Host when s.IsHost => "PAUSED FOR EVERYONE\n<size=60%>press F9 or use the Esc menu to resume</size>",
            PauseReason.Host => "PAUSED BY HOST",
            PauseReason.AllInMenu => "PAUSED\n<size=60%>everyone is in the menu</size>",
            _ => "PAUSED",
        };
    }

    // ---------- "Pause for everyone" button inside the Esc menu (host only) ----------

    private static void EnsureEscButton()
    {
        if (_options == null) return;
        if (_escButton != null && _escButtonFor == _options.Pointer) return;
        try
        {
            Button template = null;
            foreach (var b in _options.GetComponentsInChildren<Button>(true))
            {
                if (b == null || b.name.StartsWith("AfCoop") || b.GetComponentInChildren<TMP_Text>(true) == null) continue;
                if (template == null || b.transform.position.y < template.transform.position.y) template = b; // lowest = usually "Quit"
            }
            if (template == null) return;
            var row = template.transform.parent != null && template.transform.parent.GetComponentsInChildren<Button>(true).Length == 1
                ? template.transform.parent : template.transform;
            _escRow = row;
            _escButton = EscRow(row, "AfCoop_PauseAll", 1, ToggleHostPause);
            _infoButton = EscRow(row, "AfCoop_CoopInfo", 2, () => CoopInfoPanel.Open(_options.transform, row.gameObject));
            UiKit.SetLabel(_infoButton.gameObject, "CO-OP INFO");
            _escButtonFor = _options.Pointer;
            Plugin.L.LogInfo($"Esc menu: co-op buttons added (template '{template.name}')");
        }
        catch (Exception e) { Plugin.L.LogWarning("Esc menu button failed: " + e.Message); _escButtonFor = _options.Pointer; }
    }

    /// <summary>A copy of the menu's lowest row, <paramref name="n"/> rows below it.</summary>
    private static Button EscRow(Transform row, string name, int n, Action onClick)
    {
        var go = Object.Instantiate(row.gameObject, row.parent);
        go.name = name;
        UiKit.StripLocalization(go);
        var btn = go.GetComponentInChildren<Button>(true);
        btn.onClick = new Button.ButtonClickedEvent();
        btn.onClick.AddListener(UiKit.Action(onClick));
        var rt = go.GetComponent<RectTransform>();
        var srcRt = row.GetComponent<RectTransform>();
        if (row.parent.GetComponent<LayoutGroup>() == null)
            rt.anchoredPosition = srcRt.anchoredPosition - new Vector2(0, srcRt.rect.height * 1.25f * n);
        return btn;
    }

    private static void ShowRow(Button b, bool show)
    {
        if (b == null) return;
        var go = b.transform.parent != null && b.transform.parent.name.StartsWith("AfCoop") ? b.transform.parent.gameObject : b.gameObject;
        go.SetActive(show);
    }

    private static void UpdateEscButtonLabel()
    {
        if (_escButton == null) return;
        var s = CoopManager.Session;
        bool host = s != null && s.IsHost;
        bool show = host && s.PlayerCount >= 2;
        ShowRow(_escButton, show);
        ShowRow(_infoButton, s != null); // everyone: the group lives there; the host also finds the code/address
        if (show) UiKit.SetLabel(_escButton.gameObject, s.HostPauseRequested ? "RESUME FOR EVERYONE" : "PAUSE FOR EVERYONE");
    }

    /// <summary>Leave the world the way the Esc menu does (saves the character, loads the main menu).</summary>
    public static void BackToMainMenu()
    {
        try
        {
            var o = _options != null ? _options : Object.FindObjectOfType<OptionsPanel>();
            if (o != null) o.BackToMainMenu();
            else Plugin.L.LogWarning("Back to main menu: no options panel");
        }
        catch (Exception e) { Plugin.L.LogWarning("Back to main menu: " + e.Message); }
    }

    private static bool Pressed(Func<Keyboard, UnityEngine.InputSystem.Controls.KeyControl> key)
    {
        try { var kb = Keyboard.current; return kb != null && key(kb).wasPressedThisFrame; }
        catch { return false; }
    }
}
