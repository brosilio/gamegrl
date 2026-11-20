namespace Gamegrl.Audio;

/// <summary>A musical position: bar and beat from zero, and ticks into the beat.</summary>
public readonly record struct BarBeat(long Bar, int Beat, int Tick);

/// <summary>
/// Maps musical time to sample time, deterministically: positions are integer ticks (<see cref="TicksPerBeat"/> per beat),
/// tempos are rationals (beats per minute as numerator over denominator, so 174.5 BPM is 349/2), and conversions use
/// exact 128-bit integer arithmetic. The same map gives the same sample for every tick on every platform, which is what
/// replays, lockstep and rhythm judgement rely on. Sample 0 is the map's origin; a <see cref="BeatClock"/> places it on
/// the engine's timeline.
/// </summary>
public sealed class TempoMap
{
    public const int TicksPerBeat = 960;

    private struct Segment
    {
        public long Tick, Sample;     // where the segment starts
        public long BpmNum, BpmDen;
        public int BeatsPerBar;
        public long Bar;              // bar number at the segment's start (segments start on bar lines)
    }

    private readonly List<Segment> _segments = [];
    public int SampleRate { get; }

    public TempoMap(int sampleRate, long bpmNumerator, long bpmDenominator = 1, int beatsPerBar = 4)
    {
        if (sampleRate <= 0 || bpmNumerator <= 0 || bpmDenominator <= 0 || beatsPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(bpmNumerator));
        SampleRate = sampleRate;
        _segments.Add(new Segment { BpmNum = bpmNumerator, BpmDen = bpmDenominator, BeatsPerBar = beatsPerBar });
    }

    /// <summary>
    /// Changes tempo (and optionally the time signature) from a bar line on. Changes are added in order. Throws if
    /// <paramref name="bar"/> isn't after the last change.
    /// </summary>
    public void Change(long bar, long bpmNumerator, long bpmDenominator = 1, int beatsPerBar = 0)
    {
        var last = _segments[^1];
        if (bar <= last.Bar) throw new ArgumentException("Tempo changes must come in order, on later bars");
        if (bpmNumerator <= 0 || bpmDenominator <= 0 || beatsPerBar < 0) throw new ArgumentOutOfRangeException(nameof(bpmNumerator));
        long tick = last.Tick + (bar - last.Bar) * last.BeatsPerBar * TicksPerBeat;
        _segments.Add(new Segment
        {
            Tick = tick, Sample = SampleAt(tick), BpmNum = bpmNumerator, BpmDen = bpmDenominator,
            BeatsPerBar = beatsPerBar == 0 ? last.BeatsPerBar : beatsPerBar, Bar = bar,
        });
    }

    private int SegmentForTick(long tick)
    {
        int i = _segments.Count - 1;
        while (i > 0 && _segments[i].Tick > tick) i--;
        return i;
    }

    private int SegmentForSample(long sample)
    {
        int i = _segments.Count - 1;
        while (i > 0 && _segments[i].Sample > sample) i--;
        return i;
    }

    /// <summary>The first sample at or after a tick (exact: samples = ticks × 60 × rate × den ÷ (num × ticks per beat), rounded up).</summary>
    public long SampleAt(long tick)
    {
        var s = _segments[SegmentForTick(tick)];
        Int128 num = (Int128)(tick - s.Tick) * 60 * SampleRate * s.BpmDen;
        Int128 den = (Int128)s.BpmNum * TicksPerBeat;
        return s.Sample + (long)CeilDiv(num, den);
    }

    /// <summary>The tick sounding at a sample (rounded down). <c>TickAt(SampleAt(t)) == t</c> whenever a tick lasts at least a sample.</summary>
    public long TickAt(long sample)
    {
        var s = _segments[SegmentForSample(sample)];
        Int128 num = (Int128)(sample - s.Sample) * s.BpmNum * TicksPerBeat;
        Int128 den = (Int128)60 * SampleRate * s.BpmDen;
        return s.Tick + (long)FloorDiv(num, den);
    }

    public long SampleAtBeat(long beat) => SampleAt(beat * TicksPerBeat);

    /// <summary>The bar, beat in the bar and tick in the beat of a tick.</summary>
    public BarBeat BarBeatAt(long tick)
    {
        var s = _segments[SegmentForTick(tick)];
        long into = tick - s.Tick;
        long perBar = (long)s.BeatsPerBar * TicksPerBeat;
        long bars = FloorDiv(into, perBar);
        long rem = into - bars * perBar;
        return new BarBeat(s.Bar + bars, (int)(rem / TicksPerBeat), (int)(rem % TicksPerBeat));
    }

    /// <summary>The tick at the start of a bar.</summary>
    public long TickAtBar(long bar)
    {
        int i = _segments.Count - 1;
        while (i > 0 && _segments[i].Bar > bar) i--;
        var s = _segments[i];
        return s.Tick + (bar - s.Bar) * s.BeatsPerBar * TicksPerBeat;
    }

    private static Int128 FloorDiv(Int128 a, Int128 b) { var q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }
    private static long FloorDiv(long a, long b) { long q = a / b; return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q; }
    private static Int128 CeilDiv(Int128 a, Int128 b) => -FloorDiv(-a, b);
}

/// <summary>
/// A tempo map placed on the engine's sample timeline: the song's origin is engine sample <see cref="Origin"/>. Game code
/// reads positions and schedules sounds on beats with it; everything is integer arithmetic on sample counts.
/// </summary>
public readonly struct BeatClock(TempoMap map, long origin)
{
    public TempoMap Map { get; } = map;
    public long Origin { get; } = origin;

    /// <summary>The tick sounding at an engine sample (negative before the origin).</summary>
    public long TickAt(long engineSample) => Map.TickAt(engineSample - Origin);
    public BarBeat PositionAt(long engineSample) => Map.BarBeatAt(TickAt(engineSample));
    /// <summary>The engine sample where a tick starts: schedule sounds here.</summary>
    public long SampleAt(long tick) => Origin + Map.SampleAt(tick);
    /// <summary>The first beat starting at or after an engine sample, and where it starts.</summary>
    public (long Beat, long Sample) NextBeat(long engineSample)
    {
        long tick = TickAt(engineSample);
        long beat = tick >= 0 ? (tick + TempoMap.TicksPerBeat - 1) / TempoMap.TicksPerBeat : -(-tick / TempoMap.TicksPerBeat);
        long sample = SampleAt(beat * TempoMap.TicksPerBeat);
        if (sample < engineSample) { beat++; sample = SampleAt(beat * TempoMap.TicksPerBeat); }
        return (beat, sample);
    }
}
