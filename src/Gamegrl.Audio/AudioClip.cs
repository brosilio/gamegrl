namespace Gamegrl.Audio;

/// <summary>
/// Decoded audio in memory: interleaved float samples in [−1, 1] at the clip's own rate, resampled when played. Immutable
/// once made, so the audio thread reads it without locks.
/// </summary>
public sealed class AudioClip
{
    public string Name { get; }
    public int SampleRate { get; }
    public ChannelLayout Layout { get; }
    public int Channels => Layout.Count;
    /// <summary>Interleaved: frame f, channel c is <c>Samples[f * Channels + c]</c>.</summary>
    public float[] Samples { get; }
    public int Frames { get; }
    public double Duration => (double)Frames / SampleRate;

    /// <summary>
    /// How long the clip's energy lasts, in seconds: (Σx²)² ÷ Σx⁴ over its samples (its length for a steady sound, the
    /// time constant of an exponential decay). A reverb fed a clip much shorter than its own decay spreads the clip's
    /// energy over the decay, so its level stays below the steady-state level by about this over the decay's time constant.
    /// </summary>
    public float EnergySeconds
    {
        get
        {
            if (_energySeconds >= 0) return _energySeconds;
            double e2 = 0, e4 = 0;
            foreach (float x in Samples) { double p = x * x; e2 += p; e4 += p * p; }
            return _energySeconds = e4 > 0 ? (float)(e2 * e2 / e4 / Channels / SampleRate) : 0;
        }
    }
    private float _energySeconds = -1;

    public AudioClip(string name, int sampleRate, ChannelLayout layout, float[] samples)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (samples.Length % layout.Count != 0) throw new ArgumentException("Sample count isn't a whole number of frames");
        Name = name;
        SampleRate = sampleRate;
        Layout = layout;
        Samples = samples;
        Frames = samples.Length / layout.Count;
    }

    /// <summary>A mono clip computed sample by sample (test tones, generated effects).</summary>
    public static AudioClip Generate(string name, int sampleRate, int frames, Func<int, float> sample)
    {
        var s = new float[frames];
        for (int i = 0; i < frames; i++) s[i] = sample(i);
        return new AudioClip(name, sampleRate, ChannelLayout.Mono, s);
    }

    public static AudioClip Sine(float frequency, float seconds, int sampleRate = 48000, float amplitude = 0.5f) =>
        Generate($"sine {frequency} Hz", sampleRate, (int)(seconds * sampleRate), i => amplitude * MathF.Sin(2 * MathF.PI * frequency * i / sampleRate));
}
