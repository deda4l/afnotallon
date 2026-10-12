using System;
using System.Collections.Generic;
using System.Linq;
using AfallonCoop.Session;
using AfallonCoop.Transport;
using BLINK.RPGBuilder.Managers;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Adds a CO-OP button under CONTINUE in the main menu. It opens a panel: host (Steam or LAN/IP) or join
/// (Steam friends, LAN list or IP, with the host's password if it has one), then hands over to the game's own
/// character select / creation.
/// </summary>
internal static class MainMenuUI
{
    private enum Page { Main, Host, HostCharacter, Join, JoinCharacter }

    private static IntPtr _injectedFor;
    private static Page _page;
    private static bool _open;
    private static Button _template;
    private static Button _coopButton;
    private static string _coopLabel = "";
    private static GameObject _rowTemplate;
    private static GameObject _inputTemplate;
    private static readonly List<(TMP_InputField field, Action<string> set)> Inputs = new(); // typed text kept as it is typed
    private static CanvasGroup _home;
    private static GameObject _panel;
    private static string _sig = "";
    private static float _nextCheck;
    private static List<SteamPlatform.FriendHost> _friends = new();

    private static readonly Color TextColor = new(0.93f, 0.85f, 0.70f, 1f);
    private static readonly Color DimColor = new(0.70f, 0.64f, 0.55f, 1f);
    private static readonly Color ErrorColor = new(1f, 0.45f, 0.35f, 1f);

    private static MainMenuManager Menu
    {
        get { try { return MainMenuManager.Instance; } catch { return null; } }
    }

    public static void Update()
    {
        var mm = Menu;
        if (mm == null)
        {
            _injectedFor = IntPtr.Zero; _panel = null; _open = false; _coopButton = null; _coopLabel = "";
            return;
        }
        if (_injectedFor != mm.Pointer)
        {
            if (!Inject(mm)) return;
            _injectedFor = mm.Pointer;
        }
        // The menu button says when a host is chosen and waits for the character.
        string label = CoopManager.PendingHost != CoopMode.Off ? "CO-OP: HOSTING ON PLAY"
            : CoopManager.PendingJoin.Length > 0 ? "CO-OP: JOINING ON PLAY" : "CO-OP";
        if (_coopButton != null && label != _coopLabel) { _coopLabel = label; UiKit.SetLabel(_coopButton.gameObject, label); }
        if (_open && Time.unscaledTime >= _nextCheck)
        {
            _nextCheck = Time.unscaledTime + 0.35f;
            KeepTyped();
            var sig = Signature();
            if (sig != _sig) Rebuild();
        }
    }

    private static void KeepTyped()
    {
        foreach (var (field, set) in Inputs) if (field != null) set(field.text);
    }

    // ---------------- injection ----------------

    private static bool Inject(MainMenuManager mm)
    {
        _home = mm.HomeCG;
        if (_home == null) return false;
        var buttons = _home.GetComponentsInChildren<Button>(true).Where(b => b != null && !b.name.StartsWith("AfCoop")).ToList();
        if (buttons.Count == 0) return false;

        Button cont = buttons.FirstOrDefault(b => b.name == "Continue")
            ?? buttons.FirstOrDefault(b =>
            {
                var t = b.GetComponentInChildren<TMP_Text>(true);
                return t != null && t.text != null && t.text.Trim().ToUpperInvariant().Contains("CONTINUE");
            });
        if (cont == null) return false;

        var label = cont.GetComponentInChildren<TMP_Text>(true);
        if (label != null) UiKit.Font = label.font;
        _template = cont;
        _inputTemplate = mm.characterNameIF != null ? mm.characterNameIF.gameObject : null;

        // Each menu button sits in its own frame ("Continue" > "Continue"), absolutely positioned; EXIT GAME lives
        // elsewhere in the canvas. Rows = every active button of the same size in the same column.
        var canvas = cont.GetComponentInParent<Canvas>().rootCanvas;
        var size = UiKit.Rect(cont).sizeDelta;
        float x = cont.transform.position.x;
        var rows = canvas.GetComponentsInChildren<Button>(false)
            .Where(b => b != null && !b.name.StartsWith("AfCoop") && Math.Abs(b.transform.position.x - x) < 2f
                        && (UiKit.Rect(b).sizeDelta - size).sqrMagnitude < 1f)
            .Select(Row).GroupBy(t => t.Pointer).Select(g => g.First())
            .OrderByDescending(t => t.position.y).ToList();

        var contRow = Row(cont);
        int idx = rows.FindIndex(r => r.Pointer == contRow.Pointer);
        float step = idx >= 0 && idx + 1 < rows.Count ? rows[idx].position.y - rows[idx + 1].position.y
            : idx > 0 ? rows[idx - 1].position.y - rows[idx].position.y
            : size.y * canvas.transform.lossyScale.y * 1.15f;

        _rowTemplate = contRow.gameObject;
        var rowGo = Object.Instantiate(contRow.gameObject, contRow.parent);
        rowGo.name = "AfCoop_Row";
        rowGo.transform.SetSiblingIndex(contRow.GetSiblingIndex() + 1);
        rowGo.transform.position = contRow.position - new Vector3(0, step, 0);
        UiKit.StripLocalization(rowGo);
        var coop = rowGo.GetComponentInChildren<Button>(true);
        coop.name = "AfCoop_Coop";
        UiKit.SetLabel(coop.gameObject, "CO-OP");
        _coopButton = coop;
        coop.onClick = new Button.ButtonClickedEvent();
        coop.onClick.AddListener(UiKit.Action(OpenPanel));

        foreach (var r in rows)
            if (r.position.y < contRow.position.y - 1f)
                r.position -= new Vector3(0, step, 0);

        Plugin.L.LogInfo($"Main menu: CO-OP added under '{cont.name}', moved {rows.Count(r => r.position.y < contRow.position.y - step)} rows down by {step:0.0}");
        return true;
    }

    /// <summary>The object to move for a menu button: its frame if the frame holds only this button.</summary>
    private static Transform Row(Button b)
    {
        var parent = b.transform.parent;
        if (parent != null && parent.GetComponentsInChildren<Button>(true).Length == 1) return parent;
        return b.transform;
    }

    // ---------------- panel ----------------

    private static void OpenPanel()
    {
        if (_panel == null)
        {
            // Sibling of the menu with the menu's scale, so cloned widgets keep their proportions.
            _panel = new GameObject("AfCoop_Panel");
            _panel.transform.SetParent(_home.transform.parent, false);
            _panel.transform.localScale = _home.transform.localScale;
            var img = _panel.AddComponent<Image>();
            img.color = new Color(0.07f, 0.045f, 0.04f, 0.95f);
            var r = UiKit.Rect(img);
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.pivot = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = Vector2.zero;
        }
        _open = true;
        _panel.SetActive(true);
        SetHomeVisible(false);
        _page = CoopManager.Active ? _page
            : CoopManager.PendingHost != CoopMode.Off ? Page.HostCharacter
            : CoopManager.PendingJoin.Length > 0 ? Page.JoinCharacter : Page.Main;
        Rebuild();
    }

    private static void ClosePanel()
    {
        _open = false;
        if (_panel != null) _panel.SetActive(false);
        SetHomeVisible(true);
    }

    private static void SetHomeVisible(bool v)
    {
        if (_home == null) return;
        _home.alpha = v ? 1f : 0f;
        _home.interactable = v;
        _home.blocksRaycasts = v;
    }

    private static void Go(Page p)
    {
        _page = p;
        if (p == Page.Join)
        {
            _friends = CoopManager.SteamReady ? SteamPlatform.FriendsHosting() : new();
            CoopManager.StartLanDiscovery();
        }
        Rebuild();
    }

    private static string Signature()
    {
        var s = CoopManager.Session;
        string players = s == null ? "" : string.Join(",", s.Players.Values.Select(p => p.Name + p.CharacterName));
        return $"{_page}|{CoopManager.Mode}|{CoopManager.PendingHost}|{CoopManager.PendingJoin}|{s?.State}|{players}|{CoopManager.LastError}|{CoopManager.Busy}|" +
               $"{CoopManager.Discovering}|{CoopManager.LanHosts.Count}|{_friends.Count}";
    }

    private static float _y;
    private static float _w;

    private static void Rebuild()
    {
        if (_panel == null) return;
        _sig = Signature();
        for (int i = _panel.transform.childCount - 1; i >= 0; i--)
            Object.Destroy(_panel.transform.GetChild(i).gameObject);
        Inputs.Clear();

        var tsize = _rowTemplate.GetComponent<RectTransform>().sizeDelta;
        _w = Math.Max(tsize.x + 80f, 560f);
        _y = -28f;

        var s = CoopManager.Session;
        if (CoopManager.Active && s.IsHost) BuildHosting(s);
        else if (CoopManager.Active) BuildClient(s);
        else if (_page == Page.Host) BuildHost();
        else if (_page == Page.HostCharacter) BuildHostCharacter();
        else if (_page == Page.JoinCharacter) BuildJoinCharacter();
        else if (_page == Page.Join) BuildJoin();
        else BuildMain();

        var r = UiKit.Rect(_panel.GetComponent<Image>());
        r.sizeDelta = new Vector2(_w, -_y + 24f);
    }

    // ---- widgets (top-down) ----

    private static void Title(string t) => Line(t, 34, TextColor, 52);

    private static void Line(string t, float size = 22, Color? color = null, float h = 0)
    {
        var txt = UiKit.Text(_panel.transform, t, size, color ?? TextColor, TextAlignmentOptions.Center);
        float height = h > 0 ? h : size * 1.35f * Math.Max(1, t.Count(c => c == '\n') + 1);
        UiKit.Place(txt, 0, _y, _w - 40, height);
        _y -= height + 4;
    }

    private static void Btn(string text, Action onClick)
    {
        var go = UiKit.Row(_rowTemplate, _panel.transform, text, onClick);
        var size = UiKit.Rect(go.GetComponent<RectTransform>()).sizeDelta;
        UiKit.Place(go.GetComponent<RectTransform>(), 0, _y, size.x, size.y);
        _y -= size.y + 10;
    }

    private static void Gap(float h = 10) => _y -= h;

    /// <summary>A scrolling list of rows (up to <paramref name="visible"/> shown), each a button.</summary>
    private static void List(IEnumerable<(string text, Action onClick)> rows, int visible = 3)
    {
        var all = rows.ToList();
        float rowH = UiKit.Rect(_rowTemplate.GetComponent<RectTransform>()).sizeDelta.y;
        float h = Math.Min(Math.Max(all.Count, 1), visible) * (rowH + 6f);
        var list = UiKit.ScrollList(_panel.transform, _y, _w - 60f, h);
        foreach (var (text, onClick) in all) UiKit.Row(_rowTemplate, list, text, onClick);
        _y -= h + 8f;
    }

    private static void Input(string placeholder, string value, Action<string> set, bool secret = false)
    {
        if (_inputTemplate == null) { Line("(text input unavailable)", 18, DimColor); return; }
        var go = Object.Instantiate(_inputTemplate, _panel.transform);
        go.name = "AfCoop_Input";
        go.SetActive(true);
        UiKit.StripLocalization(go);
        var f = go.GetComponent<TMP_InputField>();
        f.onValueChanged = new TMP_InputField.OnChangeEvent();
        f.onEndEdit = new TMP_InputField.SubmitEvent();
        f.onSubmit = new TMP_InputField.SubmitEvent();
        f.characterLimit = 64;
        f.contentType = secret ? TMP_InputField.ContentType.Password : TMP_InputField.ContentType.Standard;
        f.text = value ?? "";
        if (f.placeholder != null)
        {
            var ph = f.placeholder.GetComponent<TMP_Text>();
            if (ph != null) ph.text = placeholder;
        }
        var size = UiKit.Rect(f).sizeDelta;
        UiKit.Place(f, 0, _y, _w - 60f, Math.Max(size.y, 44)); // the template (the character name field) is wider than the panel
        _y -= Math.Max(size.y, 44) + 10;
        Inputs.Add((f, set));
    }

    private static void Error()
    {
        if (!string.IsNullOrEmpty(CoopManager.LastError)) Line(CoopManager.LastError, 20, ErrorColor);
    }

    // ---- pages ----

    private static void BuildMain()
    {
        Title("AFNOTALLON");
        Line("Play the world together, up to 5 players.", 20, DimColor);
        Gap();
        Btn("HOST GAME", () => Go(Page.Host));
        Btn("JOIN GAME", () => Go(Page.Join));
        Btn("BACK", ClosePanel);
        Error();
        Line($"mod {Plugin.Version}", 16, DimColor);
    }

    private static void BuildHost()
    {
        Title("HOST GAME");
        bool steam = CoopManager.SteamReady;
        Btn(steam ? "HOST VIA STEAM" : "STEAM UNAVAILABLE", steam ? () => { CoopManager.PrepareHost(CoopMode.HostSteam); Go(Page.HostCharacter); } : null);
        Line("Your Steam friends see your game in CO-OP > JOIN GAME.", 18, DimColor);
        Gap();
        Btn("HOST VIA LAN / IP", () => { CoopManager.PrepareHost(CoopMode.HostLan); Go(Page.HostCharacter); });
        Line($"Same network, Radmin VPN / ZeroTier / Hamachi, or port {Plugin.CfgPort.Value} UDP forwarded.", 18, DimColor);
        Gap();
        Line("Password (optional: only who knows it can join)", 20);
        Input("no password", Plugin.CfgHostPassword.Value, v => Plugin.CfgHostPassword.Value = v, secret: true);
        Btn("BACK", () => Go(Page.Main));
        Error();
    }

    private static void BuildHostCharacter()
    {
        bool steam = CoopManager.PendingHost == CoopMode.HostSteam;
        Title(steam ? "HOST VIA STEAM" : "HOST VIA LAN / IP");
        Line("Choose the character to host with.\nThe game opens to others when you enter the world;\nyour address is in the Esc menu (CO-OP INFO).", 18, DimColor);
        Gap();
        Btn("PLAY: CHOOSE CHARACTER", Continue);
        Btn("PLAY: NEW CHARACTER", NewCharacter);
        Btn("BACK", () => { CoopManager.CancelPendingHost(); Go(Page.Host); });
        Error();
    }

    private static void Join(string label, Action join)
    {
        CoopManager.PrepareJoin(label, join);
        Go(Page.JoinCharacter);
    }

    private static void BuildJoinCharacter()
    {
        Title("JOIN " + CoopManager.PendingJoin.ToUpperInvariant());
        Line("Choose your character.\nYou join the game when you enter the world.", 18, DimColor);
        Gap();
        Btn("PLAY: CHOOSE CHARACTER", Continue);
        Btn("PLAY: NEW CHARACTER", NewCharacter);
        Btn("BACK", () => { CoopManager.CancelPendingJoin(); Go(Page.Join); });
        Error();
    }

    private static void BuildJoin()
    {
        Title("JOIN GAME");
        Line("Password (if the host set one)", 20);
        Input("no password", Plugin.CfgJoinPassword.Value, v => Plugin.CfgJoinPassword.Value = v, secret: true);
        if (CoopManager.SteamReady)
        {
            Line("Steam friends hosting:", 22);
            if (_friends.Count == 0) Line("none right now", 18, DimColor);
            else List(_friends.Select(f => ("JOIN " + f.Name.ToUpperInvariant(), (Action)(() => Join(f.Name, () => CoopManager.JoinFriendLobby(f.Lobby))))));
        }
        Line(CoopManager.Discovering ? "Searching the local network..." : "Local network:", 22);
        if (!CoopManager.Discovering && CoopManager.LanHosts.Count == 0) Line("none found", 18, DimColor);
        else List(CoopManager.LanHosts.Select(h =>
        {
            var parts = h.Info.Split('|'); // name | players | mod version | password
            string label = $"{(parts.Length > 0 ? parts[0] : "?")} ({(parts.Length > 1 ? parts[1] : "")}) {h.Address}{(parts.Length > 3 && parts[3] == "1" ? "  (PASSWORD)" : "")}";
            var addr = h.Address; var port = h.Port;
            return (label.ToUpperInvariant(), (Action)(() => Join($"{addr}:{port}", () => CoopManager.JoinLan(addr, port))));
        }));
        Btn("REFRESH", () => Go(Page.Join));
        Gap();
        Line("Host's IP[:port]", 22);
        Input("192.168.1.5", Plugin.CfgLastAddress.Value, v => Plugin.CfgLastAddress.Value = v);
        Btn("CONNECT", () =>
        {
            KeepTyped();
            string text = (Plugin.CfgLastAddress.Value ?? "").Trim();
            if (text.Length == 0) return;
            Join(text, () => CoopManager.JoinAddress(text));
        });
        Btn("BACK", () => Go(Page.Main));
        if (CoopManager.Busy) Line("Joining Steam lobby...", 20, DimColor);
        Error();
    }

    private static void BuildHosting(CoopSession s)
    {
        bool steam = CoopManager.Mode == CoopMode.HostSteam;
        Title(steam ? "HOSTING (STEAM)" : "HOSTING (LAN / IP)");
        if (steam) Line("Your Steam friends see your game in CO-OP > JOIN GAME.", 20, DimColor);
        else
        {
            var addrs = CoopManager.LocalAddresses();
            Line("Your addresses (port " + Plugin.CfgPort.Value + "):", 20, DimColor);
            Line(addrs.Count == 0 ? "unknown" : string.Join("\n", addrs.Take(4)), 24);
            Btn("COPY ADDRESS", () => GUIUtility.systemCopyBuffer = (addrs.FirstOrDefault() ?? "") + ":" + Plugin.CfgPort.Value);
        }
        Players(s);
        Btn("PLAY: CHOOSE CHARACTER", Continue);
        Btn("PLAY: NEW CHARACTER", NewCharacter);
        Btn("STOP HOSTING", () => { CoopManager.Leave(); Go(Page.Main); });
        Error();
    }

    private static void BuildClient(CoopSession s)
    {
        if (s.State != SessionState.Connected)
        {
            Title("CONNECTING");
            Line(s.Status, 20, DimColor);
            Btn("CANCEL", () => { CoopManager.Leave(); Go(Page.Join); });
            return;
        }
        var host = s.Players.TryGetValue(Protocol.ProtocolInfo.HostId, out var h) ? h.Name : "host";
        Title("CONNECTED");
        Line($"Host: {host}   ping {s.PingToHost} ms", 20, DimColor);
        Players(s);
        Btn("PLAY: CHOOSE CHARACTER", Continue);
        Btn("PLAY: NEW CHARACTER", NewCharacter);
        Btn("LEAVE", () => { CoopManager.Leave(); Go(Page.Main); });
        Error();
    }

    private static void Players(CoopSession s)
    {
        Gap(4);
        Line($"Players {s.PlayerCount}/{Protocol.ProtocolInfo.MaxPlayers}:", 22);
        var lines = s.Players.Values.OrderBy(p => p.Id).Select(p =>
            (p.Id == s.LocalId ? "> " : "") + p.Name + (string.IsNullOrEmpty(p.CharacterName) ? "" : $" ({p.CharacterName}, lvl {p.Level})"));
        Line(string.Join("\n", lines), 20, DimColor);
        Gap(6);
    }

    private static void Continue()
    {
        ClosePanel();
        Menu?.ClickContinue();
    }

    private static void NewCharacter()
    {
        ClosePanel();
        Menu?.ClickNewChar();
    }
}
