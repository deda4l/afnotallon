using System;
using System.Collections.Generic;
using System.IO;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.AuctionHouse;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// One auction house for the session: the host's (its market, its trader bots), as in an MMO.
///
/// Host: its game runs the market as ever. Remote players are owners of their own there (ids -10, -11, ... kept per
/// player across sessions in a file): the game's checks of their gold and presence pass (they paid on their machine),
/// their name shows on their listings, and what the market mails them (sold items' gold, bought items, refunds,
/// returns) goes to their machine, or waits in a file until they are back.
///
/// Client: its own traders and listings rest while in a session (they are back when playing alone; its save keeps
/// them). It shows the host's listings, its own among them as its own, on the host's market clock. Posting, buying,
/// bidding and cancelling take the item or gold here at once (as the game does) and are carried out by the host; the
/// mail it gets lands in its own mailbox (claimed there as ever).
/// </summary>
internal static class AuctionSync
{
    public const byte Kind = 22;
    private const byte OpListings = 1, OpPost = 2, OpBuy = 3, OpBid = 4, OpCancel = 5, OpMail = 6, OpMailAck = 7, OpBrowsing = 8;
    private const int HostOwner = -9, FirstRemote = -10;
    private const int ChunkSize = 60;

    private static readonly NetWriter W = new();
    private static AuctionMarket Market { get { try { return AuctionHouseService.Instance?.Market; } catch { return null; } } }
    private static bool IsClient => CoopManager.Connected && CoopManager.Session != null && !CoopManager.Session.IsHost;

    private static void Send()
    {
        CoopManager.Session?.SendWorld(W.Span);
    }

    // ================= host =================

    private sealed class Owner { public string Key = ""; public int Id; public string Name = ""; }
    private static readonly List<Owner> Owners = new();
    private static string _ownersFor;
    private static string HostChar => GameInfo.Character?.CharacterName ?? "";
    private static string OwnersFile => Path.Combine(BepInEx.Paths.ConfigPath, $"AfallonCoop.auction-players.{Safe(HostChar)}.txt");
    private static string PendingFile => Path.Combine(BepInEx.Paths.ConfigPath, $"AfallonCoop.auction-mail.{Safe(HostChar)}.txt");

    private static string Safe(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n; }

    private static void LoadOwners()
    {
        if (_ownersFor == HostChar) return;
        _ownersFor = HostChar;
        Owners.Clear();
        try
        {
            if (File.Exists(OwnersFile))
                foreach (var l in File.ReadAllLines(OwnersFile)) { var f = l.Split('\t'); if (f.Length >= 3 && int.TryParse(f[1], out var id)) Owners.Add(new Owner { Key = f[0], Id = id, Name = f[2] }); }
        }
        catch (Exception e) { Plugin.L.LogWarning("Auction owners: " + e.Message); }
    }

    private static void SaveOwners()
    {
        try { File.WriteAllLines(OwnersFile, Owners.ConvertAll(o => $"{o.Key}\t{o.Id}\t{o.Name}")); }
        catch (Exception e) { Plugin.L.LogWarning("Auction owners: " + e.Message); }
    }

    private static bool IsRemote(int owner) => owner <= FirstRemote;

    /// <summary>Host: the market owner id of a session player (made on first use, kept).</summary>
    private static int OwnerOf(byte player)
    {
        LoadOwners();
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var p) || string.IsNullOrEmpty(p.Key)) return 0;
        var o = Owners.Find(x => x.Key == p.Key);
        string name = Groups.Name(player);
        if (o == null)
        {
            int id = FirstRemote; foreach (var x in Owners) id = Math.Min(id, x.Id - 1);
            o = new Owner { Key = p.Key, Id = Owners.Count == 0 ? FirstRemote : id, Name = name };
            Owners.Add(o); SaveOwners();
        }
        else if (o.Name != name && !string.IsNullOrEmpty(name)) { o.Name = name; SaveOwners(); }
        return o.Id;
    }

    /// <summary>Host: the session player of a market owner id (255: not here).</summary>
    private static byte PlayerOf(int owner)
    {
        LoadOwners();
        var o = Owners.Find(x => x.Id == owner);
        var s = CoopManager.Session;
        if (o == null || s == null) return 255;
        foreach (var p in s.Players.Values) if (p.Key == o.Key && p.Id != s.LocalId) return p.Id;
        return 255;
    }

    private static string NameOf(int owner) { LoadOwners(); return Owners.Find(x => x.Id == owner)?.Name ?? "Player"; }

    private static int _sentRevision = int.MinValue;
    private static float _nextListings;
    private static readonly HashSet<byte> Fresh = new(); // players who need the listings now (just joined)
    private static int _snapshot;

    public static void Update()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected) return;
        if (s.IsHost) HostUpdate(s); else ClientUpdate();
    }

    private static void HostUpdate(AfallonCoop.Session.CoopSession s)
    {
        var m = Market;
        if (m == null || s.PlayerCount < 2 || Time.unscaledTime < _nextListings) return;
        _nextListings = Time.unscaledTime + 3f;
        DeliverWaiting();
        // A player's own post, buy, bid or cancel, and a newcomer: at once. The market's own trading (its trader bots
        // change it every few seconds): every 3 s while someone has the auction window open, else at most every 30 s
        // (the whole list is ~3 KB a chunk). Nothing when nothing changed.
        Browsing.RemoveWhere(id => !s.Players.ContainsKey(id));
        bool botsChanged = m.Revision != _sentRevision && Time.unscaledTime - _sentAt >= (Browsing.Count > 0 ? 3f : 30f);
        if (!_urgent && Fresh.Count == 0 && !botsChanged) return;
        _sentAt = Time.unscaledTime;
        _urgent = false;
        _sentRevision = m.Revision;
        Fresh.Clear();
        SendListings(m);
    }

    private static float _sentAt = -100f;
    private static bool _urgent;
    private static readonly HashSet<byte> Browsing = new(); // players with the auction window open

    /// <summary>The active listings in chunks (owners: the host as -9, players by their ids), with the owner names and the clock.</summary>
    private static void SendListings(AuctionMarket m)
    {
        LoadOwners();
        var s = CoopManager.Session;
        var list = new List<AuctionListing>();
        foreach (var l in m.State.Listings) if (l != null && m.IsActive(l)) list.Add(l);
        int chunks = Math.Max(1, (list.Count + ChunkSize - 1) / ChunkSize);
        _snapshot++;
        for (int c = 0; c < chunks; c++)
        {
            W.Reset(); W.U8(Kind); W.U8(OpListings); W.I32(_snapshot); W.U16((ushort)c); W.U16((ushort)chunks); W.F64(m.Clock);
            if (c == 0)
            {
                // owners: the host and every known player (their session id when here, so each finds itself)
                W.U8((byte)Math.Min(Owners.Count + 1, 255));
                W.I32(HostOwner); W.Str(Groups.Name(s.LocalId)); W.U8(s.LocalId);
                foreach (var o in Owners) { W.I32(o.Id); W.Str(o.Name); W.U8(PlayerOf(o.Id)); }
            }
            int from = c * ChunkSize, to = Math.Min(list.Count, from + ChunkSize);
            W.U8((byte)(to - from));
            for (int i = from; i < to; i++)
            {
                var l = list[i];
                W.I64(l.ID); W.I32(l.Seller == -1 ? HostOwner : l.Seller); W.I32(l.ItemID); W.I32(l.Quantity); W.I32(l.UnitPrice);
                W.I32(l.StartingBid); W.I32(l.Bidder == -1 ? HostOwner : l.Bidder); W.I32(l.CurrentBid); W.I32(l.Deposit); W.F64(l.Expires);
            }
            Send();
        }
    }

    /// <summary>Host: a remote player's request, carried out on the market as the game would for them.</summary>
    private static void HostRequest(byte from, byte op, ref NetReader r)
    {
        var m = Market;
        int owner = OwnerOf(from);
        if (m == null || owner == 0) return;
        string err = "";
        switch (op)
        {
            case OpPost:
            {
                int item = r.I32(), count = r.I32(), price = r.I32(), startBid = r.I32(), minutes = r.I32(), deposit = r.I32();
                var data = r.Blob();
                int dataId = -1;
                if (data.Length > 0) { dataId = RPGBuilderUtilities.HandleNewItemDATA(item, CharacterEntries.ItemEntryState.InWorld); ItemData.Read(data, dataId); }
                var l = new AuctionListing
                {
                    ID = m.State.NextID++, Seller = owner, ItemID = item, ItemDataID = dataId, Quantity = count, UnitPrice = price,
                    StartingBid = startBid, Bidder = -2, CurrentBid = 0, Deposit = deposit, Expires = m.Clock + minutes * 60.0,
                };
                m.State.Listings.Add(l);
                Bump(m);
                Plugin.L.LogInfo($"Auction: {NameOf(owner)} posted #{item} x{count} at {price}");
                return;
            }
            case OpBuy:
            {
                long paid = r.I64();
                int n = r.U8();
                var q = new AuctionQuote { Buyer = owner };
                long total = 0;
                for (int i = 0; i < n; i++) { var line = new AuctionQuoteLine { ListingID = r.I64(), Quantity = r.I32(), UnitPrice = r.I32() }; q.Lines.Add(line); total += (long)line.Quantity * line.UnitPrice; }
                q.Total = total;
                if (!m.Buy(q, out err)) Refund(owner, paid, "Purchase failed: " + err);
                else Plugin.L.LogInfo($"Auction: {NameOf(owner)} bought for {total}");
                Bump(m);
                return;
            }
            case OpBid:
            {
                long id = r.I64(); int amount = r.I32(); long paid = r.I64();
                if (!m.Bid(owner, id, amount, out err)) Refund(owner, paid, "Bid failed: " + err);
                Bump(m);
                return;
            }
            case OpCancel:
            {
                long id = r.I64();
                m.Cancel(owner, id, out err); // a failure shows as the unchanged listing
                Bump(m);
                return;
            }
        }
    }

    private static void Refund(int owner, long gold, string subject)
    {
        if (gold > 0) Market?.DeliverGold(owner, gold, subject);
    }

    private static void Bump(AuctionMarket m)
    {
        try { m.Revision = m.Revision + 1; } catch { }
        _nextListings = 0f; // the listings go out at once
        _urgent = true;
    }

    // ---- the market's mail for remote players: to them, or kept until they are back ----

    // Mail is kept in a file until the player's machine says it is in their mailbox: sent when they are in the world
    // (a machine still loading has no market to take it), again every few seconds until it is acknowledged.
    // Line: id, player key, item, count, gold, subject, item data (base64).

    private static readonly Dictionary<string, float> SentAt = new();

    private static void RouteMail(int owner, int itemId, int dataId, int count, long gold, string subject)
    {
        byte[] data = dataId >= 0 ? ItemData.Write(dataId) : null;
        if (dataId >= 0) RemoveData(dataId); // no longer anything of the host's
        LoadOwners();
        var key = Owners.Find(o => o.Id == owner)?.Key ?? "";
        string id = Guid.NewGuid().ToString("N");
        try { File.AppendAllLines(PendingFile, new[] { string.Join("	", id, key, itemId, count, gold, PackText(subject), data != null ? Convert.ToBase64String(data) : "") }); }
        catch (Exception e) { Plugin.L.LogWarning("Auction mail kept: " + e.Message); }
        _nextListings = 0f; // DeliverWaiting at once
    }

    /// <summary>
    /// Host: a letter from one player to another (by lasting key): into my own mailbox if it is mine, else kept and
    /// delivered as the market's mail is (now, or when they are next in the world).
    /// </summary>
    public static void HostMail(string key, List<(int item, int count, byte[] data)> items, long gold, string subject)
    {
        var s = CoopManager.Session;
        var m = Market;
        // one letter per item, the gold with the first; a letter of words alone is one letter too
        var letters = new List<(int item, int count, byte[] data, long gold)>();
        foreach (var it in items) letters.Add((it.item, it.count, it.data, letters.Count == 0 ? gold : 0));
        if (letters.Count == 0) letters.Add((0, 0, null, gold));
        if (s != null && m != null && s.Players.TryGetValue(s.LocalId, out var me) && me.Key == key)
        {
            foreach (var l in letters) Deliver(m, l.item, l.count, l.gold, subject, l.data);
            AfallonCoop.Plugin.Mail.Arrived(subject);
            return;
        }
        try
        {
            var lines = new List<string>();
            foreach (var l in letters)
                lines.Add(string.Join("	", Guid.NewGuid().ToString("N"), key, l.item, l.count, l.gold, PackText(subject), l.data != null && l.data.Length > 0 ? Convert.ToBase64String(l.data) : ""));
            File.AppendAllLines(PendingFile, lines);
        }
        catch (Exception e) { Plugin.L.LogWarning("Mail kept: " + e.Message); }
        _nextListings = 0f; // DeliverWaiting at once
    }

    /// <summary>
    /// Mail into my own mailbox, the game's way: an item (its data as sent) with any gold on the same letter, or gold
    /// alone, or (a player's letter of words alone) a letter with nothing attached.
    /// </summary>
    private static void Deliver(AuctionMarket m, int item, int count, long gold, string subject, byte[] data)
    {
        if (item > 0 && count > 0)
        {
            int dataId = -1;
            if (data != null && data.Length > 0) { dataId = RPGBuilderUtilities.HandleNewItemDATA(item, CharacterEntries.ItemEntryState.InWorld); ItemData.Read(data, dataId); }
            m.DeliverItem(-1, item, dataId, count, subject);
            if (gold > 0)
            {
                AuctionDelivery last = null;
                foreach (var d in m.State.Deliveries) if (d != null && d.Owner == -1 && d.ItemID == item && d.Subject == subject && (last == null || d.ID > last.ID)) last = d;
                if (last != null) { last.Gold += gold; try { m.Revision = m.Revision + 1; } catch { } }
                else m.DeliverGold(-1, gold, subject);
            }
            return;
        }
        if (gold > 0) { m.DeliverGold(-1, gold, subject); return; }
        long id = 1;
        foreach (var d in m.State.Deliveries) if (d != null && d.ID >= id) id = d.ID + 1;
        m.State.Deliveries.Add(new AuctionDelivery { ID = id, Owner = -1, ItemID = -1, ItemDataID = -1, Subject = subject, SentAt = m.Clock });
        try { m.Revision = m.Revision + 1; } catch { }
    }

    /// <summary>A letter's text in one field of a line (it may hold line breaks and tabs).</summary>
    private static string PackText(string t) => "b64:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(t ?? ""));
    private static string UnpackText(string f) => f.StartsWith("b64:") ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(f.Substring(4))) : f;

    private static void SendMail(byte to, string id, int itemId, int count, long gold, string subject, byte[] data)
    {
        W.Reset(); W.U8(Kind); W.U8(OpMail); W.U8(to); W.Str(id); W.I32(itemId); W.I32(count); W.I64(gold); W.Str(subject ?? ""); W.Blob(data ?? Array.Empty<byte>());
        Send();
    }

    /// <summary>Host: kept mail to its players who are in the world now (until they acknowledge it).</summary>
    private static void DeliverWaiting()
    {
        try
        {
            if (!File.Exists(PendingFile)) return;
            var s = CoopManager.Session;
            foreach (var l in File.ReadAllLines(PendingFile))
            {
                var f = l.Split('	');
                if (f.Length < 7) continue;
                byte to = 255;
                foreach (var p in s.Players.Values) if (p.Key == f[1] && p.Id != s.LocalId && !string.IsNullOrEmpty(p.Scene)) to = p.Id;
                if (to == 255 || (SentAt.TryGetValue(f[0], out var at) && Time.unscaledTime - at < 10f)) continue;
                SentAt[f[0]] = Time.unscaledTime;
                SendMail(to, f[0], int.Parse(f[2]), int.Parse(f[3]), long.Parse(f[4]), UnpackText(f[5]), f[6].Length > 0 ? Convert.FromBase64String(f[6]) : null);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Auction mail: " + e.Message); }
    }

    /// <summary>Host: that mail is in the player's mailbox: no longer kept.</summary>
    private static void MailTaken(string id)
    {
        try
        {
            if (!File.Exists(PendingFile)) return;
            var keep = new List<string>();
            foreach (var l in File.ReadAllLines(PendingFile)) if (!l.StartsWith(id + "	")) keep.Add(l);
            if (keep.Count == 0) File.Delete(PendingFile); else File.WriteAllLines(PendingFile, keep);
            SentAt.Remove(id);
        }
        catch (Exception e) { Plugin.L.LogWarning("Auction mail: " + e.Message); }
    }

    private static void RemoveData(int dataId)
    {
        try
        {
            var data = GameInfo.Character;
            var entries = data.ItemEntries;
            for (int i = entries.Count - 1; i >= 0; i--)
                if (entries[i] != null && entries[i].id == dataId)
                {
                    int rdm = entries[i].rdmItemID;
                    entries.RemoveAt(i);
                    if (rdm >= 0) for (int k = data.RandomizedItems.Count - 1; k >= 0; k--) if (data.RandomizedItems[k]?.id == rdm) { data.RandomizedItems.RemoveAt(k); break; }
                    return;
                }
        }
        catch { }
    }

    // ================= client =================

    private static Il2CppSystem.Collections.Generic.List<AuctionListing> _own; // this character's listings, resting
    private static Il2CppSystem.Collections.Generic.List<AuctionListing> _mirror;
    private static readonly Dictionary<int, string> Names = new();
    private static double _hostClock; private static float _hostClockAt = -1f;
    private static int _building = -1; private static int _chunksLeft;
    private static readonly List<AuctionListing> Building = new();
    private static int _myOwner = int.MinValue;

    private static bool Mirrored => _mirror != null && IsClient;

    private static double HostClock => _hostClockAt < 0 ? 0 : _hostClock + (Time.unscaledTime - _hostClockAt);

    private static float _worldSince = -1f;

    /// <summary>
    /// My mailbox can take mail: in the world for a moment, its market bound to this character's data (just after
    /// entering, the market may still hold the data it had before the character loaded, and what lands there is lost).
    /// </summary>
    private static bool MailReady(AuctionMarket m)
    {
        if (m == null || !GameInfo.InWorld || _worldSince < 0f || Time.unscaledTime - _worldSince < 3f) return false;
        var data = GameInfo.Character;
        return data != null && m.Data != null && m.Data.Pointer == data.Pointer;
    }

    private static void ClientUpdate()
    {
        if (!GameInfo.InWorld) _worldSince = -1f; else if (_worldSince < 0f) _worldSince = Time.unscaledTime;
        bool browsing = false;
        try { browsing = AuctionHousePanel.Instance != null && AuctionHousePanel.Instance.IsOpen(); } catch { }
        if (browsing != _browsing)
        {
            _browsing = browsing; // the host keeps my view fresh while I look
            W.Reset(); W.U8(Kind); W.U8(OpBrowsing); W.Bool(browsing);
            Send();
        }
        var m = Market;
        if (m == null || _mirror == null) return;
        if (m.State.Listings != null && m.State.Listings.Pointer != _mirror.Pointer && !_saving) { _own ??= m.State.Listings; m.State.Listings = _mirror; } // re-bound (a new market): the mirror again
    }

    private static bool _browsing;

    private static void OnListings(ref NetReader r)
    {
        int snap = r.I32(); int chunk = r.U16(), chunks = r.U16(); double clock = r.F64();
        if (chunk == 0)
        {
            _building = snap; _chunksLeft = chunks; Building.Clear(); Names.Clear(); _myOwner = int.MinValue;
            int n = r.U8();
            var s = CoopManager.Session;
            for (int i = 0; i < n; i++) { int id = r.I32(); string name = r.Str(); byte player = r.U8(); Names[id] = name; if (player == s.LocalId) _myOwner = id; }
        }
        if (snap != _building) return;
        int count = r.U8();
        for (int i = 0; i < count; i++)
        {
            var l = new AuctionListing
            {
                ID = r.I64(), Seller = r.I32(), ItemID = r.I32(), ItemDataID = -1, Quantity = r.I32(), UnitPrice = r.I32(),
                StartingBid = r.I32(), Bidder = r.I32(), CurrentBid = r.I32(), Deposit = r.I32(), Expires = r.F64(),
            };
            if (l.Seller == _myOwner) l.Seller = -1; // mine: the game's "me"
            if (l.Bidder == _myOwner) l.Bidder = -1;
            Building.Add(l);
        }
        if (--_chunksLeft > 0) return;
        _hostClock = clock; _hostClockAt = Time.unscaledTime;
        Apply();
    }

    private static void Apply()
    {
        var m = Market;
        if (m == null) return;
        var list = new Il2CppSystem.Collections.Generic.List<AuctionListing>();
        foreach (var l in Building) list.Add(l);
        if (_mirror == null) _own = m.State.Listings;
        _mirror = list;
        m.State.Listings = _mirror;
        m.Clock = HostClock;
        try { m.Revision = m.Revision + 1; } catch { }
    }

    /// <summary>The session is over: this character's own listings are back.</summary>
    public static void Reset()
    {
        var m = Market;
        if (m != null && _own != null) try { m.State.Listings = _own; m.Revision = m.Revision + 1; } catch { }
        _own = null; _mirror = null; _hostClockAt = -1f; Names.Clear(); Owners.Clear(); _ownersFor = null; _sentRevision = int.MinValue; _browsing = false; Browsing.Clear();
    }

    private static bool _saving;

    private static readonly HashSet<string> Received = new(); // mail ids already in the mailbox (a repeat is only acknowledged)
    private static string _receivedFor;
    private static string ReceivedFile => Path.Combine(BepInEx.Paths.ConfigPath, $"AfallonCoop.auction-received.{Safe(HostChar)}.txt");

    private static void LoadReceived()
    {
        if (_receivedFor == HostChar) return;
        _receivedFor = HostChar; Received.Clear();
        try { if (File.Exists(ReceivedFile)) foreach (var l in File.ReadAllLines(ReceivedFile)) if (l.Length > 0) Received.Add(l); } catch { }
    }

    private static void OnMail(ref NetReader r)
    {
        byte to = r.U8(); string id = r.Str();
        int item = r.I32(), count = r.I32(); long gold = r.I64(); string subject = r.Str(); var data = r.Blob();
        Plugin.L.LogInfo($"Auction mail in: to {to} id {id} item {item} x{count} gold {gold} data {data.Length}");
        var m = Market;
        if (to != CoopManager.Session.LocalId || !MailReady(m)) return; // not ready: it comes again
        LoadReceived();
        if (!Received.Contains(id))
        {
            Deliver(m, item, count, gold, subject, data); // throws: not acknowledged, the host sends it again
            Received.Add(id);
            try { File.AppendAllLines(ReceivedFile, new[] { id }); } catch { }
            AfallonCoop.Plugin.Mail.Arrived(subject);
            Plugin.L.LogInfo($"Auction: mail '{subject}' (item #{item} x{count}, gold {gold})");
        }
        W.Reset(); W.U8(Kind); W.U8(OpMailAck); W.Str(id);
        Send();
    }

    // ---- client actions: taken here, carried out by the host ----

    private static bool ClientPost(AuctionMarket m, AuctionBagItem source, int count, int unitPrice, int startingBid, int minutes, ref string error)
    {
        if (!m.Tradable(source.ItemID, source.DataID, out var reason)) { error = reason; return false; }
        var items = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetItems();
        if (!items.ContainsKey(source.ItemID)) { error = "Unknown item."; return false; }
        int deposit = m.DepositFor(items[source.ItemID], count, minutes);
        if (m.Balance(-1) < deposit) { error = "Not enough gold for the deposit."; return false; }
        byte[] data = source.DataID >= 0 ? ItemData.Write(source.DataID) : null;
        if (!m.RemoveFromBag(-1, source, count)) { error = "The item is no longer in your bags."; return false; }
        m.ChangeGold(-1, -deposit);
        W.Reset(); W.U8(Kind); W.U8(OpPost); W.I32(source.ItemID); W.I32(count); W.I32(unitPrice); W.I32(startingBid); W.I32(minutes); W.I32(deposit); W.Blob(data ?? Array.Empty<byte>());
        Send();
        return true;
    }

    private static bool ClientBuy(AuctionMarket m, AuctionQuote quote, ref string error)
    {
        long ownBids = 0;
        foreach (var line in quote.Lines)
            foreach (var l in m.State.Listings) if (l.ID == line.ListingID && l.Bidder == -1) ownBids += l.CurrentBid;
        long charge = Math.Max(0, quote.Total - ownBids);
        if (m.Balance(-1) < charge) { error = "Not enough gold."; return false; }
        m.ChangeGold(-1, -charge);
        W.Reset(); W.U8(Kind); W.U8(OpBuy); W.I64(charge); W.U8((byte)Math.Min(quote.Lines.Count, 255));
        foreach (var line in quote.Lines) { W.I64(line.ListingID); W.I32(line.Quantity); W.I32(line.UnitPrice); }
        Send();
        return true;
    }

    private static bool ClientBid(AuctionMarket m, long id, int amount, ref string error)
    {
        AuctionListing l = null;
        foreach (var x in m.State.Listings) if (x.ID == id) { l = x; break; }
        if (l == null) { error = "This auction is gone."; return false; }
        if (amount < m.MinimumBid(l) || amount >= l.UnitPrice) { error = "Bid out of range."; return false; }
        long pay = amount - (l.Bidder == -1 ? l.CurrentBid : 0);
        if (m.Balance(-1) < pay) { error = "Not enough gold."; return false; }
        m.ChangeGold(-1, -pay);
        W.Reset(); W.U8(Kind); W.U8(OpBid); W.I64(id); W.I32(amount); W.I64(pay);
        Send();
        return true;
    }

    private static bool ClientCancel(long id)
    {
        W.Reset(); W.U8(Kind); W.U8(OpCancel); W.I64(id);
        Send();
        return true;
    }

    // ================= messages =================

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8();
        var s = CoopManager.Session;
        if (s == null) return;
        switch (op)
        {
            case OpListings: if (!s.IsHost && from == ProtocolInfo.HostId) OnListings(ref r); break;
            case OpMail: if (!s.IsHost && from == ProtocolInfo.HostId) OnMail(ref r); break;
            case OpMailAck: if (s.IsHost) MailTaken(r.Str()); break;
            case OpBrowsing:
                if (!s.IsHost) break;
                if (r.Bool()) { Browsing.Add(from); Fresh.Add(from); _nextListings = 0f; } // the current list at once
                else Browsing.Remove(from);
                break;
            default: if (s.IsHost) HostRequest(from, op, ref r); break;
        }
    }

    public static void OnPlayerJoined(byte id) { if (CoopManager.Session?.IsHost == true) { Fresh.Add(id); _nextListings = 0f; } }

    public static string Describe()
    {
        var m = Market;
        if (m == null) return "no market";
        int active = 0; foreach (var l in m.State.Listings) if (m.IsActive(l)) active++;
        return $"listings {m.State.Listings.Count} (active {active}) clock {m.Clock:0} mirrored {Mirrored} me {_myOwner} mail {m.State.Deliveries.Count}";
    }

    // ---- dev bridge: the market's own calls, as the window makes them ----

    public static string Dev(string cmd, string arg)
    {
        var m = Market;
        if (m == null) return "no market";
        var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var items = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetItems();
        string N(int id) => items.ContainsKey(id) ? items[id].entryDisplayName : "#" + id;
        switch (cmd)
        {
            case "ahpost": // ahpost <bag slot> <unit price>
            {
                int slot = int.Parse(a[0]), price = int.Parse(a[1]);
                AuctionBagItem? src = null;
                foreach (var b in m.Bag(-1)) if (b.Slot == slot) src = b;
                if (src == null) return "nothing in that slot";
                int minutes = m.Settings.DurationMinutes[0];
                bool ok = m.Post(-1, src.Value, src.Value.Count, price, 0, minutes, out var err);
                return ok ? $"posted {N(src.Value.ItemID)} x{src.Value.Count} at {price} for {minutes} min" : "failed: " + err;
            }
            case "ahbuy": // ahbuy <listing id>
            {
                long id = long.Parse(a[0]);
                AuctionListing l = null; foreach (var x in m.State.Listings) if (x.ID == id) l = x;
                if (l == null) return "no such listing";
                var q = m.Quote(-1, l.ItemID, l.Quantity, id);
                bool ok = m.Buy(q, out var err);
                return ok ? $"bought {N(l.ItemID)} x{l.Quantity} for {q.Total}" : "failed: " + err;
            }
            case "ahlist": // ahlist [name part]: active listings
            {
                var rows = new List<string>();
                foreach (var l in m.State.Listings)
                    if (m.IsActive(l) && (a.Length == 0 || N(l.ItemID).IndexOf(a[0], StringComparison.OrdinalIgnoreCase) >= 0))
                        rows.Add($"{l.ID}:{N(l.ItemID)} x{l.Quantity} @{l.UnitPrice} by {m.OwnerName(l.Seller)}({l.Seller})");
                return rows.Count + ": " + string.Join(" | ", rows.GetRange(0, Math.Min(rows.Count, 12)));
            }
            case "ahmail":
            {
                var rows = new List<string>();
                foreach (var d in m.State.Deliveries) if (d.Owner == -1) rows.Add($"{d.ID}:'{d.Subject}' {N(d.ItemID)} x{d.Quantity} gold {d.Gold}");
                return $"gold {m.Balance(-1)} | " + string.Join(" | ", rows);
            }
            case "ahclaim":
            {
                int n = 0;
                var ids = new List<long>(); foreach (var d in m.State.Deliveries) if (d.Owner == -1) ids.Add(d.ID);
                foreach (var id in ids) if (m.Claim(-1, id, out _)) n++;
                return $"claimed {n}, gold {m.Balance(-1)}";
            }
        }
        return Describe();
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        // ---- client: its own market rests ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionHouseService), nameof(AuctionHouseService.Step))]
        private static bool NoTradersHere() => !IsClient; // the host's traders are the market's

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.ExpireStep))]
        private static bool NoExpiryHere() => !Mirrored;

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionHouseService), nameof(AuctionHouseService.SyncClock))]
        private static void HostTime(ref double __0) { if (Mirrored) __0 = HostClock; }

        /// <summary>Saving: this character's own listings, not the host's.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RPGBuilderJsonSaver), nameof(RPGBuilderJsonSaver.SaveCharacterData))]
        private static void SaveOwn()
        {
            var m = Market;
            if (m == null || _mirror == null || _own == null) return;
            _saving = true;
            try { m.State.Listings = _own; } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(RPGBuilderJsonSaver), nameof(RPGBuilderJsonSaver.SaveCharacterData))]
        private static void SaveOwnDone()
        {
            var m = Market;
            if (m != null && _mirror != null && _saving) try { m.State.Listings = _mirror; } catch { }
            _saving = false;
        }

        // ---- client: actions to the host ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.Post))]
        private static bool Post(AuctionMarket __instance, int __0, AuctionBagItem __1, int __2, int __3, int __4, int __5, ref string __6, ref bool __result)
        {
            if (!IsClient && __0 == -1) _urgent = true; // the host's own: to the others at once
            if (!Mirrored || __0 != -1) return true;
            string err = "";
            try { __result = ClientPost(__instance, __1, __2, __3, __4, __5, ref err); } catch (Exception e) { err = e.Message; __result = false; }
            __6 = err;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.Buy))]
        private static bool Buy(AuctionMarket __instance, AuctionQuote __0, ref string __1, ref bool __result)
        {
            if (!IsClient && __0?.Buyer == -1) _urgent = true;
            if (!Mirrored || __0 == null || __0.Buyer != -1) return true;
            string err = "";
            try { __result = ClientBuy(__instance, __0, ref err); } catch (Exception e) { err = e.Message; __result = false; }
            __1 = err;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.Bid))]
        private static bool Bid(AuctionMarket __instance, int __0, long __1, int __2, ref string __3, ref bool __result)
        {
            if (!IsClient && __0 == -1) _urgent = true;
            if (!Mirrored || __0 != -1) return true;
            string err = "";
            try { __result = ClientBid(__instance, __1, __2, ref err); } catch (Exception e) { err = e.Message; __result = false; }
            __3 = err;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.Cancel))]
        private static bool Cancel(AuctionMarket __instance, int __0, long __1, ref string __2, ref bool __result)
        {
            if (!IsClient && __0 == -1) _urgent = true;
            if (!Mirrored || __0 != -1) return true;
            __result = ClientCancel(__1); __2 = "";
            return false;
        }

        // ---- both: remote players as market owners ----

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.OwnerName))]
        private static bool Name(int __0, ref string __result)
        {
            if (__0 > HostOwner) return true;
            __result = IsClient ? (Names.TryGetValue(__0, out var n) ? n : "Player") : NameOf(__0);
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), "AvailableOwner")]
        private static bool Available(int __0, ref bool __result)
        {
            if (__0 > HostOwner) return true;
            __result = true; // a player: they paid on their machine
            return false;
        }

        /// <summary>Host: a remote player's gold is on their machine (taken there before the request).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.Balance))]
        private static bool Balance(int __0, ref int __result)
        {
            if (!IsRemote(__0) || IsClient) return true;
            __result = 1000000000;
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.ChangeGold))]
        private static bool Gold(int __0, ref bool __result)
        {
            if (!IsRemote(__0) || IsClient) return true;
            __result = true;
            return false;
        }

        /// <summary>Host: the market's mail to a remote player goes to them.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.DeliverItem))]
        private static bool Item(int __0, int __1, int __2, int __3, string __4)
        {
            if (!IsRemote(__0) || IsClient) return true;
            try { RouteMail(__0, __1, __2, __3, 0, __4); } catch (Exception e) { Plugin.L.LogWarning("Auction mail: " + e.Message); }
            return false;
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AuctionMarket), nameof(AuctionMarket.DeliverGold))]
        private static bool GoldMail(int __0, long __1, string __2)
        {
            if (!IsRemote(__0) || IsClient) return true;
            try { RouteMail(__0, 0, -1, 0, __1, __2); } catch (Exception e) { Plugin.L.LogWarning("Auction mail: " + e.Message); }
            return false;
        }
    }
}
