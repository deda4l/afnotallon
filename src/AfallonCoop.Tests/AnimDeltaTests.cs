using AfallonCoop.Protocol;
using Xunit;

namespace AfallonCoop.Tests;

public class AnimDeltaTests
{
    private static List<AnimParam> Sample(float speed, float dir, bool grounded) => new()
    {
        new AnimParam(1, AnimParamType.Float, speed),
        new AnimParam(2, AnimParamType.Float, dir),
        new AnimParam(3, AnimParamType.Bool, grounded ? 1 : 0),
    };

    [Fact]
    public void Unchanged_parameters_stop_being_sent_after_the_redundant_repeats()
    {
        var enc = new AnimDeltaEncoder { KeyframeInterval = 100 };
        var out1 = new List<AnimParam>();
        Assert.True(enc.Encode(0, Sample(1, 0, true), out1)); // first call is a keyframe
        Assert.Equal(3, out1.Count);
        for (int i = 1; i < AnimDeltaEncoder.Redundancy; i++) { enc.Encode(i * 0.1, Sample(1, 0, true), out1); Assert.Equal(3, out1.Count); }
        enc.Encode(1, Sample(1, 0, true), out1);
        Assert.Empty(out1);
    }

    [Fact]
    public void A_change_is_sent_in_several_packets_so_one_loss_does_not_lose_it()
    {
        var enc = new AnimDeltaEncoder { KeyframeInterval = 100 };
        var tmp = new List<AnimParam>();
        for (int i = 0; i < 5; i++) enc.Encode(i * 0.1, Sample(1, 0, true), tmp);
        int sentWithChange = 0;
        for (int i = 5; i < 12; i++)
        {
            enc.Encode(i * 0.1, Sample(2, 0, true), tmp);
            if (tmp.Any(p => p.NameHash == 1 && p.Value == 2)) sentWithChange++;
            Assert.DoesNotContain(tmp, p => p.NameHash == 2);
        }
        Assert.Equal(AnimDeltaEncoder.Redundancy, sentWithChange);
    }

    [Fact]
    public void Keyframes_resend_everything_periodically()
    {
        var enc = new AnimDeltaEncoder { KeyframeInterval = 1.0 };
        var tmp = new List<AnimParam>();
        for (int i = 0; i < 9; i++) enc.Encode(i * 0.1, Sample(1, 0, true), tmp);
        Assert.Empty(tmp);
        Assert.True(enc.Encode(1.0, Sample(1, 0, true), tmp));
        Assert.Equal(3, tmp.Count);
    }

    [Fact]
    public void Receiver_state_follows_deltas_with_losses_and_keyframes()
    {
        var enc = new AnimDeltaEncoder { KeyframeInterval = 1.0 };
        var recv = new AnimParamState();
        var tmp = new List<AnimParam>();
        var rng = new Random(5);
        float speed = 0;
        for (int i = 0; i < 300; i++)
        {
            if (i % 7 == 0) speed = rng.Next(0, 5);
            bool key = enc.Encode(i * 0.1, Sample(speed, 0, true), tmp);
            if (rng.NextDouble() < 0.1) continue; // 10% loss
            recv.Apply(new List<AnimParam>(tmp), key);
            if (i % 7 >= AnimDeltaEncoder.Redundancy + 1) // a few packets after a change it must be there
                Assert.Equal(speed, recv.Current.First(p => p.NameHash == 1).Value);
        }
    }
}
