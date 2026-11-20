using Gamegrl.Audio.Dsp;

namespace Gamegrl.Audio;

/// <summary>Generated sounds for auditioning spaces: a hand clap and a gunshot (deterministic, mono).</summary>
public static class TestSounds
{
    /// <summary>A hand clap: a few milliseconds of band-limited noise around 1–2 kHz, decaying fast.</summary>
    public static AudioClip Clap(int sampleRate = 48000)
    {
        int n = sampleRate / 10;
        var s = new float[n];
        uint state = 12345;
        var low = Biquad.LowPass(4000, sampleRate);
        var high = Biquad.LowPass(700, sampleRate);
        for (int i = 0; i < n; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            float noise = (state >> 8) * (2f / 16777216f) - 1;
            float t = (float)i / sampleRate;
            float env = MathF.Min(1, t / 0.0005f) * MathF.Exp(-t / 0.008f);
            float band = low.Process(noise);
            band -= high.Process(band);
            s[i] = env * band;
        }
        return new AudioClip("clap", sampleRate, ChannelLayout.Mono, Normalise(s, 0.9f));
    }

    /// <summary>A gunshot: a sharp broadband crack, a low thump, and a noisy decay over a few hundred milliseconds.</summary>
    public static AudioClip Gunshot(int sampleRate = 48000)
    {
        int n = sampleRate * 2 / 5;
        var s = new float[n];
        uint state = 777;
        var body = Biquad.LowPass(2500, sampleRate);
        float phase = 0;
        for (int i = 0; i < n; i++)
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            float noise = (state >> 8) * (2f / 16777216f) - 1;
            float t = (float)i / sampleRate;
            float crack = noise * MathF.Exp(-t / 0.0015f);
            float decay = body.Process(noise) * MathF.Exp(-t / 0.06f) * 1.6f;
            phase += 2 * MathF.PI * (55 + 140 * MathF.Exp(-t / 0.015f)) / sampleRate;
            float thump = MathF.Sin(phase) * MathF.Exp(-t / 0.05f);
            s[i] = MathF.Min(1, t / 0.0002f) * (crack + decay + 0.9f * thump);
        }
        return new AudioClip("gunshot", sampleRate, ChannelLayout.Mono, Normalise(s, 0.95f));
    }

    private static float[] Normalise(float[] s, float peak)
    {
        float max = 0;
        foreach (var x in s) max = MathF.Max(max, MathF.Abs(x));
        if (max > 0) for (int i = 0; i < s.Length; i++) s[i] *= peak / max;
        return s;
    }
}
