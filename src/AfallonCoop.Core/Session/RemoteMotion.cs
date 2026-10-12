using AfallonCoop.Protocol;

namespace AfallonCoop.Session;

public struct V3
{
    public float X, Y, Z;
    public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
    public static V3 Lerp(V3 a, V3 b, float t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t);
    public override string ToString() => $"({X:0.00}, {Y:0.00}, {Z:0.00})";
}

/// <summary>
/// Snapshot interpolation for one remote player. Renders the past (by <see cref="Delay"/>) so there are always two
/// samples to blend between; short gaps are extrapolated from velocity, longer ones hold the last pose.
/// Sender timestamps are mapped to the local clock with a min-latency offset estimate, so jitter does not cause stutter.
/// </summary>
public sealed class RemoteMotion
{
    public double Delay = 0.12;           // seconds behind the sender; > send interval (1/15 s) + typical jitter
    public double MaxExtrapolation = 0.25;
    public const int Capacity = 32;

    private readonly List<PlayerStateMsg> _buf = new(Capacity);
    private double _offset = double.NaN;  // localTime - senderTime, minimum seen (drifts up slowly)

    public PlayerStateMsg Latest { get; private set; }
    public int Count => _buf.Count;

    public void Add(PlayerStateMsg m, double localNow)
    {
        if (Latest != null && m.Seq <= Latest.Seq && Latest.Seq - m.Seq < uint.MaxValue / 2) return; // old or duplicate
        double o = localNow - m.SenderTime;
        if (double.IsNaN(_offset) || o < _offset) _offset = o;
        else _offset += (o - _offset) * 0.02; // clock drift and route changes

        Latest = m;
        _buf.Add(m);
        if (_buf.Count > Capacity) _buf.RemoveAt(0);
    }

    /// <summary>
    /// The sender's time of the pose shown at <paramref name="localNow"/> (what this machine sees of them now), on the
    /// sender's own clock; NaN before the first sample. Lag compensation: the sender checks a hit against where it was then.
    /// </summary>
    public double ViewTime(double localNow) => double.IsNaN(_offset) ? double.NaN : localNow - _offset - Delay;

    /// <summary>Pose to show at <paramref name="localNow"/>. False until the first sample arrives.</summary>
    public bool Sample(double localNow, out V3 pos, out float rotY)
    {
        pos = default; rotY = 0;
        if (_buf.Count == 0) return false;
        double t = localNow - _offset - Delay; // in sender time

        var first = _buf[0];
        if (t <= first.SenderTime) { pos = P(first); rotY = first.RotY; return true; }

        for (int i = _buf.Count - 1; i > 0; i--)
        {
            var a = _buf[i - 1];
            var b = _buf[i];
            if (t >= a.SenderTime && t <= b.SenderTime)
            {
                double span = b.SenderTime - a.SenderTime;
                float k = span <= 1e-6 ? 1f : (float)((t - a.SenderTime) / span);
                pos = V3.Lerp(P(a), P(b), k);
                rotY = LerpAngle(a.RotY, b.RotY, k);
                return true;
            }
        }

        // Past the newest sample: extrapolate briefly along velocity, then hold.
        var last = _buf[^1];
        float dt = (float)Math.Min(t - last.SenderTime, MaxExtrapolation);
        pos = new V3(last.PosX + last.VelX * dt, last.PosY + last.VelY * dt, last.PosZ + last.VelZ * dt);
        rotY = last.RotY;
        return true;
    }

    private static V3 P(PlayerStateMsg m) => new(m.PosX, m.PosY, m.PosZ);

    public static float LerpAngle(float a, float b, float t)
    {
        float d = ((b - a) % 360f + 540f) % 360f - 180f;
        return a + d * t;
    }
}
