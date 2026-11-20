using Gamegrl.Audio;

namespace CaveGunfight;

/// <summary>The cave's generated sounds (deterministic).</summary>
public static class Sounds
{
    /// <summary>A water drop landing in a pool: a short sine whose pitch rises as the bubble closes, and a tiny splash.</summary>
    public static AudioClip Drip(int rate, int variant)
    {
        int n = rate * 3 / 20;
        var s = new float[n];
        float start = 900 + 140 * variant, end = start * 2.3f, phase = 0;
        uint state = 0x2545F491u + (uint)variant * 977;
        for (int i = 0; i < n; i++)
        {
            float t = (float)i / rate;
            float f = start + (end - start) * MathF.Min(1, t / 0.03f);
            phase += 2 * MathF.PI * f / rate;
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            float splash = ((state >> 8) * (2f / 16777216f) - 1) * MathF.Exp(-t / 0.004f) * 0.25f;
            s[i] = MathF.Min(1, t / 0.0005f) * (MathF.Sin(phase) * MathF.Exp(-t / 0.035f) + splash) * 0.8f;
        }
        return new AudioClip("drip", rate, ChannelLayout.Mono, s);
    }

    /// <summary>Wind across a cave mouth: low-passed noise with a slow swell, its ends cross-faded so it loops seamlessly.</summary>
    public static AudioClip Wind(int rate)
    {
        int n = rate * 8, fade = rate;
        var raw = new float[n + fade];
        uint state = 0x9E3779B9u;
        float low = 0, band = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            float noise = (state >> 8) * (2f / 16777216f) - 1;
            float t = (float)i / rate;
            // A drifting cut-off: gusts brighten as they swell.
            float swell = 0.55f + 0.45f * MathF.Sin(2 * MathF.PI * t / 8 * 2) * MathF.Sin(2 * MathF.PI * t / 8 * 3 + 1);
            float a = 0.004f + 0.02f * swell;
            low += a * (noise - low);
            band += 0.3f * (low - band);
            raw[i] = (low - 0.6f * band) * (0.4f + swell) * 6;
        }
        var s = new float[n];
        for (int i = 0; i < n; i++)
        {
            float w = i < fade ? (float)i / fade : 1;
            s[i] = raw[i] * w + (i < fade ? raw[n + i] * (1 - w) : 0);
        }
        float peak = 0;
        foreach (var x in s) peak = MathF.Max(peak, MathF.Abs(x));
        for (int i = 0; i < n; i++) s[i] *= 0.7f / peak;
        return new AudioClip("wind", rate, ChannelLayout.Mono, s);
    }
}
