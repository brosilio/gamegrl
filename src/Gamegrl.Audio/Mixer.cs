using System.Numerics;
using System.Runtime.CompilerServices;
using Gamegrl.Audio.Dsp;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio;

internal enum CommandKind : byte { Play, Stop, VoiceGain, VoicePan, VoicePitch, VoiceSpatial, VoiceRotation, VoiceReflections, BusCreate, BusGain, Reverb, RemoteReverb }

/// <summary>A message from the game to the audio thread. Times are engine samples; ramps are in frames.</summary>
internal struct Command
{
    public CommandKind Kind;
    public bool Loop, Positional;
    public int Target;   // voice handle value, or bus index
    public int Bus;      // Play: the bus; BusCreate: the parent
    public int Ramp;
    public long Time;
    public long Offset;  // Play: start position in the clip, 32.32 fixed-point frames
    public float Value, Pan, Pitch;
    public SpatialParams Spatial;
    public Quaternion Rotation; // ambisonic voices: the field's rotation (the listener's inverse)
    public AudioClip? Clip;
    public float[]? Matrix;     // Play, multichannel clips: clip → bed gains, computed and cached on the game side
    public Acoustics.ReverbParams Reverb;
    public ReflectionTaps Taps;
    public int TapCount;
    public Acoustics.Bands3 Tone;
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

    /// <summary>Moves on by <paramref name="frames"/> steps at once.</summary>
    public void Advance(int frames)
    {
        if (Remaining == 0) return;
        if (frames >= Remaining) { Current = Target; Remaining = 0; }
        else { Current += Step * frames; Remaining -= frames; }
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
/// The audio thread's side of the engine: voices, buses and the master chain. Everything mixes into the bed (the output
/// layout for speakers; a virtual 7.1.4 for headphones, rendered binaurally at the end). It owns all its state; the
/// game reaches it only through the command ring and hears back through the event ring. <see cref="Render"/> never
/// allocates, locks or blocks.
/// </summary>
internal sealed class Mixer
{
    public const int MaxBlock = 512;
    public const int MaxClipChannels = 18;
    private const long One = 1L << 32;

    private sealed class Voice
    {
        public bool Active, Stopping, Positional;
        public int Handle, Bus, StopFade;
        public AudioClip Clip = null!;
        public long Start, Stop = long.MaxValue;
        public long Pos, Step;  // 32.32 fixed-point frames into the clip
        public float Pitch = 1, Doppler = 1, Pan;
        public bool Loop;
        public Ramp Gain;
        public Ramp[] Matrix = null!; // bed channel o, input i at [o * Inputs + i]
        public int Inputs;            // clip channels, or 1 for positional voices (the clip mixed to mono)
        public BandEq3 Eq;
        public Ramp ReverbSend, RemoteSend;
        public SpatialParams Spatial;
        // Early reflections: a delay line of the voice's dry signal (through the surfaces' tone), read by taps.
        public float[] Delay = null!;
        public int DelayPos, TapCount, LiveTaps; // TapCount includes removed taps still fading out
        public int Tail;                         // after the clip ends: frames left for the reflections to sound
        public bool DelayDirty;
        public BandEq3 ReflectionEq;
        public ReflectionTaps Taps;
        public Ramp[] TapDelay = null!;   // samples
        public Ramp[] TapGains = null!;   // tap k, bed channel o at [k * bed + o]
        public Quaternion Rotation = Quaternion.Identity;
    }

    private sealed class Bus
    {
        public bool Used;
        public int Parent;
        public Ramp Gain;
        public float[] Buffer = null!; // planar: channel c at [c * MaxBlock ..]
    }

    private readonly int _bedChannels, _outChannels, _rate;
    private readonly ChannelLayout _bed, _output;
    private readonly Panner _panner;
    private readonly AmbisonicDecoder _ambisonics;
    private readonly BinauralVirtualizer? _binaural;
    private readonly Fdn _reverb, _remote;
    private readonly float[] _reverbInput = new float[MaxBlock], _remoteInput = new float[MaxBlock];
    private readonly float[] _outBuffer;
    private readonly float[] _frame = new float[MaxClipChannels];
    private readonly float[] _scratch;
    private readonly float[] _voiceBlock = new float[MaxClipChannels * MaxBlock]; // a voice's block: planar inputs (or mono)
    private readonly float[] _dry = new float[MaxBlock], _tap = new float[MaxBlock];
    private readonly float[] _panScratch;
    public const float MaxReflectionDelaySeconds = 0.12f;
    private readonly int _delayLength;
    private readonly Voice[] _voices;
    private readonly Bus[] _buses;
    private readonly SpscRing<Command> _commands;
    private readonly SpscRing<AudioEvent> _events;
    private readonly Limiter? _limiter;
    private int _busCount = 1;
    private long _time;

    public int SampleRate => _rate;
    public ChannelLayout Layout => _output;
    public ChannelLayout Bed => _bed;
    public int LimiterLatency => _limiter?.LatencyFrames ?? 0;
    /// <summary>The engine sample of the next frame to render; readable from any thread.</summary>
    public long Time => Volatile.Read(ref _time);

    public Mixer(int sampleRate, ChannelLayout output, bool headphones, int maxVoices, int maxBuses, bool limiter, float limiterCeilingDb,
        SpscRing<Command> commands, SpscRing<AudioEvent> events)
    {
        if (output.IsAmbisonic) throw new ArgumentException("The output must be speakers");
        if (headphones && output.Count != 2) throw new ArgumentException("Headphone rendering needs a stereo output");
        _rate = sampleRate;
        _output = output;
        _bed = headphones ? ChannelLayout.Surround714 : output;
        _bedChannels = _bed.Count;
        _outChannels = output.Count;
        _panner = new Panner(_bed);
        _ambisonics = new AmbisonicDecoder(_bed);
        _binaural = headphones ? new BinauralVirtualizer(_bed, sampleRate) : null;
        _reverb = new Fdn(_bed, sampleRate);
        _remote = new Fdn(_bed, sampleRate, focused: true);
        _outBuffer = new float[_outChannels * MaxBlock];
        _scratch = new float[_bedChannels * MaxClipChannels];
        _panScratch = new float[_bedChannels];
        _delayLength = (int)(MaxReflectionDelaySeconds * sampleRate) + MaxBlock + 4;
        _limiter = limiter ? new Limiter(_outChannels, sampleRate, limiterCeilingDb) : null;
        _commands = commands;
        _events = events;
        _voices = new Voice[maxVoices];
        for (int i = 0; i < maxVoices; i++)
            _voices[i] = new Voice
            {
                Matrix = new Ramp[_bedChannels * MaxClipChannels], Delay = new float[_delayLength],
                TapDelay = new Ramp[ReflectionTaps.Count], TapGains = new Ramp[ReflectionTaps.Count * _bedChannels],
            };
        _buses = new Bus[maxBuses];
        for (int i = 0; i < maxBuses; i++) _buses[i] = new Bus { Buffer = new float[_bedChannels * MaxBlock], Gain = new Ramp { Current = 1, Target = 1 } };
        _buses[0].Used = true;
    }

    /// <summary>Renders interleaved frames in the output layout's channel order.</summary>
    public void Render(Span<float> interleaved, int frames)
    {
        int done = 0;
        while (done < frames)
        {
            int n = Math.Min(MaxBlock, frames - done);
            RenderBlock(interleaved.Slice(done * _outChannels, n * _outChannels), n);
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
            for (int c = 0; c < _bedChannels; c++) Array.Clear(bus.Buffer, c * MaxBlock, n);
        }
        Array.Clear(_reverbInput, 0, n);
        Array.Clear(_remoteInput, 0, n);
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
                for (int c = 0; c < _bedChannels; c++) parent[c * MaxBlock + f] += bus.Buffer[c * MaxBlock + f] * g;
            }
        }
        var master = _buses[0].Buffer;
        _reverb.Process(_reverbInput.AsSpan(0, n), master, MaxBlock, n);
        _remote.Process(_remoteInput.AsSpan(0, n), master, MaxBlock, n);
        for (int f = 0; f < n; f++)
        {
            float g = _buses[0].Gain.Next();
            for (int c = 0; c < _bedChannels; c++) master[c * MaxBlock + f] *= g;
        }
        var final = master;
        if (_binaural is not null) { _binaural.Process(master, _outBuffer, MaxBlock, n); final = _outBuffer; }
        _limiter?.Process(final, MaxBlock, n);
        for (int f = 0; f < n; f++)
            for (int c = 0; c < _outChannels; c++)
                output[f * _outChannels + c] = final[c * MaxBlock + f];

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
                v.Positional = cmd.Positional && !clip.Layout.IsAmbisonic;
                v.Spatial = cmd.Spatial;
                v.Doppler = v.Positional ? cmd.Spatial.Doppler : 1;
                v.Rotation = cmd.Rotation;
                v.Step = StepFor(v);
                v.Gain = default;
                v.Gain.Set(cmd.Ramp > 0 ? 0 : cmd.Value, 0);
                if (cmd.Ramp > 0) v.Gain.Set(cmd.Value, cmd.Ramp);
                v.Inputs = v.Positional ? 1 : clip.Channels;
                v.Eq.Init(_rate);
                v.ReverbSend = default;
                v.RemoteSend = default;
                v.TapCount = v.LiveTaps = 0;
                v.Tail = 0;
                v.DelayDirty = true;
                v.ReflectionEq.Init(_rate);
                if (v.Positional) { v.Eq.Set(cmd.Spatial.Low, cmd.Spatial.Mid, cmd.Spatial.High, 0); v.ReverbSend.Set(cmd.Spatial.Reverb, 0); v.RemoteSend.Set(cmd.Spatial.RemoteReverb, 0); }
                SetMatrix(v, 0, cmd.Matrix);
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
                if (v is null || v.Positional || v.Inputs != 1) break;
                v.Pan = cmd.Value;
                SetMatrix(v, cmd.Ramp, null);
                break;
            }
            case CommandKind.VoicePitch:
            {
                var v = Find(cmd.Target);
                if (v is null) break;
                v.Pitch = cmd.Value;
                v.Step = StepFor(v);
                break;
            }
            case CommandKind.VoiceSpatial:
            {
                var v = Find(cmd.Target);
                if (v is null || !v.Positional) break;
                v.Spatial = cmd.Spatial;
                v.Doppler = cmd.Spatial.Doppler;
                v.Step = StepFor(v);
                v.Eq.Set(cmd.Spatial.Low, cmd.Spatial.Mid, cmd.Spatial.High, cmd.Ramp);
                v.ReverbSend.Set(cmd.Spatial.Reverb, cmd.Ramp);
                v.RemoteSend.Set(cmd.Spatial.RemoteReverb, cmd.Ramp);
                SetMatrix(v, cmd.Ramp, null);
                SetTaps(v, cmd.Ramp, delays: false);
                break;
            }
            case CommandKind.VoiceReflections:
            {
                var v = Find(cmd.Target);
                if (v is null || !v.Positional) break;
                if (v.DelayDirty) { Array.Clear(v.Delay); v.DelayDirty = false; }
                // Taps that are new start at their delay; existing ones glide to the new delay (Doppler on the reflection).
                int previous = v.TapCount;
                v.Taps = cmd.Taps;
                v.TapCount = v.LiveTaps = cmd.TapCount;
                for (int k = previous; k < v.TapCount; k++)
                {
                    v.TapDelay[k] = default;
                    v.TapDelay[k].Set(DelaySamples(v.Taps[k].Delay), 0);
                    for (int o = 0; o < _bedChannels; o++) v.TapGains[k * _bedChannels + o] = default;
                }
                for (int k = v.TapCount; k < previous; k++) for (int o = 0; o < _bedChannels; o++) v.TapGains[k * _bedChannels + o].Set(0, cmd.Ramp);
                if (v.TapCount < previous) v.TapCount = previous; // fade the removed taps out; they stay until silent
                v.ReflectionEq.Set(cmd.Tone.Low, cmd.Tone.Mid, cmd.Tone.High, cmd.Ramp);
                SetTaps(v, cmd.Ramp, delays: true, instantFrom: previous);
                break;
            }
            case CommandKind.VoiceRotation:
            {
                var v = Find(cmd.Target);
                if (v is null || !v.Clip.Layout.IsAmbisonic) break;
                v.Rotation = cmd.Rotation;
                SetMatrix(v, cmd.Ramp, null);
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
            case CommandKind.Reverb: _reverb.Set(cmd.Reverb, cmd.Ramp); break;
            case CommandKind.RemoteReverb:
            {
                _remote.Set(cmd.Reverb, cmd.Ramp);
                var pan = _panScratch.AsSpan();
                _panner.Pan(cmd.Spatial.Direction, cmd.Spatial.Spread, pan);
                _remote.Focus(pan, cmd.Ramp);
                break;
            }
        }
    }

    private Voice? Find(int handle)
    {
        var v = _voices[(handle & 0xFFFF) - 1];
        return v.Active && v.Handle == handle ? v : null;
    }

    private long StepFor(Voice v) => (long)Math.Round((double)v.Clip.SampleRate / _rate * v.Pitch * v.Doppler * One);

    /// <summary>
    /// Input-to-bed gains: positional voices pan with VBAP and carry their distance/HDR gain; ambisonic clips decode with
    /// the field's rotation; other mono clips pan between front left and right with the equal-power law; other clips
    /// use the speaker-to-speaker matrix the game sent.
    /// </summary>
    private void SetMatrix(Voice v, int ramp, float[]? matrix)
    {
        var clip = v.Clip.Layout;
        int inputs = v.Inputs;
        var target = _scratch.AsSpan(0, _bedChannels * inputs);
        target.Clear();
        if (v.Positional)
        {
            _panner.Pan(v.Spatial.Direction, v.Spatial.Spread, target);
            for (int o = 0; o < _bedChannels; o++) target[o] *= v.Spatial.Gain;
        }
        else if (clip.IsAmbisonic) _ambisonics.Fill(v.Rotation, target);
        else if (inputs == 1)
        {
            int fl = _bed.IndexOf(Speaker.FrontLeft), fr = _bed.IndexOf(Speaker.FrontRight);
            if (fl >= 0 && fr >= 0)
            {
                float angle = (Math.Clamp(v.Pan, -1, 1) + 1) * MathF.PI / 4;
                target[fl] = MathF.Cos(angle);
                target[fr] = MathF.Sin(angle);
            }
            else target[_bed.Has(Speaker.FrontCenter) ? _bed.IndexOf(Speaker.FrontCenter) : 0] = 1;
        }
        else if (matrix is not null) matrix.AsSpan(0, target.Length).CopyTo(target);
        else return; // keep the current gains (only Play supplies a matrix)
        for (int i = 0; i < target.Length; i++) v.Matrix[i].Set(target[i], ramp);
    }

    /// <summary>
    /// Renders a voice in two passes: its samples for the block (resampled, filtered and with its gain; positional voices
    /// as mono), then each input added into each bed channel with its gain ramp, skipping silent channels (VBAP feeds two
    /// or three speakers) and vectorised.
    /// </summary>
    private float DelaySamples(float seconds) => Math.Clamp(seconds * _rate, 1, _delayLength - MaxBlock - 3);

    /// <summary>Each tap's gains: VBAP from its direction, times its gain relative to the direct sound, times the voice's spatial gain.</summary>
    private void SetTaps(Voice v, int ramp, bool delays, int instantFrom = int.MaxValue)
    {
        var pan = _panScratch.AsSpan();
        for (int k = 0; k < v.LiveTaps; k++)
        {
            ref var tap = ref v.Taps[k];
            _panner.Pan(tap.Direction, 0, pan);
            float g = tap.Gain * v.Spatial.Gain;
            int r = k >= instantFrom ? 0 : ramp; // new taps start at their level; existing ones glide
            for (int o = 0; o < _bedChannels; o++) v.TapGains[k * _bedChannels + o].Set(pan[o] * g, r);
            if (delays) v.TapDelay[k].Set(DelaySamples(tap.Delay), Math.Max(ramp, 1));
        }
    }

    /// <summary>Writes the voice's dry block into its delay line (through the surfaces' tone) and adds each tap into the bed.</summary>
    private void RenderReflections(Voice v, int first, int count)
    {
        var dry = _dry.AsSpan(first, count);
        v.ReflectionEq.Process(dry);
        var line = v.Delay;
        int length = line.Length, start = v.DelayPos;
        for (int i = 0; i < count; i++) line[(start + i) % length] = dry[i];
        v.DelayPos = (start + count) % length;
        var buffer = _buses[v.Bus].Buffer;
        int active = 0;
        for (int k = 0; k < v.TapCount; k++)
        {
            bool silent = true;
            for (int o = 0; o < _bedChannels && silent; o++) { ref var g = ref v.TapGains[k * _bedChannels + o]; silent = g.Current == 0 && g.Remaining == 0; }
            if (silent) continue;
            active = k + 1;
            var tap = _tap.AsSpan(first, count);
            ref var delay = ref v.TapDelay[k];
            for (int i = 0; i < count; i++)
            {
                // Linear interpolation at a fractional delay behind the write position.
                float d = delay.Next();
                float pos = start + i - d;
                int i0 = (int)MathF.Floor(pos);
                float frac = pos - i0;
                int a = ((i0 % length) + length) % length, b = a + 1 == length ? 0 : a + 1;
                tap[i] = line[a] + (line[b] - line[a]) * frac;
            }
            for (int o = 0; o < _bedChannels; o++) AddRamped(buffer.AsSpan(o * MaxBlock + first, count), tap, ref v.TapGains[k * _bedChannels + o]);
        }
        v.TapCount = Math.Max(active, v.LiveTaps); // removed taps that have faded to silence are dropped
    }

    /// <summary>The longest current tap delay, in frames: how long the reflections outlast the clip.</summary>
    private int ReflectionTail(Voice v)
    {
        float longest = 0;
        for (int k = 0; k < v.TapCount; k++) longest = MathF.Max(longest, MathF.Max(v.TapDelay[k].Current, v.TapDelay[k].Target));
        return (int)MathF.Ceiling(longest) + 2;
    }

    private void RenderVoice(Voice v, long blockStart, int n)
    {
        if (v.Tail > 0)
        {
            // The clip has ended; its reflections are still on their way.
            int remaining = Math.Min(n, v.Tail);
            _dry.AsSpan(0, remaining).Clear();
            RenderReflections(v, 0, remaining);
            v.Tail -= remaining;
            if (v.Tail <= 0 || v.TapCount == 0) End(v, blockStart + remaining);
            return;
        }
        if (v.Stop <= v.Start && v.Stop < blockStart + n)
        {
            End(v, v.Stop);
            return;
        }
        int first = (int)Math.Clamp(v.Start - blockStart, 0, n);
        if (first >= n) return;
        var clip = v.Clip;
        var samples = clip.Samples;
        int clipChannels = clip.Channels, frames = clip.Frames, inputs = v.Inputs;
        long length = (long)frames << 32;
        var x = _frame.AsSpan(0, clipChannels);
        var block = _voiceBlock;
        bool direct = v.Step == One, positional = v.Positional, ended = false;
        float toMono = 1f / clipChannels;
        int f = first;
        for (; f < n; f++)
        {
            if (blockStart + f >= v.Stop && !v.Stopping) { v.Stopping = true; v.Gain.Set(0, v.StopFade); }
            if (v.Pos >= length)
            {
                if (!v.Loop) { ended = true; break; }
                v.Pos %= length;
            }
            int i0 = (int)(v.Pos >> 32);
            if (direct && (v.Pos & 0xFFFFFFFF) == 0)
            {
                for (int c = 0; c < clipChannels; c++) x[c] = samples[i0 * clipChannels + c];
            }
            else
            {
                // Catmull-Rom (4-point cubic Hermite) between frames i0 and i0 + 1.
                float t = (uint)v.Pos * (1f / 4294967296f);
                int im = i0 - 1, i1 = i0 + 1, i2 = i0 + 2;
                for (int c = 0; c < clipChannels; c++)
                {
                    float ym = Sample(samples, im, c, clipChannels, frames, v.Loop), y0 = samples[i0 * clipChannels + c];
                    float y1 = Sample(samples, i1, c, clipChannels, frames, v.Loop), y2 = Sample(samples, i2, c, clipChannels, frames, v.Loop);
                    float a = 0.5f * (y1 - ym), b = ym - 2.5f * y0 + 2 * y1 - 0.5f * y2, d = 0.5f * (y2 - ym) + 1.5f * (y0 - y1);
                    x[c] = ((d * t + b) * t + a) * t + y0;
                }
            }
            float gain = v.Gain.Next();
            if (positional)
            {
                float mono = 0;
                for (int c = 0; c < clipChannels; c++) mono += x[c];
                block[f] = mono * toMono * gain;
            }
            else for (int c = 0; c < inputs; c++) block[c * MaxBlock + f] = x[c] * gain;
            v.Pos += v.Step;
            if (v.Stopping && v.Gain.Remaining == 0) { f++; ended = true; break; }
        }

        int count = f - first;
        bool tail = ended && positional && v.TapCount > 0;
        if (positional && (count > 0 || tail))
        {
            if (v.TapCount > 0)
            {
                block.AsSpan(first, count).CopyTo(_dry.AsSpan(first, count));
                if (tail) _dry.AsSpan(f, n - f).Clear(); // silence after the end keeps the echoes on time within this block
            }
            v.Eq.Process(block.AsSpan(first, count));
            if (v.TapCount > 0) RenderReflections(v, first, tail ? n - first : count);
            AddRamped(_reverbInput.AsSpan(first, count), block.AsSpan(first, count), ref v.ReverbSend);
            AddRamped(_remoteInput.AsSpan(first, count), block.AsSpan(first, count), ref v.RemoteSend);
        }
        if (count > 0)
        {
            var buffer = _buses[v.Bus].Buffer;
            var matrix = v.Matrix;
            for (int o = 0; o < _bedChannels; o++)
            {
                var dst = buffer.AsSpan(o * MaxBlock + first, count);
                for (int c = 0; c < inputs; c++) AddRamped(dst, block.AsSpan(c * MaxBlock + first, count), ref matrix[o * inputs + c]);
            }
        }
        if (ended)
        {
            if (tail) { v.Tail = ReflectionTail(v) - (n - f); if (v.Tail <= 0) End(v, blockStart + n); }
            else End(v, blockStart + f);
        }
    }

    /// <summary><c>dst[i] += src[i] · g(i)</c> for a linear ramp g, advanced by the span's length; nothing when it's silent.</summary>
    private static void AddRamped(Span<float> dst, ReadOnlySpan<float> src, ref Ramp g)
    {
        int n = src.Length, i = 0;
        if (g.Remaining > 0)
        {
            int m = Math.Min(g.Remaining, n);
            float start = g.Current, step = g.Step;
            if (Vector.IsHardwareAccelerated && m >= Vector<float>.Count)
            {
                int w = Vector<float>.Count;
                Span<float> lanes = stackalloc float[w];
                for (int k = 0; k < w; k++) lanes[k] = k + 1;
                var gains = new Vector<float>(start) + new Vector<float>(lanes) * step;
                var stride = new Vector<float>(step * w);
                for (; i + w <= m; i += w)
                {
                    var d = new Vector<float>(dst.Slice(i));
                    (d + new Vector<float>(src.Slice(i)) * gains).CopyTo(dst.Slice(i));
                    gains += stride;
                }
            }
            for (; i < m; i++) dst[i] += src[i] * (start + step * (i + 1));
            g.Remaining -= m;
            g.Current = g.Remaining == 0 ? g.Target : start + step * m;
        }
        float c = g.Current;
        if (c == 0 || i == n) return;
        if (Vector.IsHardwareAccelerated)
        {
            int w = Vector<float>.Count;
            var vc = new Vector<float>(c);
            for (; i + w <= n; i += w) (new Vector<float>(dst.Slice(i)) + new Vector<float>(src.Slice(i)) * vc).CopyTo(dst.Slice(i));
        }
        for (; i < n; i++) dst[i] += src[i] * c;
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
