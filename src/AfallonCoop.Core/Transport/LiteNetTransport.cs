using System.Net;
using System.Text;
using LiteNetLib;

namespace AfallonCoop.Transport;

/// <summary>
/// UDP transport over LiteNetLib: LAN, direct IP, and virtual LANs (Radmin VPN, ZeroTier, Hamachi, Tailscale).
/// </summary>
public sealed class LiteNetTransport : ITransport
{
    public const int DefaultPort = 27555;
    private const string ConnectKey = "AfallonCoop/1";
    private static readonly byte[] DiscoverProbe = Encoding.ASCII.GetBytes("AFCOOP?");
    private const string DiscoverReplyPrefix = "AFCOOP!";

    private readonly EventBasedNetListener _listener = new();
    private readonly NetManager _net;
    private readonly Dictionary<int, NetPeer> _peers = new();
    private NetPeer _server;

    /// <summary>Host only: text returned to LAN discovery probes (game name, player count).</summary>
    public Func<string> DiscoveryInfo { get; set; }

    public bool IsServer { get; }
    public string Description { get; }
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    private LiteNetTransport(bool server, string description)
    {
        IsServer = server;
        Description = description;
        _net = new NetManager(_listener)
        {
            AutoRecycle = true,
            UnconnectedMessagesEnabled = true,
            BroadcastReceiveEnabled = server,
            DisconnectTimeout = 10000,
            UpdateTime = 15,
        };
        _listener.ConnectionRequestEvent += req =>
        {
            if (IsServer) req.AcceptIfKey(ConnectKey); else req.Reject();
        };
        _listener.PeerConnectedEvent += peer =>
        {
            if (IsServer) { _peers[peer.Id + 1] = peer; Connected?.Invoke(peer.Id + 1); }
            else { _server = peer; Connected?.Invoke(ITransport.ServerConnection); }
        };
        _listener.PeerDisconnectedEvent += (peer, info) =>
        {
            string reason = info.Reason.ToString();
            if (info.AdditionalData != null && info.AdditionalData.AvailableBytes > 0)
            {
                try { reason = info.AdditionalData.GetString(); } catch { /* keep enum reason */ }
            }
            if (IsServer) { _peers.Remove(peer.Id + 1); Disconnected?.Invoke(peer.Id + 1, reason); }
            else { _server = null; Disconnected?.Invoke(ITransport.ServerConnection, reason); }
        };
        _listener.NetworkReceiveEvent += (peer, reader, channel, method) =>
        {
            var data = reader.GetRemainingBytes();
            Received?.Invoke(IsServer ? peer.Id + 1 : ITransport.ServerConnection, data);
        };
        _listener.NetworkReceiveUnconnectedEvent += (endPoint, reader, type) =>
        {
            var bytes = reader.GetRemainingBytes();
            if (IsServer && bytes.AsSpan().SequenceEqual(DiscoverProbe))
            {
                var reply = Encoding.UTF8.GetBytes(DiscoverReplyPrefix + (DiscoveryInfo?.Invoke() ?? ""));
                _net.SendUnconnectedMessage(reply, endPoint);
            }
        };
    }

    // Connection ids on the host are LiteNetLib peer ids + 1, so 0 stays free for "the server" on clients.

    public static LiteNetTransport Host(int port)
    {
        var t = new LiteNetTransport(true, $"LAN/IP host :{port}");
        if (!t._net.Start(port))
            throw new InvalidOperationException($"Port {port} is busy. Close the other game copy or pick another port.");
        return t;
    }

    public static LiteNetTransport Connect(string host, int port)
    {
        var t = new LiteNetTransport(false, $"LAN/IP {host}:{port}");
        if (!t._net.Start()) throw new InvalidOperationException("Could not open a UDP socket.");
        t._net.Connect(host, port, ConnectKey);
        return t;
    }

    public void Poll() => _net.PollEvents();

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        var peer = IsServer ? (_peers.TryGetValue(connection, out var p) ? p : null) : _server;
        if (peer == null) return;
        var method = delivery switch
        {
            Delivery.Reliable => DeliveryMethod.ReliableOrdered,
            Delivery.Unreliable => DeliveryMethod.Sequenced,
            _ => DeliveryMethod.Unreliable,
        };
        if (method != DeliveryMethod.ReliableOrdered && data.Length > peer.GetMaxSinglePacketSize(method))
        {
            // A bug on the sender's side (packet budget), not a network fault: drop it, say so once.
            if (!_warnedTooBig) { _warnedTooBig = true; Log?.Invoke($"Dropped an unreliable packet of {data.Length} bytes (max {peer.GetMaxSinglePacketSize(method)})"); }
            return;
        }
        peer.Send(data, 0, method);
    }

    private bool _warnedTooBig;

    /// <summary>Diagnostics (oversized packets...).</summary>
    public Action<string> Log;

    public void Disconnect(int connection, string reason)
    {
        var peer = IsServer ? (_peers.TryGetValue(connection, out var p) ? p : null) : _server;
        if (peer == null) return;
        var w = new LiteNetLib.Utils.NetDataWriter();
        w.Put(reason ?? "");
        peer.Disconnect(w);
    }

    public int GetPing(int connection)
    {
        var peer = IsServer ? (_peers.TryGetValue(connection, out var p) ? p : null) : _server;
        return peer?.Ping ?? -1;
    }

    public void Dispose() => _net.Stop(true);

    /// <summary>A host found by LAN broadcast.</summary>
    public sealed record LanHost(string Address, int Port, string Info);

    /// <summary>Broadcasts a probe on the LAN and collects replies for <paramref name="waitMs"/>.</summary>
    public static async Task<List<LanHost>> DiscoverAsync(int port = DefaultPort, int waitMs = 700)
    {
        var found = new List<LanHost>();
        var listener = new EventBasedNetListener();
        var net = new NetManager(listener) { UnconnectedMessagesEnabled = true, BroadcastReceiveEnabled = false };
        listener.NetworkReceiveUnconnectedEvent += (ep, reader, type) =>
        {
            var s = Encoding.UTF8.GetString(reader.GetRemainingBytes());
            if (!s.StartsWith(DiscoverReplyPrefix)) return;
            if (found.Any(f => f.Address == ep.Address.ToString() && f.Port == ep.Port)) return;
            found.Add(new LanHost(ep.Address.ToString(), ep.Port, s.Substring(DiscoverReplyPrefix.Length)));
        };
        if (!net.Start()) return found;
        try
        {
            net.SendBroadcast(DiscoverProbe, port);
            // Also probe localhost, so a second game copy on the same PC finds the first one.
            net.SendUnconnectedMessage(DiscoverProbe, new IPEndPoint(IPAddress.Loopback, port));
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < until)
            {
                net.PollEvents();
                await Task.Delay(30).ConfigureAwait(false);
            }
        }
        finally { net.Stop(); }
        return found;
    }
}
