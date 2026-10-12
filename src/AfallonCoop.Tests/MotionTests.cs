using AfallonCoop.Protocol;
using AfallonCoop.Session;
using Xunit;

namespace AfallonCoop.Tests;

public class MotionTests
{
    private static PlayerStateMsg S(uint seq, double t, float x, float vx = 0, float rot = 0)
        => new() { Seq = seq, SenderTime = t, PosX = x, VelX = vx, RotY = rot };

    [Fact]
    public void Smooth_motion_despite_jitter()
    {
        // Sender moves at 5 m/s, sends at 15 Hz; network delay jitters between 40 and 100 ms.
        var rm = new RemoteMotion();
        var rng = new Random(7);
        var arrivals = new List<(double at, PlayerStateMsg m)>();
        for (uint i = 0; i < 60; i++)
        {
            double t = 100 + i / 15.0;
            arrivals.Add((t + 0.04 + rng.NextDouble() * 0.06, S(i, t, (float)((t - 100) * 5), 5)));
        }
        arrivals.Sort((a, b) => a.at.CompareTo(b.at));

        int next = 0;
        float prev = float.NaN;
        var steps = new List<float>();
        for (double now = 100; now < 104; now += 1 / 60.0)
        {
            while (next < arrivals.Count && arrivals[next].at <= now) rm.Add(arrivals[next++].m, now);
            if (!rm.Sample(now, out var p, out _)) continue;
            if (!float.IsNaN(prev) && now > 100.5 && now < 103.5) steps.Add(p.X - prev);
            prev = p.X;
        }
        float expected = 5f / 60f;
        Assert.All(steps, s => Assert.InRange(s, expected * 0.4f, expected * 1.6f)); // no jumps, no reversals
    }

    [Fact]
    public void Out_of_order_and_duplicate_packets_are_ignored()
    {
        var rm = new RemoteMotion();
        rm.Add(S(2, 1.0, 2), 1.05);
        rm.Add(S(1, 0.9, 1), 1.06);
        rm.Add(S(2, 1.0, 2), 1.07);
        Assert.Equal(1, rm.Count);
        Assert.Equal(2u, rm.Latest.Seq);
    }

    [Fact]
    public void Extrapolation_is_capped_then_holds()
    {
        var rm = new RemoteMotion { Delay = 0 };
        rm.Add(S(1, 0, 0, vx: 4), 0);
        rm.Sample(10, out var p, out _);
        Assert.Equal(4 * 0.25f, p.X, 3);
    }

    [Fact]
    public void Rotation_takes_the_short_way_round()
    {
        Assert.Equal(360f, RemoteMotion.LerpAngle(350, 10, 0.5f), 3);
        Assert.Equal(180f, RemoteMotion.LerpAngle(170, 190, 0.5f), 3);
    }
}
