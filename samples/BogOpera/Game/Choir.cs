using System.Numerics;
using Gamegrl.Audio;
using Gamegrl.Audio.Acoustics;

namespace BogOpera;

/// <summary>The desktop stage and the offline recording conduct the same score against the mixer clock.</summary>
public sealed class Choir
{
    public const int Steps = 16;
    private const int StepTicks = TempoMap.TicksPerBeat / 2;
    private readonly AudioEngine _engine;
    private readonly AudioClip[] _calls;
    private readonly TempoMap _tempo;
    private BeatClock _clock;
    private long _nextStep;
    private readonly int[] _score = [0x1111, 0x4a44, 0x9092];
    private readonly long[] _lastCall = new long[3], _nextCall = new long[3];
    public bool Singing { get; private set; } = true;
    public bool Echo { get; set; } = true;

    public Choir(AudioEngine engine)
    {
        _engine = engine;
        _calls = [MakeCall(0), MakeCall(1), MakeCall(2)];
        _tempo = new TempoMap(engine.SampleRate, 112);
        Restart();
    }

    private void Restart()
    {
        _clock = new BeatClock(_tempo, _engine.Time + _engine.Frames(0.08));
        _nextStep = 0;
        Array.Fill(_lastCall, long.MinValue);
        Array.Fill(_nextCall, long.MinValue);
    }

    public void ToggleChoir()
    {
        Singing = !Singing;
        if (Singing) Restart();
        else _engine.StopAll();
    }

    public bool HasNote(int frog, int step) => (_score[frog] & (1 << step)) != 0;
    public void ToggleNote(int frog, int step) => _score[frog] ^= 1 << step;

    private long Heard => _engine.Time - _engine.LatencyFrames;
    public int Column => Singing && Heard >= _clock.Origin ? (int)(_clock.TickAt(Heard) / StepTicks % Steps) : -1;

    public float Mouth(int frog)
    {
        long now = Heard;
        long onset = _nextCall[frog] <= now ? _nextCall[frog] : _lastCall[frog];
        return onset == long.MinValue || onset > now ? 0 : Math.Max(0, 1 - (now - onset) / (0.22f * _engine.SampleRate));
    }

    public void Conduct(long endStep = long.MaxValue)
    {
        if (!Singing) return;
        long now = _engine.Time;
        // A stalled window skips missed cues instead of playing them all in a burst.
        _nextStep = Math.Max(_nextStep, (_clock.TickAt(now) + StepTicks - 1) / StepTicks);
        long horizon = now + _engine.Frames(0.14);
        while (_nextStep < endStep)
        {
            long at = _clock.SampleAt(_nextStep * StepTicks);
            if (at > horizon) break;
            int column = (int)(_nextStep % Steps);
            for (int frog = 0; frog < _calls.Length; frog++)
                if (HasNote(frog, column)) Call(frog, at, _nextStep);
            _nextStep++;
        }
    }

    public void Solo(int frog) => Call(frog, _engine.Time, Math.Max(0, _nextStep - 1));

    private void Call(int frog, long at, long step)
    {
        ReadOnlySpan<int> roots = [0, 5, 7, 0];
        ReadOnlySpan<int> melody = [0, 7, 3, 10, 7, 12, 10, 3];
        int note = roots[(int)(step / 8 % roots.Length)];
        if (frog > 0) note += melody[(int)((step + frog * 2) % melody.Length)];
        float x = (frog - 1) * 0.85f;
        var voice = _engine.Play(_calls[frog], PlayOptions.Default with
        {
            StartTime = at, Pitch = MathF.Pow(2, note / 12f), Positional = true,
            Spatial = SpatialParams.Default with { Direction = Vector3.Normalize(new Vector3(x, 0, -0.6f)) },
        });
        if (Echo)
        {
            // A call skips off the opposite bank, then the stone bridge overhead.
            _engine.SetReflections(voice, [
                new Reflection { Direction = Vector3.Normalize(new(-x, 0, -0.5f)), Delay = 0.071f, Gain = 0.46f },
                new Reflection { Direction = Vector3.UnitY, Delay = 0.115f, Gain = 0.24f }
            ], new Bands3(0.9f, 0.75f, 0.4f), rampSeconds: 0);
        }
        if (at >= _nextCall[frog])
        {
            _lastCall[frog] = _nextCall[frog];
            _nextCall[frog] = at;
        }
        else _lastCall[frog] = at;
    }

    private static AudioClip MakeCall(int frog)
    {
        const int rate = 32000;
        float duration = frog switch { 0 => 0.37f, 1 => 0.28f, _ => 0.43f };
        return AudioClip.Generate(frog switch { 0 => "Bullfrog bass", 1 => "Reed kazoo", _ => "Glass peeper" },
            rate, (int)(rate * duration), i =>
            {
                float t = i / (float)rate;
                float envelope = Math.Min(1, t / 0.008f) * Math.Min(1, (duration - t) / 0.025f);
                float phase = MathF.Tau * t;
                float sound = frog switch
                {
                    0 => MathF.Sin(phase * 98 + 4 * (1 - MathF.Exp(-40 * t))) *
                         (0.8f + 0.2f * MathF.Cos(phase * 27)),
                    1 => MathF.Sin(phase * 196 + 1.6f * MathF.Sin(phase * 392) * MathF.Exp(-8 * t)),
                    _ => MathF.Sin(phase * 392 + 0.6f * MathF.Sin(phase * 7)) + 0.28f * MathF.Sin(phase * 950),
                };
                return 0.39f * envelope * MathF.Exp(-6 * t) * sound;
            });
    }
}
