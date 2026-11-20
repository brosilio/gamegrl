using System.Numerics;
using Gamegrl.Audio.Devices;

namespace Gamegrl.Audio;

/// <summary>A playing (or scheduled) voice. Stale handles are ignored safely.</summary>
public readonly record struct VoiceHandle(int Value)
{
    public bool IsValid => Value != 0;
    internal int Slot => (Value & 0xFFFF) - 1;
}

/// <summary>A mixer bus. <see cref="AudioEngine.Master"/> is the root; every other bus feeds a parent.</summary>
public readonly record struct BusHandle(int Index);

/// <summary>
/// Where a positional voice is heard from, computed by the game once per frame (see <see cref="Spatial.Propagation"/>):
/// the direction in listener space (+X right, +Y up, −Z forward), the linear gain (distance, directivity, HDR), how far
/// it spreads over the speakers (0 a point, 1 everywhere), the Doppler factor, per-band gains (air absorption,
/// occlusion) for below 800 Hz, 800 Hz–8 kHz and above, and its send to the room's reverb (its level before distance).
/// </summary>
public struct SpatialParams
{
    public Vector3 Direction;
    public float Gain, Spread, Doppler;
    public float Low, Mid, High;
    public float Reverb;

    public static SpatialParams Default => new() { Direction = -Vector3.UnitZ, Gain = 1, Doppler = 1, Low = 1, Mid = 1, High = 1 };
}

/// <summary>
/// An early reflection of a positional voice: where it comes from (listener space), how long after the direct sound it
/// arrives, and its amplitude relative to the direct sound.
/// </summary>
public struct Reflection
{
    public Vector3 Direction;
    public float Delay, Gain;
}

/// <summary>Up to <see cref="Count"/> reflections, inline (no allocation).</summary>
[System.Runtime.CompilerServices.InlineArray(Count)]
public struct ReflectionTaps
{
    public const int Count = 8;
    private Reflection _first;
}

public struct PlayOptions
{
    public BusHandle Bus;
    public float Gain;
    /// <summary>Mono clips: −1 (left) to 1 (right), equal-power.</summary>
    public float Pan;
    /// <summary>Playback rate on top of the clip's own sample rate (2 is an octave up).</summary>
    public float Pitch;
    public bool Loop;
    /// <summary>The engine sample to start on (see <see cref="AudioEngine.Time"/>); anything not in the future starts at once.</summary>
    public long StartTime;
    public float FadeInSeconds;
    /// <summary>Where in the clip to start, in seconds.</summary>
    public double Offset;
    /// <summary>Heard from a direction (<see cref="Spatial"/>, updated with <see cref="AudioEngine.SetSpatial"/>); the clip plays as mono.</summary>
    public bool Positional;
    public SpatialParams Spatial;
    /// <summary>Ambisonic clips: the listener's orientation, so the sound field stays fixed in the world.</summary>
    public Quaternion ListenerOrientation;
    /// <summary>Voices with higher priority are rendered first when there are more voices than the mixer renders (music, dialogue).</summary>
    public int Priority;

    public static PlayOptions Default => new() { Gain = 1, Pitch = 1, Spatial = SpatialParams.Default, ListenerOrientation = Quaternion.Identity };
}

public struct AudioEngineSettings
{
    /// <summary>Voices the game can have playing at once (tracked, not all rendered); up to 65535.</summary>
    public int MaxVoices;
    /// <summary>Voices the mixer renders: the most important of those playing (priority, then audibility).</summary>
    public int MaxRealVoices;
    public int MaxBuses, CommandCapacity;
    /// <summary>A lookahead peak limiter on the master bus, so nothing clips at the device.</summary>
    public bool Limiter;
    public float LimiterCeilingDb;
    /// <summary>Render for headphones: mix into a virtual 7.1.4 bed heard through the spherical-head model (needs a stereo output).</summary>
    public bool Headphones;

    public static AudioEngineSettings Default => new()
    {
        MaxVoices = 4096, MaxRealVoices = 128, MaxBuses = 64, CommandCapacity = 8192, Limiter = true, LimiterCeilingDb = -1,
    };
}

/// <summary>
/// The game's handle on audio. Calls come from one thread (the game's); they post commands to the audio thread and never
/// wait for it. Voices start and stop on exact samples of the engine timeline (<see cref="Time"/>).
/// <para>
/// Voices are virtual: up to <see cref="AudioEngineSettings.MaxVoices"/> play at once, and the mixer renders the
/// <see cref="AudioEngineSettings.MaxRealVoices"/> most important (priority first, then audibility — a positional
/// voice's spatial gain times its volume). The rest keep their place in the clip and their parameters, and come in with
/// a short fade when they rank high enough; inaudible voices are never rendered. Call <see cref="Update"/> once per frame:
/// it takes in ended voices, advances virtual ones and re-ranks.
/// </para>
/// Runs on a device (<see cref="Start"/>) or offline, rendered on demand (<see cref="Offline"/>) for tests and bounces.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private const int Reserve = 32;           // mixer slots beyond MaxRealVoices, for voices fading out while others fade in
    private const float SwapFadeSeconds = 0.01f;
    private const float InaudibleDb = -100;
    private const float StickinessDb = 3;     // a rendered voice keeps its place unless a virtual one beats it by this much

    private struct Voice
    {
        public bool Active, Real, Demoting, Stopping, Loop, Positional;
        public int Generation, Mixer, Bus, Priority, FadeIn, StopFade;
        public AudioClip Clip;
        public float Gain, Pan, Pitch;
        public SpatialParams Spatial;
        public Quaternion Rotation;
        public float[]? Matrix;
        public ReflectionTaps Taps;
        public int TapCount;
        public Acoustics.Bands3 Tone;
        public long Start, StopAt;
        public double Position; // clip frames, as of the last update
    }

    private readonly Mixer _mixer;
    private readonly SpscRing<Command> _commands;
    private readonly SpscRing<AudioEvent> _events;
    private readonly Voice[] _voices;
    private readonly int[] _free;
    private int _freeCount;
    private readonly int[] _mixerOwner, _mixerGeneration, _mixerFree;
    private int _mixerFreeCount;
    private readonly (float Key, int Voice)[] _ranking;
    private readonly int _maxReal, _maxBuses;
    private readonly Dictionary<ChannelLayout, float[]> _matrices = [];
    private int _realCount, _busCount = 1, _dropped, _activeCount;
    private long _lastTime;
    private IAudioDevice? _device;

    public int SampleRate => _mixer.SampleRate;
    /// <summary>The output's layout.</summary>
    public ChannelLayout Layout => _mixer.Layout;
    /// <summary>The layout everything mixes into: the output's, or the virtual 7.1.4 for headphones.</summary>
    public ChannelLayout BedLayout => _mixer.Bed;
    public bool Headphones => _mixer.Bed != _mixer.Layout;
    public BusHandle Master => new(0);
    /// <summary>The engine sample the audio thread renders next. Sounds scheduled at or after <c>Time + LatencyFrames</c> start exactly on time.</summary>
    public long Time => _mixer.Time;
    /// <summary>Frames between rendering a sample and hearing it: the device's buffering plus the limiter's lookahead.</summary>
    public long LatencyFrames => (_device?.LatencyFrames ?? 0) + _mixer.LimiterLatency;
    public IAudioDevice? Device => _device;
    /// <summary>Voices playing, rendered or not.</summary>
    public int ActiveVoices => _activeCount;
    /// <summary>Voices the mixer is rendering now.</summary>
    public int RealVoices => _realCount;
    public int MaxVoices => _voices.Length;
    public int MaxRealVoices => _maxReal;
    /// <summary>Commands lost because the queue was full (the audio thread fell behind).</summary>
    public int DroppedCommands => _dropped;

    private AudioEngine(int sampleRate, ChannelLayout layout, in AudioEngineSettings settings)
    {
        if (settings.MaxVoices is <= 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(settings), "MaxVoices must be 1..65535");
        int maxReal = Math.Clamp(settings.MaxRealVoices <= 0 ? 128 : settings.MaxRealVoices, 1, settings.MaxVoices);
        int mixerSlots = maxReal + Reserve;
        _commands = new SpscRing<Command>(settings.CommandCapacity);
        _events = new SpscRing<AudioEvent>(mixerSlots * 2);
        _mixer = new Mixer(sampleRate, layout, settings.Headphones, mixerSlots, settings.MaxBuses, settings.Limiter, settings.LimiterCeilingDb, _commands, _events);
        _maxBuses = settings.MaxBuses;
        _maxReal = maxReal;
        _voices = new Voice[settings.MaxVoices];
        _free = new int[settings.MaxVoices];
        for (int i = 0; i < _free.Length; i++) _free[i] = _free.Length - 1 - i;
        _freeCount = _free.Length;
        _mixerOwner = new int[mixerSlots];
        _mixerGeneration = new int[mixerSlots];
        _mixerFree = new int[mixerSlots];
        for (int i = 0; i < mixerSlots; i++) { _mixerOwner[i] = -1; _mixerFree[i] = mixerSlots - 1 - i; }
        _mixerFreeCount = mixerSlots;
        _ranking = new (float, int)[settings.MaxVoices];
    }

    /// <summary>An engine rendered by <see cref="Render"/> on the caller's thread.</summary>
    public static AudioEngine Offline(int sampleRate, ChannelLayout layout, AudioEngineSettings? settings = null) =>
        new(sampleRate, layout, settings ?? AudioEngineSettings.Default);

    /// <summary>An engine driven by a device's audio thread, in the device's rate and layout.</summary>
    public static AudioEngine Start(IAudioDevice device, AudioEngineSettings? settings = null)
    {
        var engine = new AudioEngine(device.SampleRate, device.Layout, settings ?? AudioEngineSettings.Default) { _device = device };
        device.Start(engine._mixer.Render);
        return engine;
    }

    /// <summary>Offline engines only: renders the next frames (interleaved, <see cref="Layout"/> order).</summary>
    public void Render(Span<float> interleaved)
    {
        if (_device is not null) throw new InvalidOperationException("A device drives this engine");
        _mixer.Render(interleaved, interleaved.Length / Layout.Count);
    }

    public VoiceHandle Play(AudioClip clip, in PlayOptions options)
    {
        TakeEvents();
        if (_freeCount == 0) return default;
        int i = _free[--_freeCount];
        ref var v = ref _voices[i];
        int gen = (v.Generation + 1) & 0x7FFF;
        v = new Voice
        {
            Active = true, Generation = gen, Clip = clip, Bus = options.Bus.Index, Loop = options.Loop, Positional = options.Positional && !clip.Layout.IsAmbisonic,
            Gain = options.Gain, Pan = options.Pan, Pitch = options.Pitch <= 0 ? 1 : options.Pitch, Spatial = options.Spatial, Priority = options.Priority,
            Rotation = Quaternion.Conjugate(Normalised(options.ListenerOrientation)), FadeIn = Frames(options.FadeInSeconds),
            Matrix = !options.Positional && clip.Channels > 1 && !clip.Layout.IsAmbisonic ? MatrixFor(clip.Layout) : null,
            Start = Math.Max(options.StartTime, Time), StopAt = long.MaxValue, Position = Math.Max(0, options.Offset * clip.SampleRate),
        };
        _activeCount++;
        var handle = new VoiceHandle((gen << 16) | (i + 1));
        // Render at once if there's room, or if it outranks the weakest rendered voice.
        float key = Key(in v);
        if (key > float.MinValue)
        {
            if (_realCount < _maxReal) Promote(i, exact: true);
            else
            {
                int weakest = -1;
                float weakestKey = float.MaxValue;
                for (int s = 0; s < _mixerOwner.Length; s++)
                {
                    int o = _mixerOwner[s];
                    if (o < 0 || !_voices[o].Real || _voices[o].Demoting || _voices[o].Stopping) continue;
                    float k = Key(in _voices[o]) + StickinessDb;
                    if (k < weakestKey) { weakestKey = k; weakest = o; }
                }
                if (weakest >= 0 && key > weakestKey && _mixerFreeCount > 0) { Demote(weakest); Promote(i, exact: true); }
            }
        }
        return handle;
    }

    public VoiceHandle Play(AudioClip clip) => Play(clip, PlayOptions.Default);

    /// <summary>Fades the voice out over <paramref name="fadeSeconds"/> starting at engine sample <paramref name="at"/> (or now).</summary>
    public void Stop(VoiceHandle voice, float fadeSeconds = 0.005f, long at = 0)
    {
        if (!TryGet(voice, out int i)) return;
        ref var v = ref _voices[i];
        long now = Time;
        long stop = Math.Max(at, now);
        if (stop >= v.StopAt) return;
        v.StopAt = stop;
        v.StopFade = Math.Max(1, Frames(fadeSeconds));
        if (Rendering(i)) ScheduleStop(ref v);
        else if (stop == now) End(i);
    }

    private void ScheduleStop(ref Voice v)
    {
        v.Stopping = true;
        Post(new Command { Kind = CommandKind.Stop, Target = v.Mixer, Time = v.StopAt, Ramp = v.StopFade });
    }

    public void SetGain(VoiceHandle voice, float gain, float rampSeconds = 0.01f)
    {
        if (!TryGet(voice, out int i)) return;
        _voices[i].Gain = gain;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoiceGain, Target = _voices[i].Mixer, Value = gain, Ramp = Frames(rampSeconds) });
    }

    public void SetPan(VoiceHandle voice, float pan, float rampSeconds = 0.01f)
    {
        if (!TryGet(voice, out int i)) return;
        _voices[i].Pan = pan;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoicePan, Target = _voices[i].Mixer, Value = pan, Ramp = Frames(rampSeconds) });
    }

    public void SetPitch(VoiceHandle voice, float pitch)
    {
        if (pitch <= 0 || !TryGet(voice, out int i)) return;
        _voices[i].Pitch = pitch;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoicePitch, Target = _voices[i].Mixer, Value = pitch });
    }

    /// <summary>Moves a positional voice: direction, gain, spread, Doppler and band gains, ramped over <paramref name="rampSeconds"/>.</summary>
    public void SetSpatial(VoiceHandle voice, in SpatialParams spatial, float rampSeconds = 0.02f)
    {
        if (!TryGet(voice, out int i)) return;
        _voices[i].Spatial = spatial;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoiceSpatial, Target = _voices[i].Mixer, Spatial = spatial, Ramp = Frames(rampSeconds) });
    }

    /// <summary>
    /// A positional voice's early reflections (at most <see cref="ReflectionTaps.Count"/>), all shaped by
    /// <paramref name="tone"/> (per-band gains relative to the mid band: the surfaces' colour). An empty span removes them.
    /// </summary>
    public void SetReflections(VoiceHandle voice, ReadOnlySpan<Reflection> taps, Acoustics.Bands3 tone, float rampSeconds = 0.02f)
    {
        if (!TryGet(voice, out int i) || !_voices[i].Positional) return;
        ref var v = ref _voices[i];
        int n = Math.Min(taps.Length, ReflectionTaps.Count);
        for (int k = 0; k < n; k++) v.Taps[k] = taps[k];
        v.TapCount = n;
        v.Tone = tone;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoiceReflections, Target = v.Mixer, Taps = v.Taps, TapCount = n, Tone = tone, Ramp = Frames(rampSeconds) });
    }

    /// <summary>Ambisonic voices: the listener's current orientation, so the field stays fixed in the world as the head turns.</summary>
    public void SetListenerOrientation(VoiceHandle voice, Quaternion orientation, float rampSeconds = 0.02f)
    {
        if (!TryGet(voice, out int i)) return;
        var rotation = Quaternion.Conjugate(Normalised(orientation));
        _voices[i].Rotation = rotation;
        if (Rendering(i)) Post(new Command { Kind = CommandKind.VoiceRotation, Target = _voices[i].Mixer, Rotation = rotation, Ramp = Frames(rampSeconds) });
    }

    /// <summary>The room's late reverb (decay per band, level, pre-delay), usually from <see cref="Acoustics.ReverbEstimator"/>; ramps over <paramref name="rampSeconds"/>.</summary>
    public void SetReverb(in Acoustics.ReverbParams reverb, float rampSeconds = 0.25f) =>
        Post(new Command { Kind = CommandKind.Reverb, Reverb = reverb, Ramp = Frames(rampSeconds) });

    /// <summary>Whether the mixer is rendering a voice right now (rather than tracking it virtually).</summary>
    public bool IsRendered(VoiceHandle voice) => TryGet(voice, out int i) && Rendering(i);

    private static Quaternion Normalised(Quaternion q) => q.LengthSquared() < 1e-12f ? Quaternion.Identity : Quaternion.Normalize(q);

    private float[] MatrixFor(ChannelLayout clip)
    {
        if (!_matrices.TryGetValue(clip, out var m)) _matrices[clip] = m = ChannelLayout.MixMatrix(clip, BedLayout);
        return m;
    }

    /// <summary>A new bus feeding <paramref name="parent"/>. Buses live as long as the engine.</summary>
    public BusHandle CreateBus(BusHandle parent, float gain = 1)
    {
        if (_busCount == _maxBuses) throw new InvalidOperationException("Too many buses");
        if ((uint)parent.Index >= (uint)_busCount) throw new ArgumentException("Unknown parent bus");
        var bus = new BusHandle(_busCount++);
        Post(new Command { Kind = CommandKind.BusCreate, Target = bus.Index, Bus = parent.Index, Value = gain });
        return bus;
    }

    public void SetBusGain(BusHandle bus, float gain, float rampSeconds = 0.01f) =>
        Post(new Command { Kind = CommandKind.BusGain, Target = bus.Index, Value = gain, Ramp = Frames(rampSeconds) });

    public bool IsPlaying(VoiceHandle voice) => TryGet(voice, out _);

    /// <summary>Fades out every playing voice (leaving play mode, changing levels).</summary>
    public void StopAll(float fadeSeconds = 0.005f)
    {
        TakeEvents();
        for (int i = 0; i < _voices.Length; i++)
            if (_voices[i].Active) Stop(new VoiceHandle((_voices[i].Generation << 16) | (i + 1)), fadeSeconds);
    }

    /// <summary>
    /// Once per frame: takes in what the audio thread reported (ended voices free their slots), advances virtual voices
    /// through their clips (ending one-shots that run out), and re-ranks: the most important voices are rendered.
    /// </summary>
    public void Update()
    {
        TakeEvents();
        long now = Time, previous = _lastTime;
        _lastTime = now;
        for (int i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (!v.Active) continue;
            // Every voice's position is tracked here too, so a rendered voice can go virtual and come back in place.
            long from = Math.Max(v.Start, previous);
            if (now > from)
            {
                v.Position += (now - from) * Rate(in v);
                if (v.Loop) v.Position %= v.Clip.Frames;
            }
            if (v.Real) continue;
            if (v.StopAt <= now || (!v.Loop && v.Position >= v.Clip.Frames)) End(i);
        }
        Rank();
    }

    private void Rank()
    {
        int n = 0;
        for (int i = 0; i < _voices.Length; i++)
        {
            ref var v = ref _voices[i];
            if (!v.Active || v.Stopping) continue;
            float key = Key(in v);
            if (key == float.MinValue) { if (v.Real && !v.Demoting) Demote(i); continue; }
            _ranking[n++] = (v.Real && !v.Demoting ? key + StickinessDb : key, i);
        }
        var ranked = _ranking.AsSpan(0, n);
        if (n > _maxReal) ranked.Sort(static (a, b) => b.Key.CompareTo(a.Key));
        int keep = Math.Min(n, _maxReal);
        for (int k = keep; k < n; k++) { int i = ranked[k].Voice; if (_voices[i].Real && !_voices[i].Demoting) Demote(i); }
        for (int k = 0; k < keep; k++) { int i = ranked[k].Voice; if (!_voices[i].Real && _realCount < _maxReal && _mixerFreeCount > 0) Promote(i, exact: false); }
    }

    /// <summary>Priority, then loudness in dB; <see cref="float.MinValue"/> for a voice too quiet to render.</summary>
    private static float Key(in Voice v)
    {
        float audibility = v.Gain * (v.Positional ? v.Spatial.Gain : 1);
        if (audibility <= 0) return float.MinValue;
        float db = 20 * MathF.Log10(audibility);
        return db < InaudibleDb ? float.MinValue : v.Priority * 1000f + db;
    }

    private double Rate(in Voice v) => (double)v.Clip.SampleRate / SampleRate * v.Pitch * (v.Positional ? v.Spatial.Doppler : 1);

    /// <summary>
    /// Starts rendering a voice. A voice that hasn't started yet (or is starting now, from <see cref="Play"/>) starts on
    /// its exact sample with its own fade; one coming back from virtual joins at its tracked position with a short fade.
    /// </summary>
    private void Promote(int i, bool exact)
    {
        ref var v = ref _voices[i];
        int slot = _mixerFree[--_mixerFreeCount];
        int gen = _mixerGeneration[slot] = (_mixerGeneration[slot] + 1) & 0x7FFF;
        v.Mixer = (gen << 16) | (slot + 1);
        v.Real = true;
        v.Demoting = false;
        _mixerOwner[slot] = i;
        _realCount++;
        bool fromStart = exact || v.Start > Time;
        Post(new Command
        {
            Kind = CommandKind.Play, Target = v.Mixer, Clip = v.Clip, Bus = v.Bus, Loop = v.Loop,
            Time = fromStart ? v.Start : 0, Value = v.Gain, Pan = v.Pan, Pitch = v.Pitch,
            Ramp = fromStart ? v.FadeIn : Frames(SwapFadeSeconds), Offset = (long)Math.Round(v.Position * 4294967296.0),
            Positional = v.Positional, Spatial = v.Spatial, Rotation = v.Rotation, Matrix = v.Matrix,
        });
        if (v.StopAt != long.MaxValue) ScheduleStop(ref v);
        if (v.TapCount > 0) Post(new Command { Kind = CommandKind.VoiceReflections, Target = v.Mixer, Taps = v.Taps, TapCount = v.TapCount, Tone = v.Tone });
    }

    /// <summary>Fades a rendered voice out; it stays playing, virtually, once the mixer lets it go.</summary>
    private void Demote(int i)
    {
        ref var v = ref _voices[i];
        v.Demoting = true;
        _realCount--;
        Post(new Command { Kind = CommandKind.Stop, Target = v.Mixer, Ramp = Frames(SwapFadeSeconds) });
    }

    private void TakeEvents()
    {
        while (_events.TryPop(out var e))
        {
            if (e.Kind != AudioEventKind.VoiceEnded) continue;
            int slot = (e.Voice & 0xFFFF) - 1;
            if ((uint)slot >= (uint)_mixerOwner.Length || _mixerGeneration[slot] != e.Voice >> 16) continue;
            int owner = _mixerOwner[slot];
            _mixerOwner[slot] = -1;
            _mixerFree[_mixerFreeCount++] = slot;
            if (owner < 0) continue;
            ref var v = ref _voices[owner];
            if (!v.Active || v.Mixer != e.Voice) continue;
            v.Mixer = 0;
            v.Real = false;
            if (v.Demoting) v.Demoting = false; // virtual now
            else { _realCount--; End(owner); }  // it ended, or was stopped
        }
    }

    private void End(int i)
    {
        ref var v = ref _voices[i];
        if (v.Mixer != 0)
        {
            // Still fading out in the mixer: the slot frees itself when the mixer reports it.
            int slot = (v.Mixer & 0xFFFF) - 1;
            if (_mixerOwner[slot] == i) _mixerOwner[slot] = -1;
            if (v.Real && !v.Demoting) { _realCount--; Post(new Command { Kind = CommandKind.Stop, Target = v.Mixer, Ramp = Frames(SwapFadeSeconds) }); }
        }
        v.Active = false;
        v.Real = v.Demoting = v.Stopping = false;
        v.Mixer = 0;
        v.Clip = null!;
        v.Matrix = null;
        _activeCount--;
        _free[_freeCount++] = i;
    }

    private bool TryGet(VoiceHandle voice, out int i)
    {
        i = voice.Slot;
        return voice.IsValid && (uint)i < (uint)_voices.Length && _voices[i].Active && _voices[i].Generation == voice.Value >> 16;
    }

    private bool Rendering(int i) => _voices[i].Real && !_voices[i].Demoting;

    /// <summary>Engine samples for a duration in seconds.</summary>
    public int Frames(double seconds) => seconds <= 0 ? 0 : (int)Math.Round(seconds * SampleRate);

    private void Post(in Command cmd)
    {
        if (!_commands.TryPush(cmd)) _dropped++;
    }

    public void Dispose()
    {
        _device?.Stop();
        _device?.Dispose();
        _device = null;
    }
}
