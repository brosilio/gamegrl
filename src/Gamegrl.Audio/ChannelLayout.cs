namespace Gamegrl.Audio;

/// <summary>Speaker positions, numbered as WAVEFORMATEXTENSIBLE's channel mask bits (a layout's channels come in this order).</summary>
public enum Speaker : byte
{
    FrontLeft, FrontRight, FrontCenter, Lfe, BackLeft, BackRight, FrontLeftCenter, FrontRightCenter, BackCenter,
    SideLeft, SideRight, TopCenter, TopFrontLeft, TopFrontCenter, TopFrontRight, TopBackLeft, TopBackCenter, TopBackRight,
}

/// <summary>
/// The speakers of a signal, in channel order. Channels are always ordered by <see cref="Speaker"/> (the WAV and SMPTE
/// order); device backends reorder for their hardware.
/// </summary>
public sealed class ChannelLayout
{
    public string Name { get; }
    public uint Mask { get; }
    private readonly Speaker[] _speakers;
    private readonly sbyte[] _index = new sbyte[18];

    private readonly int _count;

    public ReadOnlySpan<Speaker> Speakers => _speakers;
    public int Count => _count;
    /// <summary>An ambisonic sound field (AmbiX: ACN order, SN3D) rather than speaker feeds; see <see cref="Spatial.Ambisonics"/>.</summary>
    public bool IsAmbisonic { get; }

    private ChannelLayout(string name, int ambisonicChannels)
    {
        Name = name;
        _speakers = [];
        _index.AsSpan().Fill(-1);
        _count = ambisonicChannels;
        IsAmbisonic = true;
    }

    private ChannelLayout(string name, uint mask)
    {
        Name = name;
        Mask = mask;
        var list = new List<Speaker>();
        _index.AsSpan().Fill(-1);
        for (int bit = 0; bit < 18; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;
            _index[bit] = (sbyte)list.Count;
            list.Add((Speaker)bit);
        }
        _speakers = list.ToArray();
        _count = _speakers.Length;
    }

    private static uint Bits(params Speaker[] s) { uint m = 0; foreach (var x in s) m |= 1u << (int)x; return m; }

    public static readonly ChannelLayout Mono = new("mono", Bits(Speaker.FrontCenter));
    public static readonly ChannelLayout Stereo = new("stereo", Bits(Speaker.FrontLeft, Speaker.FrontRight));
    public static readonly ChannelLayout Quad = new("quad", Bits(Speaker.FrontLeft, Speaker.FrontRight, Speaker.BackLeft, Speaker.BackRight));
    public static readonly ChannelLayout Surround51 = new("5.1", Bits(Speaker.FrontLeft, Speaker.FrontRight, Speaker.FrontCenter, Speaker.Lfe, Speaker.BackLeft, Speaker.BackRight));
    public static readonly ChannelLayout Surround71 = new("7.1", Surround51.Mask | Bits(Speaker.SideLeft, Speaker.SideRight));
    public static readonly ChannelLayout Surround714 = new("7.1.4", Surround71.Mask | Bits(Speaker.TopFrontLeft, Speaker.TopFrontRight, Speaker.TopBackLeft, Speaker.TopBackRight));
    /// <summary>First-order ambisonics, AmbiX (W, Y, Z, X; SN3D).</summary>
    public static readonly ChannelLayout AmbisonicFirstOrder = new("FOA (AmbiX)", 4);

    /// <summary>The layout with exactly these speakers (a preset when one matches).</summary>
    public static ChannelLayout FromMask(uint mask)
    {
        foreach (var l in (ReadOnlySpan<ChannelLayout>)[Mono, Stereo, Quad, Surround51, Surround71, Surround714])
            if (l.Mask == mask) return l;
        if (mask == 0 || (mask >> 18) != 0) throw new ArgumentException($"Unsupported channel mask 0x{mask:x}");
        return new ChannelLayout($"mask 0x{mask:x}", mask);
    }

    /// <summary>The usual layout for a channel count with no mask (WAV's defaults).</summary>
    public static ChannelLayout ForChannels(int channels) => channels switch
    {
        1 => Mono, 2 => Stereo, 4 => Quad, 6 => Surround51, 8 => Surround71, 12 => Surround714,
        _ => throw new ArgumentException($"No default layout for {channels} channels"),
    };

    /// <summary>The channel carrying a speaker, or −1.</summary>
    public int IndexOf(Speaker s) => _index[(int)s];
    public bool Has(Speaker s) => _index[(int)s] >= 0;

    /// <summary>
    /// The gains that carry a signal in layout <paramref name="from"/> to <paramref name="to"/> (row per output channel,
    /// <c>m[o * from.Count + i]</c>). Shared speakers pass straight through; a missing one folds into its neighbours
    /// (ITU-R BS.775's downmix: centre into left and right at −3 dB each, surrounds into the fronts at −3 dB, heights into
    /// the ear-level speakers below them). LFE is dropped when the output has none.
    /// </summary>
    public static float[] MixMatrix(ChannelLayout from, ChannelLayout to)
    {
        if (to.IsAmbisonic) throw new ArgumentException("Can't mix speaker feeds into an ambisonic layout");
        if (from.IsAmbisonic) return Spatial.Ambisonics.DecodeMatrix(to, System.Numerics.Quaternion.Identity);
        var m = new float[to.Count * from.Count];
        Span<Speaker> targets = stackalloc Speaker[2];
        for (int i = 0; i < from.Count; i++)
        {
            var s = from._speakers[i];
            if (to.Has(s)) { m[to.IndexOf(s) * from.Count + i] = 1; continue; }
            int n = Fold(s, to, targets, out float g);
            for (int k = 0; k < n; k++) m[to.IndexOf(targets[k]) * from.Count + i] = g;
        }
        return m;
    }

    private const float Minus3dB = 0.70710678f;

    // Where a missing speaker goes: the first candidate (a pair, at −3 dB each, or a single speaker) the output has.
    // Fronts folding into a mono centre are −3 dB too, so a centred stereo signal keeps its power.
    private static int Fold(Speaker s, ChannelLayout to, Span<Speaker> t, out float gain)
    {
        gain = 1;
        int Pair(Speaker a, Speaker b, Span<Speaker> t) { if (to.Has(a) && to.Has(b)) { t[0] = a; t[1] = b; return 2; } return 0; }
        int One(Speaker a, Span<Speaker> t) { if (to.Has(a)) { t[0] = a; return 1; } return 0; }
        int n;
        switch (s)
        {
            case Speaker.Lfe: return 0;
            case Speaker.FrontCenter: case Speaker.TopCenter: n = Pair(Speaker.FrontLeft, Speaker.FrontRight, t); break;
            case Speaker.FrontLeft: case Speaker.FrontRight: n = One(Speaker.FrontCenter, t); gain = Minus3dB; return n;
            case Speaker.FrontLeftCenter: case Speaker.TopFrontLeft: n = One(Speaker.FrontLeft, t); if (n == 0) { n = One(Speaker.FrontCenter, t); gain = Minus3dB; } return n;
            case Speaker.FrontRightCenter: case Speaker.TopFrontRight: n = One(Speaker.FrontRight, t); if (n == 0) { n = One(Speaker.FrontCenter, t); gain = Minus3dB; } return n;
            case Speaker.TopFrontCenter: n = One(Speaker.FrontCenter, t); if (n == 0) n = Pair(Speaker.FrontLeft, Speaker.FrontRight, t); break;
            case Speaker.BackLeft: case Speaker.TopBackLeft: n = One(Speaker.SideLeft, t); if (n == 0) n = One(Speaker.BackLeft, t); if (n == 0) n = One(Speaker.FrontLeft, t); break;
            case Speaker.BackRight: case Speaker.TopBackRight: n = One(Speaker.SideRight, t); if (n == 0) n = One(Speaker.BackRight, t); if (n == 0) n = One(Speaker.FrontRight, t); break;
            case Speaker.SideLeft: n = One(Speaker.BackLeft, t); if (n == 0) n = One(Speaker.FrontLeft, t); break;
            case Speaker.SideRight: n = One(Speaker.BackRight, t); if (n == 0) n = One(Speaker.FrontRight, t); break;
            case Speaker.BackCenter: case Speaker.TopBackCenter:
                n = Pair(Speaker.BackLeft, Speaker.BackRight, t);
                if (n == 0) n = Pair(Speaker.SideLeft, Speaker.SideRight, t);
                if (n == 0) n = Pair(Speaker.FrontLeft, Speaker.FrontRight, t);
                break;
            default: return 0;
        }
        if (n == 0) { n = One(Speaker.FrontCenter, t); gain = Minus3dB; return n; } // a mono output takes everything at −3 dB
        if (n == 2) gain = Minus3dB;
        else if (s is Speaker.BackLeft or Speaker.BackRight or Speaker.SideLeft or Speaker.SideRight or Speaker.TopBackLeft or Speaker.TopBackRight) gain = Minus3dB;
        return n;
    }

    public override string ToString() => Name;
}
