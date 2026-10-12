using System;
using System.Collections.Generic;
using System.IO;
using AfallonCoop.Protocol;

namespace AfallonCoop.Plugin;

/// <summary>
/// Players the host keeps out of its games, by their lasting key (their Steam account, or their install for a LAN/IP
/// player), with the name they had; kept in a file across sessions.
/// </summary>
internal static class BanList
{
    private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "AfallonCoop.banned.txt");
    private static Dictionary<string, string> _names; // key -> name

    private static Dictionary<string, string> Names
    {
        get
        {
            if (_names != null) return _names;
            _names = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (var l in File.ReadAllLines(FilePath)) { var f = l.Split('\t'); if (f.Length >= 2 && f[0].Length > 0) _names[f[0]] = f[1]; }
            }
            catch (Exception e) { Plugin.L.LogWarning("Ban list: " + e.Message); }
            return _names;
        }
    }

    private static void Save()
    {
        var lines = new List<string>();
        foreach (var kv in Names) lines.Add(kv.Key + "\t" + kv.Value.Replace('\t', ' ').Replace('\n', ' '));
        try { File.WriteAllLines(FilePath, lines); } catch (Exception e) { Plugin.L.LogWarning("Ban list: " + e.Message); }
    }

    public static IReadOnlyDictionary<string, string> All => Names;

    /// <summary>For the session: someone saying hello, by Steam account (0 = none) and install id.</summary>
    public static bool IsBanned(ulong steamId, string clientId) =>
        (steamId != 0 && Names.ContainsKey("steam:" + steamId)) || (!string.IsNullOrEmpty(clientId) && Names.ContainsKey("client:" + clientId));

    public static void Ban(PlayerInfo p)
    {
        if (p == null || string.IsNullOrEmpty(p.Key)) return;
        Names[p.Key] = string.IsNullOrEmpty(p.CharacterName) ? p.Name : $"{p.Name} ({p.CharacterName})";
        Save();
        CoopManager.Session?.Kick(p.Id, "The host has banned you from this game.");
        Hud.Notify($"{Names[p.Key]} is banned");
    }

    public static void Unban(string key)
    {
        if (!Names.TryGetValue(key, out var name)) return;
        Names.Remove(key);
        Save();
        Hud.Notify($"{name} is no longer banned");
    }
}
