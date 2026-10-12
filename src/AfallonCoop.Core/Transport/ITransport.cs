namespace AfallonCoop.Transport;

public enum Delivery : byte
{
    /// <summary>Reliable and ordered: events, handshakes, chat.</summary>
    Reliable,
    /// <summary>Unreliable, newest wins: movement and animation snapshots (one packet carries the whole state).</summary>
    Unreliable,
    /// <summary>Unreliable and unordered: streams split over several packets per tick (an older packet still matters).</summary>
    UnreliableUnordered,
}

/// <summary>
/// Message transport. The host side sees many connections; a client sees exactly one (id <see cref="ServerConnection"/>).
/// All events are raised from <see cref="Poll"/>, on the caller's thread (the Unity main thread in game).
/// </summary>
public interface ITransport : IDisposable
{
    const int ServerConnection = 0;

    bool IsServer { get; }
    string Description { get; }

    event Action<int> Connected;
    event Action<int, string> Disconnected;
    event Action<int, ReadOnlyMemory<byte>> Received;

    void Poll();
    void Send(int connection, ReadOnlySpan<byte> data, Delivery delivery);
    void Disconnect(int connection, string reason);
    /// <summary>Round-trip time in ms, or -1 when unknown.</summary>
    int GetPing(int connection);
}
