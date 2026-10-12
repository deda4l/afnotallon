using System;
using System.Collections.Generic;
using AfallonCoop.Session;
using AfallonCoop.World;
using BLINK.RPGBuilder.Combat;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Group management (key O, like a raid/party window): the members in group order (players, each followed by their
/// bot companions; disconnected players greyed, their place kept). Select one; the leader removes members or bots and
/// hands the lead to another player; everyone can leave. Clicking one's own portrait offers "Leave group".
/// </summary>
internal static class GroupPanel
{
    private static GameObject _canvas, _panel, _menu;
    private static object _selected; // a member key (string) or a bot (CombatEntity)
    private static string _sig = "";
    private static float _nextCheck;

    private static readonly Color Text = new(0.93f, 0.85f, 0.70f, 1f);
    private static readonly Color Dim = new(0.60f, 0.56f, 0.50f, 1f);
    private static readonly Color Back = new(0.07f, 0.045f, 0.04f, 0.95f);
    private static readonly Color Row = new(0.16f, 0.11f, 0.08f, 0.9f);
    private static readonly Color RowSelected = new(0.45f, 0.30f, 0.12f, 0.95f);
    private static readonly Color ButtonColor = new(0.42f, 0.10f, 0.08f, 1f);

    public static bool IsOpen => _panel != null && _panel.activeSelf;

    public static void Update()
    {
        if (!CoopManager.Connected || !GameInfo.InWorld) { Close(); CloseMenu(); return; }
        if (KeyO() && !Typing()) { if (IsOpen) Close(); else Open(); }
        HookOwnPortrait();
        if (IsOpen && Time.unscaledTime >= _nextCheck)
        {
            _nextCheck = Time.unscaledTime + 0.5f;
            if (Signature() != _sig) Build();
        }
    }

    private static bool KeyO()
    {
        try { var kb = Keyboard.current; return kb != null && kb.oKey.wasPressedThisFrame; } catch { return false; }
    }

    /// <summary>Typing in chat or another text field: keys are text then.</summary>
    private static bool Typing()
    {
        try
        {
            var sel = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
            return sel != null && (sel.GetComponent<TMP_InputField>() != null || sel.GetComponent<InputField>() != null);
        }
        catch { return false; }
    }

    // ---------------- canvas & widgets ----------------

    private static void EnsureCanvas()
    {
        if (_canvas != null) return;
        _canvas = new GameObject("AfCoop_GroupUI");
        Object.DontDestroyOnLoad(_canvas);
        var c = _canvas.AddComponent<Canvas>();
        c.renderMode = RenderMode.ScreenSpaceOverlay;
        c.sortingOrder = 410;
        var scaler = _canvas.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        _canvas.AddComponent<GraphicRaycaster>();
    }

    private static GameObject Box(Transform parent, string name, Color color)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.AddComponent<Image>().color = color;
        return go;
    }

    /// <summary>The game's own look: a button of its target menu (right-click on a portrait), else a plain one.</summary>
    private static GameObject ButtonTemplate()
    {
        try
        {
            var menu = BLINK.RPGBuilder.UI.TargetContextMenu.Instance;
            if (menu != null && menu.inviteButton != null) return menu.inviteButton;
        }
        catch { }
        return null;
    }

    /// <summary>The frame of the game's target menu, put on our window (sprite, slicing, colour).</summary>
    private static void GameFrame(Image img)
    {
        try
        {
            var menu = BLINK.RPGBuilder.UI.TargetContextMenu.Instance;
            var src = menu != null ? menu.GetComponent<Image>() ?? menu.GetComponentInChildren<Image>(true) : null;
            if (src == null || src.sprite == null) return;
            img.sprite = src.sprite; img.type = src.type; img.color = src.color; img.pixelsPerUnitMultiplier = src.pixelsPerUnitMultiplier;
        }
        catch { }
    }

    private static Button Button(Transform parent, string label, Color color, Action onClick, float w, float h, float x, float y, Color? textColor = null)
    {
        var template = ButtonTemplate();
        GameObject go;
        Button b;
        if (template != null)
        {
            go = Object.Instantiate(template, parent);
            go.name = "AfCoop_Btn_" + label;
            go.SetActive(true);
            UiKit.StripLocalization(go);
            UiKit.SetLabel(go, label);
            b = go.GetComponentInChildren<Button>(true);
            b.onClick = new Button.ButtonClickedEvent();
            var tmp = go.GetComponentInChildren<TMP_Text>(true);
            if (tmp != null && textColor.HasValue) tmp.color = textColor.Value;
            if (color == RowSelected) { var bg = go.GetComponent<Image>(); if (bg != null) bg.color = RowSelected; }
        }
        else
        {
            go = Box(parent, "AfCoop_Btn_" + label, color);
            b = go.AddComponent<Button>();
            b.targetGraphic = go.GetComponent<Image>();
            var t = UiKit.Text(go.transform, label, 18, textColor ?? Text, TextAlignmentOptions.Center);
            var r = UiKit.Rect(t); r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one; r.offsetMin = new Vector2(8, 0); r.offsetMax = new Vector2(-8, 0);
        }
        UiKit.Place(go.GetComponent<RectTransform>(), x, y, w, h);
        b.onClick.AddListener(UiKit.Action(onClick));
        return b;
    }

    /// <summary>
    /// Shown like the game's own windows: its panel handler frees the cursor and leaves the action-mode camera while
    /// the window is open (and gives them back when it closes).
    /// </summary>
    private static void AsGamePanel(GameObject go)
    {
        if (go.GetComponent<CanvasGroup>() == null) go.AddComponent<CanvasGroup>();
        try { go.AddComponent<UIPanelHandler>(); }
        catch (Exception e) { Plugin.L.LogWarning("Group window panel handler: " + e.Message); go.AddComponent<UnlockCursorOnEnable>(); }
    }

    // ---------------- the window ----------------

    // The game's own Friends window is the model: same chrome, header, list rows and buttons (a clone of it, with
    // its logic taken out before it ever runs), filled with the group.
    private const string FriendsPath = "RPG_Builder_ESSENTIALS(Clone)/Game_UI_Canvas/--- PANELS ---/Friends";
    private static Transform _rows;
    private static GameObject _rowTemplate, _empty;
    private static Button _remove, _promote, _leave, _membersTab, _lootTab;
    private static int _page; // 0 members, 1 loot
    private static TMP_Text _count, _detail, _status;

    private static bool BuildWindow()
    {
        var friends = GameObject.Find(FriendsPath);
        if (friends == null) return false;
        // cloned under an inactive holder: nothing of it wakes up before its Friends logic is removed
        var holder = new GameObject("AfCoop_Holder");
        holder.SetActive(false);
        holder.transform.SetParent(friends.transform.parent, false);
        var w = Object.Instantiate(friends, holder.transform);
        w.name = "AfCoop_Group";
        foreach (var c in w.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (c == null) continue;
            string n = c.GetIl2CppType().Name;
            if (n is "AdventurerFriendsPanel" or "AdventurerFriendRow" or "ResizableWindow" or "WindowResizeHandle" or "TMP_InputField")
                Object.DestroyImmediate(c);
        }
        UiKit.StripLocalization(w);
        Transform T(string path) => w.transform.Find(path);
        foreach (var gone in new[] { "Search friends" }) { var t = T(gone); if (t != null) Object.DestroyImmediate(t.gameObject); }
        // the window's two tabs: the members, and what the group rolls for (WoW's loot settings)
        _membersTab = Retarget(T("Friends tab"), "Members");
        _lootTab = Retarget(T("Find tab"), "Loot");
        _membersTab?.onClick.AddListener(UiKit.Action(() => { _page = 0; _selected = null; Build(); }));
        _lootTab?.onClick.AddListener(UiKit.Action(() => { _page = 1; _selected = null; Build(); }));
        for (int i = w.transform.childCount - 1; i >= 0; i--)
            if (w.transform.GetChild(i).name.StartsWith("Resize")) Object.DestroyImmediate(w.transform.GetChild(i).gameObject);

        var title = T("Header/Text")?.GetComponent<UnityEngine.UI.Text>();
        if (title != null) title.text = "Group";
        var close = T("Header/Button (Close)")?.GetComponent<Button>();
        if (close != null) { close.onClick = new Button.ButtonClickedEvent(); close.onClick.AddListener(UiKit.Action(Close)); }
        _count = T("Friend count")?.GetComponent<TMP_Text>();
        var nameHead = T("Name heading")?.GetComponent<TMP_Text>(); if (nameHead != null) nameHead.text = "Name";
        var regionHead = T("Region heading")?.GetComponent<TMP_Text>(); if (regionHead != null) regionHead.text = "Status";
        _detail = T("Selection detail")?.GetComponent<TMP_Text>();
        _status = T("Status message")?.GetComponent<TMP_Text>();
        _empty = T("Friend list/Empty list")?.gameObject;
        _rows = T("Friend list/Viewport/Content");
        _rowTemplate = _rows != null ? _rows.Find("Friend row")?.gameObject : null;
        _remove = Retarget(T("Remove friend"), "Remove");
        _promote = Retarget(T("Primary action"), "Make leader");
        var leaveGo = T("Primary action") != null ? Object.Instantiate(T("Primary action").gameObject, w.transform) : null;
        if (leaveGo != null)
        {
            leaveGo.name = "Leave group";
            var rt = leaveGo.GetComponent<RectTransform>();
            var left = T("Remove friend")?.GetComponent<RectTransform>();
            var right = T("Primary action")?.GetComponent<RectTransform>();
            if (left != null && right != null)
            {
                rt.anchorMin = left.anchorMin; rt.anchorMax = left.anchorMax; rt.pivot = left.pivot;
                // midway between Remove (left) and Make leader (right), in Remove's frame of reference
                var mid = (left.position + right.position) * 0.5f;
                rt.position = new Vector3(mid.x, left.position.y, left.position.z);
            }
            _leave = Retarget(leaveGo.transform, "Leave group");
        }
        var cg = w.GetComponent<CanvasGroup>() ?? w.AddComponent<CanvasGroup>();
        cg.alpha = 1f; cg.interactable = true; cg.blocksRaycasts = true;
        w.transform.SetParent(friends.transform.parent, false);
        Object.Destroy(holder);
        AsGamePanel(w);
        _panel = w;
        return true;
    }

    private static Button Retarget(Transform t, string label)
    {
        if (t == null) return null;
        var b = t.GetComponent<Button>();
        if (b == null) return null;
        b.onClick = new Button.ButtonClickedEvent();
        UiKit.SetLabel(t.gameObject, label);
        return b;
    }

    public static void Open()
    {
        if (_panel == null && !BuildWindow())
        {
            Hud.Notify("Group window unavailable (the game's Friends window was not found)");
            return;
        }
        bool was = _panel.activeSelf;
        _panel.SetActive(true);
        _panel.transform.SetAsLastSibling();
        if (!was) PanelEvent(open: true);
        Build();
    }

    public static void Close()
    {
        if (_panel != null && _panel.activeSelf)
        {
            _panel.SetActive(false);
            PanelEvent(open: false); // the game gives the camera and cursor back on this, as for its own windows
        }
        _selected = null;
    }

    private static void PanelEvent(bool open)
    {
        try
        {
            var ui = UIEvents.Instance;
            if (ui == null) return;
            if (open) ui.OnOpenPanel("AfCoop_Group"); else ui.OnClosePanel("AfCoop_Group");
            // The panel handler told the character controller a UI window opened; the closing side says it closed.
            if (!open) GameInfo.Player?.controllerEssentials?.GameUIPanelAction(false);
        }
        catch (Exception e) { Plugin.L.LogWarning("Group window event: " + e.Message); }
    }

    private sealed class Entry
    {
        public string Name, Status; public bool Online; public bool Leader; public object Key;
        public GroupMemberInfo Member; public CombatEntity Bot; public byte BotOwner;
        public string Label => Name + "|" + Status;
    }

    private static List<Entry> Entries()
    {
        var list = new List<Entry>();
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        if (g == null) return list;
        foreach (var m in g.Members)
        {
            bool me = m.Online && m.Id == s.LocalId;
            bool leader = m.Key == g.LeaderKey;
            list.Add(new Entry
            {
                Name = (m.Online ? Groups.Name(m.Id) : m.Name) + (me ? " (you)" : ""), // the leader is marked in Status
                Status = !m.Online ? "Disconnected" : leader ? "Leader" : "Online",
                Online = m.Online, Leader = leader, Key = m.Key, Member = m,
            });
            if (!m.Online) continue;
            foreach (var bot in Bots(m.Id, me))
                list.Add(new Entry { Name = "    " + (bot.GetNPCData()?.entryDisplayName ?? bot.name), Status = "Companion", Online = true, Key = bot, Bot = bot, BotOwner = m.Id });
        }
        return list;
    }

    private static IEnumerable<CombatEntity> Bots(byte player, bool local)
    {
        if (local)
        {
            var pets = GameInfo.Player?.GetCurrentPets();
            if (pets != null) foreach (var p in pets) { if (IsBot(p)) yield return p; }
        }
        else foreach (var p in MobSync.PetCopiesOf(player)) if (IsBot(p)) yield return p;
    }

    private static bool IsBot(CombatEntity e)
    {
        try { var n = e?.GetNPCData(); return n != null && (int)n.npcType == 9; } catch { return false; }
    }

    private static string Signature()
    {
        var s = CoopManager.Session;
        var parts = new List<string> { s?.MyGroup?.LeaderKey ?? "-", _selected?.GetHashCode().ToString() ?? "", _page.ToString(), GroupLoot.MyMask().ToString() };
        foreach (var e in Entries()) parts.Add(e.Label);
        return string.Join("|", parts);
    }

    private static readonly List<GameObject> RowObjects = new();

    /// <summary>Redraw if open (the group's loot choice changed).</summary>
    public static void Refresh() { if (IsOpen) Build(); }

    private static void Build()
    {
        if (_panel == null) return;
        _sig = Signature();
        if (_page == 1) { BuildLoot(); return; }
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        var entries = Entries();
        foreach (var r in RowObjects) if (r != null) Object.Destroy(r);
        RowObjects.Clear();
        if (_count != null) _count.text = g == null ? "" : $"{g.Size} / {GroupRegistry.MaxSize}";
        if (_empty != null)
        {
            _empty.SetActive(g == null);
            var t = _empty.GetComponent<TMP_Text>();
            if (t != null) t.text = "You are not in a group.\nRight-click a player's portrait to invite them.";
        }
        float rowH = _rowTemplate != null ? _rowTemplate.GetComponent<RectTransform>().rect.height + 4f : 40f;
        var tplPos = _rowTemplate != null ? _rowTemplate.GetComponent<RectTransform>().anchoredPosition : Vector2.zero;
        int index = 0;
        if (_rowTemplate != null && _rows != null)
            foreach (var e in entries)
            {
                var entry = e;
                var row = Object.Instantiate(_rowTemplate, _rows);
                row.name = "AfCoop_Row";
                row.SetActive(true);
                if (_rows.GetComponent<LayoutGroup>() == null)
                    row.GetComponent<RectTransform>().anchoredPosition = tplPos - new Vector2(0, rowH * index);
                index++;
                // the friend row's own action button (invite/remove) has no meaning here
                foreach (var inner in row.GetComponentsInChildren<Button>(true))
                    if (inner.gameObject != row) inner.gameObject.SetActive(false);
                var texts = row.GetComponentsInChildren<TMP_Text>(true);
                for (int i = 0; i < texts.Length; i++) texts[i].text = i == 0 ? e.Name : i == 1 ? e.Status : "";
                foreach (var t in texts) t.color = e.Online ? t.color : new Color(0.55f, 0.52f, 0.48f, 1f);
                var b = row.GetComponent<Button>();
                if (b != null)
                {
                    b.onClick = new Button.ButtonClickedEvent();
                    b.onClick.AddListener(UiKit.Action(() => { _selected = entry.Key; Build(); }));
                }
                var img = row.GetComponent<Image>();
                if (img != null && Equals(_selected, e.Key)) img.color = new Color(img.color.r + 0.25f, img.color.g + 0.15f, img.color.b, Mathf.Max(img.color.a, 0.6f));
                RowObjects.Add(row);
            }
        var content = _rows != null ? _rows.GetComponent<RectTransform>() : null;
        if (content != null && _rows.GetComponent<LayoutGroup>() == null) content.sizeDelta = new Vector2(content.sizeDelta.x, rowH * index + 8f);

        bool leader = g != null && g.Leader == s.LocalId;
        var target = entries.Find(e => Equals(e.Key, _selected));
        bool other = target != null && !(target.Member != null && target.Member.Online && target.Member.Id == s.LocalId);
        if (_detail != null) _detail.text = target != null ? target.Name.Trim() + " - " + target.Status : (g != null ? "Select a member." : "");
        if (_status != null) _status.text = g == null ? "" : leader ? "You lead the group." : "Leader: " + (g.LeaderMember?.Name ?? "?");
        void Set(Button b, bool on, Action a)
        {
            if (b == null) return;
            b.gameObject.SetActive(on);
            b.onClick = new Button.ButtonClickedEvent();
            if (a != null) b.onClick.AddListener(UiKit.Action(a));
        }
        Set(_remove, leader && other, () => { if (target != null) Kick(target); });
        if (_remove != null && target != null) UiKit.SetLabel(_remove.gameObject, target.Bot != null ? "Send away" : "Remove");
        Set(_promote, leader && other && target?.Member != null && target.Member.Online, () => { if (target?.Member != null) s.GroupPromote(target.Member.Id); });
        Set(_leave, g != null, () => { s.GroupLeave(); Close(); });
    }

    /// <summary>
    /// The loot tab: each of the game's rarities, rolled for by the group in its dungeons or not (armor and weapons;
    /// the rest is everyone's own loot). The leader switches them; the others see the choice.
    /// </summary>
    private static void BuildLoot()
    {
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        bool leader = g != null && g.Leader == s.LocalId;
        foreach (var r in RowObjects) if (r != null) Object.Destroy(r);
        RowObjects.Clear();
        if (_empty != null) _empty.SetActive(g == null);
        if (_count != null) _count.text = "";
        float rowH = _rowTemplate != null ? _rowTemplate.GetComponent<RectTransform>().rect.height + 4f : 40f;
        var tplPos = _rowTemplate != null ? _rowTemplate.GetComponent<RectTransform>().anchoredPosition : Vector2.zero;
        int mask = GroupLoot.MyMask();
        var rarities = GroupLoot.Rarities();
        if (g != null && _rowTemplate != null && _rows != null)
            for (int i = 0; i < rarities.Count; i++)
            {
                int rarity = i;
                bool on = (mask & (1 << i)) != 0;
                var row = Object.Instantiate(_rowTemplate, _rows);
                row.name = "AfCoop_LootRow";
                row.SetActive(true);
                if (_rows.GetComponent<LayoutGroup>() == null) row.GetComponent<RectTransform>().anchoredPosition = tplPos - new Vector2(0, rowH * i);
                var texts = row.GetComponentsInChildren<TMP_Text>(true);
                foreach (var t in texts)
                {
                    if (t.GetComponentInParent<Button>() != null && t.GetComponentInParent<Button>().gameObject != row) continue;
                    t.text = t.name switch
                    {
                        "Name" => rarities[i],
                        "Level and class" => "Armor and weapons",
                        "Region and status" => on ? "Group roll" : "Personal loot",
                        _ => t.text,
                    };
                    if (t.name == "Name") t.color = GroupLoot.RarityColor(i);
                }
                foreach (var b in row.GetComponentsInChildren<Button>(true))
                {
                    if (b.gameObject == row) { b.onClick = new Button.ButtonClickedEvent(); continue; }
                    b.gameObject.SetActive(true);
                    b.onClick = new Button.ButtonClickedEvent();
                    UiKit.SetLabel(b.gameObject, on ? "Roll: on" : "Roll: off");
                    b.interactable = leader;
                    if (leader) b.onClick.AddListener(UiKit.Action(() => GroupLoot.Toggle(rarity)));
                }
                RowObjects.Add(row);
            }
        if (_detail != null) _detail.text = g == null ? "" : "In a dungeon, checked rarities drop once for the group and are rolled for.";
        if (_status != null) _status.text = g == null ? "" : leader ? "You choose what the group rolls for." : "The group leader chooses what is rolled for.";
        if (_remove != null) _remove.gameObject.SetActive(false);
        if (_promote != null) _promote.gameObject.SetActive(false);
        if (_leave != null) _leave.gameObject.SetActive(false);
    }

    private static void Kick(Entry e)
    {
        var s = CoopManager.Session;
        if (e.Bot != null)
        {
            if (e.BotOwner == s.LocalId) e.Bot.TryCast<MobCombatEntity>()?.LeavePlayerParty(); // our own bot: dismiss it here
            else s.GroupDismissBot(e.BotOwner, e.Bot.GetNPCData().ID);
        }
        else if (e.Member != null) s.GroupKick(e.Member.Key);
        _selected = null;
    }

    // ---------------- own portrait: "Leave group" ----------------

    private static IntPtr _hooked;

    /// <summary>Clicking the local player's portrait opens a small menu (the game has none there).</summary>
    private static float _nextHook;

    private static void HookOwnPortrait()
    {
        if (Time.unscaledTime < _nextHook) return;
        _nextHook = Time.unscaledTime + 2f;
        try
        {
            // The HUD's own portrait: HUD_Main/Avatar Frame (its images do not take clicks by themselves).
            var frame = GameObject.Find("RPG_Builder_ESSENTIALS(Clone)/Game_UI_Canvas/--- HUD ---/HUD_Main/Avatar Frame");
            if (frame == null || frame.Pointer == _hooked) return;
            _hooked = frame.Pointer;
            var img = frame.GetComponent<Image>() ?? frame.AddComponent<Image>();
            if (img.sprite == null && img.color.a > 0f) img.color = new Color(0, 0, 0, 0);
            img.raycastTarget = true;
            var trigger = frame.GetComponent<EventTrigger>() ?? frame.AddComponent<EventTrigger>();
            var entry = new EventTrigger.Entry { eventID = EventTriggerType.PointerClick };
            entry.callback.AddListener(DelegateSupportAction(_ => OwnPortraitClicked()));
            trigger.triggers.Add(entry);
            Plugin.L.LogInfo("Own portrait: click opens the group menu");
        }
        catch (Exception e) { Plugin.L.LogWarning("Own portrait hook: " + e.Message); _hooked = IntPtr.Zero; }
    }

    private static UnityEngine.Events.UnityAction<BaseEventData> DelegateSupportAction(Action<BaseEventData> a) =>
        Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<UnityEngine.Events.UnityAction<BaseEventData>>(a);

    private static void OwnPortraitClicked()
    {
        var s = CoopManager.Session;
        var me = GameInfo.Player;
        if (s?.MyGroup == null || me == null) return;
        try
        {
            // the game's own target menu, for oneself: just "Leave group" (Groups patches it)
            var menu = BLINK.RPGBuilder.UI.TargetContextMenu.Instance;
            var mouse = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
            if (menu != null) menu.Show(me, mouse);
        }
        catch (Exception e) { Plugin.L.LogWarning("Own portrait menu: " + e.Message); }
    }

    private static void CloseMenu()
    {
        if (_menu != null) Object.Destroy(_menu);
        _menu = null;
    }
}
