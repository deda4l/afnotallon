using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.AuctionHouse;
using BLINK.RPGBuilder.UIElements;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace AfallonCoop.Plugin;

/// <summary>
/// Letters between players at the game's mailboxes, as in WoW: the mailbox window gets two tabs, "Inbox" (the game's
/// own) and "Send Mail": to a player of the session or a friend, a subject, the letter, up to <see cref="Slots"/> items
/// (dragged from the bags or right-clicked there) and gold. The items and gold leave the sender's bags at once (as
/// posting at the auction does); the host delivers the letter as it delivers the market's mail: into the recipient's
/// mailbox now, or kept until they are next in the world. There it is an ordinary letter (one per item, the gold with
/// the first), its sender, subject and text shown in place of the auction house's.
/// </summary>
internal static class Mail
{
    public const byte Kind = 37;
    private const byte OpSend = 1;
    private const int Slots = 8;
    private const char BodyMark = '\u001f'; // subject | body, in the delivery's one text field
    private static readonly NetWriter W = new();

    private static AuctionMarket Market { get { try { return MailboxService.CurrentMarket ?? AuctionHouseService.Instance?.Market; } catch { return null; } } }
    private static string MyName => GameInfo.Character?.CharacterName ?? "";

    // ---------------- the letter's text in the delivery ----------------

    /// <summary>"From Name: subject␟body" (a player's letter), or null.</summary>
    private static (string from, string subject, string body)? Parse(AuctionDelivery d)
    {
        var s = d?.Subject;
        if (s == null || d.Owner != -1 || !s.StartsWith("From ")) return null;
        int c = s.IndexOf(':');
        string from = (c > 5 ? s.Substring(5, c - 5) : s.Substring(5)).Trim();
        string rest = c > 0 ? s.Substring(c + 1).TrimStart() : "";
        int b = rest.IndexOf(BodyMark);
        return (from, b >= 0 ? rest.Substring(0, b) : rest, b >= 0 ? rest.Substring(b + 1) : "");
    }

    /// <summary>"Auction House • New" → "Lirae • New".</summary>
    private static string Rename(string label, string who)
    {
        if (string.IsNullOrEmpty(label)) return who;
        int dot = label.IndexOf('•');
        return dot > 0 ? who + " " + label.Substring(dot) : who;
    }

    // ---------------- sending ----------------

    private static readonly List<(string key, string name)> To = new();
    private static readonly List<AuctionBagItem> Attached = new();

    private static void FillRecipients()
    {
        To.Clear();
        var s = CoopManager.Session;
        if (s == null) return;
        foreach (var p in s.Players.Values)
            if (p.Id != s.LocalId && !string.IsNullOrEmpty(p.Key)) To.Add((p.Key, Groups.Name(p.Id)));
        string mine = s.Players.TryGetValue(s.LocalId, out var me) ? me.Key : "";
        foreach (var f in Friends.All())
            if (f.key != mine && !To.Exists(t => t.key == f.key)) To.Add((f.key, f.name + " (offline)"));
    }

    private static RPGItem Item(int id)
    {
        try { var items = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetItems(); return items.ContainsKey(id) ? items[id] : null; } catch { return null; }
    }

    private static string ItemName(int id) => Item(id)?.entryDisplayName ?? "#" + id;

    /// <summary>A bag slot's item onto the letter (the whole stack).</summary>
    private static bool Attach(int slot, int itemId, out string error)
    {
        error = null;
        var m = Market;
        if (m == null) { error = "No mailbox."; return false; }
        if (Attached.Count >= Slots) { error = "The letter is full."; return false; }
        if (Attached.Exists(a => a.Slot == slot)) return false;
        AuctionBagItem? found = null;
        foreach (var b in m.Bag(-1)) if (b.Slot == slot && b.ItemID == itemId) { found = b; break; }
        if (found == null) foreach (var b in m.Bag(-1)) if (b.ItemID == itemId && !Attached.Exists(a => a.Slot == b.Slot)) { found = b; break; }
        if (found is not { } f) { error = "This bag cannot be mailed from."; return false; }
        if (!m.Tradable(f.ItemID, f.DataID, out var why)) { error = why; return false; }
        Attached.Add(f);
        return true;
    }

    /// <summary>Send the letter: its items and gold leave my bags here and now; the host delivers it.</summary>
    private static bool Send(int to, string subject, string body, long gold, out string error)
    {
        error = null;
        var m = Market;
        var s = CoopManager.Session;
        if (m == null || s == null || !CoopManager.Connected) { error = "Not in a co-op session."; return false; }
        if (to < 0 || to >= To.Count) { error = "Choose who the letter is for."; return false; }
        subject = (subject ?? "").Replace(BodyMark, ' ').Replace(':', ' ').Trim();
        body = (body ?? "").Replace(BodyMark, ' ').Trim();
        if (subject.Length == 0) { error = "The letter needs a subject."; return false; }
        if (gold < 0 || m.Balance(-1) < gold) { error = "Not enough gold."; return false; }
        // what is still in the bags as it was put on the letter
        var bag = m.Bag(-1);
        foreach (var a in Attached)
        {
            bool there = false;
            foreach (var b in bag) if (b.Slot == a.Slot && b.ItemID == a.ItemID && b.DataID == a.DataID && b.Count >= a.Count) { there = true; break; }
            if (!there) { error = $"{ItemName(a.ItemID)} is no longer in your bags."; return false; }
        }
        var items = new List<(int item, int count, byte[] data)>();
        foreach (var a in Attached)
        {
            byte[] data = a.DataID >= 0 ? ItemData.Write(a.DataID) : null;
            if (!m.RemoveFromBag(-1, a, a.Count)) { error = $"{ItemName(a.ItemID)} is no longer in your bags."; break; }
            items.Add((a.ItemID, a.Count, data));
        }
        if (gold > 0) m.ChangeGold(-1, -gold);
        string text = $"From {MyName}: {subject}" + (body.Length > 0 ? BodyMark + body : "");
        string key = To[to].key;
        if (s.IsHost) AuctionSync.HostMail(key, items, gold, text);
        else
        {
            W.Reset(); W.U8(Kind); W.U8(OpSend); W.Str(key); W.I64(gold); W.Str(text); W.U8((byte)items.Count);
            foreach (var (item, count, data) in items) { W.I32(item); W.I32(count); W.Blob(data ?? Array.Empty<byte>()); }
            s.SendWorld(W.Span);
        }
        Attached.Clear();
        Hud.Notify($"Letter sent to {To[to].name.Replace(" (offline)", "")}");
        return error == null;
    }

    public static void Receive(byte from, ref NetReader r)
    {
        if (r.U8() != OpSend) return;
        string key = r.Str(); long gold = r.I64(); string text = r.Str();
        int n = r.U8();
        var items = new List<(int, int, byte[])>();
        for (int i = 0; i < n; i++) { int item = r.I32(), count = r.I32(); items.Add((item, count, r.Blob())); }
        if (CoopManager.Session?.IsHost != true) return;
        AuctionSync.HostMail(key, items, gold, text);
        Plugin.L.LogInfo($"Mail: {Groups.Name(from)} sends a letter ({n} items, gold {gold})");
    }

    /// <summary>A letter came into my mailbox: say so, as the auction's mail is not shouted either but a letter from a friend is news.</summary>
    public static void Arrived(string subject)
    {
        if (subject == _lastArrived && Time.unscaledTime - _lastArrivedAt < 30f) return; // its other items
        _lastArrived = subject; _lastArrivedAt = Time.unscaledTime;
        var p = Parse(new AuctionDelivery { Owner = -1, Subject = subject });
        if (p is { } l) Hud.Notify($"New letter from {l.from}: {l.subject}");
    }

    private static string _lastArrived;
    private static float _lastArrivedAt;

    // ---------------- the window: Inbox | Send Mail ----------------

    private static MailboxPanel _panel;
    private static GameObject _send;
    private static readonly List<GameObject> InboxParts = new();
    private static readonly List<bool> InboxWasActive = new();
    private static Button _inboxTab, _sendTab;
    private static TMP_Dropdown _toBox;
    private static TMP_InputField _subject, _body, _gold;
    private static TMP_Text _status;
    private static readonly List<(GameObject go, Image icon, Image rarity, TMP_Text count, AuctionItemHover hover)> SlotViews = new();
    private static bool SendOpen => _send != null && _send.activeSelf;

    private static readonly Color Ink = new(0.24f, 0.15f, 0.08f);
    private static readonly Color Light = new(0.92f, 0.86f, 0.72f);

    private static void Build(MailboxPanel p)
    {
        if (_inboxTab != null && _panel == p) return;
        _panel = p;
        var ah = Object.FindObjectOfType<AuctionHousePanel>(true);
        var parchment = p.transform.Find("Letter parchment");
        var inbox = p.transform.Find("Inbox");
        if (ah == null || parchment == null || inbox == null || p.CollectButton == null) return;
        var panel = p.GetComponent<RectTransform>();

        // what the inbox shows: everything but the frame, its title, close button and the wallet
        InboxParts.Clear();
        for (int i = 0; i < p.transform.childCount; i++)
        {
            var c = p.transform.GetChild(i);
            var n = c.name;
            if (n is "Chrome" or "Drag Handle" or "Header" or "Close" || n.StartsWith("Wallet") || n.StartsWith("AfCoop_")) continue;
            InboxParts.Add(c.gameObject);
        }

        // the tabs, under the window as in WoW
        var tabTemplate = ah.Tabs != null && ah.Tabs.Length > 0 ? ah.Tabs[0] : p.CollectButton;
        _inboxTab = Tab(tabTemplate, panel, "Inbox", 0, ShowInbox);
        _sendTab = Tab(tabTemplate, panel, "Send Mail", 1, ShowSend);
        _built = false;
    }

    private static bool _built;

    /// <summary>Send Mail over the inbox list and the letter (their union), once the window has its layout.</summary>
    private static void BuildSend()
    {
        if (_built && _send != null) return;
        var p = _panel;
        var ah = Object.FindObjectOfType<AuctionHousePanel>(true);
        var parchment = p.transform.Find("Letter parchment");
        var inbox = p.transform.Find("Inbox");
        var panel = p.GetComponent<RectTransform>();
        Canvas.ForceUpdateCanvases();
        // the inbox's own look: a message row's backing for the fields, its icon frame for the slots
        _rowStyle = p.RowTemplate != null ? p.RowTemplate.GetComponent<Image>() : null;
        var border = p.RowTemplate != null ? p.RowTemplate.transform.Find("Icon border") : null;
        _frameStyle = border != null ? border.GetComponent<Image>() : null;
        var area = Union(panel, inbox.GetComponent<RectTransform>(), parchment.GetComponent<RectTransform>());
        _send = new GameObject("AfCoop_SendMail");
        var rt = _send.AddComponent<RectTransform>();
        rt.SetParent(panel, false);
        rt.anchorMin = rt.anchorMax = rt.pivot = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = area.center - panel.rect.center;
        rt.sizeDelta = area.size;
        float w = area.width, h = area.height;

        float y = 0f;
        Label(rt, "To:", -w / 2f + 50f, y - 4f, 90f);
        _toBox = Dropdown(ah.SortOrder, rt, -w / 2f + 105f + (w * 0.5f - 110f) / 2f, y, w * 0.5f - 110f);
        y -= 44f;
        Label(rt, "Subject:", -w / 2f + 50f, y - 4f, 90f);
        _subject = Input(ah.Search, rt, -w / 2f + 105f + (w - 115f) / 2f, y, w - 115f, 36f, false, Light);
        _subject.characterLimit = 60;
        y -= 48f;

        // the letter: the game's parchment, written on in ink
        float bodyH = h - 44f - 48f - 82f - 56f;
        var paper = Object.Instantiate(parchment.gameObject, rt);
        paper.name = "AfCoop_Paper";
        paper.SetActive(true);
        UiKit.Place(paper.GetComponent<RectTransform>(), 0f, y, w, bodyH);
        _body = Input(ah.Search, rt, 0f, y - 14f, w - 44f, bodyH - 28f, true, Ink);
        _body.characterLimit = 500;
        var bg = _body.GetComponent<Image>(); if (bg != null) { bg.sprite = null; bg.color = new Color(0f, 0f, 0f, 0f); }
        for (int i = 0; i < 4; i++) { var e = _body.transform.Find("AfCoop_Edge_" + i); if (e != null) Object.Destroy(e.gameObject); } // the parchment has its own edge
        y -= bodyH + 8f;

        // attachments
        SlotViews.Clear();
        var template = Object.FindObjectOfType<ItemSlotHolder>(true);
        float size = Mathf.Min(64f, (w - (Slots - 1) * 8f) / Slots);
        float x0 = -((Slots - 1) * (size + 8f)) / 2f;
        for (int i = 0; i < Slots; i++)
        {
            int k = i;
            SlotViews.Add(Slot(template, rt, x0 + i * (size + 8f), y, size, () => Detach(k)));
        }
        y -= size + 14f;

        Label(rt, "Gold:", -w / 2f + 40f, y - 4f, 70f);
        _gold = Input(ah.PriceInput, rt, -w / 2f + 80f + 80f, y, 160f, 36f, false, Light);
        _gold.contentType = TMP_InputField.ContentType.IntegerNumber;
        var send = UiKit.CloneButton(p.CollectButton, rt, "Send", DoSend);
        var cancel = UiKit.CloneButton(p.CollectButton, rt, "Cancel", () => { Clear(); ShowInbox(); });
        float bw = Mathf.Min(170f, send.GetComponent<RectTransform>().rect.width), bh = send.GetComponent<RectTransform>().rect.height;
        UiKit.Place(cancel, w / 2f - bw / 2f, y + 2f, bw, bh);
        UiKit.Place(send, w / 2f - bw * 1.5f - 10f, y + 2f, bw, bh);
        _status = UiKit.Text(rt, "", 18, new Color(0.85f, 0.35f, 0.3f), TextAlignmentOptions.Center);
        UiKit.Place(_status, -40f, y - 40f, w * 0.6f, 26f);
        _send.SetActive(false);
        _built = true;
    }

    /// <summary>The rectangle covering both, in the panel's own space.</summary>
    private static Rect Union(RectTransform panel, RectTransform a, RectTransform b)
    {
        Vector2 min = new(float.MaxValue, float.MaxValue), max = new(float.MinValue, float.MinValue);
        foreach (var r in new[] { a, b })
            foreach (var corner in new[] { r.rect.min, r.rect.max }) // (GetWorldCorners does not fill a managed array here)
            {
                var l = (Vector2)panel.InverseTransformPoint(r.TransformPoint(corner));
                min = Vector2.Min(min, l); max = Vector2.Max(max, l);
            }
        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }

    private static Image _rowStyle, _frameStyle;
    private static readonly Color Gold = new(0.62f, 0.48f, 0.25f, 0.9f);

    /// <summary>A field or slot drawn as the inbox draws its rows and icons, edged in the window's gold.</summary>
    private static void Style(Image target, Image like, Color fallback)
    {
        if (like != null && like.sprite != null)
        {
            target.sprite = like.sprite; target.type = like.type; target.color = like.color;
            target.pixelsPerUnitMultiplier = like.pixelsPerUnitMultiplier;
        }
        else { target.sprite = null; target.color = fallback; }
        Edges(target.rectTransform);
    }

    /// <summary>Thin gold lines along the four sides (as the wallet's bar is edged).</summary>
    private static void Edges(RectTransform r)
    {
        if (r.Find("AfCoop_Edge_0") != null) return;
        for (int i = 0; i < 4; i++)
        {
            var go = new GameObject("AfCoop_Edge_" + i);
            var e = go.AddComponent<RectTransform>();
            e.SetParent(r, false);
            var img = go.AddComponent<Image>();
            img.color = Gold; img.raycastTarget = false;
            bool horizontal = i < 2;
            e.anchorMin = horizontal ? new Vector2(0f, i == 0 ? 1f : 0f) : new Vector2(i == 2 ? 0f : 1f, 0f);
            e.anchorMax = horizontal ? new Vector2(1f, i == 0 ? 1f : 0f) : new Vector2(i == 2 ? 0f : 1f, 1f);
            e.pivot = new Vector2(0.5f, 0.5f);
            e.anchoredPosition = Vector2.zero;
            e.sizeDelta = horizontal ? new Vector2(0f, 1.5f) : new Vector2(1.5f, 0f);
        }
    }

    private static void Label(RectTransform parent, string text, float x, float y, float width)
    {
        var t = UiKit.Text(parent, text, 20, Light, TextAlignmentOptions.Right);
        UiKit.Place(t, x, y, width, 30);
    }

    private static Button Tab(Button template, RectTransform panel, string text, int index, Action onClick)
    {
        var b = UiKit.CloneButton(template, panel, text, onClick);
        b.gameObject.name = "AfCoop_Tab_" + text;
        var r = b.GetComponent<RectTransform>();
        r.anchorMin = r.anchorMax = new Vector2(0f, 0f);
        r.pivot = new Vector2(0f, 1f);
        r.anchoredPosition = new Vector2(24f + index * (r.rect.width + 6f), 2f);
        return b;
    }

    private static TMP_Dropdown Dropdown(TMP_Dropdown template, RectTransform parent, float x, float y, float width)
    {
        var go = Object.Instantiate(template.gameObject, parent);
        go.name = "AfCoop_Dropdown";
        go.SetActive(true);
        UiKit.StripLocalization(go);
        var dd = go.GetComponent<TMP_Dropdown>();
        dd.onValueChanged = new TMP_Dropdown.DropdownEvent();
        dd.ClearOptions();
        UiKit.Place(dd, x, y, width, 36);
        return dd;
    }

    private static TMP_InputField Input(TMP_InputField template, RectTransform parent, float x, float y, float width, float height, bool multiline, Color textColor)
    {
        var go = Object.Instantiate(template.gameObject, parent);
        go.name = "AfCoop_Input";
        go.SetActive(true);
        UiKit.StripLocalization(go);
        var f = go.GetComponent<TMP_InputField>();
        f.onValueChanged = new TMP_InputField.OnChangeEvent();
        f.onEndEdit = new TMP_InputField.SubmitEvent();
        f.onSubmit = new TMP_InputField.SubmitEvent();
        f.contentType = TMP_InputField.ContentType.Standard;
        f.lineType = multiline ? TMP_InputField.LineType.MultiLineNewline : TMP_InputField.LineType.SingleLine;
        f.text = "";
        foreach (var t in go.GetComponentsInChildren<TMP_Text>(true)) if (t != f.textComponent) t.text = ""; // the auction's search hint
        var bg = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        Style(bg, _rowStyle, new Color(0f, 0f, 0f, 0.55f));
        if (f.textComponent != null)
        {
            f.textComponent.color = textColor;
            f.textComponent.alignment = multiline ? TextAlignmentOptions.TopLeft : TextAlignmentOptions.MidlineLeft;
            f.textComponent.enableWordWrapping = multiline;
        }
        // a caret you can see from the first click
        f.customCaretColor = true;
        f.caretColor = textColor;
        f.caretWidth = 2;
        f.caretBlinkRate = 0.85f;
        f.selectionColor = new Color(textColor.r, textColor.g, textColor.b, 0.3f);
        UiKit.Place(f, x, y, width, height);
        return f;
    }

    private static (GameObject, Image, Image, TMP_Text, AuctionItemHover) Slot(ItemSlotHolder template, RectTransform parent, float x, float y, float size, Action onClick)
    {
        GameObject go;
        Image icon = null, rarity = null;
        TMP_Text count = null;
        if (template != null)
        {
            go = Object.Instantiate(template.gameObject, parent);
            var holder = go.GetComponent<ItemSlotHolder>();
            icon = holder?.icon;
            count = holder?.stackText;
            rarity = holder?.rarity;
            if (holder?.cooldownOverlay != null) holder.cooldownOverlay.gameObject.SetActive(false);
            if (holder?.cooldownText != null) holder.cooldownText.gameObject.SetActive(false);
            foreach (var mb in go.GetComponentsInChildren<MonoBehaviour>(true))
                if (mb != null && mb is not Graphic && mb is not Mask && mb is not RectMask2D) Object.DestroyImmediate(mb);
        }
        else
        {
            go = new GameObject("slot");
            go.AddComponent<RectTransform>().SetParent(parent, false);
            go.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.6f);
        }
        go.name = "AfCoop_MailSlot";
        go.SetActive(true);
        if (icon == null)
        {
            var ig = new GameObject("icon"); ig.AddComponent<RectTransform>().SetParent(go.transform, false);
            icon = ig.AddComponent<Image>();
            var ir = icon.rectTransform; ir.anchorMin = Vector2.zero; ir.anchorMax = Vector2.one; ir.offsetMin = new Vector2(4, 4); ir.offsetMax = new Vector2(-4, -4);
        }
        if (count == null) { count = UiKit.Text(go.transform, "", 16, Color.white, TextAlignmentOptions.BottomRight); var cr = count.rectTransform; cr.anchorMin = Vector2.zero; cr.anchorMax = Vector2.one; cr.offsetMin = new Vector2(2, 2); cr.offsetMax = new Vector2(-4, -2); }
        var target = go.GetComponent<Image>() ?? go.AddComponent<Image>();
        Style(target, _frameStyle ?? _rowStyle, new Color(0f, 0f, 0f, 0.6f));
        target.raycastTarget = true;
        var b = go.AddComponent<Button>();
        b.targetGraphic = target;
        b.onClick.AddListener(UiKit.Action(onClick));
        var hover = go.AddComponent<AuctionItemHover>();
        UiKit.Place(go.GetComponent<RectTransform>(), x, y, size, size);
        return (go, icon, rarity, count, hover);
    }

    private static void RefreshSlots()
    {
        for (int i = 0; i < SlotViews.Count; i++)
        {
            var (_, icon, rarity, count, hover) = SlotViews[i];
            if (i < Attached.Count)
            {
                var a = Attached[i];
                var item = Item(a.ItemID);
                icon.sprite = item?.entryIcon; icon.enabled = icon.sprite != null; icon.color = Color.white;
                icon.gameObject.SetActive(true);
                for (var t = icon.transform; t != null && t != SlotViews[i].go.transform; t = t.parent) t.gameObject.SetActive(true);
                count.text = a.Count > 1 ? a.Count.ToString() : "";
                if (rarity != null) { var bgs = item?.ItemRarity?.background; rarity.sprite = bgs; rarity.enabled = bgs != null; }
                hover.ItemID = a.ItemID; hover.DataID = a.DataID; hover.enabled = true;
            }
            else
            {
                icon.sprite = null; icon.enabled = false;
                if (rarity != null) rarity.enabled = false;
                count.text = "";
                hover.ItemID = -1; hover.DataID = -1; hover.enabled = false;
            }
        }
    }

    private static void Detach(int i)
    {
        if (i >= Attached.Count) return;
        Attached.RemoveAt(i);
        try { BLINK.RPGBuilder.UI.ItemTooltip.Instance?.Hide(); } catch { }
        RefreshSlots();
    }

    private static void Clear()
    {
        Attached.Clear();
        if (_subject != null) { _subject.text = ""; _body.text = ""; _gold.text = ""; _status.text = ""; }
        RefreshSlots();
    }

    private static void ShowInbox()
    {
        if (_send == null || _panel == null) return;
        if (SendOpen)
        {
            _send.SetActive(false);
            for (int i = 0; i < InboxParts.Count && i < InboxWasActive.Count; i++) if (InboxParts[i] != null) InboxParts[i].SetActive(InboxWasActive[i]);
        }
        SetTabs(false);
        try { _panel.Refresh(); } catch { }
    }

    private static void ShowSend()
    {
        if (_panel == null) return;
        if (!CoopManager.Connected) { Hud.Notify("Letters go to players of a co-op session"); return; }
        BuildSend();
        if (_send == null) return;
        if (!SendOpen)
        {
            InboxWasActive.Clear();
            foreach (var g in InboxParts) { InboxWasActive.Add(g != null && g.activeSelf); if (g != null) g.SetActive(false); }
            _send.SetActive(true);
        }
        FillRecipients();
        int keep = _toBox.options.Count > 0 ? _toBox.value : 0;
        _toBox.ClearOptions();
        foreach (var t in To) _toBox.options.Add(new TMP_Dropdown.OptionData(t.name));
        if (To.Count == 0) _toBox.options.Add(new TMP_Dropdown.OptionData("(nobody yet)"));
        _toBox.value = Mathf.Clamp(keep, 0, _toBox.options.Count - 1); _toBox.RefreshShownValue();
        _status.text = "";
        RefreshSlots();
        SetTabs(true);
        _subject.ActivateInputField();
    }

    private static void SetTabs(bool send)
    {
        if (_inboxTab != null) _inboxTab.interactable = send;
        if (_sendTab != null) _sendTab.interactable = !send;
    }

    private static void DoSend()
    {
        long.TryParse(_gold.text, out var gold);
        if (Send(To.Count > 0 ? _toBox.value : -1, _subject.text, _body.text, gold, out var error)) { Clear(); _status.text = ""; }
        else _status.text = error;
        RefreshSlots();
    }

    /// <summary>Is the pointer over a slot of the letter (or the letter at all)?</summary>
    private static bool OverLetter(Vector2 screen)
    {
        if (!SendOpen) return false;
        return RectTransformUtility.RectangleContainsScreenPoint(_send.GetComponent<RectTransform>(), screen, null)
            || RectTransformUtility.RectangleContainsScreenPoint(_send.GetComponent<RectTransform>(), screen, Camera.main);
    }

    private static void AttachFrom(ItemSlotHolder h)
    {
        if (h?.thisItem == null) return;
        if (!Attach(h.slotIndex, h.thisItem.ID, out var error)) { if (error != null) _status.text = error; }
        else _status.text = "";
        RefreshSlots();
    }

    // ---------------- dev ----------------

    public static string Dev(string arg)
    {
        FillRecipients();
        var m = Market;
        if (arg.Length == 0)
        {
            var sb = new System.Text.StringBuilder("to:");
            for (int i = 0; i < To.Count; i++) sb.Append($" {i}={To[i].name}");
            sb.Append(" | bag:");
            if (m != null) foreach (var b in m.Bag(-1)) sb.Append($" {b.Slot}={ItemName(b.ItemID)}x{b.Count}");
            sb.Append(" | attached:");
            foreach (var a in Attached) sb.Append($" {ItemName(a.ItemID)}x{a.Count}");
            return sb.ToString();
        }
        if (arg == "where")
        {
            var sb = new System.Text.StringBuilder();
            foreach (var mb in Object.FindObjectsOfType<MailboxInteractable>()) sb.Append($"{mb.name} {mb.transform.position}; ");
            return sb.Length > 0 ? sb.ToString() : "no mailbox here";
        }
        if (arg == "use")
        {
            MailboxInteractable best = null; float d = float.MaxValue; var me = GameInfo.Player.transform.position;
            foreach (var mb in Object.FindObjectsOfType<MailboxInteractable>()) { float x = Vector3.Distance(me, mb.transform.position); if (x < d) { d = x; best = mb; } }
            if (best == null) return "none";
            MailboxPanel.OpenForMailbox(best); return $"opened at {d:0.0} m";
        }
        if (arg == "rect" && _send != null && _panel != null)
        {
            var pr = _panel.GetComponent<RectTransform>(); var sr = _send.GetComponent<RectTransform>();
            var inbox = _panel.transform.Find("Inbox").GetComponent<RectTransform>(); var par = _panel.transform.Find("Letter parchment").GetComponent<RectTransform>();
            return $"panel rect {pr.rect} pivot {pr.pivot} | send pos {sr.anchoredPosition} size {sr.sizeDelta} world {sr.position} | panel world {pr.position} scale {pr.lossyScale} | inbox {inbox.rect} pos {inbox.position} | parch {par.rect} pos {par.position} | union {Union(pr, inbox, par)}";
        }
        if (arg.StartsWith("fill ") && _subject != null) { var f = arg.Substring(5).Split('|'); _subject.text = f[0]; if (f.Length > 1) _body.text = f[1].Replace("\\n", "\n"); if (f.Length > 2) _gold.text = f[2]; return "filled"; }
        if (arg == "click send") { DoSend(); return _status.text.Length > 0 ? _status.text : "sent"; }
        if (arg == "send") { ShowSend(); return SendOpen ? "send tab" : "no window"; }
        if (arg == "inbox") { ShowInbox(); return "inbox tab"; }
        if (arg.StartsWith("read ")) { Object.FindObjectOfType<MailboxPanel>(true)?.SelectMail(long.Parse(arg.Substring(5))); return "selected"; }
        if (arg.StartsWith("attach ")) // attach <bag slot>
        {
            int slot = int.Parse(arg.Substring(7));
            if (m == null) return "no market";
            foreach (var b in m.Bag(-1)) if (b.Slot == slot) { bool ok = Attach(slot, b.ItemID, out var e); RefreshSlots(); return ok ? "attached" : e ?? "already"; }
            return "no such slot";
        }
        if (arg.StartsWith("write ")) // write <to> <gold> <subject>|<body>
        {
            var a = arg.Substring(6).Split(new[] { ' ' }, 3);
            var t = a.Length > 2 ? a[2].Split('|') : new[] { "" };
            bool ok = Send(int.Parse(a[0]), t[0], t.Length > 1 ? t[1] : "", long.Parse(a[1]), out var error);
            RefreshSlots();
            return ok ? "sent" : error;
        }
        return "mail [where | use | send | inbox | read <id> | attach <bag slot> | write <to> <gold> <subject>|<body>]";
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxPanel), nameof(MailboxPanel.Show))]
        private static void Shown(MailboxPanel __instance)
        {
            try
            {
                Build(__instance);
                bool coop = CoopManager.Connected;
                if (_inboxTab != null) _inboxTab.gameObject.SetActive(coop);
                if (_sendTab != null) _sendTab.gameObject.SetActive(coop);
                if (SendOpen) ShowInbox(); else SetTabs(false);
            }
            catch (Exception e) { Plugin.L.LogWarning("Mail window: " + e.Message); }
        }

        /// <summary>The window's own refresh brings the inbox's parts back: not while the letter is being written.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxPanel), nameof(MailboxPanel.Refresh))]
        private static void Refreshed(MailboxPanel __instance)
        {
            try { LetterSender(__instance); } catch { }
            if (!SendOpen) return;
            foreach (var g in InboxParts) if (g != null && g.activeSelf) g.SetActive(false);
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxPanel), nameof(MailboxPanel.SelectMail))]
        private static void Selected(MailboxPanel __instance) { try { LetterSender(__instance); } catch { } }

        private static void LetterSender(MailboxPanel p)
        {
            var m = Market;
            if (m == null || p.Sender == null) return;
            foreach (var d in m.State.Deliveries)
                if (d != null && d.ID == p.selectedID) { if (Parse(d) is { } l) p.Sender.text = Rename(p.Sender.text, l.from); return; }
        }

        /// <summary>A player's letter in the list: its sender instead of the auction house.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxRow), nameof(MailboxRow.Bind))]
        private static void RowSender(MailboxRow __instance, AuctionDelivery __0)
        {
            try { if (Parse(__0) is { } l && __instance.Sender != null) __instance.Sender.text = Rename(__instance.Sender.text, l.from); } catch { }
        }

        /// <summary>A player's letter: its subject as written.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxService), nameof(MailboxService.Subject))]
        private static void Subject(AuctionDelivery __0, ref string __result) { if (Parse(__0) is { } l) __result = l.subject; }

        /// <summary>A player's letter: its text, signed.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(MailboxService), nameof(MailboxService.Letter))]
        private static void Body(AuctionDelivery __0, ref string __result)
        {
            if (Parse(__0) is not { } l) return;
            __result = (l.body.Length > 0 ? l.body + "\n\n" : "") + l.from;
        }

        /// <summary>An item dragged from the bags onto the letter goes on it (and stays in the bags until sent).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemSlotHolder), nameof(ItemSlotHolder.OnEndDrag))]
        private static bool DroppedOnLetter(ItemSlotHolder __instance, PointerEventData __0)
        {
            try
            {
                if (__0 == null || !OverLetter(__0.position)) return true;
                AttachFrom(__instance);
                if (__instance.curDraggedItem != null) { Object.Destroy(__instance.curDraggedItem); __instance.curDraggedItem = null; }
                __instance.ClearDraggedSlot();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Mail drop: " + e.Message); return true; }
        }

        /// <summary>Right-click in the bags while writing a letter: onto the letter (as in WoW), not used.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(ItemSlotHolder), nameof(ItemSlotHolder.OnPointerClick))]
        private static bool RightClickOntoLetter(ItemSlotHolder __instance, PointerEventData __0)
        {
            if (!SendOpen || __0 == null || __0.button != PointerEventData.InputButton.Right) return true;
            try { AttachFrom(__instance); } catch (Exception e) { Plugin.L.LogWarning("Mail attach: " + e.Message); }
            return false;
        }
    }
}
