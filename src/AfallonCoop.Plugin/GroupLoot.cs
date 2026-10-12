using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.UIElements;
using BLINK.RPGBuilder.World;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Group loot in a group's dungeon, WoW-like. Everyone keeps their own loot (each machine's kill drops its own bag),
/// except valuable items: armor and weapons (the game's rollable types) of Rare rarity or better drop once for the
/// group and are rolled for, with the game's own roll window and rules.
///  1. Whoever gets a bag with valuable items takes them out of it and tells the host what dropped from that mob; the
///     host keeps the first such report per mob (each machine rolls its own loot tables: one drop counts).
///  2. The host starts a roll for each item with every group member in that dungeon: each gets the game's roll slot
///     (Need / Greed / Pass, its timer; timing out passes) and rolls there, their bots too (the game's own rule for a
///     companion's need), and sends the host the result.
///  3. The host resolves as the game does: a need beats a greed, the highest roll wins, nobody: the item is gone. Every
///     roll and the winner go to everyone's chat; the winner gets the item with its data, or their bot equips it
///     (a bot winning on greed destroys it, as in the game).
/// Elsewhere (open world, alone in a dungeon) the game's own rolls between a player and their bots are untouched.
/// </summary>
internal static class GroupLoot
{
    public const byte Kind = 20;
    private const byte OpFound = 1, OpStart = 2, OpChoice = 3, OpResult = 4, OpSettings = 5;
    private const byte Pass = 0, Need = 1, Greed = 2;
    private const string MinRarity = "Rare"; // and the ones after it in the game's rarity order
    private const float HostGrace = 8f;       // after the roll timer, missing answers count as passes

    private static readonly NetWriter W = new();
    private static readonly System.Random Rng = new();

    private static CompanionLootManager Loot { get { try { return CompanionLootManager.Instance; } catch { return null; } } }

    // ---------------- which items, where ----------------

    private static int RarityRank(RPGItem item)
    {
        var name = item?.ItemRarity != null ? item.ItemRarity.name : item?.rarity ?? "";
        var order = GameDatabase.Instance.GetEconomySettings().itemRarityList;
        if (order != null)
            for (int i = 0; i < order.Count; i++)
                if (name.StartsWith(order[i].ToString(), StringComparison.OrdinalIgnoreCase)) return i;
        return name.Length > 0 && order != null ? order.Count : -1; // a rarity not in the list (gold): above all
    }

    // ---------------- which rarities the group rolls for: the leader's choice ----------------

    /// <summary>The game's rarities in order (Common ... Legendary).</summary>
    public static List<string> Rarities()
    {
        var list = new List<string>();
        try { var order = GameDatabase.Instance.GetEconomySettings().itemRarityList; if (order != null) foreach (var r in order) list.Add(r.ToString()); } catch { }
        return list;
    }

    /// <summary>Rarity colours as the game shows them, by the same order.</summary>
    public static Color RarityColor(int index)
    {
        try { var c = GameDatabase.Instance.GetEconomySettings().itemRarityColorsList; if (c != null && index < c.Count) return c[index]; } catch { }
        return Color.white;
    }

    private static readonly Dictionary<byte, int> Masks = new(); // group id -> rarities rolled for (bit = index in Rarities)

    private static int DefaultMask()
    {
        int mask = 0, min = int.MaxValue;
        var r = Rarities();
        for (int i = 0; i < r.Count; i++) if (r[i] == MinRarity) min = i;
        for (int i = 0; i < Math.Max(r.Count, 1) && i < 31; i++) if (i >= min) mask |= 1 << i;
        return mask;
    }

    /// <summary>The rarities my group rolls for.</summary>
    public static int MyMask()
    {
        var g = CoopManager.Session?.MyGroup;
        return g != null && Masks.TryGetValue(g.Id, out var m) ? m : DefaultMask();
    }

    /// <summary>Leader: roll for this rarity or not (the host checks it is the leader and tells everyone).</summary>
    public static void Toggle(int rarity)
    {
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        if (g == null || g.Leader != s.LocalId) return;
        int mask = MyMask() ^ (1 << rarity);
        W.Reset(); W.U8(Kind); W.U8(OpSettings); W.U8(g.Id); W.I32(mask);
        Send(ProtocolInfo.HostId);
    }

    private static void OnSettings(byte from, ref NetReader r)
    {
        byte group = r.U8(); int mask = r.I32();
        var s = CoopManager.Session;
        if (s.IsHost)
        {
            if (from != ProtocolInfo.HostId || !_relaying)
            {
                // a leader's choice: kept and told to everyone
                var g = s.GroupOf(from);
                if (g == null || g.Id != group || g.Leader != from) return;
                Masks[group] = mask;
                W.Reset(); W.U8(Kind); W.U8(OpSettings); W.U8(group); W.I32(mask);
                _relaying = true;
                try { s.SendWorld(W.Span); } finally { _relaying = false; }
                GroupPanel.Refresh();
            }
            return;
        }
        if (from != ProtocolInfo.HostId) return; // only the host's word
        Masks[group] = mask;
        GroupPanel.Refresh();
    }

    private static bool _relaying;
    private static float _nextSettings;

    private static bool Valuable(RPGItem item)
    {
        if (item == null || Loot == null || !Loot.IsRollableItem(item)) return false;
        int rank = RarityRank(item);
        int last = Math.Max(Rarities().Count - 1, 0);
        if (rank < 0) return false;
        return (MyMask() & (1 << Math.Min(rank, last))) != 0;
    }

    /// <summary>In the group's dungeon with another member of the group there: group loot.</summary>
    private static bool GroupDungeon()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected || !Dungeons.InDungeon) return false;
        var g = s.MyGroup;
        if (g == null || !Dungeons.Place.EndsWith("#g" + g.Id)) return false;
        foreach (var id in g.Players) if (id != s.LocalId && s.Players.TryGetValue(id, out var p) && p.Scene == Dungeons.Place) return true;
        return false;
    }

    // ---------------- 1. a kill here: valuable items out of the bag, reported ----------------

    private static void AfterLootGenerated(CombatEntity mob)
    {
        if (!GroupDungeon() || LootBag.AllActiveBags == null || LootBag.AllActiveBags.Count == 0) return;
        var bag = LootBag.AllActiveBags[LootBag.AllActiveBags.Count - 1]; // the one just made for this kill
        if (bag == null || bag.lootData == null) return;
        // A mob only this machine has (not synced): its kill is this player's own, and so is its loot. Rolled for, its
        // key here (an instance id) would differ from another machine's kill of its own such mob: a roll per machine.
        uint mobKey = MobSync.SyncedId(mob);
        if (mobKey == 0) return;
        var found = new List<(int id, int count, byte[] data)>();
        foreach (var l in bag.lootData)
        {
            if (l == null || l.looted || l.isBeingRolledOn || !Valuable(l.item)) continue;
            found.Add((l.item.ID, l.count, l.itemDataID >= 0 ? ItemData.Write(l.itemDataID) : null));
            l.looted = true; // the group rolls for it
        }
        if (found.Count == 0) return;
        bag.CheckLootState();
        W.Reset(); W.U8(Kind); W.U8(OpFound); W.U32(mobKey); W.U8((byte)found.Count);
        foreach (var f in found) { W.I32(f.id); W.I32(f.count); W.Blob(f.data ?? Array.Empty<byte>()); }
        Send(ProtocolInfo.HostId);
        Plugin.L.LogInfo($"Group loot: {found.Count} valuable item(s) from mob {mobKey:x8} for the group");
    }

    private static void Send(byte toHost)
    {
        var s = CoopManager.Session;
        if (s == null) return;
        if (s.IsHost && toHost == s.LocalId) { var r = new NetReader(W.Span); r.U8(); Receive(s.LocalId, ref r); }
        else s.SendWorld(W.Span);
    }

    private static void Broadcast()
    {
        var s = CoopManager.Session;
        if (s == null) return;
        s.SendWorld(W.Span); // the others
        var r = new NetReader(W.Span); r.U8(); Receive(s.LocalId, ref r); // and the host itself
    }

    // ---------------- 2. host: rolls ----------------

    private sealed class Roll
    {
        public uint Id; public int Item, Count; public byte[] Data; public string Place;
        public HashSet<byte> Waiting = new();
        public Dictionary<byte, string> Keys = new(); // lasting keys of those rolling: a winner who left gets it by mail
        public List<(byte player, int bot, string name, byte choice, int value)> Entries = new();
        public float Deadline;
    }

    private static readonly Dictionary<uint, Roll> Rolls = new();
    private static readonly Dictionary<uint, float> SeenMobs = new();
    private static uint _nextRoll = 1;

    private static void HostFound(byte from, ref NetReader r)
    {
        uint mob = r.U32();
        int n = r.U8();
        var items = new List<(int id, int count, byte[] data)>();
        for (int i = 0; i < n; i++) items.Add((r.I32(), r.I32(), r.Blob()));
        if (SeenMobs.ContainsKey(mob)) return; // someone's report already counts for this kill
        SeenMobs[mob] = Time.unscaledTime;
        var s = CoopManager.Session;
        var g = s.GroupOf(from);
        if (g == null || !s.Players.TryGetValue(from, out var finder)) return;
        float timer = 60f;
        try { timer = GameDatabase.Instance.GetEconomySettings().RollTimerDuration; } catch { }
        foreach (var (id, count, data) in items)
        {
            var roll = new Roll { Id = _nextRoll++, Item = id, Count = count, Data = data, Place = finder.Scene, Deadline = Time.unscaledTime + timer + HostGrace };
            foreach (var p in g.Players) if (s.Players.TryGetValue(p, out var info) && info.Scene == finder.Scene) { roll.Waiting.Add(p); roll.Keys[p] = info.Key; }
            Rolls[roll.Id] = roll;
            W.Reset(); W.U8(Kind); W.U8(OpStart); W.U32(roll.Id); W.I32(id); W.I32(count); W.Blob(data ?? Array.Empty<byte>()); W.F32(timer);
            W.U8((byte)roll.Waiting.Count); foreach (var p in roll.Waiting) W.U8(p);
            Broadcast();
        }
    }

    private static void HostChoice(byte from, ref NetReader r)
    {
        uint id = r.U32();
        byte choice = r.U8(); int value = r.U16();
        int bots = r.U8();
        var botRolls = new List<(string name, bool need, int value)>();
        for (int i = 0; i < bots; i++) botRolls.Add((r.Str(), r.U8() == 1, r.U8()));
        if (!Rolls.TryGetValue(id, out var roll) || !roll.Waiting.Remove(from)) return;
        roll.Entries.Add((from, -1, Groups.Name(from), choice, value));
        for (int i = 0; i < botRolls.Count; i++) roll.Entries.Add((from, i, botRolls[i].name, botRolls[i].need ? Need : Greed, botRolls[i].value));
        if (roll.Waiting.Count == 0) Resolve(roll);
    }

    public static void Update()
    {
        var s = CoopManager.Session;
        if (s == null || !s.IsHost) return;
        if (Time.unscaledTime > _nextSettings && Masks.Count > 0)
        {
            // now and then: players who joined since know their group's choice too
            _nextSettings = Time.unscaledTime + 10f;
            foreach (var kv in Masks) { W.Reset(); W.U8(Kind); W.U8(OpSettings); W.U8(kv.Key); W.I32(kv.Value); _relaying = true; try { s.SendWorld(W.Span); } finally { _relaying = false; } }
        }
        if (Rolls.Count == 0) return;
        List<Roll> due = null;
        foreach (var r in Rolls.Values) if (Time.unscaledTime > r.Deadline) (due ??= new()).Add(r);
        if (due != null) foreach (var r in due) { foreach (var p in r.Waiting) r.Entries.Add((p, -1, Groups.Name(p), Pass, 0)); r.Waiting.Clear(); Resolve(r); }
        if (SeenMobs.Count > 200) // only old kills go: a late report of a recent one must still find it
            foreach (var k in new List<uint>(SeenMobs.Keys)) if (Time.unscaledTime - SeenMobs[k] > 120f) SeenMobs.Remove(k);
    }

    /// <summary>The game's rule: a need beats a greed, then the highest roll (the first of equals); no one: gone.</summary>
    private static void Resolve(Roll roll)
    {
        Rolls.Remove(roll.Id);
        int best = -1;
        foreach (byte kind in new[] { Need, Greed })
        {
            for (int i = 0; i < roll.Entries.Count; i++)
                if (roll.Entries[i].choice == kind && (best < 0 || roll.Entries[i].value > roll.Entries[best].value)) best = i;
            if (best >= 0) break;
        }
        var items = GameDatabase.Instance.GetItems();
        string itemName = items.ContainsKey(roll.Item) ? items[roll.Item].entryDisplayName : "#" + roll.Item;
        var lines = new List<string>();
        foreach (var e in roll.Entries)
            lines.Add(e.choice == Pass ? $"{e.name} passed on {itemName}" : $"{e.name} rolled {(e.choice == Need ? "Need" : "Greed")} {e.value} for {itemName}");
        bool mailed = false;
        if (best >= 0)
        {
            // the winner (or their bot's player) left meanwhile: kept on the host and mailed when they are back, as in WoW
            byte w = roll.Entries[best].player;
            var s = CoopManager.Session;
            if (roll.Keys.TryGetValue(w, out var key) && !string.IsNullOrEmpty(key) && (!s.Players.TryGetValue(w, out var p) || p.Key != key))
            {
                AuctionSync.HostMail(key, new() { (roll.Item, roll.Count, roll.Data) }, 0, "Loot: " + itemName);
                mailed = true;
            }
        }
        lines.Add(best < 0 ? $"Everyone passed on {itemName}: it is gone" : $"{roll.Entries[best].name} won {itemName}{(mailed ? " (sent by mail)" : "")}");
        W.Reset(); W.U8(Kind); W.U8(OpResult); W.U32(roll.Id);
        if (best < 0) { W.U8(255); W.I32(-1); W.U8(Pass); }
        else { W.U8(roll.Entries[best].player); W.I32(roll.Entries[best].bot); W.U8(roll.Entries[best].choice); }
        W.U8((byte)Math.Min(lines.Count, 255)); foreach (var l in lines) W.Str(l);
        Broadcast();
        Plugin.L.LogInfo("Group loot: " + lines[lines.Count - 1]);
    }

    // ---------------- 2. everyone in the roll: the game's roll slot ----------------

    private sealed class Mine
    {
        public uint Id; public int Item, Count; public byte[] Data; public int TempData = -1;
        public CompanionLootManager.RollEntry Entry;
        public List<CompanionLootManager.CompanionRoll> Bots = new();
        public bool Sent, Needed;
    }

    private static readonly Dictionary<uint, Mine> Open = new();

    private static void OnStart(ref NetReader r)
    {
        uint id = r.U32(); int item = r.I32(), count = r.I32(); var data = r.Blob(); float timer = r.F32();
        int n = r.U8(); bool me = false;
        var s = CoopManager.Session;
        for (int i = 0; i < n; i++) if (r.U8() == s.LocalId) me = true;
        var items = GameDatabase.Instance.GetItems();
        var lm = Loot;
        if (!me || lm == null || !items.ContainsKey(item)) return;
        var m = new Mine { Id = id, Item = item, Count = count, Data = data.Length > 0 ? data : null };
        m.TempData = ItemData.CreateTemp("roll" + id, item, m.Data); // its tooltip shows it as it is
        var rpgItem = items[item];
        // my bots roll as the game has them roll with me
        var pets = GameInfo.Player?.CurrentPets;
        if (pets != null)
            foreach (var pet in pets)
            {
                if (pet == null || pet.Dead) continue;
                int type = -1; try { type = (int)pet.GetNPCData().npcType; } catch { }
                if (type != 8 && type != 9) continue;
                bool need = false; try { need = CompanionLootManager.ShouldCompanionNeed(pet, rpgItem, m.TempData); } catch { }
                m.Bots.Add(new CompanionLootManager.CompanionRoll { companionName = pet.GetNPCData().entryDisplayName, rollValue = Rng.Next(0, 101), isNeedRoll = need, companionEntity = pet });
            }
        var rolls = new Il2CppSystem.Collections.Generic.List<CompanionLootManager.CompanionRoll>();
        foreach (var b in m.Bots) rolls.Add(b);
        m.Entry = new CompanionLootManager.RollEntry { item = rpgItem, itemDataID = m.TempData, count = count, timeRemaining = timer, maxTime = timer, companionRolls = rolls };
        Open[id] = m;
        try
        {
            lm.activeRolls.Add(m.Entry);
            m.Entry.uiSlot = lm.rollPanel.AddRollSlot(m.Entry); // as the game keeps it: the slot goes when its roll is decided
            lm.rollPanel.Show();
        }
        catch (Exception e) { Plugin.L.LogWarning("Group loot window: " + e.Message); }
    }

    /// <summary>My click (or the timer's pass): rolled here, sent to the host; the slot goes.</summary>
    private static bool Choose(CompanionLootManager.RollEntry entry, byte choice)
    {
        Mine m = null;
        foreach (var x in Open.Values) if (x.Entry != null && x.Entry.Pointer == entry.Pointer) { m = x; break; }
        if (m == null) return false;
        if (m.Sent) return true;
        m.Sent = true;
        int value = 0;
        if (choice == Need)
        {
            int bonus = 0; try { bonus = NeedRollBadLuck.GetBonus(GameInfo.Character); } catch { }
            value = Rng.Next(0, 101) + bonus;
            m.Needed = true;
        }
        else if (choice == Greed) value = Rng.Next(0, 101);
        W.Reset(); W.U8(Kind); W.U8(OpChoice); W.U32(m.Id); W.U8(choice); W.U16((ushort)value);
        W.U8((byte)m.Bots.Count);
        foreach (var b in m.Bots) { W.Str(b.companionName ?? "?"); W.U8((byte)(b.isNeedRoll ? 1 : 0)); W.U8((byte)b.rollValue); }
        Send(ProtocolInfo.HostId);
        CloseSlot(m);
        return true;
    }

    private static void CloseSlot(Mine m)
    {
        var lm = Loot;
        try
        {
            m.Entry.resolved = true;
            if (m.Entry.uiSlot != null) UnityEngine.Object.Destroy(m.Entry.uiSlot);
            lm?.activeRolls?.Remove(m.Entry);
            if (lm != null && lm.activeRolls.Count == 0) lm.rollPanel?.Hide();
        }
        catch (Exception e) { Plugin.L.LogWarning("Group loot slot: " + e.Message); }
    }

    // ---------------- 3. the result ----------------

    private static void OnResult(ref NetReader r)
    {
        uint id = r.U32(); byte winner = r.U8(); int bot = r.I32(); byte how = r.U8();
        int n = r.U8();
        var lines = new List<string>();
        for (int i = 0; i < n; i++) lines.Add(r.Str());
        if (!Open.TryGetValue(id, out var m)) return; // not in this roll
        Open.Remove(id);
        if (!m.Sent) CloseSlot(m);
        foreach (var l in lines) Chat(l);
        var s = CoopManager.Session;
        bool mine = winner == s.LocalId;
        if (m.Needed) try { NeedRollBadLuck.RecordResult(GameInfo.Character, mine && bot < 0); } catch { }
        ItemData.DropTemps("roll" + id);
        if (!mine) return;
        try
        {
            int dataId = -1;
            if (m.Data != null) { dataId = RPGBuilderUtilities.HandleNewItemDATA(m.Item, CharacterEntries.ItemEntryState.InBag); ItemData.Read(m.Data, dataId); }
            if (bot < 0) RPGBuilderUtilities.HandleItemLooting(m.Item, dataId, m.Count, false, true);
            else if (bot < m.Bots.Count)
            {
                // the game's own: equipped if the bot needs it, else (a greed win) gone
                m.Entry.itemDataID = dataId;
                Loot.GiveItemToCompanion(m.Entry, m.Bots[bot]);
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Group loot give: " + e.Message); }
    }

    private static void Chat(string text)
    {
        try { UIEvents.Instance.OnShowChatTextMessage(text); } catch { }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8();
        var s = CoopManager.Session;
        if (s == null) return;
        switch (op)
        {
            case OpSettings: OnSettings(from, ref r); break;
            case OpFound: if (s.IsHost) HostFound(from, ref r); break;
            case OpChoice: if (s.IsHost) HostChoice(from, ref r); break;
            case OpStart: if (from == ProtocolInfo.HostId) OnStart(ref r); break;
            case OpResult: if (from == ProtocolInfo.HostId) OnResult(ref r); break;
        }
    }

    /// <summary>Dev: as if a valuable item (the first rollable Rare+ one, or this id) dropped here for the group.</summary>
    public static string DevFound(string arg)
    {
        var items = GameDatabase.Instance.GetItems();
        int id = -1;
        if (!int.TryParse(arg, out id)) foreach (var kv in items) if (Valuable(kv.Value)) { id = kv.Key; break; }
        if (id < 0 || !items.ContainsKey(id)) return "no valuable item";
        W.Reset(); W.U8(Kind); W.U8(OpFound); W.U32((uint)Rng.Next(1, int.MaxValue)); W.U8(1); W.I32(id); W.I32(1); W.Blob(Array.Empty<byte>());
        Send(ProtocolInfo.HostId);
        return $"dropped '{items[id].entryDisplayName}' ({items[id].ItemRarity?.name})";
    }

    /// <summary>Dev: the first open roll here, as its button would.</summary>
    public static string DevChoose(string arg)
    {
        foreach (var m in Open.Values)
            if (!m.Sent)
            {
                var lm = Loot;
                if (arg == "need") lm.PlayerNeed(m.Entry); else if (arg == "greed") lm.PlayerGreed(m.Entry); else lm.PlayerPass(m.Entry);
                return $"{arg} on #{m.Item} (bots: {string.Join(", ", m.Bots.ConvertAll(b => b.companionName + (b.isNeedRoll ? " need " : " greed ") + b.rollValue))})";
            }
        return "no open roll";
    }

    public static string Describe()
    {
        var r = Rarities(); int m = MyMask(); var on = new List<string>();
        for (int i = 0; i < r.Count; i++) if ((m & (1 << i)) != 0) on.Add(r[i]);
        return $"open rolls here {Open.Count}, host rolls {Rolls.Count}, in group dungeon {GroupDungeon()}, rolled for [{string.Join(", ", on)}]";
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(EconomyUtilities), nameof(EconomyUtilities.GenerateDroppedLoot))]
        private static void Generated(CombatEntity __1)
        {
            try { AfterLootGenerated(__1); } catch (Exception e) { Plugin.L.LogWarning("Group loot: " + e.Message); }
        }

        /// <summary>
        /// In a group the leader's rarities decide what is rolled for, the game's own rolls between a player and their
        /// bots too: an item of another rarity is not rolled, it stays in the bag for whoever loots it.
        /// </summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(CompanionLootManager), nameof(CompanionLootManager.CreateRollEntry))]
        private static bool OnlyChosenRarities(LootBag.Loot_Data __0, ref bool __result)
        {
            try
            {
                if (CoopManager.Session?.MyGroup == null || __0?.item == null || Valuable(__0.item)) return true;
                __result = false;
                return false;
            }
            catch { return true; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CompanionLootManager), nameof(CompanionLootManager.PlayerNeed))]
        private static bool OnNeed(CompanionLootManager.RollEntry __0) { try { return !Choose(__0, Need); } catch { return true; } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CompanionLootManager), nameof(CompanionLootManager.PlayerGreed))]
        private static bool OnGreed(CompanionLootManager.RollEntry __0) { try { return !Choose(__0, Greed); } catch { return true; } }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(CompanionLootManager), nameof(CompanionLootManager.PlayerPass))]
        private static bool OnPass(CompanionLootManager.RollEntry __0) { try { return !Choose(__0, Pass); } catch { return true; } }
    }
}
