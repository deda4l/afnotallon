using System;
using System.Collections.Generic;
using System.IO;
using BLINK.RPGBuilder.Social;
using HarmonyLib;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Players in the game's own Friends window (made for adventurer bots), WoW-like:
///  - "Friends" lists the player friends first (online ones, i.e. in this session, before offline ones), then the
///    game's adventurer friends; the row's button invites an online friend to the group; "Remove" forgets one;
///  - "Find adventurers" lists the players of this session first (the row's button adds them as friends);
///  - a right click on an online player's row offers Summon / Travel to (the target menu, as on a far group frame);
///  - "Add Friend" in the interaction menu (F) and the target menu adds that player.
/// Friends are kept on this PC (BepInEx/config), by their lasting key (Steam account, else install): who they are
/// across sessions, with the name, level and class last seen.
/// </summary>
internal static class Friends
{
    private sealed class Friend { public string Key = "", Name = ""; public int Level; public string Class = ""; }

    private static readonly List<Friend> List = new();
    private static string _loadedFor; // friends are per character, as in WoW
    private static string Me => GameInfo.Character?.CharacterName ?? "";
    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "AfallonCoop.friends." + Safe(Me) + ".txt");

    private static string Safe(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n;
    }

    /// <summary>My own lasting key, when the host told it (a friend list never shows oneself).</summary>
    private static string MyKey
    {
        get { var s = CoopManager.Session; return s != null && s.Players.TryGetValue(s.LocalId, out var p) ? p.Key : ""; }
    }

    private static bool IsMe(Friend f) => (MyKey.Length > 0 && f.Key == MyKey) || (Me.Length > 0 && f.Name == Me && Online(f.Key) == 255);

    // rows of the window: their int key (negative, never an adventurer's) -> the player friend or session player
    private const int KeyBase = -1000;
    private static readonly Dictionary<int, (string key, byte player)> Rows = new();

    private static void Load()
    {
        if (_loadedFor == Me) return;
        _loadedFor = Me;
        List.Clear();
        if (Me.Length == 0) return;
        try
        {
            if (!File.Exists(FilePath)) return;
            foreach (var l in File.ReadAllLines(FilePath))
            {
                var f = l.Split('\t');
                if (f.Length >= 4 && f[0].Length > 0) List.Add(new Friend { Key = f[0], Name = f[1], Level = int.TryParse(f[2], out var lv) ? lv : 0, Class = f[3] });
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Friends: " + e.Message); }
    }

    private static void Save()
    {
        try { File.WriteAllLines(FilePath, List.ConvertAll(f => string.Join("\t", f.Key, f.Name, f.Level, f.Class))); }
        catch (Exception e) { Plugin.L.LogWarning("Friends: " + e.Message); }
    }

    private static string ClassName(int id)
    {
        try { var c = BLINK.RPGBuilder.Managers.GameDatabase.Instance.GetClasses(); return c.ContainsKey(id) ? c[id].entryDisplayName : ""; } catch { return ""; }
    }

    /// <summary>The session player with this lasting key (online), else 255.</summary>
    private static byte Online(string key)
    {
        var s = CoopManager.Session;
        if (s == null || string.IsNullOrEmpty(key)) return 255;
        foreach (var p in s.Players.Values) if (p.Id != s.LocalId && p.Key == key) return p.Id;
        return 255;
    }

    /// <summary>My friends: lasting key and name as last seen.</summary>
    public static List<(string key, string name)> All()
    {
        Load();
        return List.ConvertAll(f => (f.Key, f.Name));
    }

    public static bool IsFriend(byte player)
    {
        Load();
        var s = CoopManager.Session;
        return s != null && s.Players.TryGetValue(player, out var p) && !string.IsNullOrEmpty(p.Key) && List.Exists(f => f.Key == p.Key);
    }

    /// <summary>Add (or forget) a session player.</summary>
    public static void Toggle(byte player)
    {
        Load();
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var p) || string.IsNullOrEmpty(p.Key)) return;
        int i = List.FindIndex(f => f.Key == p.Key);
        string name = Groups.Name(player);
        if (i >= 0) { List.RemoveAt(i); Hud.Notify($"{name} is no longer your friend"); }
        else { List.Add(new Friend { Key = p.Key, Name = name, Level = p.Level, Class = ClassName(p.ClassId) }); Hud.Notify($"{name} is now your friend"); }
        Save();
        RefreshWindow();
    }

    /// <summary>Friends seen in the session: their name, level and class as last seen.</summary>
    public static void Update()
    {
        if (_loadedFor == null || List.Count == 0 || Time.unscaledTime < _nextSeen) return;
        _nextSeen = Time.unscaledTime + 5f;
        var s = CoopManager.Session;
        if (s == null) return;
        bool changed = false;
        foreach (var f in List)
        {
            byte id = Online(f.Key);
            if (id == 255 || !s.Players.TryGetValue(id, out var p) || string.IsNullOrEmpty(p.CharacterName)) continue;
            string cls = ClassName(p.ClassId);
            if (f.Name != p.CharacterName || f.Level != p.Level || f.Class != cls) { f.Name = p.CharacterName; f.Level = p.Level; f.Class = cls; changed = true; }
        }
        if (changed) Save();
    }

    private static float _nextSeen;

    private static void RefreshWindow()
    {
        try { var w = UnityEngine.Object.FindObjectOfType<AdventurerFriendsPanel>(true); if (w != null && w.IsOpen()) w.Refresh(); } catch { }
    }

    // ---------------- the window ----------------

    private static FriendListing Listing(int key, string name, int level, string cls, string region, string status, bool online, bool inParty, bool friend) =>
        new() { Key = key, Name = name, Level = level, ClassName = cls, Region = region, Status = status, Available = online, InParty = inParty, IsFriend = friend, NPC = null };

    private static string Where(byte player)
    {
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var p) || string.IsNullOrEmpty(p.Scene)) return "In the menu";
        return p.Scene.Split('#')[0];
    }

    /// <summary>Player rows put before the game's own (adventurers) in either tab.</summary>
    private static void AddRows(bool discover, string search, Il2CppSystem.Collections.Generic.List<FriendListing> list)
    {
        Load();
        Rows.Clear();
        var s = CoopManager.Session;
        var add = new List<FriendListing>();
        bool Match(string n) => string.IsNullOrEmpty(search) || (n ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
        int next = KeyBase;
        if (discover)
        {
            // the players here, always first
            if (s != null)
                foreach (var p in s.Players.Values)
                {
                    if (p.Id == s.LocalId || string.IsNullOrEmpty(p.Key)) continue;
                    string name = Groups.Name(p.Id);
                    if (!Match(name)) continue;
                    bool friend = List.Exists(f => f.Key == p.Key);
                    int key = next--;
                    Rows[key] = (p.Key, p.Id);
                    add.Add(Listing(key, name, p.Level, ClassName(p.ClassId), Where(p.Id), friend ? "Friend" : "Player", true, s.InSameGroup(s.LocalId, p.Id), friend));
                }
        }
        else
        {
            var online = new List<FriendListing>(); var offline = new List<FriendListing>();
            foreach (var f in List)
            {
                if (IsMe(f)) continue;
                byte id = Online(f.Key);
                string name = id != 255 ? Groups.Name(id) : f.Name;
                if (!Match(name)) continue;
                int key = next--;
                Rows[key] = (f.Key, id);
                if (id != 255)
                {
                    s.Players.TryGetValue(id, out var p);
                    online.Add(Listing(key, name, p?.Level ?? f.Level, p != null ? ClassName(p.ClassId) : f.Class, Where(id), "Online", true, s.InSameGroup(s.LocalId, id), true));
                }
                else offline.Add(Listing(key, name, f.Level, f.Class, "", "Offline", false, false, true));
            }
            add.AddRange(online); add.AddRange(offline);
        }
        for (int i = add.Count - 1; i >= 0; i--) list.Insert(0, add[i]);
    }

    /// <summary>The row's button: add (find tab) or invite to the group (friends tab).</summary>
    private static bool Act(AdventurerFriendsPanel panel, int key)
    {
        if (!Rows.TryGetValue(key, out var row)) return false;
        var s = CoopManager.Session;
        if (panel.discover)
        {
            if (row.player != 255 && !IsFriend(row.player)) Toggle(row.player);
        }
        else
        {
            var why = CannotInvite(key);
            if (why != null) Hud.Notify(why);
            else { s.GroupInvite(row.player); Hud.Notify($"Group invite sent to {Groups.Name(row.player)}"); }
        }
        panel.Refresh();
        return true;
    }

    private static bool Remove(int key)
    {
        if (!Rows.TryGetValue(key, out var row)) return false;
        Load();
        int i = List.FindIndex(f => f.Key == row.key);
        if (i >= 0) { Hud.Notify($"{List[i].Name} is no longer your friend"); List.RemoveAt(i); Save(); }
        RefreshWindow();
        return true;
    }

    /// <summary>Why a friend's row cannot invite now (null: it can).</summary>
    private static string CannotInvite(int key)
    {
        if (!Rows.TryGetValue(key, out var row)) return null;
        var s = CoopManager.Session;
        if (row.player == 255 || s == null) return "This friend is not in your session.";
        if (s.InSameGroup(s.LocalId, row.player)) return "Already in your group.";
        var g = s.MyGroup;
        if (g != null && g.Size >= AfallonCoop.World.GroupRegistry.MaxSize) return "Your group is full.";
        if (g != null && g.Leader != s.LocalId) return "Only your group leader can invite.";
        return null;
    }

    /// <summary>A right click on an online player's row: Summon / Travel to.</summary>
    private static void RightClick(AdventurerFriendsPanel panel)
    {
        var mouse = UnityEngine.InputSystem.Mouse.current;
        if (mouse == null || !mouse.rightButton.wasPressedThisFrame || panel.rows == null) return;
        var pos = mouse.position.ReadValue();
        foreach (var row in panel.rows)
        {
            if (row == null || !row.gameObject.activeInHierarchy || !Rows.TryGetValue(row.Key, out var r) || r.player == 255) continue;
            var rt = row.GetComponent<RectTransform>();
            var canvas = rt.GetComponentInParent<Canvas>();
            var cam = canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
            if (RectTransformUtility.RectangleContainsScreenPoint(rt, pos, cam)) { Groups.OpenFarMenu(r.player); return; }
        }
    }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AdventurerFriends), nameof(AdventurerFriends.Listings))]
        private static void Listed(bool __0, string __1, Il2CppSystem.Collections.Generic.List<FriendListing> __result)
        {
            try { if (__result != null && CoopManager.Session != null) AddRows(__0, __1, __result); }
            catch (Exception e) { Plugin.L.LogWarning("Friends list: " + e.Message); }
        }

        /// <summary>A player's row button: the game would look for an adventurer behind it.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(AdventurerFriendRow), nameof(AdventurerFriendRow.Bind))]
        private static void Bound(AdventurerFriendRow __instance, FriendListing __0, bool __1)
        {
            try
            {
                if (__0 == null || !Rows.TryGetValue(__0.Key, out var row) || __instance.ActionButton == null) return;
                var s = CoopManager.Session;
                bool online = row.player != 255;
                __instance.ActionButton.interactable = __1 ? online && !__0.IsFriend : CannotInvite(__0.Key) == null;
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerFriendsPanel), nameof(AdventurerFriendsPanel.Act))]
        private static bool Acted(AdventurerFriendsPanel __instance, int __0)
        {
            try { return !Act(__instance, __0); } catch (Exception e) { Plugin.L.LogWarning("Friends: " + e.Message); return true; }
        }

        /// <summary>The game's invite check, for a player's row (it would look for an adventurer).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerFriends), nameof(AdventurerFriends.CanInvite))]
        private static bool CanInvite(int __0, ref string __1, ref bool __result)
        {
            if (!Rows.ContainsKey(__0)) return true;
            var why = CannotInvite(__0);
            __1 = why ?? "";
            __result = why == null;
            return false;
        }

        /// <summary>"Remove Friend" on a player's row.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerFriendsPanel), nameof(AdventurerFriendsPanel.RemoveSelected))]
        private static bool RemoveSelected(AdventurerFriendsPanel __instance)
        {
            try { if (!Rows.ContainsKey(__instance.selected)) return true; Remove(__instance.selected); __instance.Refresh(); return false; }
            catch { return true; }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(AdventurerFriends), nameof(AdventurerFriends.Remove), new[] { typeof(int) })]
        private static bool Removed(int __0)
        {
            try { return !Remove(__0); } catch { return true; }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(AdventurerFriendsPanel), nameof(AdventurerFriendsPanel.Update))]
        private static void PanelUpdate(AdventurerFriendsPanel __instance)
        {
            try { if (Rows.Count > 0 && __instance.IsOpen()) RightClick(__instance); } catch { }
        }

        /// <summary>"Add Friend" in the interaction menu (F) on a player: that player (not the shell's template).</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.AddFriend))]
        private static bool AddFromInteraction(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player)) return true;
            Toggle(player);
            try { __instance.Hide(); } catch { }
            return false;
        }

        /// <summary>Its label on a player: whether they are a friend.</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void FriendLabel(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player) || __instance.optionSlots == null) return;
                foreach (var o in __instance.optionSlots)
                    if (o != null && o.Type == BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.AddFriend && o.OptionText != null)
                        o.OptionText.text = IsFriend(player) ? "Remove Friend" : "Add Friend";
            }
            catch { }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.TargetContextMenu), nameof(BLINK.RPGBuilder.UI.TargetContextMenu.OnAddFriendClicked))]
        private static bool AddFromMenu(BLINK.RPGBuilder.UI.TargetContextMenu __instance)
        {
            if (CoopManager.Session == null || !PlayerShells.TryGetPlayer(__instance.GetTargetEntity(), out var player)) return true;
            Toggle(player);
            __instance.Hide();
            return false;
        }
    }
}
