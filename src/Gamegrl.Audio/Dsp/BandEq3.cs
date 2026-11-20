using System.Runtime.CompilerServices;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Dsp;

/// <summary>
/// A three-band equaliser as a shelving cascade: the mid-band gain, times a low shelf at 800 Hz (low ÷ mid) and a high
/// shelf at 8 kHz (high ÷ mid), both second order (RBJ's cookbook, slope 1). It's minimum phase, so the bands never
/// cancel each other: DC gets exactly the low gain, the middle the mid gain, the top the high gain, with smooth
/// transitions at the crossovers (a band's gain ratio is clamped to ±80 dB). Gains ramp: the mid gain per sample, the
/// shelves' coefficients every 32 samples. Used for air absorption, occlusion and transmission, and (later) materials
/// on reflections.
/// </summary>
internal struct BandEq3
{
    private const int Step = 32;
    private Biquad _low, _high;
    private float _cosLow, _alphaLow, _cosHigh, _alphaHigh;
    public Ramp Low, Mid, High;

    public void Init(int sampleRate)
    {
        double wl = 2 * Math.PI * Bands.LowCrossover / sampleRate, wh = 2 * Math.PI * Math.Min(Bands.HighCrossover, 0.45 * sampleRate) / sampleRate;
        _cosLow = (float)Math.Cos(wl); _alphaLow = (float)(Math.Sin(wl) / 2 * Math.Sqrt(2));
        _cosHigh = (float)Math.Cos(wh); _alphaHigh = (float)(Math.Sin(wh) / 2 * Math.Sqrt(2));
        _low = default; _high = default;
        Low = Mid = High = new Ramp { Current = 1, Target = 1 };
        Shelves();
    }

    public void Set(float low, float mid, float high, int ramp)
    {
        Low.Set(low, ramp); Mid.Set(mid, ramp); High.Set(high, ramp);
    }

    /// <summary>Filters a block in place, advancing the ramps by its length.</summary>
    public void Process(Span<float> x)
    {
        for (int i = 0; i < x.Length; i += Step)
        {
            int n = Math.Min(Step, x.Length - i);
            float g0 = Mid.Current;
            Shelves();
            Low.Advance(n); High.Advance(n); Mid.Advance(n);
            float g1 = Mid.Current, dg = (g1 - g0) / n;
            for (int k = 0; k < n; k++)
            {
                float y = _high.Process(_low.Process(x[i + k]));
                x[i + k] = y * (g0 + dg * (k + 1));
            }
        }
    }

    /// <summary>For filters run a sample at a time (the reverb's lines): advance the ramps and refresh the shelves for the next <paramref name="frames"/>.</summary>
    public void Tick(int frames)
    {
        Low.Advance(frames); High.Advance(frames); Mid.Advance(frames);
        Shelves();
    }

    /// <summary>One sample at the current coefficients (see <see cref="Tick"/>).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Sample(float x) => _high.Process(_low.Process(x)) * Mid.Current;

    private void Shelves()
    {
        float mid = MathF.Max(Mid.Current, MathF.Max(Low.Current, High.Current) * 1e-4f);
        if (mid <= 0) mid = 1;
        _low.SetShelf(Math.Clamp(Low.Current / mid, 1e-4f, 1e4f), _cosLow, _alphaLow, low: true);
        _high.SetShelf(Math.Clamp(High.Current / mid, 1e-4f, 1e4f), _cosHigh, _alphaHigh, low: false);
    }
}

/// <summary>A biquad filter in transposed direct form II (RBJ's audio EQ cookbook coefficients). Coefficients can change between samples; the state carries over.</summary>
internal struct Biquad
{
    private float _b0, _b1, _b2, _a1, _a2, _z1, _z2;

    /// <summary>A second-order low-pass; Q = 1/√2 is Butterworth (maximally flat).</summary>
    public static Biquad LowPass(float frequency, int sampleRate, float q = 0.70710678f)
    {
        double w = 2 * Math.PI * Math.Min(frequency, 0.49 * sampleRate) / sampleRate, cos = Math.Cos(w), alpha = Math.Sin(w) / (2 * q);
        double a0 = 1 + alpha;
        return new Biquad
        {
            _b0 = (float)((1 - cos) / 2 / a0), _b1 = (float)((1 - cos) / a0), _b2 = (float)((1 - cos) / 2 / a0),
            _a1 = (float)(-2 * cos / a0), _a2 = (float)((1 - alpha) / a0),
        };
    }

    /// <summary>A shelf with linear gain <paramref name="gain"/> below (low) or above (high) its corner; cos and alpha precomputed for the corner.</summary>
    public void SetShelf(float gain, float cos, float alpha, bool low)
    {
        float a = MathF.Sqrt(gain), sa = 2 * MathF.Sqrt(a) * alpha, ap = a + 1, am = a - 1;
        float b0, b1, b2, a0, a1, a2;
        if (low)
        {
            b0 = a * (ap - am * cos + sa); b1 = 2 * a * (am - ap * cos); b2 = a * (ap - am * cos - sa);
            a0 = ap + am * cos + sa; a1 = -2 * (am + ap * cos); a2 = ap + am * cos - sa;
        }
        else
        {
            b0 = a * (ap + am * cos + sa); b1 = -2 * a * (am + ap * cos); b2 = a * (ap + am * cos - sa);
            a0 = ap - am * cos + sa; a1 = 2 * (am - ap * cos); a2 = ap - am * cos - sa;
        }
        float inv = 1 / a0;
        _b0 = b0 * inv; _b1 = b1 * inv; _b2 = b2 * inv; _a1 = a1 * inv; _a2 = a2 * inv;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Process(float x)
    {
        float y = _b0 * x + _z1;
        _z1 = _b1 * x - _a1 * y + _z2;
        _z2 = _b2 * x - _a2 * y;
        return y;
    }
}
