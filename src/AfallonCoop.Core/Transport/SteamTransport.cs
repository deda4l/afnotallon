using System.Runtime.InteropServices;
using Steamworks;

namespace AfallonCoop.Transport;

/// <summary>
/// Steam P2P over ISteamNetworkingSockets: NAT traversal and Valve's relay (SDR), no port forwarding.
/// Requires <see cref="SteamPlatform.Initialized"/> and SteamAPI.RunCallbacks() being pumped.
/// </summary>
public sealed class SteamTransport : ITransport
{
    private const int VirtualPort = 0;
    private const int MaxMessagesPerPoll = 256;

    private readonly HSteamListenSocket _listen = HSteamListenSocket.Invalid;
    private readonly HSteamNetPollGroup _group;
    private readonly Dictionary<int, HSteamNetConnection> _conns = new();
    private readonly Dictionary<uint, int> _ids = new();
    private readonly IntPtr[] _msgs = new IntPtr[MaxMessagesPerPoll];
    private readonly Callback<SteamNetConnectionStatusChangedCallback_t> _statusCb;
    private readonly Queue<Action> _pending = new();
    private int _nextId = 1;

    public bool IsServer { get; }
    public string Description { get; }
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    private SteamTransport(bool server, string description)
    {
        if (!SteamPlatform.Initialized) throw new InvalidOperationException("Steam is not running or failed to initialize.");
        IsServer = server;
        Description = description;
        SteamNetworkingUtils.InitRelayNetworkAccess();
        _group = SteamNetworkingSockets.CreatePollGroup();
        _statusCb = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnStatusChanged);
        if (server)
            _listen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, null);
    }

    public static SteamTransport Host() => new(true, "Steam host");

    public static SteamTransport Connect(CSteamID host)
    {
        var t = new SteamTransport(false, $"Steam -> {host.m_SteamID}");
        var identity = new SteamNetworkingIdentity();
        identity.SetSteamID(host);
        var conn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, null);
        t.Track(conn, ITransport.ServerConnection);
        return t;
    }

    private void Track(HSteamNetConnection conn, int id)
    {
        _conns[id] = conn;
        _ids[conn.m_HSteamNetConnection] = id;
        SteamNetworkingSockets.SetConnectionPollGroup(conn, _group);
    }

    private void OnStatusChanged(SteamNetConnectionStatusChangedCallback_t cb)
    {
        var conn = cb.m_hConn;
        var state = cb.m_info.m_eState;
        bool known = _ids.TryGetValue(conn.m_HSteamNetConnection, out int id);

        if (IsServer && !known && state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting
            && cb.m_info.m_hListenSocket == _listen)
        {
            // Only the host's Steam friends (as the friends-only lobby): anyone else who knows the host's id stays out.
            var who = cb.m_info.m_identityRemote.GetSteamID();
            if (!SteamFriends.HasFriend(who, EFriendFlags.k_EFriendFlagImmediate) && who != SteamUser.GetSteamID())
            {
                SteamNetworkingSockets.CloseConnection(conn, 0, "Only the host's Steam friends can join.", false);
                return;
            }
            if (SteamNetworkingSockets.AcceptConnection(conn) == EResult.k_EResultOK)
                Track(conn, _nextId++);
            else
                SteamNetworkingSockets.CloseConnection(conn, 0, "accept failed", false);
            return;
        }
        if (!known) return;

        switch (state)
        {
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected:
                _pending.Enqueue(() => Connected?.Invoke(id));
                break;
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer:
            case ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally:
                string reason = string.IsNullOrEmpty(cb.m_info.m_szEndDebug) ? state.ToString() : cb.m_info.m_szEndDebug;
                SteamNetworkingSockets.CloseConnection(conn, 0, null, false);
                _conns.Remove(id);
                _ids.Remove(conn.m_HSteamNetConnection);
                _pending.Enqueue(() => Disconnected?.Invoke(id, reason));
                break;
        }
    }

    public void Poll()
    {
        while (_pending.Count > 0) _pending.Dequeue()();
        while (true)
        {
            int n = SteamNetworkingSockets.ReceiveMessagesOnPollGroup(_group, _msgs, MaxMessagesPerPoll);
            if (n <= 0) break;
            for (int i = 0; i < n; i++)
            {
                var msg = SteamNetworkingMessage_t.FromIntPtr(_msgs[i]);
                try
                {
                    if (_ids.TryGetValue(msg.m_conn.m_HSteamNetConnection, out int id))
                    {
                        var data = new byte[msg.m_cbSize];
                        Marshal.Copy(msg.m_pData, data, 0, msg.m_cbSize);
                        Received?.Invoke(id, data);
                    }
                }
                finally { SteamNetworkingMessage_t.Release(_msgs[i]); }
            }
            if (n < MaxMessagesPerPoll) break;
        }
        while (_pending.Count > 0) _pending.Dequeue()();
    }

    public unsafe void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (!_conns.TryGetValue(connection, out var conn)) return;
        int flags = delivery == Delivery.Reliable
            ? Constants.k_nSteamNetworkingSend_Reliable
            : Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
        fixed (byte* p = data)
            SteamNetworkingSockets.SendMessageToConnection(conn, (IntPtr)p, (uint)data.Length, flags, out _);
    }

    public void Disconnect(int connection, string reason)
    {
        if (!_conns.TryGetValue(connection, out var conn)) return;
        SteamNetworkingSockets.CloseConnection(conn, 0, reason, true);
        _conns.Remove(connection);
        _ids.Remove(conn.m_HSteamNetConnection);
        _pending.Enqueue(() => Disconnected?.Invoke(connection, reason));
    }

    public int GetPing(int connection)
    {
        if (!_conns.TryGetValue(connection, out var conn)) return -1;
        var status = new SteamNetConnectionRealTimeStatus_t();
        var lanes = new SteamNetConnectionRealTimeLaneStatus_t();
        return SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref lanes) == EResult.k_EResultOK
            ? status.m_nPing : -1;
    }

    public void Dispose()
    {
        foreach (var c in _conns.Values) SteamNetworkingSockets.CloseConnection(c, 0, "shutdown", true);
        _conns.Clear();
        _ids.Clear();
        if (_listen != HSteamListenSocket.Invalid) SteamNetworkingSockets.CloseListenSocket(_listen);
        SteamNetworkingSockets.DestroyPollGroup(_group);
        _statusCb.Dispose();
    }
}
