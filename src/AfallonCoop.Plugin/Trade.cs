using System;
using System.Collections.Generic;
using System.Text;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Trade between players in the game's own trade window (the one for trading with adventurers). "Trade" on another
/// player asks them in the game's confirmation popup; both then see the window with the other player's shell as the
/// adventurer: the left side is one's own offer (items dragged in, gold typed), the right side the other player's offer,
/// kept live from their messages (their bag grid and the fairness bar are hidden: players judge for themselves).
/// "Accept" marks one ready; any change of either offer clears both marks. Once both are ready for the same pair of
/// offers the host runs the exchange so that nothing is duplicated or lost:
///  1. each side checks its offer is still in its bags and that it has room, takes it out (escrow) and sends it with
///     the items' own data (random stats, enchantment, sockets...) to everyone;
///  2. the host, having both, tells both to deliver: each adds what the other took out;
///  3. if a side could not take its offer out, or left meanwhile, the host tells the rest to refund: each puts back
///     what it took out.
/// </summary>
internal static class Trade
{
    public const byte Kind = 16;
    private const byte OpRequest = 1, OpAnswer = 2, OpOffer = 3, OpAccept = 4, OpCancel = 5, OpEscrowed = 6, OpDeliver = 7, OpRefund = 8;
    private const float KeepDistance = 10f; // opened within the game's trade distance (5 m); closed beyond this
    private const float RequestTimeout = 30f;

    private sealed class Item
    {
        public int Id, Count, Slot = -1, DataId = -1;
        public byte[] Data; // the item's own data (ItemData), taken at escrow
    }

    private sealed class Deal
    {
        public uint Id;
        public byte Other;
        public string MySig = "", TheirSig = "";
        public List<Item> Mine = new(), Theirs = new();
        public int MyGold, TheirGold;
        public bool MineReady, TheirsReady;
        public int TheirHash;
        public bool Committing;
        public List<Item> Escrow; public int EscrowGold = -1;          // what I took out, until delivered or refunded
        public List<Item> TheirEscrow; public int TheirEscrowGold = -1; // what they took out
    }

    private static Deal _deal;
    private static uint _asked; private static byte _askedOf; private static float _askedAt; // my request, unanswered
    private static (uint id, byte from)? _incoming; // a request shown to me (dev: answer it without the popup)
    private static string _acceptLabel;
    private static bool _devStall;
    private static bool _rebuilding;

    // host: exchanges in progress
    private sealed class Commit { public byte A, B; public bool OkA, OkB; }
    private static readonly Dictionary<uint, Commit> Commits = new();
    private static readonly HashSet<uint> Refunded = new(); // a side that takes its offer out later gets it back too

    private static readonly NetWriter W = new();
    private static readonly System.Random Rng = new();

    private static TradePanel Panel { get { try { return TradePanel.Instance; } catch { return null; } } }
    private static RPGCurrency Currency => Panel?.tradeCurrency;

    private static void Header(byte op, uint id, byte to) { W.Reset(); W.U8(Kind); W.U8(op); W.U32(id); W.U8(to); }

    // ---------------- asking ----------------

    /// <summary>"Trade" on another player (their shell): ask them.</summary>
    public static void Ask(byte player)
    {
        var s = CoopManager.Session;
        if (s == null) return;
        if (_deal != null) { Hud.Notify("Already trading"); return; }
        var shell = PlayerShells.Get(player);
        var me = GameInfo.Player;
        float max = Panel != null ? Panel.MaxOpenDistance : 5f;
        if (shell == null || me == null || Vector3.Distance(shell.transform.position, me.transform.position) > max)
        {
            Hud.Notify("Too far away to trade");
            return;
        }
        _asked = (uint)Rng.Next(1, int.MaxValue);
        _askedOf = player;
        _askedAt = Time.unscaledTime;
        Header(OpRequest, _asked, player);
        s.SendWorld(W.Span);
        Hud.Notify($"Trade request sent to {Groups.Name(player)}");
    }

    private static void OnRequest(byte from, uint id)
    {
        var s = CoopManager.Session;
        bool blocked = false;
        try { blocked = GameplaySettings.BlockTrades; } catch { }
        if (_deal != null || blocked)
        {
            Header(OpAnswer, id, from); W.U8(0); s.SendWorld(W.Span);
            return;
        }
        _incoming = (id, from);
        string who = Groups.Name(from);
        Hud.Notify($"{who} wants to trade");
        Groups.Ask($"{who} wants to trade with you. Accept?", () => Answer(true), () => Answer(false));
    }

    /// <summary>My answer to the request shown to me (the popup, or the dev bridge).</summary>
    public static string Answer(bool yes)
    {
        if (_incoming is not { } inc) return "no request";
        _incoming = null;
        Header(OpAnswer, inc.id, inc.from); W.U8((byte)(yes ? 1 : 0));
        CoopManager.Session?.SendWorld(W.Span);
        if (yes) Open(inc.id, inc.from);
        return yes ? "accepted" : "declined";
    }

    private static void OnAnswer(byte from, uint id, bool yes)
    {
        if (id != _asked || from != _askedOf) return;
        _asked = 0;
        if (yes) Open(id, from);
        else Hud.Notify($"{Groups.Name(from)} declined the trade");
    }

    // ---------------- the window ----------------

    private static void Open(uint id, byte other)
    {
        var panel = Panel;
        var shell = PlayerShells.Get(other);
        if (panel == null || shell == null) { Cancel("the other player is not here"); return; }
        try
        {
            if (panel.IsOpen()) panel.Hide();
            // The window reads the adventurer's bag and gold: here, the other player's offer (filled from their messages).
            shell.adventurerInventory = new Il2CppSystem.Collections.Generic.List<MobCombatEntity.AdventurerInventorySlot>();
            shell.adventurerGold = int.MaxValue;
            _deal = new Deal { Id = id, Other = other };
            panel.Open(shell);
            Restyle();
            Plugin.L.LogInfo($"Trade {id:x8} with #{other} open");
        }
        catch (Exception e) { Plugin.L.LogWarning("Trade open: " + e.Message); _deal = null; }
    }

    /// <summary>A player trade has no adventurer bag and no fairness: hidden; the other player's gold is theirs to type.</summary>
    private static void Restyle()
    {
        var p = Panel;
        if (p == null || _deal == null) return;
        try
        {
            panelSet(BagWindow(p), false);
            panelSet(Top(p, p.fairnessBarFill?.transform), false);
            panelSet(Top(p, p.fairnessText?.transform), false);
            panelSet(p.adventurerGoldText?.gameObject, false);
            p.debugForceFullFairness = true; // the accept button stays enabled
            if (p.adventurerOfferedGoldInput != null) p.adventurerOfferedGoldInput.interactable = false;
            if (p.playerOfferedGoldInput != null) p.playerOfferedGoldInput.interactable = !_deal.Committing;
            var caption = TheirCaption(p);
            if (caption != null) caption.text = Groups.Name(_deal.Other) + "'s offer" + (_deal.TheirsReady ? "  - ready" : "");
            var label = p.acceptButton != null ? p.acceptButton.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null;
            if (label != null)
            {
                _acceptLabel ??= label.text;
                label.text = _deal.Committing ? "Trading..." : _deal.MineReady ? "Waiting..." : _acceptLabel;
            }
            if (p.acceptButton != null) p.acceptButton.interactable = !_deal.Committing;
        }
        catch (Exception e) { Plugin.L.LogWarning("Trade window: " + e.Message); }

        static void panelSet(GameObject go, bool on) { if (go != null && go.activeSelf != on) go.SetActive(on); }
    }

    /// <summary>The adventurer's bag window: the part of the trade window holding both its name and its slots.</summary>
    private static GameObject BagWindow(TradePanel p)
    {
        if (p.adventurerSlotsParent == null || p.adventurerNameText == null) return null;
        var root = p.transform;
        for (var t = p.adventurerSlotsParent; t != null && t != root; t = t.parent)
            if (t.parent == root || p.adventurerNameText.transform.IsChildOf(t)) return t.gameObject;
        return null;
    }

    /// <summary>The part of the trade window (a direct child of it) that holds this element.</summary>
    private static GameObject Top(TradePanel p, Transform t)
    {
        for (; t != null; t = t.parent) if (t.parent == p.transform) return t.gameObject;
        return null;
    }

    private static TMPro.TextMeshProUGUI _caption;
    private static string _captionText;

    /// <summary>The caption over the adventurer's offer ("Adventurer trade offer").</summary>
    private static TMPro.TextMeshProUGUI TheirCaption(TradePanel p)
    {
        if (_caption != null) return _caption;
        foreach (var t in p.GetComponentsInChildren<TMPro.TextMeshProUGUI>(true))
            if (t != null && (t.text ?? "").StartsWith("Adventurer trade offer", StringComparison.OrdinalIgnoreCase)) { _caption = t; _captionText = t.text; break; }
        return _caption;
    }

    private static void RestoreLook()
    {
        var p = Panel;
        if (p == null) return;
        try
        {
            BagWindow(p)?.SetActive(true);
            if (_caption != null && _captionText != null) _caption.text = _captionText;
            Top(p, p.fairnessBarFill?.transform)?.SetActive(true);
            Top(p, p.fairnessText?.transform)?.SetActive(true);
            if (p.adventurerGoldText != null) p.adventurerGoldText.gameObject.SetActive(true);
            if (p.adventurerOfferedGoldInput != null) p.adventurerOfferedGoldInput.interactable = true;
            if (p.playerOfferedGoldInput != null) p.playerOfferedGoldInput.interactable = true;
            var label = p.acceptButton != null ? p.acceptButton.GetComponentInChildren<TMPro.TextMeshProUGUI>(true) : null;
            if (label != null && _acceptLabel != null) label.text = _acceptLabel;
            if (p.acceptButton != null) p.acceptButton.interactable = true;
        }
        catch { }
    }

    private static void Close()
    {
        var p = Panel;
        var d = _deal;
        _deal = null;
        ItemData.DropTemps("trade");
        // emptied, not null: the game's saving and companion bookkeeping read every entity's bag (null throws there)
        if (d != null) { var shell = PlayerShells.Get(d.Other); if (shell != null) try { shell.adventurerInventory = new Il2CppSystem.Collections.Generic.List<MobCombatEntity.AdventurerInventorySlot>(); } catch { } }
        RestoreLook();
        try { if (p != null && p.IsOpen()) p.Hide(); } catch { }
    }

    /// <summary>Stop the trade before the exchange (window closed, too far, the other left): both windows close.</summary>
    private static void Cancel(string why)
    {
        var d = _deal;
        if (d == null || d.Committing) return;
        Header(OpCancel, d.Id, d.Other);
        CoopManager.Session?.SendWorld(W.Span);
        Close();
        Hud.Notify("Trade cancelled" + (why != null ? ": " + why : ""));
        Plugin.L.LogInfo($"Trade {d.Id:x8} cancelled ({why})");
    }

    // ---------------- offers ----------------

    private static string Sig(int gold, List<Item> items)
    {
        var b = new StringBuilder().Append(gold);
        foreach (var i in items) b.Append(';').Append(i.Id).Append('x').Append(i.Count);
        return b.ToString();
    }

    /// <summary>The pair of offers both sides must be ready for (the same text on both machines).</summary>
    private static int PairHash(Deal d)
    {
        var s = CoopManager.Session;
        bool meFirst = s.LocalId < d.Other;
        return StableHash.Of((meFirst ? d.MySig : d.TheirSig) + "|" + (meFirst ? d.TheirSig : d.MySig));
    }

    private static void ReadMyOffer(Deal d, List<Item> items, out int gold)
    {
        gold = 0;
        var p = Panel;
        if (p == null) return;
        var offer = p.playerOffer;
        for (int k = 0; k < offer.Count; k++)
            if (offer[k]?.item is { } it && offer[k] is var e) items.Add(new Item { Id = it.ID, Count = e.count, Slot = e.sourceSlotIndex, DataId = e.itemDataID });
        gold = p.GetPlayerOfferedGold();
    }

    private static void SendOffer(Deal d)
    {
        Header(OpOffer, d.Id, d.Other);
        W.I32(d.MyGold); W.U8((byte)Math.Min(d.Mine.Count, 255));
        for (int i = 0; i < d.Mine.Count && i < 255; i++)
        {
            W.I32(d.Mine[i].Id); W.I32(d.Mine[i].Count);
            W.Blob(d.Mine[i].DataId >= 0 ? ItemData.Write(d.Mine[i].DataId) ?? Array.Empty<byte>() : Array.Empty<byte>()); // shown there as it is
        }
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnOffer(Deal d, ref NetReader r)
    {
        d.TheirGold = r.I32();
        int n = r.U8();
        d.Theirs = new List<Item>(n);
        for (int i = 0; i < n; i++)
        {
            var it = new Item { Id = r.I32(), Count = r.I32() };
            var data = r.Blob();
            it.Data = data.Length > 0 ? data : null;
            d.Theirs.Add(it);
        }
        d.TheirSig = Sig(d.TheirGold, d.Theirs);
        d.MineReady = d.TheirsReady = false;
        ShowTheirOffer(d);
        Restyle();
    }

    /// <summary>The other player's offer on the adventurer side of the window: their items as the shell's bag, all offered.</summary>
    private static void ShowTheirOffer(Deal d)
    {
        var p = Panel;
        var shell = PlayerShells.Get(d.Other);
        if (p == null || shell == null) return;
        _rebuilding = true;
        try
        {
            var bag = new Il2CppSystem.Collections.Generic.List<MobCombatEntity.AdventurerInventorySlot>();
            var db = GameDatabase.Instance.GetItems();
            ItemData.DropTemps("trade");
            foreach (var i in d.Theirs)
                if (db.ContainsKey(i.Id)) bag.Add(new MobCombatEntity.AdventurerInventorySlot { item = db[i.Id], itemDataID = ItemData.CreateTemp("trade", i.Id, i.Data), count = i.Count });
            shell.adventurerInventory = bag;
            p.ClearOffer(p.adventurerOffer);
            for (int i = 0; i < bag.Count; i++) p.AddAdventurerOfferedItem(i);
            p.adventurerOfferedGold = d.TheirGold;
            p.adventurerOfferedGoldInput?.SetTextWithoutNotify(d.TheirGold > 0 ? d.TheirGold.ToString() : "");
            p.Recalculate();
        }
        catch (Exception e) { Plugin.L.LogWarning("Trade offer: " + e.Message); }
        finally { _rebuilding = false; }
    }

    // ---------------- ready ----------------

    /// <summary>"Accept": ready for the offers as they are (a change of either clears it). The button can fire twice per click.</summary>
    private static void SetReady()
    {
        var d = _deal;
        if (d == null || d.Committing || d.MineReady) return;
        d.MineReady = true;
        Header(OpAccept, d.Id, d.Other); W.U8((byte)(d.MineReady ? 1 : 0)); W.I32(PairHash(d));
        CoopManager.Session.SendWorld(W.Span);
        Restyle();
        TryCommit(d);
    }

    private static void OnAccept(Deal d, bool on, int hash)
    {
        d.TheirsReady = on;
        d.TheirHash = hash;
        if (on) Hud.Notify($"{Groups.Name(d.Other)} is ready to trade");
        Restyle();
        TryCommit(d);
    }

    private static void TryCommit(Deal d)
    {
        if (d.Committing || !d.MineReady || !d.TheirsReady || d.TheirHash != PairHash(d)) return;
        Escrow(d);
    }

    // ---------------- the exchange ----------------

    private static void Escrow(Deal d)
    {
        d.Committing = true;
        Restyle();
        var inv = InventoryManager.Instance;
        var character = BLINK.RPGBuilder.Characters.Character.Instance;
        string fail = null;
        try
        {
            var slots = character.CharacterData.Inventory.baseSlots;
            foreach (var i in d.Mine)
            {
                if (i.Slot < 0 || i.Slot >= slots.Count) { fail = "an item moved"; break; }
                var sl = slots[i.Slot];
                if (sl.itemID != i.Id || sl.itemStack < i.Count) { fail = "an item is no longer in the bags"; break; }
            }
            var cur = Currency;
            if (fail == null && d.MyGold > 0 && (cur == null || character.getCurrencyAmount(cur) < d.MyGold)) fail = "not enough gold";
            // a slot frees up only when its whole stack goes (5 of 20 potions leave the slot taken)
            int freed = 0;
            if (fail == null) foreach (var i in d.Mine) if (slots[i.Slot].itemStack == i.Count) freed++;
            if (fail == null && inv.getEmptySlotsCount() + freed < d.Theirs.Count) fail = "no room in the bags";
            if (fail == null)
            {
                // kept as it goes out of the bags: an error halfway still refunds what already went
                d.Escrow = new List<Item>();
                d.EscrowGold = 0;
                foreach (var i in d.Mine)
                {
                    i.Data = i.DataId >= 0 ? ItemData.Write(i.DataId) : null;
                    inv.RemoveItem(i.Id, i.DataId, i.Count, 0, i.Slot, true);
                    d.Escrow.Add(i);
                }
                if (d.MyGold > 0) { inv.RemoveCurrency(cur.ID, d.MyGold); d.EscrowGold = d.MyGold; }
            }
        }
        catch (Exception e) { fail = "error"; Plugin.L.LogWarning("Trade escrow: " + e); }

        Header(OpEscrowed, d.Id, d.Other);
        W.U8((byte)(fail == null ? 1 : 0));
        if (fail == null)
        {
            W.I32(d.EscrowGold); W.U8((byte)d.Escrow.Count);
            foreach (var i in d.Escrow) { W.I32(i.Id); W.I32(i.Count); W.Blob(i.Data ?? Array.Empty<byte>()); }
        }
        CoopManager.Session.SendWorld(W.Span);
        Plugin.L.LogInfo($"Trade {d.Id:x8}: escrow {(fail == null ? "ok" : "failed: " + fail)}");
        if (fail != null) Hud.Notify("Trade failed: " + fail);
        if (CoopManager.Session.IsHost) Coordinate(d.Id, CoopManager.Session.LocalId, d.Other, fail == null);
    }

    private static void OnEscrowed(byte from, uint id, byte to, ref NetReader r)
    {
        bool ok = r.U8() == 1;
        var d = _deal;
        if (ok && d != null && d.Id == id && d.Other == from)
        {
            d.TheirEscrowGold = r.I32();
            int n = r.U8();
            d.TheirEscrow = new List<Item>(n);
            for (int i = 0; i < n; i++)
            {
                var it = new Item { Id = r.I32(), Count = r.I32() };
                var data = r.Blob();
                it.Data = data.Length > 0 ? data : null;
                d.TheirEscrow.Add(it);
            }
            // They took theirs out while I changed my mind (not ready, another offer): I fail, the host refunds them.
            if (!d.Committing)
            {
                if (d.MineReady && d.TheirsReady && d.TheirHash == PairHash(d)) Escrow(d);
                else Decline(d);
            }
        }
        if (CoopManager.Session.IsHost) Coordinate(id, from, to, ok);
    }

    /// <summary>I will not take my offer out (the other side did, but we no longer agree): the host refunds them.</summary>
    private static void Decline(Deal d)
    {
        d.Committing = true; // nothing of mine is out: a refund just closes the window
        Header(OpEscrowed, d.Id, d.Other); W.U8(0);
        CoopManager.Session.SendWorld(W.Span);
        if (CoopManager.Session.IsHost) Coordinate(d.Id, CoopManager.Session.LocalId, d.Other, false);
    }

    /// <summary>Host: both sides took their offers out → deliver; one could not → refund.</summary>
    private static void Coordinate(uint id, byte from, byte other, bool ok)
    {
        if (_devStall) return; // dev: a host that never gets to finish it (gone, crashed)
        // the other side already left (before any of it reached me): nobody will answer for it
        if (Refunded.Contains(id) || !CoopManager.Session.Players.ContainsKey(other)) { Finish(id, OpRefund); return; }
        if (!Commits.TryGetValue(id, out var c)) Commits[id] = c = new Commit { A = from, B = other };
        if (!ok) { Finish(id, OpRefund); return; }
        if (from == c.A) c.OkA = true; else c.OkB = true;
        if (c.OkA && c.OkB) Finish(id, OpDeliver);
    }

    private static void Finish(uint id, byte op)
    {
        Commits.Remove(id);
        if (op == OpRefund) Refunded.Add(id);
        Header(op, id, 255);
        CoopManager.Session.SendWorld(W.Span);
        Plugin.L.LogInfo($"Trade {id:x8}: {(op == OpDeliver ? "deliver" : "refund")}");
        OnFinish(id, op); // the host's own side
    }

    private static void OnFinish(uint id, byte op)
    {
        var d = _deal;
        if (d == null || d.Id != id) return;
        if (!d.Committing)
        {
            // refused before I got to it: I take nothing out
            if (op == OpRefund) { Close(); Hud.Notify("Trade failed: nothing was exchanged"); }
            return;
        }
        if (op == OpDeliver && d.TheirEscrow != null)
        {
            foreach (var i in d.TheirEscrow) Give(i);
            if (d.TheirEscrowGold > 0 && Currency != null) InventoryManager.Instance.AddCurrency(Currency.ID, d.TheirEscrowGold);
            Chat($"Trade with {Groups.Name(d.Other)} complete.");
            Chat("You gave: " + Describe(d.Escrow, d.EscrowGold));
            Chat("You received: " + Describe(d.TheirEscrow, d.TheirEscrowGold));
        }
        else
        {
            if (d.Escrow != null) foreach (var i in d.Escrow) Give(i);
            if (d.EscrowGold > 0 && Currency != null) InventoryManager.Instance.AddCurrency(Currency.ID, d.EscrowGold);
            Chat($"Trade with {Groups.Name(d.Other)} failed: nothing was exchanged.");
        }
        Close();
    }

    /// <summary>A line in the game's chat (as its own trade with an adventurer reports).</summary>
    private static void Chat(string text)
    {
        try { UIEvents.Instance?.OnShowChatTextMessage(text); }
        catch (Exception e) { Plugin.L.LogWarning("Trade chat: " + e.Message); }
        Plugin.L.LogInfo("Trade chat: " + text);
    }

    /// <summary>An offer in the game's own words (TradePanel.BuildOfferDescription: items with counts, gold).</summary>
    private static string Describe(List<Item> items, int gold)
    {
        var p = Panel;
        try
        {
            if (p != null)
            {
                var list = new Il2CppSystem.Collections.Generic.List<TradePanel.TradeItemEntry>();
                var db = GameDatabase.Instance.GetItems();
                if (items != null) foreach (var i in items) if (db.ContainsKey(i.Id)) list.Add(new TradePanel.TradeItemEntry { item = db[i.Id], count = i.Count, itemDataID = -1, sourceSlotIndex = -1 });
                var text = p.BuildOfferDescription(list, Math.Max(gold, 0));
                if (!string.IsNullOrEmpty(text)) return text;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Trade describe: " + e.Message); }
        return (items == null || items.Count == 0) && gold <= 0 ? "nothing" : $"{items?.Count ?? 0} item(s), {Math.Max(gold, 0)} gold";
    }

    /// <summary>An item into my bags as it was on the other machine: a new data entry here, filled with its data.</summary>
    private static void Give(Item i)
    {
        try
        {
            int dataId = -1;
            if (i.Data != null)
            {
                dataId = RPGBuilderUtilities.HandleNewItemDATA(i.Id, CharacterEntries.ItemEntryState.InBag);
                ItemData.Read(i.Data, dataId);
            }
            RPGBuilderUtilities.HandleItemLooting(i.Id, dataId, i.Count, false, false);
        }
        catch (Exception e) { Plugin.L.LogWarning($"Trade give #{i.Id}: {e.Message}"); }
    }

    /// <summary>Host: a player left during an exchange: the other side gets its offer back.</summary>
    public static void OnPlayerLeft(byte player)
    {
        var s = CoopManager.Session;
        if (s != null && s.IsHost)
        {
            var gone = new List<uint>();
            foreach (var kv in Commits) if (kv.Value.A == player || kv.Value.B == player) gone.Add(kv.Key);
            foreach (var id in gone) Finish(id, OpRefund);
        }
        if (_deal != null && _deal.Other == player && !_deal.Committing) { Close(); Hud.Notify("Trade cancelled: the other player left"); }
    }

    /// <summary>
    /// The session is over: no host will finish an exchange now, what I took out comes back.
    /// shortcut: had the host already told the other side to deliver, they keep theirs too; rare, and better than losing it.
    /// </summary>
    public static void Reset()
    {
        if (_deal is { Committing: true } d) OnFinish(d.Id, OpRefund);
        else if (_deal != null) Close();
        Commits.Clear(); Refunded.Clear();
        _asked = 0; _incoming = null;
    }

    // ---------------- every frame ----------------

    public static void Update()
    {
        if (_asked != 0 && Time.unscaledTime - _askedAt > RequestTimeout) { _asked = 0; Hud.Notify("Trade request expired"); }
        var d = _deal;
        if (d == null) return;
        var p = Panel;
        if (d.Committing) return; // the exchange finishes even with the window closed
        if (p == null || !p.IsOpen()) { Cancel("window closed"); return; }
        var shell = PlayerShells.Get(d.Other);
        var me = GameInfo.Player;
        if (shell == null || me == null || Vector3.Distance(shell.transform.position, me.transform.position) > KeepDistance) { Cancel("too far away"); return; }

        var mine = new List<Item>();
        ReadMyOffer(d, mine, out int gold);
        string sig = Sig(gold, mine);
        if (sig != d.MySig)
        {
            d.MySig = sig; d.Mine = mine; d.MyGold = gold;
            d.MineReady = d.TheirsReady = false;
            SendOffer(d);
            Restyle();
        }
    }

    // ---------------- messages ----------------

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8();
        uint id = r.U32();
        byte to = r.U8();
        var s = CoopManager.Session;
        if (s == null) return;
        switch (op)
        {
            case OpEscrowed: OnEscrowed(from, id, to, ref r); return; // the host coordinates any pair
            case OpDeliver:
            case OpRefund:
                if (from == ProtocolInfo.HostId) OnFinish(id, op);
                return;
        }
        if (to != s.LocalId) return;
        switch (op)
        {
            case OpRequest: OnRequest(from, id); break;
            case OpAnswer: OnAnswer(from, id, r.U8() == 1); break;
            default:
            {
                var d = _deal;
                if (d == null || d.Id != id || d.Other != from) return;
                switch (op)
                {
                    case OpOffer: if (!d.Committing) OnOffer(d, ref r); break;
                    case OpAccept: { bool on = r.U8() == 1; OnAccept(d, on, r.I32()); break; }
                    case OpCancel:
                        if (!d.Committing) { Close(); Hud.Notify($"{Groups.Name(from)} cancelled the trade"); }
                        else
                        {
                            // they left before taking theirs out: mine comes back through the host
                            Header(OpEscrowed, d.Id, d.Other); W.U8(0);
                            CoopManager.Session.SendWorld(W.Span);
                            if (CoopManager.Session.IsHost) Coordinate(d.Id, CoopManager.Session.LocalId, d.Other, false);
                        }
                        break;
                }
                break;
            }
        }
    }

    // ---------------- dev bridge ----------------

    public static string Dev(string cmd, string arg)
    {
        var p = Panel;
        switch (cmd)
        {
            case "trade": Ask(byte.Parse(arg)); return "asked";
            case "tradeanswer": return Answer(arg != "no");
            case "tradeoffer": // tradeoffer <bag slot>
            {
                int slot = int.Parse(arg);
                var sl = BLINK.RPGBuilder.Characters.Character.Instance.CharacterData.Inventory.baseSlots[slot];
                var db = GameDatabase.Instance.GetItems();
                if (sl.itemID < 0 || !db.ContainsKey(sl.itemID)) return "empty slot";
                return p.AddPlayerOfferedItem(db[sl.itemID], slot) ? $"offered #{sl.itemID} x{sl.itemStack} (data {sl.itemDataID})" : "not added";
            }
            case "tradegold": p.playerOfferedGoldInput.text = arg; return "gold " + p.GetPlayerOfferedGold();
            case "tradeready": SetReady(); return "ready=" + _deal?.MineReady;
            case "tradecancel": Cancel("dev"); return "cancelled";
            case "tradestall": _devStall = arg != "off"; return "host finishes exchanges: " + !_devStall; // tradestall [off]
            case "fillbag": // fillbag <item id>: one of it into every free bag slot
            {
                int item = int.Parse(arg), free = InventoryManager.Instance.getEmptySlotsCount();
                for (int k = 0; k < free; k++) RPGBuilderUtilities.HandleItemLooting(item, -1, 1, false, false);
                return "free=" + InventoryManager.Instance.getEmptySlotsCount();
            }
            case "tradeui": // where the parts of the trade window are
            {
                string Path(Transform t) { var b = new List<string>(); for (; t != null && t != p.transform.parent; t = t.parent) b.Insert(0, t.name); return string.Join("/", b); }
                var root = p.transform;
                var kids = new List<string>();
                for (int i = 0; i < root.childCount; i++) kids.Add(root.GetChild(i).name);
                return $"slots={Path(p.adventurerSlotsParent)} | name={Path(p.adventurerNameText?.transform)} | caption={Path(TheirCaption(p)?.transform)} | hide={Path(BagWindow(p)?.transform)} | root kids=[{string.Join(",", kids)}]";
            }
            case "bag": // bag: my base slots with items
            {
                var slots = BLINK.RPGBuilder.Characters.Character.Instance.CharacterData.Inventory.baseSlots;
                var db = GameDatabase.Instance.GetItems();
                var rows = new List<string>();
                for (int i = 0; i < slots.Count; i++)
                    if (slots[i].itemID >= 0 && db.ContainsKey(slots[i].itemID))
                        rows.Add($"{i}:'{db[slots[i].itemID].entryName}' #{slots[i].itemID} x{slots[i].itemStack} d{slots[i].itemDataID}{(slots[i].itemDataID >= 0 ? " " + ItemData.Describe(slots[i].itemDataID) : "")}");
                var cur = Currency;
                return $"gold={(cur != null ? BLINK.RPGBuilder.Characters.Character.Instance.getCurrencyAmount(cur) : -1)} free={InventoryManager.Instance.getEmptySlotsCount()} | " + string.Join(" | ", rows);
            }
            default: // tradestate
            {
                var d = _deal;
                if (d == null) return $"no trade (asked {_asked:x8}, incoming {_incoming?.id:x8})";
                return $"{d.Id:x8} with #{d.Other} mine='{d.MySig}' ready={d.MineReady} theirs='{d.TheirSig}' ready={d.TheirsReady} " +
                       $"same={d.TheirHash == PairHash(d)} committing={d.Committing} window={p?.IsOpen()}";
            }
        }
    }

    // ---------------- hooks ----------------

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnTradeClicked))]
        private static bool TradeClicked(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            try
            {
                if (CoopManager.Session == null || !Groups.Resolve(__instance.GetTargetEntity(), out var player, out var bot) || bot) return true;
                Ask(player);
                __instance.Hide();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Trade menu: " + e.Message); return true; }
        }

        /// <summary>"Trade" in the interaction menu (F) on a player's shell: ask the player.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.OpenTrade))]
        private static bool TradeFromInteraction(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player)) return true;
                Ask(player);
                __instance.Hide();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Trade interaction: " + e.Message); return true; }
        }

        /// <summary>
        /// The interaction menu (F) offers "Trade" only for NPCs that trade; a shell's template does not: on a player
        /// it is added as the game adds it (its text option prefab, type Trade), and leads to <see cref="TradeFromInteraction"/>.
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void TradeOption(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out _)) return;
                var slots = __instance.optionSlots;
                const BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType trade = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.Trade;
                if (slots != null) for (int i = 0; i < slots.Count; i++) if (slots[i] != null && slots[i].Type == trade) return;
                var go = UnityEngine.Object.Instantiate(__instance.TextOptionPrefab, __instance.TextOptionsParent);
                var option = go.GetComponent<NPCInteractionTextOption>();
                option.Initialize("Trade", __instance);
                option.Type = trade;
                slots?.Add(option);
            }
            catch (Exception e) { Plugin.L.LogWarning("Trade option: " + e.Message); }
        }

        /// <summary>In a player trade "Accept" marks me ready; the exchange runs when both are.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.OnAccept))]
        private static bool Accept()
        {
            if (_deal == null) return true;
            try { SetReady(); } catch (Exception e) { Plugin.L.LogWarning("Trade accept: " + e.Message); }
            return false;
        }

        /// <summary>The other player's offer is theirs to change.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.RemoveAdventurerOfferedItem))]
        private static bool RemoveTheirs() => _deal == null || _rebuilding;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.AddAdventurerOfferedItem))]
        private static bool AddTheirs() => _deal == null || _rebuilding;

        /// <summary>Nothing changes once the exchange runs.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.AddPlayerOfferedItem))]
        private static bool AddMine() => _deal == null || !_deal.Committing;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.RemovePlayerOfferedItem))]
        private static bool RemoveMine() => _deal == null || !_deal.Committing;

        [HarmonyPostfix]
        [HarmonyPatch(typeof(TradePanel), nameof(TradePanel.RefreshAdventurerDisplay))]
        private static void Refreshed() { if (_deal != null) Restyle(); }
    }
}

/// <summary>
/// An item's own data (CharacterData.ItemEntries: enchantment, sockets, corruption, heroic, loot rolls; and its rolled
/// random stats in CharacterData.RandomizedItems) as bytes, written from one character's entry and read into another's.
/// </summary>
internal static class ItemData
{
    private static BLINK.RPGBuilder.Characters.CharacterData Data => BLINK.RPGBuilder.Characters.Character.Instance.CharacterData;

    private static CharacterEntries.RandomizedItem Randomized(int rdmId)
    {
        if (rdmId < 0) return null;
        foreach (var r in Data.RandomizedItems) if (r != null && r.id == rdmId) return r;
        return null;
    }

    public static byte[] Write(int dataId)
    {
        var e = RPGBuilderUtilities.GetItemDataFromDataID(dataId);
        if (e == null) return null;
        var w = new NetWriter();
        w.I32(e.enchantmentID); w.I32(e.enchantmentTierIndex);
        int n = e.sockets?.Count ?? 0;
        w.U8((byte)n);
        for (int i = 0; i < n; i++) { var so = e.sockets[i]; w.Str(so?.socketType ?? ""); w.Str(so?.GemSocketType ?? ""); w.I32(so?.gemItemID ?? -1); }
        w.I32(e.corruptionValue); w.I32(e.corruptionLevel);
        n = e.corruptionAffixes?.Count ?? 0;
        w.U8((byte)n);
        for (int i = 0; i < n; i++) w.I32(e.corruptionAffixes[i]);
        w.U8((byte)(e.isHeroic ? 1 : 0)); w.U8((byte)(e.AdventurerGenerated ? 1 : 0));
        n = e.lootRolls?.Count ?? 0;
        w.U8((byte)n);
        for (int i = 0; i < n; i++) { var lr = e.lootRolls[i]; w.I32(lr.itemID); w.I32(lr.count); w.U8((byte)(lr.looted ? 1 : 0)); }
        var rnd = Randomized(e.rdmItemID);
        n = rnd?.randomStats?.Count ?? 0;
        w.U8((byte)n);
        for (int i = 0; i < n; i++) { var st = rnd.randomStats[i]; w.I32(st.statID); w.F32(st.statValue); }
        return w.Span.ToArray();
    }

    public static void Read(byte[] bytes, int dataId)
    {
        var e = RPGBuilderUtilities.GetItemDataFromDataID(dataId);
        if (e == null || bytes == null) return;
        var r = new NetReader(bytes);
        e.enchantmentID = r.I32(); e.enchantmentTierIndex = r.I32();
        int n = r.U8();
        var sockets = new Il2CppSystem.Collections.Generic.List<CharacterEntries.ItemSocketEntry>();
        for (int i = 0; i < n; i++) sockets.Add(new CharacterEntries.ItemSocketEntry { socketType = r.Str(), GemSocketType = r.Str(), gemItemID = r.I32() });
        e.sockets = sockets;
        e.corruptionValue = r.I32(); e.corruptionLevel = r.I32();
        n = r.U8();
        var affixes = new Il2CppSystem.Collections.Generic.List<int>();
        for (int i = 0; i < n; i++) affixes.Add(r.I32());
        e.corruptionAffixes = affixes;
        e.isHeroic = r.U8() == 1; e.AdventurerGenerated = r.U8() == 1;
        n = r.U8();
        var rolls = new Il2CppSystem.Collections.Generic.List<CharacterEntries.ItemLootRollEntry>();
        for (int i = 0; i < n; i++) rolls.Add(new CharacterEntries.ItemLootRollEntry { itemID = r.I32(), count = r.I32(), looted = r.U8() == 1 });
        e.lootRolls = rolls;
        n = r.U8();
        var rnd = Randomized(e.rdmItemID); // rolled anew when the entry was made: the sender's roll replaces it
        if (rnd != null && n > 0)
        {
            var stats = new Il2CppSystem.Collections.Generic.List<RPGItemDATA.RandomizedStat>();
            for (int i = 0; i < n; i++) stats.Add(new RPGItemDATA.RandomizedStat { statID = r.I32(), statValue = r.F32() });
            rnd.randomStats = stats;
        }
    }

    // ---------------- temporary entries: another player's item shown here (inspect, a trade offer) ----------------

    private static readonly Dictionary<string, List<int>> Temps = new();

    /// <summary>
    /// A data entry here holding another player's item data, to show that item as it is (tooltips read the entry by
    /// id, as for an adventurer's items); dropped with <see cref="DropTemps"/> when no longer shown. -1 without data.
    /// </summary>
    public static int CreateTemp(string owner, int itemId, byte[] data)
    {
        if (data == null || data.Length == 0) return -1;
        try
        {
            int id = RPGBuilderUtilities.HandleNewItemDATA(itemId, CharacterEntries.ItemEntryState.InWorld);
            Read(data, id);
            if (!Temps.TryGetValue(owner, out var list)) Temps[owner] = list = new List<int>();
            list.Add(id);
            return id;
        }
        catch (Exception e) { Plugin.L.LogWarning("Item data: " + e.Message); return -1; }
    }

    /// <summary>Remove the entries made for one display (and their rolled stats) from the character's data.</summary>
    public static void DropTemps(string owner)
    {
        if (!Temps.TryGetValue(owner, out var list) || list.Count == 0) return;
        try
        {
            var entries = Data.ItemEntries;
            foreach (var id in list)
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    if (e == null || e.id != id) continue;
                    int rdm = e.rdmItemID;
                    entries.RemoveAt(i);
                    if (rdm >= 0)
                        for (int k = Data.RandomizedItems.Count - 1; k >= 0; k--)
                            if (Data.RandomizedItems[k]?.id == rdm) { Data.RandomizedItems.RemoveAt(k); break; }
                    break;
                }
        }
        catch (Exception e) { Plugin.L.LogWarning("Item data drop: " + e.Message); }
        list.Clear();
    }

    public static string Describe(int dataId)
    {
        var e = RPGBuilderUtilities.GetItemDataFromDataID(dataId);
        if (e == null) return "(no data)";
        var rnd = Randomized(e.rdmItemID);
        var stats = new List<string>();
        if (rnd?.randomStats != null) foreach (var st in rnd.randomStats) stats.Add($"{st.statID}={st.statValue:0.##}");
        return $"(ench {e.enchantmentID}/{e.enchantmentTierIndex} sockets {e.sockets?.Count ?? 0} heroic {e.isHeroic} stats [{string.Join(",", stats)}])";
    }
}
