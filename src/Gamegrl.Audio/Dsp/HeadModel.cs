using System.Numerics;

namespace Gamegrl.Audio.Dsp;

/// <summary>
/// The spherical-head model of Brown and Duda ("A structural model for binaural sound synthesis", 1998): a rigid sphere
/// of radius <see cref="Radius"/> with ears at its poles. For each ear, with θ the angle between the source and that
/// ear's axis:
/// <list type="bullet">
/// <item>arrival delay <c>−(a/c)·cos θ</c> for θ &lt; 90°, <c>(a/c)·(θ − 90°)</c> beyond (sound wrapping round the head),
/// which gives Woodworth's interaural time difference <c>(a/c)(φ + sin φ)</c> for a source at azimuth φ;</item>
/// <item>head shadow, a one-pole one-zero shelf <c>(1 + jαω/2ω₀) / (1 + jω/2ω₀)</c> with ω₀ = c/a and
/// <c>α(θ) = (1 + α_min/2) + (1 − α_min/2)·cos(θ/θ_min · 180°)</c>, α_min = 0.1, θ_min = 150°: +6 dB of high
/// frequencies facing the ear, down to −20 dB in its shadow.</item>
/// </list>
/// It has no pinna, so elevation and front/back cues are weak; measured HRTFs (SOFA) come later.
/// </summary>
public static class HeadModel
{
    public const float Radius = 0.0875f, SpeedOfSound = 343f;
    private const float AlphaMin = 0.1f, ThetaMin = 150f * MathF.PI / 180f;

    /// <summary>The ears' axes in listener space (+X right).</summary>
    public static readonly Vector3 LeftEar = -Vector3.UnitX, RightEar = Vector3.UnitX;

    /// <summary>Arrival delay at an ear in seconds, offset so the nearest possible arrival is 0.</summary>
    public static float EarDelay(Vector3 direction, Vector3 ear)
    {
        float theta = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(direction), ear), -1, 1));
        float t = theta < MathF.PI / 2 ? -MathF.Cos(theta) : theta - MathF.PI / 2;
        return (Radius / SpeedOfSound) * (t + 1);
    }

    /// <summary>The head-shadow filter's high-frequency gain α for an ear.</summary>
    public static float ShadowAlpha(Vector3 direction, Vector3 ear)
    {
        float theta = MathF.Acos(Math.Clamp(Vector3.Dot(Vector3.Normalize(direction), ear), -1, 1));
        return (1 + AlphaMin / 2) + (1 - AlphaMin / 2) * MathF.Cos(theta / ThetaMin * MathF.PI);
    }

    /// <summary>
    /// The shadow filter for an ear, bilinear-transformed: <c>y = b0·x + b1·x₁ − a1·y₁</c>. Unity at DC, α at high frequencies.
    /// </summary>
    public static (float B0, float B1, float A1) ShadowFilter(float alpha, int sampleRate)
    {
        float k = sampleRate / (SpeedOfSound / Radius); // 2·fs / (2·ω₀)
        float norm = 1 / (1 + k);
        return ((1 + alpha * k) * norm, (1 - alpha * k) * norm, (1 - k) * norm);
    }

    /// <summary>Woodworth's interaural time difference for a source at azimuth φ (radians, |φ| ≤ 90°) on the horizontal plane.</summary>
    public static float Woodworth(float azimuth) => (Radius / SpeedOfSound) * (MathF.Abs(azimuth) + MathF.Sin(MathF.Abs(azimuth)));
}

/// <summary>
/// Headphone rendering of a speaker bed: each virtual speaker is heard through the head model from its direction (a
/// fractional delay and a shadow filter per ear), LFE goes to both ears at −6 dB. The bed layout is fixed, so the cost
/// is constant however many voices play.
/// </summary>
internal sealed class BinauralVirtualizer
{
    private const int DelaySize = 64; // > the largest ear delay (~0.66 ms = 32 samples at 48 kHz)
    private readonly int _channels, _lfe;
    private readonly float[] _history;          // per channel ring of DelaySize samples
    private readonly int[] _delayWhole;         // [channel * 2 + ear]
    private readonly float[] _frac, _b0, _b1, _a1, _x1, _y1;
    private int _pos;

    public BinauralVirtualizer(ChannelLayout bed, int sampleRate)
    {
        _channels = bed.Count;
        _lfe = bed.IndexOf(Speaker.Lfe);
        _history = new float[_channels * DelaySize];
        int n = _channels * 2;
        _delayWhole = new int[n]; _frac = new float[n]; _b0 = new float[n]; _b1 = new float[n]; _a1 = new float[n]; _x1 = new float[n]; _y1 = new float[n];
        for (int c = 0; c < _channels; c++)
        {
            var dir = Spatial.Directions.SpeakerVector(bed, bed.Speakers[c]);
            for (int ear = 0; ear < 2; ear++)
            {
                var axis = ear == 0 ? HeadModel.LeftEar : HeadModel.RightEar;
                float delay = HeadModel.EarDelay(dir, axis) * sampleRate + 1; // +1 keeps the cubic's taps in the past
                int i = c * 2 + ear;
                _delayWhole[i] = (int)delay;
                _frac[i] = delay - (int)delay;
                (_b0[i], _b1[i], _a1[i]) = HeadModel.ShadowFilter(HeadModel.ShadowAlpha(dir, axis), sampleRate);
            }
        }
    }

    /// <summary>Planar bed in (channel c at <c>bed[c * stride ..]</c>), planar stereo out (left at 0, right at <c>stride</c>).</summary>
    public void Process(ReadOnlySpan<float> bed, Span<float> output, int stride, int frames)
    {
        for (int f = 0; f < frames; f++)
        {
            float left = 0, right = 0;
            for (int c = 0; c < _channels; c++)
            {
                float x = bed[c * stride + f];
                int ring = c * DelaySize;
                _history[ring + _pos] = x;
                if (c == _lfe) { left += 0.5f * x; right += 0.5f * x; continue; }
                for (int ear = 0; ear < 2; ear++)
                {
                    int i = c * 2 + ear;
                    // 4-point cubic (Catmull-Rom) read between delay d and d + 1.
                    int d = _delayWhole[i];
                    float t = _frac[i];
                    float ym = _history[ring + ((_pos - d + 1) & (DelaySize - 1))], y0 = _history[ring + ((_pos - d) & (DelaySize - 1))];
                    float y1 = _history[ring + ((_pos - d - 1) & (DelaySize - 1))], y2 = _history[ring + ((_pos - d - 2) & (DelaySize - 1))];
                    float a = 0.5f * (y1 - ym), b = ym - 2.5f * y0 + 2 * y1 - 0.5f * y2, e = 0.5f * (y2 - ym) + 1.5f * (y0 - y1);
                    float delayed = ((e * t + b) * t + a) * t + y0;
                    float y = _b0[i] * delayed + _b1[i] * _x1[i] - _a1[i] * _y1[i];
                    _x1[i] = delayed; _y1[i] = y;
                    if (ear == 0) left += y; else right += y;
                }
            }
            output[f] = left;
            output[stride + f] = right;
            _pos = (_pos + 1) & (DelaySize - 1);
        }
    }
}
