using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace AfallonCoop.Plugin;

[BepInPlugin(Guid, Name, Version)]
public class Plugin : BasePlugin
{
    public const string Guid = "afallon.coop";
    public const string Name = "AfNotAllon";
    public const string Version = "0.6.0";

    internal static ManualLogSource L;
    internal static string Dir;

    internal static ConfigEntry<string> CfgPlayerName;
    internal static ConfigEntry<int> CfgPort;
    internal static ConfigEntry<uint> CfgSteamAppId;
    internal static ConfigEntry<int> CfgSendRate;
    internal static ConfigEntry<string> CfgLastAddress;
    internal static ConfigEntry<string> CfgHostPassword, CfgJoinPassword;
    internal static ConfigEntry<bool> CfgShowDebug;
    internal static ConfigEntry<string> CfgClientId;
    internal static ConfigEntry<bool> CfgStealthTranslucent;
    internal static ConfigEntry<int> CfgSimPing, CfgSimJitter;
    internal static ConfigEntry<double> CfgSimLoss;
    internal static ConfigEntry<float> CfgStealthOpacity;

    public override void Load()
    {
        L = Log;
        Dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        CfgPlayerName = Config.Bind("General", "PlayerName", "",
            "Name shown to other players. Empty = your Steam name (or Windows user name without Steam).");
        CfgPort = Config.Bind("Network", "Port", Transport.LiteNetTransport.DefaultPort,
            "UDP port for LAN / direct IP hosting. Others connect to <your ip>:<port>.");
        CfgSteamAppId = Config.Bind("Network", "SteamAppId", 2597810u,
            "App id used for Steam lobbies when the game was NOT started from Steam. 2597810 = Afallon, 480 = Spacewar fallback.");
        CfgSendRate = Config.Bind("Network", "SendRate", 15, "Movement updates per second (10-30).");
        CfgLastAddress = Config.Bind("Network", "LastAddress", "", "Last address you joined (remembered for convenience).");
        CfgHostPassword = Config.Bind("Network", "HostPassword", "", "When you host: players must enter it to join. Empty = anyone you invite or who finds the game.");
        CfgJoinPassword = Config.Bind("Network", "JoinPassword", "", "When you join: the host's password, if it has one.");
        CfgShowDebug = Config.Bind("General", "ShowDebug", false, "Show the co-op status line (players, ping, traffic) in the corner. Also in Esc > CO-OP INFO.");
        CfgClientId = Config.Bind("Network", "ClientId", "", "This install's own id, made automatically: lets a host recognise you when you reconnect (LAN/IP). Do not copy it to another PC.");
        CfgSimPing = Config.Bind("Testing", "SimulatedPingMs", 0, "Imitate a slower network: extra round-trip time in ms (set on both machines). 0 = off.");
        CfgSimJitter = Config.Bind("Testing", "SimulatedJitterMs", 0, "Imitated random extra delay per packet, ms.");
        CfgSimLoss = Config.Bind("Testing", "SimulatedLossPercent", 0.0, "Imitated packet loss, percent (reliable packets arrive late instead).");
        CfgStealthTranslucent = Config.Bind("Visuals", "StealthTranslucent", false,
            "EXPERIMENTAL: show allies in stealth semi-transparent. Armor layers are lost while translucent (the game has no transparent character shader).");
        CfgStealthOpacity = Config.Bind("Visuals", "StealthOpacity", 0.35f, "Opacity of an ally in stealth (0.1-0.9).");

        // steam_api64.dll lives next to this plugin, not next to the game exe.
        NativeLibrary.SetDllImportResolver(typeof(Steamworks.SteamAPI).Assembly, (name, asm, path) =>
        {
            if (name.StartsWith("steam_api", StringComparison.OrdinalIgnoreCase))
            {
                var p = Path.Combine(Dir, "steam_api64.dll");
                if (File.Exists(p)) return NativeLibrary.Load(p);
            }
            return IntPtr.Zero;
        });

        RunInBackground();

        var harmony = new HarmonyLib.Harmony(Guid);
        try { ChatBridge.Install(harmony); }
        catch (Exception e) { L.LogError("Chat bridge failed to install: " + e); }
        try { FxSync.Install(harmony); }
        catch (Exception e) { L.LogError("Fx sync failed to install: " + e); }
        try { PlayerShells.Install(harmony); }
        catch (Exception e) { L.LogError("Player shells failed to install: " + e); }
        try { StateSync.Install(harmony); }
        catch (Exception e) { L.LogError("State sync failed to install: " + e); }
        try { MobSync.Install(harmony); }
        catch (Exception e) { L.LogError("Mob sync failed to install: " + e); }
        try { VisualMirror.Install(harmony); }
        catch (Exception e) { L.LogError("Visual mirror failed to install: " + e); }
        try { GroupFrames.Install(harmony); }
        catch (Exception e) { L.LogError("Group frames failed to install: " + e); }
        try { Groups.Install(harmony); }
        catch (Exception e) { L.LogError("Groups failed to install: " + e); }
        try { EnterWorldGate.Install(harmony); }
        catch (Exception e) { L.LogError("Enter world gate failed to install: " + e); }
#if DEBUG
        try { DevGameActions.Install(harmony); }
        catch (Exception e) { L.LogError("Game action trace failed to install: " + e); }
#endif
        try { GroupFinder.Install(harmony); }
        catch (Exception e) { L.LogError("Group finder failed to install: " + e); }
        try { Trade.Install(harmony); }
        catch (Exception e) { L.LogError("Trade failed to install: " + e); }
        try { Inspect.Install(harmony); }
        catch (Exception e) { L.LogError("Inspect failed to install: " + e); }
        try { Battlegrounds.Install(harmony); BattlegroundTestArena.Install(harmony); }
        catch (Exception e) { L.LogWarning("Battlegrounds patches: " + e.Message); }
        try { Duel.Install(harmony); }
        catch (Exception e) { L.LogError("Duel failed to install: " + e); }
        try { GroupLoot.Install(harmony); }
        catch (Exception e) { L.LogError("Group loot failed to install: " + e); }
        try { MeterSync.Install(harmony); }
        catch (Exception e) { L.LogError("Meter sync failed to install: " + e); }
        try { Friends.Install(harmony); }
        catch (Exception e) { L.LogError("Friends failed to install: " + e); }
        try { AuctionSync.Install(harmony); }
        catch (Exception e) { L.LogError("Auction sync failed to install: " + e); }
        try { Corruption.Install(harmony); }
        catch (Exception e) { L.LogError("Corruption failed to install: " + e); }
        try { Mail.Install(harmony); }
        catch (Exception e) { L.LogError("Mail failed to install: " + e); }
        try { WorldRandom.Install(harmony); }
        catch (Exception e) { L.LogError("World random patches failed: " + e); }
        try { WorldRules.Install(harmony); }
        catch (Exception e) { L.LogError("World rules failed to install: " + e); }
        try { WorldQuests.Install(harmony); }
        catch (Exception e) { L.LogError("World quests failed to install: " + e); }
        try { Housing.Install(harmony); }
        catch (Exception e) { L.LogError("Housing failed to install: " + e); }
        try { QuestShare.Install(harmony); }
        catch (Exception e) { L.LogError("Quest share failed to install: " + e); }
        try { PlayerActions.Install(harmony); }
        catch (Exception e) { L.LogError("Player actions failed to install: " + e); }
        try { FlightVisual.Install(harmony); }
        catch (Exception e) { L.LogError("Flight visual failed to install: " + e); }
        try { BossPhases.Install(harmony); }
        catch (Exception e) { L.LogError("Boss phases failed to install: " + e); }
        try { BossSync.Install(harmony); }
        catch (Exception e) { L.LogError("Boss sync failed to install: " + e); }
#if DEBUG // traces and the dev bridge's tools: dev builds only (the hooks sit on hot combat calls)
        try { harmony.PatchAll(typeof(BossTrace.Patches)); }
        catch (Exception e) { L.LogError("Boss trace failed to install: " + e); }
        try { DevCastTrace.Install(harmony); }
        catch (Exception e) { L.LogError("Cast trace failed to install: " + e); }
#endif

        ClassInjector.RegisterTypeInIl2Cpp<CoopRuntime>();
        AddComponent<CoopRuntime>();
        L.LogInfo($"{Name} {Version} loaded. Game build {GameInfo.BuildGuid}, pid {System.Diagnostics.Process.GetCurrentProcess().Id}");
    }

    private delegate void SetBoolICall(bool value);

    /// <summary>Keep simulating and sending while unfocused (two copies on one PC, alt-tab). The managed setter is stripped.</summary>
    private static void RunInBackground()
    {
        try
        {
            var set = IL2CPP.ResolveICall<SetBoolICall>("UnityEngine.Application::set_runInBackground");
            set(true);
            L.LogInfo("runInBackground = true");
        }
        catch (Exception e) { L.LogWarning("Could not enable runInBackground: " + e.Message); }
    }
}

/// <summary>The one Unity object the mod owns. Everything is driven from its Update.</summary>
public class CoopRuntime : MonoBehaviour
{
    public CoopRuntime(IntPtr ptr) : base(ptr) { }

    private void Update() => Safe.Run(CoopManager.Update);
    private void LateUpdate() => Safe.Run(CoopManager.LateUpdate);
    private void OnApplicationQuit() => Safe.Run(CoopManager.Shutdown);
}

/// <summary>Never let a mod exception escape into the game loop; log it at most every 2 s.</summary>
internal static class Safe
{
    private static float _lastErrorLog;

    public static void Run(Action a)
    {
        try { a(); }
        catch (Exception e)
        {
            if (Time.unscaledTime - _lastErrorLog > 2f)
            {
                _lastErrorLog = Time.unscaledTime;
                Plugin.L.LogError(e);
            }
        }
    }
}
