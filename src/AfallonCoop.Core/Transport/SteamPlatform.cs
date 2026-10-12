using Steamworks;

namespace AfallonCoop.Transport;

/// <summary>
/// Steam glue: init, callback pump, lobbies (friends-only, max 5) and join requests.
/// Steam is optional: if it fails to start, LAN/IP still works.
/// </summary>
public static class SteamPlatform
{
    public const string LobbyTag = "afcoop";

    public static bool Initialized { get; private set; }
    public static string InitError { get; private set; } = "";
    public static uint AppId { get; private set; }

    public static CSteamID CurrentLobby { get; private set; } = CSteamID.Nil;

    /// <summary>A friend accepted an invite / clicked "Join game", or we entered a friend's lobby. Arg = host SteamID.</summary>
    public static event Action<CSteamID> JoinHostRequested;
    public static event Action<string> Log;

    private static Callback<GameLobbyJoinRequested_t> _lobbyJoinReq;
    private static Callback<GameRichPresenceJoinRequested_t> _rpJoinReq;
    private static Callback<LobbyEnter_t> _lobbyEnter;
    private static CallResult<LobbyCreated_t> _lobbyCreated;
    private static Action<bool, string> _createDone;

    public static bool Init(uint appIdIfNotLaunchedBySteam)
    {
        if (Initialized) return true;
        try
        {
            // Launched through Steam: SteamAppId is already set to the game's id. Launched from the exe: provide one.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SteamAppId")))
            {
                Environment.SetEnvironmentVariable("SteamAppId", appIdIfNotLaunchedBySteam.ToString());
                Environment.SetEnvironmentVariable("SteamGameId", appIdIfNotLaunchedBySteam.ToString());
            }
            if (!SteamAPI.Init())
            {
                InitError = "SteamAPI.Init failed (is Steam running and do you own the game?)";
                return false;
            }
            Initialized = true;
            AppId = SteamUtils.GetAppID().m_AppId;
            _lobbyJoinReq = Callback<GameLobbyJoinRequested_t>.Create(cb => JoinLobby(cb.m_steamIDLobby));
            _rpJoinReq = Callback<GameRichPresenceJoinRequested_t>.Create(cb =>
            {
                var s = cb.m_rgchConnect ?? "";
                const string prefix = "+afcoop_lobby ";
                if (s.StartsWith(prefix) && ulong.TryParse(s.Substring(prefix.Length).Trim(), out var id))
                    JoinLobby(new CSteamID(id));
            });
            _lobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
            _lobbyCreated = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);
            Log?.Invoke($"Steam ready: {SteamFriends.GetPersonaName()} ({SteamUser.GetSteamID().m_SteamID}), appid {AppId}");
            return true;
        }
        catch (Exception e)
        {
            InitError = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }

    public static void RunCallbacks()
    {
        if (Initialized) SteamAPI.RunCallbacks();
    }

    public static void Shutdown()
    {
        if (!Initialized) return;
        LeaveLobby();
        SteamAPI.Shutdown();
        Initialized = false;
    }

    public static ulong MySteamId => Initialized ? SteamUser.GetSteamID().m_SteamID : 0;
    public static string MyName => Initialized ? SteamFriends.GetPersonaName() : Environment.UserName;

    // ---------- hosting ----------

    public static void CreateLobby(string hostName, string modVersion, Action<bool, string> done)
    {
        _createDone = (ok, info) =>
        {
            if (ok)
            {
                SteamMatchmaking.SetLobbyData(CurrentLobby, LobbyTag, "1");
                SteamMatchmaking.SetLobbyData(CurrentLobby, "host", MySteamId.ToString());
                SteamMatchmaking.SetLobbyData(CurrentLobby, "name", hostName);
                SteamMatchmaking.SetLobbyData(CurrentLobby, "ver", modVersion);
                SteamFriends.SetRichPresence("status", "Afallon Co-op");
                SteamFriends.SetRichPresence("connect", "+afcoop_lobby " + CurrentLobby.m_SteamID);
            }
            done(ok, info);
        };
        var call = SteamMatchmaking.CreateLobby(ELobbyType.k_ELobbyTypeFriendsOnly, Protocol.ProtocolInfo.MaxPlayers);
        _lobbyCreated.Set(call);
    }

    private static void OnLobbyCreated(LobbyCreated_t cb, bool ioFailure)
    {
        if (ioFailure || cb.m_eResult != EResult.k_EResultOK)
        {
            _createDone?.Invoke(false, "Lobby creation failed: " + (ioFailure ? "IO failure" : cb.m_eResult.ToString()));
            return;
        }
        CurrentLobby = new CSteamID(cb.m_ulSteamIDLobby);
        _createDone?.Invoke(true, "");
    }

    public static void LeaveLobby()
    {
        if (!Initialized) return;
        if (CurrentLobby != CSteamID.Nil) SteamMatchmaking.LeaveLobby(CurrentLobby);
        CurrentLobby = CSteamID.Nil;
        SteamFriends.ClearRichPresence();
    }

    // ---------- joining ----------

    public static void JoinLobby(CSteamID lobby)
    {
        if (!Initialized) return;
        Log?.Invoke($"Joining lobby {lobby.m_SteamID}");
        SteamMatchmaking.JoinLobby(lobby);
    }

    private static void OnLobbyEnter(LobbyEnter_t cb)
    {
        var lobby = new CSteamID(cb.m_ulSteamIDLobby);
        if (cb.m_EChatRoomEnterResponse != (uint)EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess)
        {
            Log?.Invoke($"Could not enter lobby: {(EChatRoomEnterResponse)cb.m_EChatRoomEnterResponse}");
            return;
        }
        CurrentLobby = lobby;
        var owner = SteamMatchmaking.GetLobbyOwner(lobby);
        if (owner == SteamUser.GetSteamID()) return; // our own lobby (host side)
        if (SteamMatchmaking.GetLobbyData(lobby, LobbyTag) != "1")
        {
            Log?.Invoke("That lobby is not an Afallon Co-op lobby.");
            SteamMatchmaking.LeaveLobby(lobby);
            CurrentLobby = CSteamID.Nil;
            return;
        }
        var hostStr = SteamMatchmaking.GetLobbyData(lobby, "host");
        var host = ulong.TryParse(hostStr, out var h) ? new CSteamID(h) : owner;
        JoinHostRequested?.Invoke(host);
    }

    public sealed record FriendHost(string Name, CSteamID Friend, CSteamID Lobby);

    /// <summary>Friends currently in a joinable lobby of this game.</summary>
    public static List<FriendHost> FriendsHosting()
    {
        var list = new List<FriendHost>();
        if (!Initialized) return list;
        int n = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
        for (int i = 0; i < n; i++)
        {
            var f = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
            if (!SteamFriends.GetFriendGamePlayed(f, out var info)) continue;
            if (info.m_gameID.AppID().m_AppId != AppId) continue;
            if (!info.m_steamIDLobby.IsValid()) continue;
            list.Add(new FriendHost(SteamFriends.GetFriendPersonaName(f), f, info.m_steamIDLobby));
        }
        return list;
    }
}
