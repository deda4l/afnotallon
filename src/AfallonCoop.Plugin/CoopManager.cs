using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using AfallonCoop.Protocol;
using AfallonCoop.Session;
using AfallonCoop.Transport;
using Steamworks;

namespace AfallonCoop.Plugin;

public enum CoopMode { Off, HostSteam, HostLan, Client }

/// <summary>Owns the session and wires network events to the game-side systems.</summary>
internal static class CoopManager
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    public static double Now => Clock.Elapsed.TotalSeconds;

    public static CoopSession Session { get; private set; }
    public static CoopMode Mode { get; private set; }
    public static string LastError { get; private set; } = "";
    public static bool Busy { get; private set; }

    public static List<LiteNetTransport.LanHost> LanHosts { get; private set; } = new();
    public static bool Discovering { get; private set; }
    private static Task<List<LiteNetTransport.LanHost>> _discoverTask;

    private static bool _steamTried;

    public static bool Active => Session != null && Session.State != SessionState.Closed;
    public static bool Connected => Session != null && Session.State == SessionState.Connected;

    public static string LocalName
    {
        get
        {
            var n = Plugin.CfgPlayerName.Value;
            if (!string.IsNullOrWhiteSpace(n)) return n.Trim();
            return SteamReady ? SteamPlatform.MyName : Environment.UserName;
        }
    }

    public static bool SteamReady
    {
        get
        {
            if (!_steamTried)
            {
                _steamTried = true;
                SteamPlatform.Log += m => Plugin.L.LogInfo("[Steam] " + m);
                SteamPlatform.JoinHostRequested += host => JoinSteamHost(host);
                if (!SteamPlatform.Init(Plugin.CfgSteamAppId.Value))
                    Plugin.L.LogWarning("Steam unavailable: " + SteamPlatform.InitError + " (LAN/IP still works)");
            }
            return SteamPlatform.Initialized;
        }
    }

    private static SessionConfig Config(bool host) => new()
    {
        Password = (host ? Plugin.CfgHostPassword.Value : Plugin.CfgJoinPassword.Value) ?? "",
        ModVersion = Plugin.Version,
        GameBuild = GameInfo.BuildGuid,
        PlayerName = LocalName,
        SteamId = SteamPlatform.MySteamId,
        ClientId = ClientId(),
    };

    /// <summary>This install's own random id, made once and kept in the config (test copies get theirs from the launcher).</summary>
    internal static string ClientId()
    {
        var env = Environment.GetEnvironmentVariable("AFCOOP_CLIENTID");
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
        if (string.IsNullOrEmpty(Plugin.CfgClientId.Value)) Plugin.CfgClientId.Value = Guid.NewGuid().ToString("N");
        return Plugin.CfgClientId.Value;
    }

    // ---------------- host ----------------

    /// <summary>
    /// The chosen way to host, waiting for the host to enter their world (main menu: HOST GAME, a way, a character).
    /// The session starts with the world and ends with it.
    /// </summary>
    public static CoopMode PendingHost { get; private set; }

    public static void PrepareHost(CoopMode mode)
    {
        LastError = "";
        PendingHost = mode;
        CancelPendingJoin();
    }

    /// <summary>Where to join, waiting for the player to enter their world with the chosen character.</summary>
    public static string PendingJoin { get; private set; } = "";
    private static Action _pendingJoin;

    public static void PrepareJoin(string label, Action join)
    {
        LastError = "";
        PendingHost = CoopMode.Off;
        PendingJoin = label;
        _pendingJoin = join;
    }

    public static void CancelPendingJoin() { PendingJoin = ""; _pendingJoin = null; }

    // ---------------- entering the world ----------------
    //
    // "Play" in the game's character select (EnterWorldGate hooks it): the host opens the session right there, so the
    // world loads into it; a client connects first and the world loads only once the host accepted them, so nothing
    // of a world of their own ever spawns (it would double the host's mobs and NPCs).

    private static Action _enterWorld;
    private static bool _resumingEnter;

    /// <summary>False = do not load the world yet (connecting; it resumes by itself).</summary>
    public static bool BeforeEnterWorld(Action enter)
    {
        if (_resumingEnter) return true;
        if (PendingHost != CoopMode.Off && !Active) { StartPendingHost(); return true; }
        if (_pendingJoin == null || Active) return true;
        var join = _pendingJoin;
        var label = PendingJoin;
        CancelPendingJoin();
        _enterWorld = enter;
        Hud.Notify("Joining " + label + "...");
        join();
        return false;
    }

    private static void UpdateEnterWorld()
    {
        if (_enterWorld == null) return;
        if (Connected)
        {
            var enter = _enterWorld;
            _enterWorld = null;
            _resumingEnter = true;
            try { enter(); }
            catch (Exception e) { Plugin.L.LogWarning("Entering the world: " + e.Message); }
            finally { _resumingEnter = false; }
        }
        else if (!Active && !Busy)
        {
            _enterWorld = null; // the join failed: stay in the menu with the reason
            Hud.Notify("Could not join: " + LastError);
        }
    }

    private static void StartPendingJoin()
    {
        var join = _pendingJoin;
        var label = PendingJoin;
        CancelPendingJoin();
        Hud.Notify("Joining " + label + "...");
        join();
        // A join that fails at once (bad code or address) leaves no session: back to the menu with the reason.
        if (!Active && !Busy && GameInfo.InWorld)
        {
            Hud.Notify("Could not join: " + LastError);
            _wasInWorld = false;
            PauseController.BackToMainMenu();
        }
    }

    public static void CancelPendingHost() => PendingHost = CoopMode.Off;

    private static void StartPendingHost()
    {
        var m = PendingHost;
        PendingHost = CoopMode.Off;
        if (m == CoopMode.HostSteam) HostSteam(); else HostLan();
        if (Active) Hud.Notify(m == CoopMode.HostSteam ? "Hosting via Steam: Esc > CO-OP INFO for the code" : "Hosting via LAN / IP: Esc > CO-OP INFO for your address");
    }

    public static void HostLan()
    {
        if (Active) return;
        try
        {
            var t = LiteNetTransport.Host(Plugin.CfgPort.Value);
            t.Log = m => Plugin.L.LogWarning(m);
            t.DiscoveryInfo = () => $"{LocalName}|{Session?.PlayerCount ?? 1}/{ProtocolInfo.MaxPlayers}|{Plugin.Version}|{(string.IsNullOrEmpty(Plugin.CfgHostPassword.Value) ? 0 : 1)}";
            Begin(CoopSession.StartHost(Condition(t), Config(true), () => Now), CoopMode.HostLan);
        }
        catch (Exception e) { Fail(e.Message); }
    }

    public static void HostSteam()
    {
        if (Active || Busy) return;
        if (!SteamReady) { Fail("Steam is not available: " + SteamPlatform.InitError); return; }
        try
        {
            var t = SteamTransport.Host();
            Begin(CoopSession.StartHost(Condition(t), Config(true), () => Now), CoopMode.HostSteam);
            Busy = true;
            _busySince = Now;
            SteamPlatform.CreateLobby(LocalName, Plugin.Version, (ok, info) =>
            {
                Busy = false;
                if (ok) Plugin.L.LogInfo("Steam lobby created");
                else LastError = info + " (friends can still join from your Steam profile if the lobby appears later)";
            });
        }
        catch (Exception e) { Fail(e.Message); }
    }

    // ---------------- join ----------------

    /// <summary>"192.168.1.5" or "192.168.1.5:7777" (the default port without one).</summary>
    public static void JoinAddress(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) { Fail("Enter the host's IP address."); return; }
        Plugin.CfgLastAddress.Value = text;
        string host = text;
        int port = Plugin.CfgPort.Value;
        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text.Substring(colon + 1), out var p)) { host = text.Substring(0, colon); port = p; }
        JoinLan(host, port);
    }

    public static void JoinLan(string host, int port)
    {
        if (Active) return;
        try
        {
            var t = LiteNetTransport.Connect(host, port);
            t.Log = m => Plugin.L.LogWarning(m);
            Begin(CoopSession.StartClient(Condition(t), Config(false), () => Now), CoopMode.Client);
        }
        catch (Exception e) { Fail(e.Message); }
    }

    public static void JoinSteamHost(CSteamID host)
    {
        Busy = false;
        if (Active)
        {
            Plugin.L.LogWarning("Ignoring Steam join request: already in a session.");
            return;
        }
        if (!SteamReady) { Fail("Steam is not available."); return; }
        try { Begin(CoopSession.StartClient(Condition(SteamTransport.Connect(host)), Config(false), () => Now), CoopMode.Client); }
        catch (Exception e) { Fail(e.Message); }
    }

    /// <summary>A friend's Steam lobby: the session starts when the lobby is entered (JoinSteamHost).</summary>
    public static void JoinFriendLobby(CSteamID lobby)
    {
        if (Active || !SteamReady) return;
        LastError = "";
        Busy = true;
        _busySince = Now;
        SteamPlatform.JoinLobby(lobby);
    }

    private static double _busySince;

    public static void StartLanDiscovery()
    {
        if (Discovering) return;
        Discovering = true;
        _discoverTask = Task.Run(() => LiteNetTransport.DiscoverAsync(Plugin.CfgPort.Value, 800));
    }

    // ---------------- lifecycle ----------------

    /// <summary>Current session's network layer: imitated latency/loss for testing, traffic counters.</summary>
    public static ConditionedTransport Net { get; private set; }

    private static ITransport Condition(ITransport t)
    {
        Net = new ConditionedTransport(t);
        Net.Conditions.PingMs = Plugin.CfgSimPing.Value;
        Net.Conditions.JitterMs = Plugin.CfgSimJitter.Value;
        Net.Conditions.LossPercent = Plugin.CfgSimLoss.Value;
        // Test launches (scripts/) set "ping,jitter,loss" here so dev builds never run under sterile LAN conditions.
        var env = Environment.GetEnvironmentVariable("AFCOOP_SIMNET");
        if (!string.IsNullOrWhiteSpace(env))
        {
            var v = env.Split(',');
            if (env.Trim() == "off") { Net.Conditions.PingMs = 0; Net.Conditions.JitterMs = 0; Net.Conditions.LossPercent = 0; }
            else
            {
                if (v.Length > 0 && int.TryParse(v[0], out var ping)) Net.Conditions.PingMs = ping;
                if (v.Length > 1 && int.TryParse(v[1], out var jit)) Net.Conditions.JitterMs = jit;
                if (v.Length > 2 && double.TryParse(v[2], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var loss)) Net.Conditions.LossPercent = loss;
            }
        }
        if (Net.Conditions.Active) Plugin.L.LogWarning("Network simulation on: " + Net.Conditions);
        return Net;
    }

    private static void Begin(CoopSession s, CoopMode mode)
    {
        LastError = "";
        Session = s;
        Mode = mode;
        s.Log += m => Plugin.L.LogInfo("[Session] " + m);
        if (s.IsHost) s.IsBanned = BanList.IsBanned;
        s.Closed += reason =>
        {
            LastError = reason;
            Plugin.L.LogInfo("Session closed: " + reason);
        };
        s.PlayerJoined += p => { AuctionSync.OnPlayerJoined(p.Id); Hud.Notify($"{p.Name} joined"); Puppets.OnInfo(p); FxPrewarm.Enqueue(p.FxPrewarm); };
        s.PlayerInfoChanged += p => { Puppets.OnInfo(p); FxPrewarm.Enqueue(p.FxPrewarm); GroupFrames.OnInfo(p); };
        s.PlayerLeft += p => { Hud.Notify($"{p.Name} left"); Puppets.Remove(p.Id); Battlegrounds.OnPlayerLeft(p.Id); MobSync.OnPlayerLeft(p.Id); Trade.OnPlayerLeft(p.Id); ShellStats.OnPlayerLeft(p.Id); Duel.OnPlayerLeft(p.Id); StateSync.OnPlayerLeft(p.Id); };
        s.StateReceived += m => Puppets.OnState(m, Now);
        s.ChatReceived += (id, text) =>
        {
            if (id == s.LocalId) return; // the game already shows our own line
            var p = s.Players.TryGetValue(id, out var info) ? info : null;
            string name = p == null ? "#" + id : (string.IsNullOrEmpty(p.CharacterName) ? p.Name : p.CharacterName);
            ChatBridge.Incoming(id, name, text);
        };
        s.FxReceived += FxSync.Receive;
        s.WorldReceived += MobSync.Receive;
        TrafficView.Wire(s);
        s.LeaseChanged += MobSync.OnLeaseChanged;
        Groups.Wire(s);
        GroupFinder.Wire(s);
        s.PauseChanged += (paused, reason) =>
            Hud.Notify(paused ? (reason == PauseReason.Host ? "Host paused the game" : "Everyone is in the menu: paused") : "Game resumed");
        PlayerSync.Reset();
        Plugin.L.LogInfo($"Session started: {mode} via {s.Transport}");
    }

    public static void Leave()
    {
        var s = Session;
        Trade.Reset(); // while the session (names) and the shells are still there
        Session = null;
        Mode = CoopMode.Off;
        Busy = false;
        Puppets.Clear();
        VisualMirror.Clear();
        MobSync.Reset();
        PlayerShells.Clear();
        ShellStats.Reset();
        Dungeons.Reset();
        WorldClock.Reset();
        AuctionSync.Reset();
        try { s?.Dispose(); } catch (Exception e) { Plugin.L.LogWarning(e.Message); }
        if (SteamPlatform.Initialized) SteamPlatform.LeaveLobby();
    }

    private static void Fail(string msg)
    {
        LastError = msg;
        Busy = false;
        Plugin.L.LogWarning(msg);
    }

    private static bool _wasInWorld;

    public static void Update()
    {
        if (_steamTried) SteamPlatform.RunCallbacks();

        if (_discoverTask != null && _discoverTask.IsCompleted)
        {
            LanHosts = _discoverTask.Status == TaskStatus.RanToCompletion ? _discoverTask.Result : new();
            _discoverTask = null;
            Discovering = false;
        }

        // Hosting and joining are about one's world: back in the main menu means out of the session (for the host:
        // hosting stops). Loading screens between scenes keep it.
        UpdateEnterWorld();
        bool inWorld = GameInfo.InWorld;
        if (inWorld && PendingHost != CoopMode.Off && !Active) StartPendingHost();
        if (inWorld && _pendingJoin != null && !Active && !Busy) StartPendingJoin();
        // A Steam lobby that never answers: give up instead of leaving the player in a world nobody shares.
        if (Busy && !Active && Mode == CoopMode.Off && _busySince > 0 && Now - _busySince > 15)
        {
            Busy = false; _busySince = 0;
            LastError = "The Steam lobby did not answer.";
            Hud.Notify("Could not join: " + LastError);
            if (inWorld) { _wasInWorld = false; PauseController.BackToMainMenu(); }
            else if (_enterWorld != null) { _enterWorld = null; }
        }
        // A lobby Steam never makes: hosting goes on (friends join from the Steam profile), only the wait ends.
        if (Busy && Mode == CoopMode.HostSteam && Now - _busySince > 15)
        {
            Busy = false;
            LastError = "Steam did not create the lobby (friends can still join from your Steam profile)";
        }
        if (inWorld) _wasInWorld = true;
        else if (_wasInWorld && GameInfo.SceneName == "MainMenu")
        {
            _wasInWorld = false;
            if (Session != null)
            {
                bool host = Mode != CoopMode.Client;
                Leave();
                Hud.Notify(host ? "Hosting stopped (main menu)" : "Left co-op (main menu)");
                Plugin.L.LogInfo("Back in the main menu: session closed");
            }
        }

        if (Session != null)
        {
            Prof.Run("session.tick", Session.Tick);
            if (Session.State == SessionState.Closed)
            {
                var reason = Session.Status;
                bool client = Mode == CoopMode.Client;
                Leave();
                LastError = reason;
                Hud.Notify("Co-op ended: " + reason);
                // A client's world is the host's: without the host it ends, back to the main menu (saved, as the game does).
                if (client && GameInfo.InWorld)
                {
                    _wasInWorld = false;
                    PauseController.BackToMainMenu();
                }
            }
        }

        Prof.Run("pause", PauseController.Update);
        if (Connected) Prof.Run("playersync", () => PlayerSync.Update(Session, Now));
        Prof.Run("puppets.update", () => Puppets.Update(Now));
        Prof.Run("fx.play", FxSync.Update);
        if (Connected) Prof.Run("mirror", VisualMirror.Update);
        if (Connected) Prof.Run("groups", Groups.Update);
        if (Connected) Prof.Run("finder", GroupFinder.Update);
        if (Connected) Prof.Run("states", StateSync.Update);
        if (Connected) Prof.Run("shell stats", ShellStats.Update);
        if (Connected) Prof.Run("battlegrounds", Battlegrounds.Update);
        if (Connected) Prof.Run("clock", WorldClock.Update);
        if (Connected) Prof.Run("weather", WorldClock.FollowEcosystems);
        if (Connected) Prof.Run("trade", Trade.Update);
        if (Connected) Prof.Run("loot", GroupLoot.Update);
        if (Connected) Prof.Run("meter", MeterSync.Update);
        if (Connected) Prof.Run("friends", Friends.Update);
        if (Connected) Prof.Run("auction", AuctionSync.Update);
        if (Connected) Prof.Run("corruption", Corruption.Update);
        if (Connected) Prof.Run("challenges", Challenges.Update);
        Prof.Run("rules", WorldRules.Update);
        if (Connected) Prof.Run("world quests", WorldQuests.Update);
        if (Connected) Prof.Run("houses", Housing.Update);
        if (Connected) Prof.Run("boss phases", BossPhases.Update);
        Prof.Run("flights", FlightVisual.Update);
        Prof.Run("player actions", PlayerActions.Update);
        if (Connected) Prof.Run("inspect", Inspect.Update);
        if (Connected) Prof.Run("dungeons", Dungeons.Update);
        if (Connected) Prof.Run("mounts", MountVisual.Tick);
        Prof.Run("group.panel", GroupPanel.Update);
        Prof.Run("mobs", () => MobSync.Update(Now));
        Prof.Run("fx.prewarm", FxPrewarm.Update);
        if (Connected && GameInfo.InWorld) { AssetIndex.BuildIfNeeded(); EntryIndex.BuildIfNeeded(); }
        Prof.Run("menu", MainMenuUI.Update);
        Prof.Run("hud", Hud.Update);
#if DEBUG // F6/F7/F8 and the test scripts' command file
        Prof.Run("devtools", DevTools.Update);
        Prof.Run("devbridge", DevBridge.Update);
#endif
    }

    public static void LateUpdate()
    {
        PauseController.LateUpdate();
        Prof.Run("puppets", () => Puppets.LateUpdate(Now));
        Prof.Run("mirror.late", VisualMirror.LateUpdate);
        Prof.Run("mobs.late", () => MobSync.LateUpdate(Now));
        Prof.Run("shells", PlayerShells.LateUpdate);
        Prof.Run("riders", PlayerActions.LateUpdate);
        Prof.Run("flights.late", FlightVisual.LateUpdate);
        Prof.Run("hud.labels", Hud.LateUpdate);
        Prof.EndFrame();
    }

    public static void Shutdown()
    {
        Leave();
        SteamPlatform.Shutdown();
    }

    /// <summary>Local IPv4 addresses worth telling a friend (LAN, Radmin 26.x, ZeroTier, Hamachi 25.x, Tailscale 100.x).</summary>
    public static List<string> LocalAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                        list.Add(ua.Address.ToString());
            }
        }
        catch { /* ignore */ }
        return list.Distinct().ToList();
    }
}
