using System;
using System.Collections.Generic;
using System.Text;
using BLINK.RPGBuilder.Dungeons;
using BLINK.RPGBuilder.LogicMono;
using BLINK.RPGBuilder.Managers;
using UnityEngine;

namespace AfallonCoop.Plugin;

/// <summary>
/// Dungeons are instances, WoW-like: the scene of a dungeon is shared only by one co-op group (or one player outside
/// a group). For the mod a dungeon is "scene#instance" (<see cref="Place"/>): spawner keys, who sees whom and whose mob
/// copies appear all go by it, so two groups in the same dungeon scene never meet nor share its mobs.
///
/// The instance is fixed when the scene is entered (the group's at that moment): joining or leaving a group inside a
/// dungeon does not move anyone to another instance; they leave and come back, as in WoW.
/// </summary>
internal static class Dungeons
{
    private static string _scene = "";
    private static string _instance = "";
    private static HashSet<int> _dungeonIds;

    /// <summary>Where the local player is, for the mod: the scene, plus the instance in a dungeon.</summary>
    public static string Place
    {
        get
        {
            var scene = GameInfo.SceneName;
            if (scene != _scene)
            {
                if (!GameInfo.InWorld) return scene; // loading: the group may not be known yet, decide once in the world
                Enter(scene);
            }
            return _instance.Length == 0 ? scene : scene + "#" + _instance;
        }
    }

    public static bool InDungeon { get { var p = Place; int i = p.IndexOf('#'); return i >= 0 && string.CompareOrdinal(p, i + 1, "bg", 0, 2) != 0; } } // a battleground's match is no dungeon

    private static void Enter(string scene)
    {
        _scene = scene;
        var match = Battlegrounds.InstanceFor(scene); // a battleground's scene: the match's own
        if (match != null) { _instance = match; Plugin.L.LogInfo($"Battleground '{scene}': instance {_instance}"); return; }
        _instance = IsDungeonScene(scene) ? InstanceForMe() : "";
        if (_instance.Length > 0 && _outsideScene.Length > 0) SaveEntrance(scene, _outsideScene, _outsidePos);
        if (_instance.Length > 0) Plugin.L.LogInfo($"Dungeon '{scene}': instance {_instance}");
    }

    // ---------------- back from a disconnect into a finished run ----------------
    //
    // The game saves the character where it is: one who dropped out inside a dungeon loads back into that dungeon.
    // While the group is still in there, that is right (and Groups puts them back where they were). If the group's
    // run is over by then (nobody of the group in that instance any more), they leave as the game leaves a run: the
    // Dungeon Finder's LeaveDungeon, back to where they entered. Only for an arrival from the main menu (a load), not
    // for travel into the dungeon with the group (the first one there is alone for a moment).

    private const float StrandedCheckAfter = 6f; // group info and the others' places have come by then
    private static bool _fromMenu = true;
    private static float _arrivedAt = -1f;

    public static void Update()
    {
        if (GameInfo.SceneName == "MainMenu") { _fromMenu = true; _arrivedAt = -1f; return; }
        if (!GameInfo.InWorld) return;
        TrackOutside();
        PlaceAtEntrance();
        if (_fromMenu) { _fromMenu = false; _arrivedAt = Time.unscaledTime; }
        if (_leftAt >= 0f && Time.unscaledTime - _leftAt > 3f) { _leftAt = -1f; if (GameInfo.SceneName == _leftScene) ToGroupScene(); }
        if (_arrivedAt < 0f || Time.unscaledTime - _arrivedAt < StrandedCheckAfter) return;
        _arrivedAt = -1f;
        try { LeaveIfRunIsOver(); }
        catch (Exception e) { Plugin.L.LogWarning("Dungeon check: " + e.Message); }
    }

    private static void LeaveIfRunIsOver()
    {
        var s = CoopManager.Session;
        if (s == null || !CoopManager.Connected || !InDungeon) return;
        var g = s.MyGroup;
        if (g == null || _instance != "g" + g.Id) return; // alone: a personal run, the game's own business
        string place = Place;
        foreach (var id in g.Players)
            if (id != s.LocalId && s.Players.TryGetValue(id, out var p) && p.Scene == place) return; // the group is still in there
        Plugin.L.LogInfo($"Dungeon '{place}': the group's run is over, leaving");
        Hud.Notify("Your group has left this dungeon: back to where you entered");
        _leftScene = GameInfo.SceneName;
        _leftAt = Time.unscaledTime;
        DungeonFinderService.Instance?.LeaveDungeon(); // needs the run the game saved; without one it does nothing
    }

    private static float _leftAt = -1f;
    private static string _leftScene = "";

    // ---------------- where a dungeon was entered from ----------------
    //
    // Entered on foot (a portal in the world, not the Dungeon Finder), a dungeon has no run to leave by. Where the
    // character was just before (the scene and the spot by the entrance) is kept per character in a file, so that a
    // character back from a crash into a finished run can be put at the entrance.

    private static string _outsideScene = "";
    private static Vector3 _outsidePos;
    private static float _nextOutside;
    private static (string scene, Vector3 pos, float since, string note)? _toEntrance;
    private static string EntranceFile => System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "AfallonCoop.dungeon-entrances.txt");

    private static void TrackOutside()
    {
        if (Time.unscaledTime < _nextOutside) return;
        _nextOutside = Time.unscaledTime + 1f;
        var scene = GameInfo.SceneName;
        if (string.IsNullOrEmpty(scene) || IsDungeonScene(scene) || GameInfo.Player == null) return;
        _outsideScene = scene;
        _outsidePos = GameInfo.Player.transform.position;
    }

    private static string CharacterKey => GameInfo.Character?.CharacterName ?? "";

    private static void SaveEntrance(string dungeon, string fromScene, Vector3 pos)
    {
        try
        {
            var lines = new List<string>();
            if (System.IO.File.Exists(EntranceFile))
                foreach (var l in System.IO.File.ReadAllLines(EntranceFile))
                    if (!l.StartsWith(CharacterKey + "	" + dungeon + "	")) lines.Add(l);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            lines.Add(string.Join("	", CharacterKey, dungeon, fromScene, pos.x.ToString(inv), pos.y.ToString(inv), pos.z.ToString(inv)));
            System.IO.File.WriteAllLines(EntranceFile, lines);
        }
        catch (Exception e) { Plugin.L.LogWarning("Dungeon entrance: " + e.Message); }
    }

    private static bool LoadEntrance(string dungeon, out string scene, out Vector3 pos)
    {
        scene = ""; pos = default;
        try
        {
            if (!System.IO.File.Exists(EntranceFile)) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var l in System.IO.File.ReadAllLines(EntranceFile))
            {
                var f = l.Split('	');
                if (f.Length < 6 || f[0] != CharacterKey || f[1] != dungeon) continue;
                scene = f[2];
                pos = new Vector3(float.Parse(f[3], inv), float.Parse(f[4], inv), float.Parse(f[5], inv));
                return true;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("Dungeon entrance: " + e.Message); }
        return false;
    }

    /// <summary>
    /// To a spot in a place (scene#instance as <see cref="Place"/> gives it): a teleport in the same place, else the game
    /// loads the scene and the character is put on the spot once there. A dungeon instance only if it would be mine
    /// there (my group's). False if it cannot be reached.
    /// </summary>
    public static bool GoTo(string place, Vector3 pos)
    {
        if (string.IsNullOrEmpty(place) || GameInfo.Player == null) return false;
        if (place == Place) { GameInfo.TeleportPlayer(pos); return true; }
        var parts = place.Split('#');
        string scene = parts[0];
        if (parts.Length > 1 && parts[1] != InstanceForMe()) return false; // another group's or player's instance
        var gs = RPGBuilderUtilities.GetGameSceneFromName(scene);
        if (gs == null) return false;
        if (scene == GameInfo.SceneName) { GameInfo.TeleportPlayer(pos); return true; }
        _toEntrance = (scene, pos, -1f, null);
        ArriveAt(gs.ID, scene, pos);
        LoadingScreenManager.Instance.LoadGameScene(gs.ID);
        return true;
    }

    /// <summary>
    /// The game puts a character who loads a scene at its last position there (CharacterData.GameScenes): set to the
    /// destination before loading, the character appears on the spot. A scene never visited has no entry: then the
    /// teleport after arrival does it.
    /// </summary>
    private static void ArriveAt(int sceneId, string sceneName, Vector3 pos)
    {
        try
        {
            var scenes = GameInfo.Character?.GameScenes;
            if (scenes == null) return;
            foreach (var e in scenes) if (e != null && e.GameSceneID == sceneId) { e.LastPosition = pos; return; }
            // never been there: the entry the game would make on arrival, with this spot (its lists as it makes them)
            var entry = new CharacterEntries.GameSceneEntry { GameSceneName = sceneName, GameSceneID = sceneId, LastPosition = pos };
            if (entry.SavedTransforms == null || entry.DestroyedObjects == null) return; // not as the game makes them: the teleport after arrival does it
            scenes.Add(entry);
        }
        catch (Exception e) { Plugin.L.LogWarning("Arrival spot: " + e.Message); }
    }

    /// <summary>Arrived in the entrance's scene: to the spot by the entrance once the game has placed the character.</summary>
    private static void PlaceAtEntrance()
    {
        if (_toEntrance is not { } t || GameInfo.SceneName != t.scene) return;
        if (t.since < 0f) { _toEntrance = (t.scene, t.pos, Time.unscaledTime, t.note); return; }
        if (Time.unscaledTime - t.since < 2f) return;
        _toEntrance = null;
        if (GameInfo.Player != null && Vector3.Distance(GameInfo.Player.transform.position, t.pos) > 3f) GameInfo.TeleportPlayer(t.pos); // not placed there by the load
        if (!string.IsNullOrEmpty(t.note)) Hud.Notify(t.note);
    }

    /// <summary>No run to leave by (not entered through the Dungeon Finder): to its entrance, else to the group's scene.</summary>
    private static void ToGroupScene()
    {
        if (LoadEntrance(_leftScene, out var entranceScene, out var entrancePos) && !IsDungeonScene(entranceScene))
        {
            var es = RPGBuilderUtilities.GetGameSceneFromName(entranceScene);
            if (es != null)
            {
                Plugin.L.LogInfo($"Dungeon: no run to leave by, to its entrance in '{entranceScene}'");
                _toEntrance = (entranceScene, entrancePos, -1f, "Back at the dungeon's entrance");
                ArriveAt(es.ID, entranceScene, entrancePos);
                LoadingScreenManager.Instance.LoadGameScene(es.ID);
                return;
            }
        }
        var s = CoopManager.Session;
        var g = s?.MyGroup;
        if (g != null)
            foreach (var id in g.Players)
            {
                if (id == s.LocalId || !s.Players.TryGetValue(id, out var p) || string.IsNullOrEmpty(p.Scene)) continue;
                string scene = p.Scene.Split('#')[0];
                if (IsDungeonScene(scene)) continue;
                var gs = RPGBuilderUtilities.GetGameSceneFromName(scene);
                if (gs == null) continue;
                Plugin.L.LogInfo($"Dungeon: no run to leave by, to the group's scene '{scene}'");
                LoadingScreenManager.Instance.LoadGameScene(gs.ID);
                return;
            }
        Hud.Notify("Leave this dungeon through its portal");
    }

    /// <summary>The group's instance (its id lives as long as the group), or a personal one.</summary>
    private static string InstanceForMe()
    {
        var s = CoopManager.Session;
        if (s == null) return "solo";
        var g = s.MyGroup;
        if (g != null) return "g" + g.Id;
        return "p" + StableHashHex(CoopManager.ClientId());
    }

    private static string StableHashHex(string s) => ((uint)AfallonCoop.Protocol.StableHash.Of(s ?? "")).ToString("x8");

    /// <summary>
    /// A scene played as an instance: one the Dungeon Finder lists (the game's own dungeons) or a challenge stone's
    /// (the game's challenge scenes: the group goes in together, its bots wait outside as the game parks them).
    /// </summary>
    public static bool IsDungeonScene(string scene)
    {
        if (string.IsNullOrEmpty(scene)) return false;
        try { if (BLINK.RPGBuilder.Challenges.ChallengeStoneParty.IsChallengeScene(scene)) return true; } catch { }
        if (_dungeonIds == null)
        {
            try
            {
                var finder = DungeonFinderService.Instance;
                if (finder == null) return false; // not ready yet: ask again later
                var ids = new HashSet<int>();
                foreach (var d in finder.Dungeons) if (d != null) ids.Add(d.ID);
                _dungeonIds = ids;
            }
            catch (Exception e) { Plugin.L.LogWarning("Dungeon list: " + e.Message); return false; }
        }
        try { var gs = RPGBuilderUtilities.GetGameSceneFromName(scene); return gs != null && _dungeonIds.Contains(gs.ID); }
        catch { return false; }
    }

    /// <summary>Forget the latched instance (session change): the next <see cref="Place"/> decides again.</summary>
    public static void Reset() { _scene = ""; _instance = ""; }

    /// <summary>Dev: the dungeons the finder knows (id, scene name, levels), and where we are.</summary>
    public static string DevList()
    {
        var sb = new StringBuilder($"place '{Place}' | ");
        var finder = DungeonFinderService.Instance;
        if (finder == null) return sb + "no finder";
        var scenes = GameDatabase.Instance.GetGameScenes();
        foreach (var r in finder.Dungeons)
        {
            var d = scenes.TryGetValue(r.ID, out var full) ? full : r;
            sb.Append($"#{d.ID} '{d.entryName}' lvl {d.DungeonLevelMin}-{d.DungeonLevelMax}; ");
        }
        sb.Append($"phase {finder.Phase}, run {(finder.Run != null ? "dungeon #" + finder.Run.DungeonID : "none")}");
        return sb.ToString();
    }

    /// <summary>Dev: every game scene, challenge scenes marked.</summary>
    public static string DevScenes()
    {
        var sb = new StringBuilder();
        foreach (var kv in GameDatabase.Instance.GetGameScenes())
        {
            bool ch = false;
            try { ch = BLINK.RPGBuilder.Challenges.ChallengeStoneParty.IsChallengeScene(kv.Value.entryName); } catch { }
            sb.Append($"#{kv.Key} '{kv.Value.entryName}'{(ch ? " CHALLENGE" : "")}; ");
        }
        return sb.ToString();
    }

    /// <summary>Dev: travel to a game scene by id (at its start position).</summary>
    public static string DevTravel(string arg)
    {
        var scenes = GameDatabase.Instance.GetGameScenes();
        RPGGameScene gs = null;
        if (int.TryParse(arg, out var sceneId)) scenes.TryGetValue(sceneId, out gs);
        else foreach (var kv in scenes) if (kv.Value.entryName != null && kv.Value.entryName.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0) { gs = kv.Value; sceneId = kv.Key; break; }
        if (gs == null) return "no such scene";
        LoadingScreenManager.Instance.LoadGameScene(sceneId);
        return $"loading #{sceneId} '{gs.entryName}'";
    }
}
