using System.Runtime.CompilerServices;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Dsp;

/// <summary>
/// A three-band equaliser built from two one-pole low-passes at the band crossovers: low = LP₈₀₀(x),
/// mid = LP₈ₖ(x) − LP₈₀₀(x), high = x − LP₈ₖ(x). The bands sum back to the input exactly, so unit gains are transparent.
/// Gains ramp linearly. Used for air absorption, directivity and (later) occlusion and materials.
/// </summary>
internal struct BandEq3
{
    private float _lp1, _lp2;  // filter states
    private float _a1, _a2;    // one-pole coefficients
    public Ramp Low, Mid, High;

    public void Init(int sampleRate)
    {
        _a1 = 1 - MathF.Exp(-2 * MathF.PI * Bands.LowCrossover / sampleRate);
        _a2 = 1 - MathF.Exp(-2 * MathF.PI * Bands.HighCrossover / sampleRate);
        _lp1 = _lp2 = 0;
        Low = Mid = High = new Ramp { Current = 1, Target = 1 };
    }

    public readonly bool IsFlat => Low.Remaining == 0 && Mid.Remaining == 0 && High.Remaining == 0 && Low.Current == 1 && Mid.Current == 1 && High.Current == 1;

    public void Set(float low, float mid, float high, int ramp)
    {
        Low.Set(low, ramp); Mid.Set(mid, ramp); High.Set(high, ramp);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        _lp1 += _a1 * (x - _lp1);
        _lp2 += _a2 * (x - _lp2);
        float low = _lp1, mid = _lp2 - _lp1, high = x - _lp2;
        return low * Low.Next() + mid * Mid.Next() + high * High.Next();
    }
}
