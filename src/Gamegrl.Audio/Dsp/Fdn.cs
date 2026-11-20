using Gamegrl.Audio.Acoustics;

namespace Gamegrl.Audio.Dsp;

/// <summary>
/// The late reverb: an 8-line feedback delay network (Jot) with a Householder feedback matrix (lossless and dense), per-line
/// three-band decay filters (the shelving cascade of <see cref="BandEq3"/> with gains 10^(−3·d ÷ (fs·T60)) for line
/// length d, so every band decays at its own rate), a pre-delay and four input allpass diffusers. Line outputs reach
/// each bed speaker through a different ± combination (rows of a Hadamard matrix), so the tail surrounds the listener
/// decorrelated. Scaled so a unit impulse puts out unit energy (summed over the bed) whatever the decay time;
/// <see cref="ReverbParams.Level"/> then sets the reverberant level. A focused reverb (another room heard through an
/// opening) instead sums its lines to one signal and pans it with the gains <see cref="Focus"/> sets. Allocation-free once built.
/// </summary>
internal sealed class Fdn
{
    private const int Lines = 8;
    private static readonly int[] BaseDelays = [1123, 1291, 1453, 1621, 1789, 1999, 2203, 2411]; // samples at 48 kHz, mutually prime
    private static readonly int[] DiffuserDelays = [142, 107, 379, 277];
    private const float DiffuserGain = 0.6f;

    private readonly int _rate, _channels, _lfe;
    private readonly float[][] _lines;
    private readonly int[] _lengths, _positions = new int[Lines];
    private readonly BandEq3[] _filters = new BandEq3[Lines];
    private readonly float[][] _diffusers;
    private readonly int[] _diffuserPositions = new int[4];
    private readonly float[] _preDelay;
    private int _prePos, _preLength;
    private readonly sbyte[,] _signs;       // [bed channel, line]
    private readonly float[] _taps = new float[Lines];
    private Ramp _level;
    private float _norm = 1, _focusNorm = 1;
    private readonly Ramp[]? _focus;  // per bed channel, when focused
    private ReverbParams _params = ReverbParams.None;

    public Fdn(ChannelLayout bed, int sampleRate, bool focused = false)
    {
        if (focused) _focus = new Ramp[bed.Count];
        _rate = sampleRate;
        _channels = bed.Count;
        _lfe = bed.IndexOf(Speaker.Lfe);
        _lines = new float[Lines][];
        _lengths = new int[Lines];
        for (int j = 0; j < Lines; j++)
        {
            _lengths[j] = Math.Max(16, BaseDelays[j] * sampleRate / 48000);
            _lines[j] = new float[_lengths[j]];
            _filters[j].Init(sampleRate);
        }
        _diffusers = new float[4][];
        for (int i = 0; i < 4; i++) _diffusers[i] = new float[Math.Max(4, DiffuserDelays[i] * sampleRate / 48000)];
        _preDelay = new float[sampleRate / 5 + 1];
        // Hadamard rows (Sylvester) for the output signs; LFE takes none.
        _signs = new sbyte[_channels, Lines];
        int row = 1;
        for (int c = 0; c < _channels; c++)
        {
            if (c == _lfe) continue;
            for (int j = 0; j < Lines; j++) _signs[c, j] = (sbyte)((System.Numerics.BitOperations.PopCount((uint)(row & j)) & 1) == 0 ? 1 : -1);
            row = row % (Lines - 1) + 1; // rows 1..7: each sums to zero, so channels decorrelate
        }
        Set(ReverbParams.None, 0);
    }

    public bool Silent => _level.Current == 0 && _level.Remaining == 0;

    /// <summary>New decay times, level and pre-delay; the level and the decay filters ramp over <paramref name="ramp"/> frames.</summary>
    public void Set(in ReverbParams p, int ramp)
    {
        _params = p;
        var t = p.DecaySeconds;
        double inverseEnergy = 0;
        for (int j = 0; j < Lines; j++)
        {
            float G(float seconds) => seconds <= 0 ? 0 : MathF.Pow(10, -3f * _lengths[j] / (_rate * seconds));
            float low = G(t.Low), mid = G(t.Mid), high = G(t.High);
            _filters[j].Set(low, mid, high, ramp);
            // A unit of energy passing this line once per trip returns g² of itself: 1/(1 − g²) in all.
            inverseEnergy += 1 - mid * mid;
        }
        // Energy normalisation: the taps see each trip once; the output combination spreads it over the speakers.
        int speakers = _channels - (_lfe >= 0 ? 1 : 0);
        _norm = t.Mid > 0 ? (float)Math.Sqrt(inverseEnergy / Lines) / MathF.Sqrt(speakers) : 0;
        _focusNorm = t.Mid > 0 ? (float)Math.Sqrt(inverseEnergy / Lines) : 0;
        _level.Set(p.IsSilent ? 0 : p.Level, ramp);
        int length = Math.Clamp((int)(p.PreDelaySeconds * _rate), 1, _preDelay.Length - 1);
        if (length != _preLength)
        {
            // Cross-fade from the old pre-delay to the new: a jump in where the line is read would click (and moving the
            // read point gradually would bend the pitch).
            _preOld = _preFade > 0 ? _preOld : _preLength;
            _preLength = length;
            _preFade = PreFadeFrames;
        }
    }

    private const int PreFadeFrames = 1024;
    private int _preOld, _preFade;

    /// <summary>A focused reverb's pan gains over the bed (power-normalised, e.g. from VBAP), ramped.</summary>
    public void Focus(ReadOnlySpan<float> gains, int ramp)
    {
        if (_focus is null) return;
        for (int c = 0; c < _focus.Length; c++) _focus[c].Set(gains[c], ramp);
    }

    /// <summary>Reads <paramref name="input"/> (mono sends) and adds the tail into the planar bed.</summary>
    public void Process(ReadOnlySpan<float> input, Span<float> bed, int stride, int frames)
    {
        if (Silent) { _level.Advance(frames); return; }
        for (int j = 0; j < Lines; j++) _filters[j].Tick(frames);
        var lines = _lines;
        for (int f = 0; f < frames; f++)
        {
            // Pre-delay, then diffusion.
            _preDelay[_prePos] = input[f];
            int read = _prePos - _preLength; if (read < 0) read += _preDelay.Length;
            float x = _preDelay[read];
            if (_preFade > 0)
            {
                int old = _prePos - _preOld; if (old < 0) old += _preDelay.Length;
                float w = (float)_preFade / PreFadeFrames;
                x += (_preDelay[old] - x) * w;
                _preFade--;
            }
            _prePos = _prePos + 1 == _preDelay.Length ? 0 : _prePos + 1;
            for (int i = 0; i < 4; i++)
            {
                var d = _diffusers[i];
                ref int p = ref _diffuserPositions[i];
                float delayed = d[p];
                float v = x + DiffuserGain * delayed;
                d[p] = v;
                x = delayed - DiffuserGain * v;
                p = p + 1 == d.Length ? 0 : p + 1;
            }

            // Line outputs through their decay filters; Householder feedback: y − (2/N)·Σy.
            float sum = 0;
            for (int j = 0; j < Lines; j++)
            {
                float y = _filters[j].Sample(lines[j][_positions[j]]);
                _taps[j] = y;
                sum += y;
            }
            float mix = 2f / Lines * sum;
            for (int j = 0; j < Lines; j++)
            {
                float injected = (j & 1) == 0 ? x : -x;
                lines[j][_positions[j]] = _taps[j] - mix + injected * 0.35355339f; // 1/√8
                _positions[j] = _positions[j] + 1 == _lengths[j] ? 0 : _positions[j] + 1;
            }

            if (_focus is not null)
            {
                float mono = 0;
                for (int j = 0; j < Lines; j++) mono += (j & 1) == 0 ? _taps[j] : -_taps[j];
                mono *= _level.Next() * _focusNorm;
                for (int c = 0; c < _channels; c++) bed[c * stride + f] += mono * _focus[c].Next();
                continue;
            }
            float gain = _level.Next() * _norm;
            if (gain == 0) continue;
            for (int c = 0; c < _channels; c++)
            {
                if (c == _lfe) continue;
                float o = 0;
                for (int j = 0; j < Lines; j++) o += _signs[c, j] * _taps[j];
                bed[c * stride + f] += o * gain;
            }
        }
    }
}
