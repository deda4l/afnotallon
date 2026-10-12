using System;
using System.Collections.Generic;
using System.IO;
using AfallonCoop.Protocol;
using BLINK.RPGBuilder.Characters;
using BLINK.RPGBuilder.Managers;
using BLINK.RPGBuilder.World;
using HarmonyLib;
using UnityEngine;
using FurnitureEntry = CharacterEntries.FurniturePlacementEntry;

namespace AfallonCoop.Plugin;

/// <summary>
/// Houses of the players online. A house is a property in the open world, its furniture in its owner's save (the game's
/// own: which property, item, position, rotation; and the scene's initial furniture they took away). Each player tells
/// the others what they own and how it is furnished, so:
///  - at a house another player owns, the game shows that player's furniture and no "for sale" sign, as at one's own;
///  - its owner can make players co-owners (F menu on a player): a co-owner gets the house's build mode, the game's own,
///    on the owner's furniture, and each change goes to the owner, whose save keeps it (the co-owner places their own
///    items and takes back what they pick up, as the game does).
/// A house of one's own always shows one's own furniture. The owner offline: their house is as the game has it here.
/// </summary>
internal static class Housing
{
    public const byte Kind = 26;
    private const byte OpHouse = 1, OpEdit = 2, OpBuilding = 3;
    private static readonly NetWriter W = new();

    private struct Piece { public int Prop, Item; public Vector3 Pos, Rot; }

    private sealed class House
    {
        public List<int> Props = new();
        public List<Piece> Furniture = new();
        public List<string> Claimed = new();
        public List<string> CoOwners = new();
    }

    private static readonly Dictionary<byte, House> Houses = new();
    private static string _sentSignature = "";
    private static float _nextCheck;
    private static bool _resend;
    private static readonly HashSet<byte> InWorld = new();

    private static CharacterData Data { get { try { return Character.Instance?.CharacterData; } catch { return null; } } }
    private static string Me => GameInfo.Character?.CharacterName ?? "";

    private static string MyKey
    {
        get { var s = CoopManager.Session; return s != null && s.Players.TryGetValue(s.LocalId, out var p) ? p.Key ?? "" : ""; }
    }

    // ---------------- co-owners: the owner's list, per character ----------------

    private static readonly List<string> CoOwners = new(); // lasting keys
    private static string _coOwnersFor;
    private static string CoOwnersFile => Path.Combine(BepInEx.Paths.ConfigPath, "AfallonCoop.coowners." + Safe(Me) + ".txt");

    private static string Safe(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return n;
    }

    private static void LoadCoOwners()
    {
        if (_coOwnersFor == Me) return;
        _coOwnersFor = Me;
        CoOwners.Clear();
        try { if (File.Exists(CoOwnersFile)) foreach (var l in File.ReadAllLines(CoOwnersFile)) if (l.Trim().Length > 0) CoOwners.Add(l.Trim()); } catch { }
    }

    private static void SaveCoOwners()
    {
        try { File.WriteAllLines(CoOwnersFile, CoOwners); } catch (Exception e) { Plugin.L.LogWarning("Co-owners: " + e.Message); }
    }

    private static bool MyHouses(out CharacterData d)
    {
        d = Data;
        return d?.OwnedProperties != null && d.OwnedProperties.Count > 0;
    }

    private static void ToggleCoOwner(byte player)
    {
        var s = CoopManager.Session;
        if (s == null || !s.Players.TryGetValue(player, out var p) || string.IsNullOrEmpty(p.Key)) return;
        LoadCoOwners();
        bool add = !CoOwners.Contains(p.Key);
        if (add) CoOwners.Add(p.Key); else CoOwners.Remove(p.Key);
        SaveCoOwners();
        _resend = true;
        Hud.Notify(add ? $"{Groups.Name(player)} is now a co-owner of your house" : $"{Groups.Name(player)} is no longer a co-owner of your house");
    }

    // ---------------- mine: told to the others ----------------

    public static void Update()
    {
        if (!CoopManager.Connected || !GameInfo.InWorld || Time.unscaledTime < _nextCheck) return;
        _nextCheck = Time.unscaledTime + 2f;
        FollowPlayers();
        if (_session >= 0) return; // building at someone's house: my lists are theirs for now
        var d = Data;
        if (d == null) return;
        LoadCoOwners();
        var mine = Read(d);
        string sig = Signature(mine);
        if (sig == _sentSignature && !_resend) return;
        _sentSignature = sig;
        _resend = false;
        W.Reset(); W.U8(Kind); W.U8(OpHouse);
        WriteHouse(mine);
        CoopManager.Session.SendWorld(W.Span);
    }

    /// <summary>Someone joined: they learn my house. Someone left: their houses are the game's own again here.</summary>
    private static void FollowPlayers()
    {
        var s = CoopManager.Session;
        foreach (var p in s.Players.Values)
            if (p.Id != s.LocalId && !string.IsNullOrEmpty(p.Scene) && InWorld.Add(p.Id)) _resend = true; // in the world now: can take it
        InWorld.RemoveWhere(id => !s.Players.TryGetValue(id, out var p) || string.IsNullOrEmpty(p.Scene));
        var gone = new List<byte>();
        foreach (var id in Houses.Keys) if (!s.Players.ContainsKey(id)) gone.Add(id);
        foreach (var id in gone)
        {
            var h = Houses[id];
            Houses.Remove(id);
            foreach (var prop in h.Props) Refresh(prop);
        }
        foreach (var id in new List<byte>(Building.Keys)) if (!s.Players.ContainsKey(id)) Building.Remove(id);
    }

    // ---------------- one builder per house at a time ----------------
    //
    // A co-owner's edit is the whole house as they left it: two building at once would each overwrite the other's
    // changes, and what one picked up (back in their bags) would stay placed in the other's list, or the other way round.
    // shortcut: two entering in the same instant both get in; a lock through the host if that ever shows up.

    private static readonly Dictionary<byte, int> Building = new(); // player -> the property they build at

    private static byte BuilderAt(int prop)
    {
        var s = CoopManager.Session;
        if (s == null) return 255;
        foreach (var kv in Building) if (kv.Value == prop && kv.Key != s.LocalId && s.Players.ContainsKey(kv.Key)) return kv.Key;
        return 255;
    }

    private static void TellBuilding(int prop)
    {
        if (!CoopManager.Connected) return;
        W.Reset(); W.U8(Kind); W.U8(OpBuilding); W.I32(prop);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static House Read(CharacterData d)
    {
        var h = new House();
        if (d.OwnedProperties != null) foreach (var p in d.OwnedProperties) if (p != null) h.Props.Add(p.propertyID);
        if (d.PlacedFurniture != null)
            foreach (var f in d.PlacedFurniture)
                if (f != null && h.Props.Contains(f.propertyID)) h.Furniture.Add(new Piece { Prop = f.propertyID, Item = f.itemID, Pos = f.position, Rot = f.rotation });
        if (d.ClaimedInitialFurniture != null) foreach (var k in d.ClaimedInitialFurniture) h.Claimed.Add(k);
        if (h.Props.Count > 0) h.CoOwners.AddRange(CoOwners);
        return h;
    }

    private static string Signature(House h)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var p in h.Props) sb.Append(p).Append(',');
        sb.Append('|');
        foreach (var f in h.Furniture) sb.Append($"{f.Prop}:{f.Item}:{f.Pos.x:0.00},{f.Pos.y:0.00},{f.Pos.z:0.00}:{f.Rot.y:0.0};");
        sb.Append('|').Append(string.Join(",", h.Claimed)).Append('|').Append(string.Join(",", h.CoOwners));
        return sb.ToString();
    }

    private static void WriteHouse(House h)
    {
        W.U8((byte)h.Props.Count); foreach (var p in h.Props) W.I32(p);
        WritePieces(h.Furniture);
        W.U16((ushort)h.Claimed.Count); foreach (var k in h.Claimed) W.Str(k);
        W.U8((byte)h.CoOwners.Count); foreach (var k in h.CoOwners) W.Str(k);
    }

    private static void WritePieces(List<Piece> pieces)
    {
        W.U16((ushort)pieces.Count);
        foreach (var f in pieces) { W.I32(f.Prop); W.I32(f.Item); W.F32(f.Pos.x); W.F32(f.Pos.y); W.F32(f.Pos.z); W.F32(f.Rot.x); W.F32(f.Rot.y); W.F32(f.Rot.z); }
    }

    private static List<Piece> ReadPieces(ref NetReader r)
    {
        var list = new List<Piece>();
        int n = r.U16();
        for (int i = 0; i < n; i++)
            list.Add(new Piece { Prop = r.I32(), Item = r.I32(), Pos = new Vector3(r.F32(), r.F32(), r.F32()), Rot = new Vector3(r.F32(), r.F32(), r.F32()) });
        return list;
    }

    private static void OnHouse(byte from, ref NetReader r)
    {
        var h = new House();
        int n = r.U8(); for (int i = 0; i < n; i++) h.Props.Add(r.I32());
        h.Furniture = ReadPieces(ref r);
        n = r.U16(); for (int i = 0; i < n; i++) h.Claimed.Add(r.Str());
        n = r.U8(); for (int i = 0; i < n; i++) h.CoOwners.Add(r.Str());
        Houses.TryGetValue(from, out var old);
        Houses[from] = h;
        var me = MyKey;
        if (me.Length > 0 && h.CoOwners.Contains(me) && (old == null || !old.CoOwners.Contains(me)) && old != null)
            Hud.Notify($"{Groups.Name(from)} made you a co-owner of their house");
        if (me.Length > 0 && old != null && old.CoOwners.Contains(me) && !h.CoOwners.Contains(me))
            Hud.Notify($"You are no longer a co-owner of {Groups.Name(from)}'s house");
        var props = new HashSet<int>(h.Props);
        if (old != null) foreach (var p in old.Props) props.Add(p);
        foreach (var p in props) if (ShownOwner(p) == from || (old != null && old.Props.Contains(p))) Refresh(p);
    }

    // ---------------- whose house is shown ----------------

    private static bool ReallyMine(int prop)
    {
        var d = Data;
        if (d?.OwnedProperties == null) return false;
        foreach (var p in d.OwnedProperties) if (p != null && p.propertyID == prop) return true;
        return false;
    }

    /// <summary>The player whose furniture this property shows here; 255 = the game's own (mine, or nobody's online).</summary>
    private static byte ShownOwner(int prop)
    {
        if (!CoopManager.Connected || ReallyMine(prop)) return 255;
        var s = CoopManager.Session;
        var me = MyKey;
        byte best = 255;
        foreach (var kv in Houses)
        {
            if (!kv.Value.Props.Contains(prop) || !s.Players.ContainsKey(kv.Key)) continue;
            if (me.Length > 0 && kv.Value.CoOwners.Contains(me)) return kv.Key; // the house I share first
            if (kv.Key < best) best = kv.Key;
        }
        return best;
    }

    private static bool CoOwns(int prop)
    {
        byte owner = ShownOwner(prop);
        var me = MyKey;
        return owner != 255 && me.Length > 0 && Houses.TryGetValue(owner, out var h) && h.CoOwners.Contains(me);
    }

    private static void Refresh(int prop)
    {
        if (_session == prop) return; // building there now: my changes stand
        try
        {
            foreach (var a in UnityEngine.Object.FindObjectsOfType<PropertyRemodelArea>())
                if (a.PropertyID == prop) { a.RespawnFurniture(); a.RefreshInitialFurniture(); }
            foreach (var sign in UnityEngine.Object.FindObjectsOfType<PropertyForSaleSign>()) sign.RefreshState();
        }
        catch (Exception e) { Plugin.L.LogWarning("House refresh: " + e.Message); }
    }

    // ---------------- the game reads the owner's furniture ----------------

    private static Il2CppSystem.Collections.Generic.List<FurnitureEntry> _myFurniture;
    private static Il2CppSystem.Collections.Generic.List<string> _myClaimed;
    private static int _swapDepth;

    private static Il2CppSystem.Collections.Generic.List<FurnitureEntry> FurnitureOf(House h, int prop)
    {
        var list = new Il2CppSystem.Collections.Generic.List<FurnitureEntry>();
        foreach (var f in h.Furniture)
            if (f.Prop == prop) list.Add(new FurnitureEntry { propertyID = f.Prop, itemID = f.Item, position = f.Pos, rotation = f.Rot });
        return list;
    }

    private static Il2CppSystem.Collections.Generic.List<string> ClaimedOf(House h)
    {
        var list = new Il2CppSystem.Collections.Generic.List<string>();
        foreach (var k in h.Claimed) list.Add(k);
        return list;
    }

    /// <summary>The save's furniture lists are the owner's while the game reads them for this property.</summary>
    private static bool SwapIn(int prop)
    {
        var d = Data;
        byte owner = ShownOwner(prop);
        if (d == null || owner == 255 || !Houses.TryGetValue(owner, out var h)) return false;
        if (_swapDepth++ == 0) { _myFurniture = d.PlacedFurniture; _myClaimed = d.ClaimedInitialFurniture; }
        d.PlacedFurniture = FurnitureOf(h, prop);
        d.ClaimedInitialFurniture = ClaimedOf(h);
        return true;
    }

    private static void SwapOut()
    {
        if (_swapDepth == 0 || --_swapDepth > 0) return;
        var d = Data;
        if (d == null) return;
        d.PlacedFurniture = _myFurniture;
        d.ClaimedInitialFurniture = _myClaimed;
    }

    // ---------------- a co-owner building: the owner's lists, changes to the owner ----------------

    private static int _session = -1;
    private static byte _sessionOwner;
    private static Il2CppSystem.Collections.Generic.List<FurnitureEntry> _sessionFurniture;
    private static Il2CppSystem.Collections.Generic.List<string> _sessionClaimed;

    private static void BeginSession(int prop)
    {
        var d = Data;
        byte owner = ShownOwner(prop);
        if (d == null || owner == 255 || !Houses.TryGetValue(owner, out var h)) return;
        _session = prop; _sessionOwner = owner;
        _myFurniture = d.PlacedFurniture; _myClaimed = d.ClaimedInitialFurniture;
        d.PlacedFurniture = _sessionFurniture = FurnitureOf(h, prop);
        d.ClaimedInitialFurniture = _sessionClaimed = ClaimedOf(h);
        Plugin.L.LogInfo($"House {prop}: building as {Groups.Name(owner)}'s co-owner");
    }

    private static void EndSession()
    {
        if (_session < 0) return;
        SendEdit();
        var d = Data;
        if (d != null) { d.PlacedFurniture = _myFurniture; d.ClaimedInitialFurniture = _myClaimed; }
        _session = -1;
    }

    /// <summary>The house as the co-owner left it: to its owner.</summary>
    private static void SendEdit()
    {
        if (_session < 0 || _sessionFurniture == null) return;
        var pieces = new List<Piece>();
        foreach (var f in _sessionFurniture) if (f != null && f.propertyID == _session) pieces.Add(new Piece { Prop = f.propertyID, Item = f.itemID, Pos = f.position, Rot = f.rotation });
        W.Reset(); W.U8(Kind); W.U8(OpEdit); W.U8(_sessionOwner); W.I32(_session);
        WritePieces(pieces);
        W.U16((ushort)_sessionClaimed.Count); foreach (var k in _sessionClaimed) W.Str(k);
        CoopManager.Session.SendWorld(W.Span);
    }

    private static void OnEdit(byte from, ref NetReader r)
    {
        byte owner = r.U8();
        int prop = r.I32();
        var pieces = ReadPieces(ref r);
        var claimed = new List<string>();
        int n = r.U16(); for (int i = 0; i < n; i++) claimed.Add(r.Str());
        var s = CoopManager.Session;
        if (s == null || owner != s.LocalId || !ReallyMine(prop)) return;
        LoadCoOwners();
        if (!s.Players.TryGetValue(from, out var p) || string.IsNullOrEmpty(p.Key) || !CoOwners.Contains(p.Key)) return;
        var d = Data;
        if (d == null) return;
        bool away = _session >= 0; // building at someone else's house: my own lists are held aside until I stop
        try
        {
            var keep = new Il2CppSystem.Collections.Generic.List<FurnitureEntry>();
            foreach (var f in away ? _myFurniture : d.PlacedFurniture) if (f != null && f.propertyID != prop) keep.Add(f);
            foreach (var f in pieces) if (f.Prop == prop) keep.Add(new FurnitureEntry { propertyID = f.Prop, itemID = f.Item, position = f.Pos, rotation = f.Rot });
            if (away) _myFurniture = keep; else d.PlacedFurniture = keep;
            var myClaimed = away ? _myClaimed : d.ClaimedInitialFurniture;
            foreach (var k in claimed) if (!myClaimed.Contains(k)) myClaimed.Add(k);
            RPGBuilderJsonSaver.SaveCharacterData();
            Plugin.L.LogInfo($"House {prop}: {Groups.Name(from)} rearranged it ({pieces.Count} pieces)");
            Hud.Notify($"{Groups.Name(from)} rearranged your house");
        }
        catch (Exception e) { Plugin.L.LogWarning("House edit: " + e.Message); return; }
        try { if (!BuildingModeManager.Instance.IsActive) foreach (var a in UnityEngine.Object.FindObjectsOfType<PropertyRemodelArea>()) if (a.PropertyID == prop) { a.RespawnFurniture(); a.RefreshInitialFurniture(); } } catch { }
    }

    public static void Receive(byte from, ref NetReader r)
    {
        switch (r.U8())
        {
            case OpHouse: OnHouse(from, ref r); break;
            case OpEdit: OnEdit(from, ref r); break;
            case OpBuilding: { int prop = r.I32(); if (prop < 0) Building.Remove(from); else Building[from] = prop; break; }
        }
    }

    public static string Dev()
    {
        var parts = new List<string>();
        foreach (var kv in Houses)
            parts.Add($"{Groups.Name(kv.Key)}: props [{string.Join(",", kv.Value.Props)}] pieces {kv.Value.Furniture.Count} co-owners {kv.Value.CoOwners.Count}");
        LoadCoOwners();
        parts.Add($"mine co-owners {CoOwners.Count}, session {_session}");
        return string.Join(" | ", parts);
    }

    public static string DevCoOwner(byte player) { ToggleCoOwner(player); return Dev(); }

    public static void Install(Harmony h) => h.PatchAll(typeof(Patches));

    [HarmonyPatch]
    private static class Patches
    {
        /// <summary>Owned, for the game here, when a player online owns it (its furniture shown, no sign).</summary>
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PropertyManager), nameof(PropertyManager.IsOwned))]
        private static void OwnedByAPlayer(int __0, ref bool __result)
        {
            if (!__result) try { __result = ShownOwner(__0) != 255; } catch { }
        }

        /// <summary>Only the owner sells.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(PropertyManager), nameof(PropertyManager.SellProperty))]
        private static bool OnlyOwnerSells(RPGProperty __0) => __0 == null || ReallyMine(__0.ID);

        /// <summary>Build mode at someone's house: for its co-owners only.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildingModeManager), nameof(BuildingModeManager.ShowEnterPrompt))]
        private static bool PromptForOwners(PropertyRemodelArea __0) => __0 == null || ShownOwner(__0.PropertyID) == 255 || CoOwns(__0.PropertyID);

        [HarmonyPrefix]
        [HarmonyPatch(typeof(BuildingModeManager), nameof(BuildingModeManager.EnterBuildMode))]
        private static bool Enter(PropertyRemodelArea __0)
        {
            if (__0 == null) return true;
            byte builder = BuilderAt(__0.PropertyID);
            if (builder != 255) { Hud.Notify($"{Groups.Name(builder)} is building here now"); return false; }
            if (ShownOwner(__0.PropertyID) == 255) return true;
            if (!CoOwns(__0.PropertyID)) return false;
            try { BeginSession(__0.PropertyID); } catch (Exception e) { Plugin.L.LogWarning("House build: " + e.Message); }
            return true;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingModeManager), nameof(BuildingModeManager.EnterBuildMode))]
        private static void Entered(PropertyRemodelArea __0)
        {
            try { if (__0 != null && BuildingModeManager.Instance.IsActive) TellBuilding(__0.PropertyID); } catch { }
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BuildingModeManager), nameof(BuildingModeManager.ExitBuildMode))]
        private static void Exit()
        {
            try { EndSession(); } catch (Exception e) { Plugin.L.LogWarning("House build end: " + e.Message); }
            TellBuilding(-1);
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PropertyRemodelArea), nameof(PropertyRemodelArea.RespawnFurniture))]
        private static void BeforeRespawn(PropertyRemodelArea __instance, out bool __state)
        {
            __state = false;
            try { if (_session != __instance.PropertyID) __state = SwapIn(__instance.PropertyID); } catch { }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(PropertyRemodelArea), nameof(PropertyRemodelArea.RespawnFurniture))]
        private static void AfterRespawn(bool __state) { if (__state) SwapOut(); }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(PropertyRemodelArea), nameof(PropertyRemodelArea.RefreshInitialFurniture))]
        private static void BeforeInitial(PropertyRemodelArea __instance, out bool __state)
        {
            __state = false;
            try { if (_session != __instance.PropertyID) __state = SwapIn(__instance.PropertyID); } catch { }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(PropertyRemodelArea), nameof(PropertyRemodelArea.RefreshInitialFurniture))]
        private static void AfterInitial(bool __state) { if (__state) SwapOut(); }

        /// <summary>The save keeps my own furniture; a co-owner's change goes to the owner as it is saved.</summary>
        [HarmonyPrefix]
        [HarmonyPatch(typeof(RPGBuilderJsonSaver), nameof(RPGBuilderJsonSaver.SaveCharacterData))]
        private static void BeforeSave(out bool __state)
        {
            __state = false;
            var d = Data;
            if (d == null) return;
            if (_session >= 0)
            {
                try { SendEdit(); } catch { }
                d.PlacedFurniture = _myFurniture; d.ClaimedInitialFurniture = _myClaimed;
                __state = true;
            }
            else if (_swapDepth > 0) { d.PlacedFurniture = _myFurniture; d.ClaimedInitialFurniture = _myClaimed; __state = true; }
        }

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(RPGBuilderJsonSaver), nameof(RPGBuilderJsonSaver.SaveCharacterData))]
        private static void AfterSave(bool __state)
        {
            if (!__state) return;
            var d = Data;
            if (d == null) return;
            if (_session >= 0) { d.PlacedFurniture = _sessionFurniture; d.ClaimedInitialFurniture = _sessionClaimed; }
        }

        // ---- "Make co-owner" in the interaction menu (F) on a player, for one who owns a house ----

        private static readonly Dictionary<IntPtr, Action> Options = new();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(BLINK.RPGBuilder.UI.NPCInteractionsPanel), nameof(BLINK.RPGBuilder.UI.NPCInteractionsPanel.Initialize))]
        private static void CoOwnerOption(BLINK.RPGBuilder.UI.NPCInteractionsPanel __instance)
        {
            try
            {
                Options.Clear();
                var s = CoopManager.Session;
                if (s == null || !PlayerShells.TryGetPlayer(__instance.entity, out var player) || !MyHouses(out _)) return;
                if (!s.Players.TryGetValue(player, out var p) || string.IsNullOrEmpty(p.Key)) return;
                LoadCoOwners();
                var go = UnityEngine.Object.Instantiate(__instance.TextOptionPrefab, __instance.TextOptionsParent);
                var option = go.GetComponent<NPCInteractionTextOption>();
                option.Initialize(CoOwners.Contains(p.Key) ? "Remove house co-owner" : "Make house co-owner", __instance);
                option.Type = BLINK.RPGBuilder.UI.NPCInteractionsPanel.NPCInteractionOptionType.Trade;
                __instance.optionSlots?.Add(option);
                var panel = __instance;
                Options[option.Pointer] = () => { ToggleCoOwner(player); try { panel.Hide(); } catch { } };
            }
            catch (Exception e) { Plugin.L.LogWarning("Co-owner option: " + e.Message); }
        }

        [HarmonyPrefix]
        [HarmonyPatch(typeof(NPCInteractionTextOption), nameof(NPCInteractionTextOption.Click))]
        private static bool OptionClick(NPCInteractionTextOption __instance)
        {
            if (!Options.TryGetValue(__instance.Pointer, out var act)) return true;
            try { act(); } catch (Exception e) { Plugin.L.LogWarning("Co-owner option: " + e.Message); }
            return false;
        }
    }
}
