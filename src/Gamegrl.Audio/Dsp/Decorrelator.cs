namespace Gamegrl.Audio.Dsp;

/// <summary>
/// Spreads a mono signal over the bed at equal power as mutually uncorrelated copies: each speaker (but the LFE) hears it
/// through its own chain of three Schroeder allpasses (flat in magnitude, gain 0.5, prime delays from 1 to 11 ms, no two
/// speakers alike). The same signal sent to every speaker unchanged would reach each ear many times at slightly
/// different delays (through a binaural virtualizer's per-speaker ITDs, or in a room between real speakers) and comb into
/// a hollow, phasey sound; uncorrelated copies add by power. Allocation-free once built.
/// </summary>
internal sealed class Decorrelator
{
    // Samples at 48 kHz, all primes; a speaker's three come from the same column.
    private static readonly int[] First = [53, 71, 89, 107, 131, 149, 167, 191, 211, 233, 251, 271];
    private static readonly int[] Second = [113, 137, 163, 181, 199, 223, 241, 263, 283, 307, 331, 353];
    private static readonly int[] Third = [241, 277, 307, 331, 359, 383, 409, 433, 461, 487, 509, 541];
    private const float G = 0.5f;

    private readonly int[] _speakers;          // bed channels that take it
    private readonly float[][] _lines;         // three per speaker
    private readonly int[] _positions;
    private readonly float _scale;

    public Decorrelator(ChannelLayout bed, int sampleRate)
    {
        int lfe = bed.IndexOf(Speaker.Lfe);
        var speakers = new List<int>();
        for (int c = 0; c < bed.Count; c++) if (c != lfe) speakers.Add(c);
        _speakers = [.. speakers];
        _lines = new float[_speakers.Length * 3][];
        _positions = new int[_lines.Length];
        for (int i = 0; i < _speakers.Length; i++)
        {
            int k = i % First.Length;
            _lines[3 * i] = new float[Math.Max(2, First[k] * sampleRate / 48000)];
            _lines[3 * i + 1] = new float[Math.Max(2, Second[k] * sampleRate / 48000)];
            _lines[3 * i + 2] = new float[Math.Max(2, Third[k] * sampleRate / 48000)];
        }
        _scale = 1 / MathF.Sqrt(Math.Max(1, _speakers.Length));
    }

    /// <summary>Adds <paramref name="input"/>, decorrelated per speaker, into the planar bed (channel c at c × stride).</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> bed, int stride, int frames)
    {
        for (int i = 0; i < _speakers.Length; i++)
        {
            var out_ = bed.Slice(_speakers[i] * stride, frames);
            var a = _lines[3 * i]; var b = _lines[3 * i + 1]; var c = _lines[3 * i + 2];
            int pa = _positions[3 * i], pb = _positions[3 * i + 1], pc = _positions[3 * i + 2];
            for (int f = 0; f < frames; f++)
            {
                float x = input[f];
                x = Allpass(a, ref pa, x);
                x = Allpass(b, ref pb, x);
                x = Allpass(c, ref pc, x);
                out_[f] += x * _scale;
            }
            _positions[3 * i] = pa; _positions[3 * i + 1] = pb; _positions[3 * i + 2] = pc;
        }
    }

    private static float Allpass(float[] line, ref int p, float x)
    {
        float delayed = line[p];
        float v = x + G * delayed;
        line[p] = v;
        p = p + 1 == line.Length ? 0 : p + 1;
        return delayed - G * v;
    }
}
