namespace AfallonCoop.Protocol;

/// <summary>
/// Sender side of animator parameter compression. Only parameters that changed are sent; every change is repeated in
/// the next <see cref="Redundancy"/> packets (streams are unreliable, a single loss must not lose a change); a full
/// keyframe goes out every <see cref="KeyframeInterval"/> seconds so a receiver that missed more catches up quickly.
/// </summary>
public sealed class AnimDeltaEncoder
{
    public const int Redundancy = 3;
    public double KeyframeInterval = 1.0;
    public float Epsilon = 0.01f;

    private readonly Dictionary<int, (float value, int resend)> _sent = new();
    private double _nextKeyframe = double.NegativeInfinity;
    private readonly double _phase;

    /// <param name="phase">0..1: shifts this encoder's keyframes within the interval, so many encoders
    /// (one per mob) do not all send a full set in the same tick.</param>
    public AnimDeltaEncoder(double phase = 0) { _phase = phase; }

    public void Reset() { _sent.Clear(); _nextKeyframe = double.NegativeInfinity; }

    /// <summary>Reduce a full parameter sample to what must be sent now. Returns true when it is a keyframe.</summary>
    public bool Encode(double now, List<AnimParam> all, List<AnimParam> toSend)
    {
        toSend.Clear();
        bool key = now >= _nextKeyframe;
        if (key) _nextKeyframe = now + KeyframeInterval * (double.IsNegativeInfinity(_nextKeyframe) ? 1 + _phase : 1);
        foreach (var p in all)
        {
            bool changed = !_sent.TryGetValue(p.NameHash, out var s) || Math.Abs(s.value - p.Value) > Epsilon;
            int resend = changed ? Redundancy : s.resend;
            if (key || resend > 0) toSend.Add(p);
            _sent[p.NameHash] = (changed ? p.Value : s.value, Math.Max(0, resend - 1));
        }
        return key;
    }
}

/// <summary>Receiver side: the full current parameter set, updated by deltas and keyframes.</summary>
public sealed class AnimParamState
{
    private readonly Dictionary<int, int> _index = new();
    public readonly List<AnimParam> Current = new();

    public void Apply(List<AnimParam> received, bool keyframe)
    {
        if (keyframe) { _index.Clear(); Current.Clear(); }
        foreach (var p in received)
        {
            if (_index.TryGetValue(p.NameHash, out int i)) Current[i] = p;
            else { _index[p.NameHash] = Current.Count; Current.Add(p); }
        }
    }

    public void Clear() { _index.Clear(); Current.Clear(); }
}
