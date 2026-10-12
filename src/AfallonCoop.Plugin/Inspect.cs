using System;
using System.Collections.Generic;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Combat;
using BLINK.RPGBuilder.Managers;
using HarmonyLib;

namespace AfallonCoop.Plugin;

/// <summary>
/// "Inspect" on another player opens the game's own inspect window (the one for adventurers) on their shell, showing
/// what the player really has: the shell is asked for nothing locally; the player is asked for their equipment (all of
/// it, a helmet they hide included) and stats, which are put on the shell (its equipment lists, its non-vital stats;
/// health and mana follow the player all the time anyway), and then the window opens. The model in it is the player's
/// puppet (PlayerShells redirects the portrait renderer).
/// A bot run by another machine is the same: its copy here wears the right items, but not their own data (random stats,
/// enchantments: too much to stream), so the window opens on its owner's answer, with the items as they are there.
/// </summary>
internal static class Inspect
{
    public const byte Kind = 18;
    private const byte OpAsk = 1, OpAnswer = 2, OpAskBot = 3, OpAnswerBot = 4;
    private static readonly NetWriter W = new();
    private static byte _asked = 255;
    private static uint _askedBot;

    /// <summary>Ask the machine that runs this bot (its copy is here) for what the inspect window shows.</summary>
    public static void AskBot(MobCombatEntity copy)
    {
        var s = CoopManager.Session;
        uint id = MobSync.NetIdOfCopy(copy);
        if (s == null || id == 0 || !MobSync.TryGetCopyOwner(copy, out var owner)) return;
        _askedBot = id;
        W.Reset(); W.U8(Kind); W.U8(OpAskBot); W.U8(owner); W.U32(id);
        s.SendWorld(W.Span);
    }

    /// <summary>Ask a player for what the inspect window shows.</summary>
    public static void Ask(byte player)
    {
        var s = CoopManager.Session;
        if (s == null) return;
        _asked = player;
        W.Reset(); W.U8(Kind); W.U8(OpAsk); W.U8(player);
        s.SendWorld(W.Span);
    }

    public static void Receive(byte from, ref NetReader r)
    {
        byte op = r.U8(), to = r.U8();
        var s = CoopManager.Session;
        if (s == null || to != s.LocalId) return;
        if (op == OpAsk) Answer(from);
        else if (op == OpAnswer) Show(from, ref r);
        else if (op == OpAskBot) AnswerBot(from, r.U32());
        else if (op == OpAnswerBot)
        {
            uint id = r.U32();
            var copy = MobSync.GetCopy(id);
            if (id != _askedBot || copy == null) return;
            _askedBot = 0;
            Show(copy, ref r);
        }
    }

    private static void AnswerBot(byte to, uint netId)
    {
        var bot = MobSync.GetOwned(netId);
        if (bot == null) return;
        var ids = new List<int>(); var weaponIds = new List<int>(); var slots = new List<int>(); var data = new List<int>(); var weaponData = new List<int>();
        MobSync.ReadGear(bot, ids, weaponIds, slots, data, weaponData);
        var armor = new List<(int id, int slot, int data)>();
        for (int i = 0; i < ids.Count; i++) armor.Add((ids[i], slots[i], data[i]));
        var weapons = new List<(int id, int data)>();
        for (int i = 0; i < weaponIds.Count; i++) weapons.Add((weaponIds[i], weaponData[i]));
        W.Reset(); W.U8(Kind); W.U8(OpAnswerBot); W.U8(to); W.U32(netId);
        WriteGear(bot, armor, weapons);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void Answer(byte to)
    {
        var p = GameInfo.Player;
        var cd = GameInfo.Character;
        if (p == null || cd == null) return;
        W.Reset(); W.U8(Kind); W.U8(OpAnswer); W.U8(to);
        // each piece with its own data (random stats, enchantment, sockets) and its slot: shown there as it is
        var armor = new List<(int id, int slot, int data)>();
        if (cd.ArmorPiecesEquipped != null) foreach (var a in cd.ArmorPiecesEquipped) if (a != null && a.itemID > 0) armor.Add((a.itemID, MobSync.SlotCode(a.armorSlotName), a.itemDataID));
        var weapons = new List<(int id, int data)>();
        if (cd.WeaponsEquipped != null) foreach (var w in cd.WeaponsEquipped) weapons.Add(w != null ? (w.itemID, w.itemDataID) : (-1, -1));
        WriteGear(p, armor, weapons);
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>Equipment with each piece's data, then the non-vital stats (health, mana...: followed all the time).</summary>
    private static void WriteGear(CombatEntity of, List<(int id, int slot, int data)> armor, List<(int id, int data)> weapons)
    {
        byte[] Data(int id) => id >= 0 ? ItemData.Write(id) ?? Array.Empty<byte>() : Array.Empty<byte>();
        W.U8((byte)armor.Count); foreach (var x in armor) { W.I32(x.id); W.I32(x.slot); W.Blob(Data(x.data)); }
        W.U8((byte)weapons.Count); foreach (var x in weapons) { W.I32(x.id); W.Blob(Data(x.data)); }
        var stats = new List<(int id, float cur, float max)>();
        try
        {
            var db = GameDatabase.Instance.GetStats();
            foreach (var kv in of.Stats)
            {
                int id = kv.Key;
                if (!db.ContainsKey(id) || db[id] == null || db[id].isVitalityStat) continue;
                stats.Add((id, CombatUtilities.GetCurrentStatValue(of, id), CombatUtilities.GetMaxStatValue(of, id)));
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Inspect stats: " + e.Message); }
        int n = Math.Min(stats.Count, 255);
        W.U8((byte)n);
        for (int i = 0; i < n; i++) { W.I32(stats[i].id); W.F32(stats[i].cur); W.F32(stats[i].max); }
    }

    private static void Show(byte from, ref NetReader r)
    {
        if (from != _asked) return;
        _asked = 255;
        var shell = PlayerShells.Get(from);
        if (shell == null) { Hud.Notify($"{Groups.Name(from)} is too far away to inspect"); return; }
        Show(shell, ref r);
    }

    /// <summary>The answer on the entity here (a player's shell, a bot's copy), then the game's inspect window on it.</summary>
    private static void Show(MobCombatEntity target, ref NetReader r)
    {
        var armor = new List<(int id, int slot, byte[] data)>(); var weapons = new List<(int id, byte[] data)>();
        int n = r.U8(); for (int i = 0; i < n; i++) armor.Add((r.I32(), r.I32(), r.Blob()));
        n = r.U8(); for (int i = 0; i < n; i++) weapons.Add((r.I32(), r.Blob()));
        var stats = new List<(int id, float cur, float max)>();
        n = r.U8(); for (int i = 0; i < n; i++) stats.Add((r.I32(), r.F32(), r.F32()));
        bool bot = MobSync.IsCopy(target);
        try
        {
            ItemData.DropTemps("inspect");
            var armorIds = new List<int>(); var armorSlots = new List<int>(); var armorData = new List<int>();
            foreach (var (id, slot, data) in armor) { armorIds.Add(id); armorSlots.Add(slot); armorData.Add(ItemData.CreateTemp("inspect", id, data)); }
            var weaponIds = new List<int>(); var weaponData = new List<int>();
            foreach (var (id, data) in weapons) { weaponIds.Add(id); weaponData.Add(id > 0 ? ItemData.CreateTemp("inspect", id, data) : -1); }
            MobSync.Dress(target, armorIds, weaponIds, refreshLook: bot, armorData, weaponData, armorSlots); // a shell's own body stays hidden
            _shown = target;
            foreach (var (id, cur, max) in stats)
            {
                if (max > 0f) CombatUtilities.SetMaxStatValue(target, id, max);
                CombatUtilities.SetCurrentStatValue(target, id, cur);
            }
            UIEvents.Instance.OnShowNpcInspectPanel(target);
        }
        catch (Exception e) { Plugin.L.LogWarning("Inspect: " + e.Message); }
    }

    private static MobCombatEntity _shown;

    /// <summary>The inspect window closed: the item data made for it goes.</summary>
    public static void Update()
    {
        if (_shown == null) return;
        try
        {
            var panel = UnityEngine.Object.FindObjectOfType<NpcInspectPanel>(true);
            if (panel != null && panel.IsOpen() && panel.InspectedEntity != null && panel.InspectedEntity.Pointer == _shown.Pointer) return;
        }
        catch { }
        if (MobSync.IsCopy(_shown)) MobSync.ForgetItemData(_shown); // the data made for the window goes: not worn without it
        _shown = null;
        ItemData.DropTemps("inspect");
    }

    /// <summary>The window's header for a player: their name, level and class, and their class icon (not the template's).</summary>
    private static void Header(NpcInspectPanel p)
    {
        var e = p.InspectedEntity;
        if (e == null || !PlayerShells.TryGetPlayer(e, out var player)) return;
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var info)) return;
        if (p.npcNameText != null) p.npcNameText.text = Groups.Name(player);
        string race = "", cls = "";
        try
        {
            var races = GameDatabase.Instance.GetRaces();
            if (races.ContainsKey(info.RaceId)) race = races[info.RaceId].entryDisplayName;
            var classes = GameDatabase.Instance.GetClasses();
            if (classes.ContainsKey(info.ClassId))
            {
                var c = classes[info.ClassId];
                cls = c.entryDisplayName;
                var icon = c.icon != null ? c.icon : c.entryIcon;
                if (p.npcIcon != null && icon != null) p.npcIcon.sprite = icon;
            }
        }
        catch { }
        if (p.npcLevelText != null) p.npcLevelText.text = $"Level: {info.Level}  {race} {cls}".TrimEnd();
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>"Inspect" in the target menu on a player: ask them first, the window opens with their answer.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnInspectClicked))]
        private static bool FromMenu(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            try
            {
                var target = __instance.GetTargetEntity();
                if (CoopManager.Session == null) return true;
                if (PlayerShells.TryGetPlayer(target, out var player)) Ask(player);
                else if (MobSync.IsCopy(target)) AskBot(target.TryCast<MobCombatEntity>());
                else return true;
                __instance.Hide();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Inspect menu: " + e.Message); return true; }
        }

        /// <summary>
        /// The interaction menu (F) on a player: "Inspect" (gear) as the game offers it for NPCs it lets one inspect, and no
        /// "Inspect talents" (it would show the shell's template's).
        /// </summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void Options(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out _)) return;
                var slots = __instance.optionSlots;
                if (slots == null) return;
                const BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType gear = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.InspectGear;
                const BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType talents = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.InspectTalents;
                bool hasGear = false;
                for (int i = slots.Count - 1; i >= 0; i--)
                {
                    var o = slots[i];
                    if (o == null) continue;
                    if (o.Type == talents) { UnityEngine.Object.Destroy(o.gameObject); slots.RemoveAt(i); }
                    else if (o.Type == gear) hasGear = true;
                }
                if (hasGear) return;
                var go = UnityEngine.Object.Instantiate(__instance.TextOptionPrefab, __instance.TextOptionsParent);
                go.transform.SetSiblingIndex(0);
                var option = go.GetComponent<NPCInteractionTextOption>();
                option.Initialize("Inspect", __instance);
                option.Type = gear;
                slots.Add(option);
            }
            catch (Exception e) { Plugin.L.LogWarning("Inspect option: " + e.Message); }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(NpcInspectPanel), nameof(NpcInspectPanel.Refresh))]
        private static void Refreshed(NpcInspectPanel __instance)
        {
            try { Header(__instance); } catch (Exception e) { Plugin.L.LogWarning("Inspect header: " + e.Message); }
        }

        /// <summary>The same from the interaction menu (F).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.ShowInspectGear))]
        private static bool FromInteraction(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                if (CoopManager.Session == null) return true;
                if (PlayerShells.TryGetPlayer(__instance.entity, out var player)) Ask(player);
                else if (MobSync.IsCopy(__instance.entity)) AskBot(__instance.entity.TryCast<MobCombatEntity>());
                else return true;
                __instance.Hide();
                return false;
            }
            catch (Exception e) { Plugin.L.LogWarning("Inspect interaction: " + e.Message); return true; }
        }
    }
}
