namespace AfallonCoop.Transport;

/// <summary>In-memory transport for tests: a hub plays the host, each client gets its own endpoint.</summary>
public sealed class LoopbackHub
{
    internal readonly LoopbackTransport Server;
    private readonly List<LoopbackTransport> _clients = new();
    private int _nextId = 1;

    public LoopbackHub() { Server = new LoopbackTransport(this, isServer: true, 0); }

    public ITransport Host => Server;

    public ITransport Connect()
    {
        var c = new LoopbackTransport(this, isServer: false, _nextId++);
        _clients.Add(c);
        c.Enqueue(() => c.RaiseConnected(ITransport.ServerConnection));
        Server.Enqueue(() => Server.RaiseConnected(c.Id));
        return c;
    }

    internal LoopbackTransport Client(int id) => _clients.FirstOrDefault(c => c.Id == id && !c.Closed);

    internal void Drop(LoopbackTransport client, string reason)
    {
        if (client.Closed) return;
        client.Closed = true;
        client.Enqueue(() => client.RaiseDisconnected(ITransport.ServerConnection, reason));
        Server.Enqueue(() => Server.RaiseDisconnected(client.Id, reason));
    }
}

public sealed class LoopbackTransport : ITransport
{
    private readonly LoopbackHub _hub;
    private readonly Queue<Action> _inbox = new();
    internal readonly int Id;
    internal bool Closed;

    internal LoopbackTransport(LoopbackHub hub, bool isServer, int id) { _hub = hub; IsServer = isServer; Id = id; }

    public bool IsServer { get; }
    public string Description => IsServer ? "loopback host" : $"loopback client {Id}";
    public event Action<int> Connected;
    public event Action<int, string> Disconnected;
    public event Action<int, ReadOnlyMemory<byte>> Received;

    internal void Enqueue(Action a) => _inbox.Enqueue(a);
    internal void RaiseConnected(int c) => Connected?.Invoke(c);
    internal void RaiseDisconnected(int c, string r) => Disconnected?.Invoke(c, r);

    public void Poll()
    {
        int n = _inbox.Count; // only what was queued before this poll, like a real network tick
        while (n-- > 0) _inbox.Dequeue()();
    }

    public void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery)
    {
        if (Closed) return;
        var copy = data.ToArray();
        if (IsServer)
        {
            var c = _hub.Client(connection);
            c?.Enqueue(() => c.Received?.Invoke(ITransport.ServerConnection, copy));
        }
        else
        {
            var s = _hub.Server;
            int from = Id;
            s.Enqueue(() => s.Received?.Invoke(from, copy));
        }
    }

    public void Disconnect(int connection, string reason)
    {
        if (IsServer) { var c = _hub.Client(connection); if (c != null) _hub.Drop(c, reason); }
        else _hub.Drop(this, reason);
    }

    public int GetPing(int connection) => 0;
    public void Dispose() { if (!IsServer) _hub.Drop(this, "disposed"); }
}
