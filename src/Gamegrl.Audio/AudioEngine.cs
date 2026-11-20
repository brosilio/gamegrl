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
/// it spreads over the speakers (0 a point, 1 everywhere), the Doppler factor, and per-band gains (air absorption,
/// occlusion) for below 800 Hz, 800 Hz–8 kHz and above.
/// </summary>
public struct SpatialParams
{
    public Vector3 Direction;
    public float Gain, Spread, Doppler;
    public float Low, Mid, High;

    public static SpatialParams Default => new() { Direction = -Vector3.UnitZ, Gain = 1, Doppler = 1, Low = 1, Mid = 1, High = 1 };
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

    public static PlayOptions Default => new() { Gain = 1, Pitch = 1, Spatial = SpatialParams.Default, ListenerOrientation = Quaternion.Identity };
}

public struct AudioEngineSettings
{
    public int MaxVoices, MaxBuses, CommandCapacity;
    /// <summary>A lookahead peak limiter on the master bus, so nothing clips at the device.</summary>
    public bool Limiter;
    public float LimiterCeilingDb;
    /// <summary>Render for headphones: mix into a virtual 7.1.4 bed heard through the spherical-head model (needs a stereo output).</summary>
    public bool Headphones;

    public static AudioEngineSettings Default => new() { MaxVoices = 256, MaxBuses = 64, CommandCapacity = 4096, Limiter = true, LimiterCeilingDb = -1 };
}

/// <summary>
/// The game's handle on audio. Calls come from one thread (the game's); they post commands to the audio thread and never
/// wait for it. Voices start and stop on exact samples of the engine timeline (<see cref="Time"/>). Call
/// <see cref="Update"/> once per frame to learn which voices have ended. Runs on a device (<see cref="Start"/>) or offline,
/// rendered on demand (<see cref="Offline"/>) for tests and bounces.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private readonly Mixer _mixer;
    private readonly SpscRing<Command> _commands;
    private readonly SpscRing<AudioEvent> _events;
    private readonly int[] _generations;
    private readonly bool[] _playing;
    private readonly int[] _free;
    private readonly int _maxBuses;
    private readonly Dictionary<ChannelLayout, float[]> _matrices = [];
    private int _freeCount, _busCount = 1, _dropped;
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
    public int ActiveVoices => _playing.Length - _freeCount;
    public int MaxVoices => _playing.Length;
    /// <summary>Commands lost because the queue was full (the audio thread fell behind).</summary>
    public int DroppedCommands => _dropped;

    private AudioEngine(int sampleRate, ChannelLayout layout, in AudioEngineSettings settings)
    {
        if (settings.MaxVoices is <= 0 or > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(settings), "MaxVoices must be 1..65535");
        _commands = new SpscRing<Command>(settings.CommandCapacity);
        _events = new SpscRing<AudioEvent>(settings.MaxVoices * 2);
        _mixer = new Mixer(sampleRate, layout, settings.Headphones, settings.MaxVoices, settings.MaxBuses, settings.Limiter, settings.LimiterCeilingDb, _commands, _events);
        _maxBuses = settings.MaxBuses;
        _generations = new int[settings.MaxVoices];
        _playing = new bool[settings.MaxVoices];
        _free = new int[settings.MaxVoices];
        for (int i = 0; i < _free.Length; i++) _free[i] = _free.Length - 1 - i;
        _freeCount = _free.Length;
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
        Update();
        if (_freeCount == 0) return default;
        int slot = _free[--_freeCount];
        int gen = _generations[slot] = (_generations[slot] + 1) & 0x7FFF;
        var handle = new VoiceHandle((gen << 16) | (slot + 1));
        _playing[slot] = true;
        Post(new Command
        {
            Kind = CommandKind.Play, Target = handle.Value, Clip = clip, Bus = options.Bus.Index, Loop = options.Loop,
            Time = options.StartTime, Value = options.Gain, Pan = options.Pan, Pitch = options.Pitch <= 0 ? 1 : options.Pitch,
            Ramp = Frames(options.FadeInSeconds), Offset = (long)Math.Round(options.Offset * clip.SampleRate * 4294967296.0),
            Positional = options.Positional, Spatial = options.Spatial, Rotation = Quaternion.Conjugate(Normalised(options.ListenerOrientation)),
            Matrix = !options.Positional && clip.Channels > 1 && !clip.Layout.IsAmbisonic ? MatrixFor(clip.Layout) : null,
        });
        return handle;
    }

    public VoiceHandle Play(AudioClip clip) => Play(clip, PlayOptions.Default);

    /// <summary>Fades the voice out over <paramref name="fadeSeconds"/> starting at engine sample <paramref name="at"/> (or now).</summary>
    public void Stop(VoiceHandle voice, float fadeSeconds = 0.005f, long at = 0)
    {
        if (IsPlaying(voice)) Post(new Command { Kind = CommandKind.Stop, Target = voice.Value, Time = at, Ramp = Math.Max(1, Frames(fadeSeconds)) });
    }

    public void SetGain(VoiceHandle voice, float gain, float rampSeconds = 0.01f)
    {
        if (IsPlaying(voice)) Post(new Command { Kind = CommandKind.VoiceGain, Target = voice.Value, Value = gain, Ramp = Frames(rampSeconds) });
    }

    public void SetPan(VoiceHandle voice, float pan, float rampSeconds = 0.01f)
    {
        if (IsPlaying(voice)) Post(new Command { Kind = CommandKind.VoicePan, Target = voice.Value, Value = pan, Ramp = Frames(rampSeconds) });
    }

    public void SetPitch(VoiceHandle voice, float pitch)
    {
        if (IsPlaying(voice) && pitch > 0) Post(new Command { Kind = CommandKind.VoicePitch, Target = voice.Value, Value = pitch });
    }

    /// <summary>Moves a positional voice: direction, gain, spread, Doppler and band gains, ramped over <paramref name="rampSeconds"/>.</summary>
    public void SetSpatial(VoiceHandle voice, in SpatialParams spatial, float rampSeconds = 0.02f)
    {
        if (IsPlaying(voice)) Post(new Command { Kind = CommandKind.VoiceSpatial, Target = voice.Value, Spatial = spatial, Ramp = Frames(rampSeconds) });
    }

    /// <summary>Ambisonic voices: the listener's current orientation, so the field stays fixed in the world as the head turns.</summary>
    public void SetListenerOrientation(VoiceHandle voice, Quaternion orientation, float rampSeconds = 0.02f)
    {
        if (IsPlaying(voice)) Post(new Command { Kind = CommandKind.VoiceRotation, Target = voice.Value, Rotation = Quaternion.Conjugate(Normalised(orientation)), Ramp = Frames(rampSeconds) });
    }

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

    public bool IsPlaying(VoiceHandle voice)
    {
        int slot = voice.Slot;
        return voice.IsValid && (uint)slot < (uint)_playing.Length && _playing[slot] && _generations[slot] == voice.Value >> 16;
    }

    /// <summary>Fades out every playing voice (leaving play mode, changing levels).</summary>
    public void StopAll(float fadeSeconds = 0.005f)
    {
        Update();
        for (int slot = 0; slot < _playing.Length; slot++)
            if (_playing[slot]) Stop(new VoiceHandle((_generations[slot] << 16) | (slot + 1)), fadeSeconds);
    }

    /// <summary>Takes in what the audio thread reported: ended voices free their slots.</summary>
    public void Update()
    {
        while (_events.TryPop(out var e))
        {
            if (e.Kind != AudioEventKind.VoiceEnded) continue;
            int slot = (e.Voice & 0xFFFF) - 1;
            if (!_playing[slot] || _generations[slot] != e.Voice >> 16) continue;
            _playing[slot] = false;
            _free[_freeCount++] = slot;
        }
    }

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
