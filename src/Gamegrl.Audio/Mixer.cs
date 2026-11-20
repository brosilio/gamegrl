using System.Runtime.CompilerServices;

namespace Gamegrl.Audio;

internal enum CommandKind : byte { Play, Stop, VoiceGain, VoicePan, VoicePitch, BusCreate, BusGain }

/// <summary>A message from the game to the audio thread. Times are engine samples; ramps are in frames.</summary>
internal struct Command
{
    public CommandKind Kind;
    public bool Loop;
    public int Target;   // voice handle value, or bus index
    public int Bus;      // Play: the bus; BusCreate: the parent
    public int Ramp;
    public long Time;
    public long Offset;  // Play: start position in the clip, 32.32 fixed-point frames
    public float Value, Pan, Pitch;
    public AudioClip? Clip;
}

internal enum AudioEventKind : byte { VoiceEnded }

/// <summary>A message from the audio thread back to the game.</summary>
internal struct AudioEvent
{
    public AudioEventKind Kind;
    public int Voice;
    public long Time;
}

/// <summary>A linear parameter ramp: smooth changes without zipper noise.</summary>
internal struct Ramp
{
    public float Current, Target, Step;
    public int Remaining;

    public void Set(float target, int frames)
    {
        Target = target;
        if (frames <= 0) { Current = target; Remaining = 0; Step = 0; return; }
        Remaining = frames;
        Step = (target - Current) / frames;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float Next()
    {
        if (Remaining == 0) return Current;
        if (--Remaining == 0) Current = Target; else Current += Step;
        return Current;
    }
}

/// <summary>
/// The audio thread's side of the engine: voices, buses and the master chain. It owns all its state; the game reaches it
/// only through the command ring, and hears back through the event ring. <see cref="Render"/> never allocates, locks or
/// blocks.
/// </summary>
internal sealed class Mixer
{
    public const int MaxBlock = 512;
    public const int MaxClipChannels = 18;
    private const long One = 1L << 32;

    private sealed class Voice
    {
        public bool Active, Stopping;
        public int Handle, Bus, StopFade;
        public AudioClip Clip = null!;
        public long Start, Stop = long.MaxValue;
        public long Pos, Step;  // 32.32 fixed-point frames into the clip
        public float Pitch = 1, Pan;
        public bool Loop;
        public Ramp Gain;
        public Ramp[] Matrix = null!; // output channel o, clip channel i at [o * clipChannels + i]
        public int Inputs;
    }

    private sealed class Bus
    {
        public bool Used;
        public int Parent;
        public Ramp Gain;
        public float[] Buffer = null!; // planar: channel c at [c * MaxBlock ..]
    }

    private readonly int _channels, _rate;
    private readonly ChannelLayout _layout;
    private readonly Voice[] _voices;
    private readonly Bus[] _buses;
    private readonly float[] _frame = new float[MaxClipChannels];
    private readonly SpscRing<Command> _commands;
    private readonly SpscRing<AudioEvent> _events;
    private readonly Limiter? _limiter;
    private int _busCount = 1;
    private long _time;

    public int SampleRate => _rate;
    public ChannelLayout Layout => _layout;
    public int LimiterLatency => _limiter?.LatencyFrames ?? 0;
    /// <summary>The engine sample of the next frame to render; readable from any thread.</summary>
    public long Time => Volatile.Read(ref _time);

    public Mixer(int sampleRate, ChannelLayout layout, int maxVoices, int maxBuses, Limiter? limiter, SpscRing<Command> commands, SpscRing<AudioEvent> events)
    {
        _rate = sampleRate;
        _layout = layout;
        _channels = layout.Count;
        _limiter = limiter;
        _commands = commands;
        _events = events;
        _voices = new Voice[maxVoices];
        for (int i = 0; i < maxVoices; i++) _voices[i] = new Voice { Matrix = new Ramp[_channels * MaxClipChannels] };
        _buses = new Bus[maxBuses];
        for (int i = 0; i < maxBuses; i++) _buses[i] = new Bus { Buffer = new float[_channels * MaxBlock], Gain = new Ramp { Current = 1, Target = 1 } };
        _buses[0].Used = true;
    }

    /// <summary>Renders interleaved frames in the output layout's channel order.</summary>
    public void Render(Span<float> interleaved, int frames)
    {
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(MaxBlock, frames - done);
            RenderBlock(interleaved.Slice(done * _channels, n * _channels), n);
            done += n;
        }
    }

    private void RenderBlock(Span<float> output, int n)
    {
        long blockStart = _time;
        while (_commands.TryPop(out var cmd)) Apply(in cmd, blockStart);

        for (int b = 0; b < _busCount; b++)
        {
            var bus = _buses[b];
            if (!bus.Used) continue;
            for (int c = 0; c < _channels; c++) Array.Clear(bus.Buffer, c * MaxBlock, n);
        }
        foreach (var v in _voices) if (v.Active) RenderVoice(v, blockStart, n);

        // Children have higher indices than their parents: fold the tree from the leaves up.
        for (int b = _busCount - 1; b > 0; b--)
        {
            var bus = _buses[b];
            if (!bus.Used) continue;
            var parent = _buses[bus.Parent].Buffer;
            for (int f = 0; f < n; f++)
            {
                float g = bus.Gain.Next();
                for (int c = 0; c < _channels; c++) parent[c * MaxBlock + f] += bus.Buffer[c * MaxBlock + f] * g;
            }
        }
        var master = _buses[0];
        for (int f = 0; f < n; f++)
        {
            float g = master.Gain.Next();
            for (int c = 0; c < _channels; c++) master.Buffer[c * MaxBlock + f] *= g;
        }
        _limiter?.Process(master.Buffer, MaxBlock, n);
        for (int f = 0; f < n; f++)
            for (int c = 0; c < _channels; c++)
                output[f * _channels + c] = master.Buffer[c * MaxBlock + f];

        Volatile.Write(ref _time, blockStart + n);
    }

    private void Apply(in Command cmd, long now)
    {
        switch (cmd.Kind)
        {
            case CommandKind.Play:
            {
                var v = _voices[(cmd.Target & 0xFFFF) - 1];
                var clip = cmd.Clip!;
                v.Active = true; v.Stopping = false;
                v.Handle = cmd.Target; v.Clip = clip; v.Bus = cmd.Bus; v.Loop = cmd.Loop;
                v.Start = Math.Max(cmd.Time, now); v.Stop = long.MaxValue;
                v.Pos = cmd.Offset; v.Pitch = cmd.Pitch; v.Pan = cmd.Pan;
                v.Step = StepFor(clip, cmd.Pitch);
                v.Gain = default;
                v.Gain.Set(cmd.Ramp > 0 ? 0 : cmd.Value, 0);
                if (cmd.Ramp > 0) v.Gain.Set(cmd.Value, cmd.Ramp);
                v.Inputs = clip.Channels;
                SetMatrix(v, 0);
                break;
            }
            case CommandKind.Stop:
            {
                var v = Find(cmd.Target);
                if (v is null) break;
                v.Stop = Math.Max(cmd.Time, now);
                v.StopFade = Math.Max(1, cmd.Ramp);
                break;
            }
            case CommandKind.VoiceGain: Find(cmd.Target)?.Gain.Set(cmd.Value, cmd.Ramp); break;
            case CommandKind.VoicePan:
            {
                var v = Find(cmd.Target);
                if (v is null) break;
                v.Pan = cmd.Value;
                SetMatrix(v, cmd.Ramp);
                break;
            }
            case CommandKind.VoicePitch:
            {
                var v = Find(cmd.Target);
                if (v is null) break;
                v.Pitch = cmd.Value;
                v.Step = StepFor(v.Clip, cmd.Value);
                break;
            }
            case CommandKind.BusCreate:
            {
                var bus = _buses[cmd.Target];
                bus.Used = true; bus.Parent = cmd.Bus;
                bus.Gain = new Ramp { Current = cmd.Value, Target = cmd.Value };
                _busCount = Math.Max(_busCount, cmd.Target + 1);
                break;
            }
            case CommandKind.BusGain: _buses[cmd.Target].Gain.Set(cmd.Value, cmd.Ramp); break;
        }
    }

    private Voice? Find(int handle)
    {
        var v = _voices[(handle & 0xFFFF) - 1];
        return v.Active && v.Handle == handle ? v : null;
    }

    private long StepFor(AudioClip clip, float pitch) => (long)Math.Round((double)clip.SampleRate / _rate * pitch * One);

    /// <summary>
    /// Clip-to-output gains: mono clips pan between front left and right with the equal-power law (or go to the only
    /// speaker of a mono output); other clips map speaker to speaker through <see cref="ChannelLayout.MixMatrix"/>.
    /// </summary>
    private void SetMatrix(Voice v, int ramp)
    {
        var clip = v.Clip.Layout;
        int inputs = clip.Count;
        Span<float> target = stackalloc float[_channels * inputs];
        target.Clear();
        int fl = _layout.IndexOf(Speaker.FrontLeft), fr = _layout.IndexOf(Speaker.FrontRight);
        if (inputs == 1 && fl >= 0 && fr >= 0)
        {
            float angle = (Math.Clamp(v.Pan, -1, 1) + 1) * MathF.PI / 4;
            target[fl] = MathF.Cos(angle);
            target[fr] = MathF.Sin(angle);
        }
        else ChannelLayout.MixMatrix(clip, _layout).AsSpan().CopyTo(target);
        for (int i = 0; i < target.Length; i++) v.Matrix[i].Set(target[i], ramp);
    }

    private void RenderVoice(Voice v, long blockStart, int n)
    {
        int first = (int)Math.Clamp(v.Start - blockStart, 0, n);
        if (first >= n) return;
        var clip = v.Clip;
        var samples = clip.Samples;
        int inputs = v.Inputs, frames = clip.Frames, channels = _channels;
        long length = (long)frames << 32;
        var buffer = _buses[v.Bus].Buffer;
        var x = _frame.AsSpan(0, inputs);
        var matrix = v.Matrix;
        bool direct = v.Step == One;

        for (int f = first; f < n; f++)
        {
            if (blockStart + f >= v.Stop && !v.Stopping) { v.Stopping = true; v.Gain.Set(0, v.StopFade); }
            if (v.Pos >= length)
            {
                if (!v.Loop) { End(v, blockStart + f); return; }
                v.Pos %= length;
            }
            int i0 = (int)(v.Pos >> 32);
            if (direct && (v.Pos & 0xFFFFFFFF) == 0)
            {
                for (int c = 0; c < inputs; c++) x[c] = samples[i0 * inputs + c];
            }
            else
            {
                // Catmull-Rom (4-point cubic Hermite) between frames i0 and i0 + 1.
                float t = (uint)v.Pos * (1f / 4294967296f);
                int im = i0 - 1, i1 = i0 + 1, i2 = i0 + 2;
                for (int c = 0; c < inputs; c++)
                {
                    float ym = Sample(samples, im, c, inputs, frames, v.Loop), y0 = samples[i0 * inputs + c];
                    float y1 = Sample(samples, i1, c, inputs, frames, v.Loop), y2 = Sample(samples, i2, c, inputs, frames, v.Loop);
                    float a = 0.5f * (y1 - ym), b = ym - 2.5f * y0 + 2 * y1 - 0.5f * y2, d = 0.5f * (y2 - ym) + 1.5f * (y0 - y1);
                    x[c] = ((d * t + b) * t + a) * t + y0;
                }
            }
            float gain = v.Gain.Next();
            for (int o = 0; o < channels; o++)
            {
                float sum = 0;
                int row = o * inputs;
                for (int c = 0; c < inputs; c++) sum += matrix[row + c].Next() * x[c];
                buffer[o * MaxBlock + f] += sum * gain;
            }
            v.Pos += v.Step;
            if (v.Stopping && v.Gain.Remaining == 0) { End(v, blockStart + f + 1); return; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float Sample(float[] samples, int i, int c, int inputs, int frames, bool loop)
    {
        if ((uint)i < (uint)frames) return samples[i * inputs + c];
        if (!loop) return 0;
        i %= frames;
        if (i < 0) i += frames;
        return samples[i * inputs + c];
    }

    private void End(Voice v, long time)
    {
        v.Active = false;
        v.Clip = null!;
        _events.TryPush(new AudioEvent { Kind = AudioEventKind.VoiceEnded, Voice = v.Handle, Time = time });
    }
}
