using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Audio.Acoustics;
using Gamegrl.Audio.Devices;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio;

public enum AudioOutput : byte
{
    /// <summary>The platform's default output (ALSA on Linux), or the null device when there is none.</summary>
    Device,
    /// <summary>No hardware: rendered in real time and discarded (servers, CI).</summary>
    Null,
    /// <summary>Rendered only when <see cref="AudioEngine.Render"/> is called (tests, bounces).</summary>
    Offline,
}

/// <summary>
/// How a positional sound carries: full level within <see cref="MinDistance"/>, −6 dB per doubling beyond it (scaled by
/// <see cref="Rolloff"/>), silent past <see cref="MaxDistance"/>; how loud it is in the world (<see cref="LoudnessDb"/>
/// at 1 m, for HDR mixing); a directivity cone along the entity's forward axis (−Z), whole angles in degrees; its size
/// (<see cref="Radius"/>: near or large sources spread over the speakers); and how much Doppler it gets.
/// </summary>
public struct AudioEmitter
{
    [Range(0.01, 100)] public float MinDistance;
    [Range(1, 10000)] public float MaxDistance;
    [Range(0, 4)] public float Rolloff;
    [Range(0, 200)] public float LoudnessDb;
    [Range(0, 360)] public float ConeInner;
    [Range(0, 360)] public float ConeOuter;
    [Range(0, 1)] public float ConeOuterGain;
    [Range(0, 50)] public float Radius;
    [Range(0, 4)] public float DopplerScale;
    /// <summary>Acoustic surfaces between it and the listener muffle it (see <see cref="AcousticSurface"/>).</summary>
    public bool Occlusion;
    /// <summary>Early reflections off acoustic surfaces (for the loudest few voices).</summary>
    public bool Reflections;

    public static AudioEmitter Default => new()
    {
        MinDistance = 1, MaxDistance = 500, Rolloff = 1, LoudnessDb = 80, ConeInner = 360, ConeOuter = 360, ConeOuterGain = 1, DopplerScale = 1, Occlusion = true, Reflections = true,
    };
}

/// <summary>
/// A sound on an entity: its clip, level and pitch. Positional sources are heard from the entity's position (its
/// <see cref="LocalToWorld"/>) by the <see cref="AudioListener"/>, shaped by <see cref="Emitter"/>; others play as is,
/// mono clips panned by <see cref="Pan"/>. Ambisonic clips play as a sound field fixed in the world. It starts when the
/// entity first runs with <see cref="PlayOnStart"/>, or whenever gameplay sets <see cref="Play"/>; <see cref="Stop"/>
/// fades it out. Despawning the entity or removing the component stops its voice.
/// </summary>
public struct AudioSource : IComponent
{
    public AssetRef<AudioAsset> Clip;
    [Range(0, 4)] public float Volume;
    [Range(0.25, 4)] public float Pitch;
    [Range(-1, 1)] public float Pan;
    public bool Loop;
    public bool PlayOnStart;
    public bool Positional;
    public AudioEmitter Emitter;
    /// <summary>Set to start (or restart) the clip; the audio system clears it.</summary>
    public bool Play;
    /// <summary>Set to fade the clip out; the audio system clears it.</summary>
    public bool Stop;
    [NoSerialize] public bool IsPlaying;
    [NoSerialize] public bool Started;
    [NoSerialize] public int Voice;

    public static AudioSource Default => new() { Volume = 1, Pitch = 1, PlayOnStart = true, Emitter = AudioEmitter.Default };
    public static AudioSource Of(AssetRef<AudioAsset> clip, bool loop = false) => Default with { Clip = clip, Loop = loop };
    /// <summary>A positional source heard from its entity.</summary>
    public static AudioSource At(AssetRef<AudioAsset> clip, bool loop = false) => Default with { Clip = clip, Loop = loop, Positional = true };
}

/// <summary>Where the game hears from: put it on the camera. The first one found is used.</summary>
public struct AudioListener : IComponent
{
    [Range(0, 4)] public float Gain;

    public static AudioListener Default => new() { Gain = 1 };
}

/// <summary>
/// The air and the mix: temperature and humidity set the speed of sound and air absorption (ISO 9613-1); Doppler can
/// be scaled; HDR mixing (see <see cref="HdrWindow"/>) maps world loudness to the output with a moving window.
/// </summary>
public struct AudioEnvironment
{
    [Range(-30, 50)] public float TemperatureC;
    [Range(0, 100)] public float HumidityPercent;
    [Range(0, 4)] public float DopplerScale;
    public bool Hdr;
    [Range(10, 120)] public float HdrRangeDb;
    [Range(1, 100)] public float HdrReleaseDbPerSecond;
    [Range(0, 140)] public float HdrFloorDb;
    /// <summary>How far below full scale the window's top plays, leaving room for sounds that overlap.</summary>
    [Range(0, 24)] public float HdrHeadroomDb;
    /// <summary>The room's late reverb, estimated from the acoustic surfaces around the listener.</summary>
    public bool Reverb;
    /// <summary>Early reflections off acoustic surfaces, for the <see cref="AudioUpdate.ReflectionVoices"/> loudest positional voices.</summary>
    public bool Reflections;
    /// <summary>Hidden sources heard round the edges in the way (see <see cref="Diffraction"/>).</summary>
    public bool Diffraction;
    /// <summary>A loud hidden source's own room reverberating, heard through the opening its sound comes round.</summary>
    public bool RemoteReverb;

    public static AudioEnvironment Default => new()
    {
        TemperatureC = 20, HumidityPercent = 50, DopplerScale = 1, Hdr = true, HdrRangeDb = 60, HdrReleaseDbPerSecond = 12, HdrFloorDb = 50, HdrHeadroomDb = 6,
        Reverb = true, Reflections = true, Diffraction = true, RemoteReverb = true,
    };
}

/// <summary>
/// The audio resource: the <see cref="AudioEngine"/>, the clips loaded from the asset source (reloaded when their asset
/// is reimported; voices already playing keep the clip they started with), the listener as last seen, the HDR window,
/// and positional one-shots.
/// </summary>
public sealed class AudioContext : IDisposable
{
    private readonly Dictionary<Guid, AudioClip?> _clips = [];
    private readonly Dictionary<Guid, AcousticProbeGrid?> _probes = [];
    private IAssetSource? _assets;
    /// <summary>This frame's baked probe volumes (set by the audio update).</summary>
    internal readonly List<AcousticProbeGrid> Grids = [];

    internal struct OneShot
    {
        public VoiceHandle Voice;
        public Vector3 Position;
        public AudioEmitter Emitter;
        public float Volume;
        /// <summary>The entity it moves with (<see cref="PlayOn"/>), at <see cref="Offset"/> in its space.</summary>
        public Entity Follow;
        public Vector3 Offset;
    }

    /// <summary>The world whose entities one-shots can follow.</summary>
    internal World? World;

    /// <summary>The direct distance's gain under an emitter's distance law (for scaling reflections relative to it).</summary>
    internal static float DistanceLaw(float distance, in AudioEmitter emitter) =>
        Propagation.DistanceGain(MathF.Max(distance, emitter.MinDistance), emitter.MinDistance, float.MaxValue, emitter.Rolloff);

    /// <summary>
    /// Reflection paths as taps for a listener, from each path's image of the source where it is now: direction toward
    /// the image (listener space; the line to it passes through the last reflection point), delay and gain relative to
    /// the direct sound; and the surfaces' tone from the strongest. Returns how many taps; <paramref name="keys"/>, when
    /// given, gets each tap's path key.
    /// </summary>
    internal static int Taps(ReadOnlySpan<ReflectionPath> paths, Vector3 source, in Matrix4x4 listener, in AudioEmitter emitter, float speedOfSound,
        Span<Reflection> taps, out Bands3 tone, Span<int> keys = default)
    {
        tone = Bands3.All(1);
        Matrix4x4.Invert(listener, out var toListener);
        var at = listener.Translation;
        float direct = Vector3.Distance(source, at);
        float directLaw = DistanceLaw(direct, emitter);
        int n = 0;
        foreach (ref readonly var path in paths)
        {
            if (n == taps.Length) break;
            var image = path.ImageOf(source);
            float length = Vector3.Distance(image, at);
            float delay = (length - direct) / speedOfSound;
            if (delay <= 0 || delay > Mixer.MaxReflectionDelaySeconds) continue;
            var dir = Vector3.TransformNormal(image - at, toListener);
            if (!keys.IsEmpty) keys[n] = path.Key;
            taps[n++] = new Reflection
            {
                Direction = dir.LengthSquared() > 1e-8f ? Vector3.Normalize(dir) : -Vector3.UnitZ, Delay = delay,
                Gain = DistanceLaw(length, emitter) / MathF.Max(directLaw, 1e-6f) * path.Surfaces.Mid,
            };
        }
        if (n > 0)
        {
            var s = paths[0].Surfaces;
            if (s.Mid > 0) tone = new Bands3(s.Low / s.Mid, 1, s.High / s.Mid);
        }
        return n;
    }
    internal readonly List<OneShot> OneShots = [];

    public AudioEngine Engine { get; }
    public HdrWindow Hdr { get; } = new();
    /// <summary>What the last update did, voice by voice (fill it by setting <see cref="AudioDiagnostics.Enabled"/>).</summary>
    public AudioDiagnostics Diagnostics { get; } = new();
    /// <summary>The acoustic geometry, rebuilt from <see cref="AcousticSurface"/>s when they change.</summary>
    public AcousticScene Scene { get; internal set; } = AcousticScene.Empty;
    /// <summary>The late reverb at the listener, as last estimated (see <see cref="ReverbEstimator"/>).</summary>
    public ReverbParams Reverb { get; internal set; } = ReverbParams.None;
    /// <summary>The other room's reverb (round a loud hidden source), as last estimated; silent when there's none.</summary>
    public ReverbParams RemoteReverb { get; internal set; } = ReverbParams.None;
    internal AcousticSimulator Simulator;
    /// <summary>The listener's world transform as of the last audio update (identity before one is found).</summary>
    public Matrix4x4 Listener { get; internal set; } = Matrix4x4.Identity;
    internal float ListenerGain = 1;
    internal Vector3 ListenerVelocity;
    internal AudioEnvironment Environment = AudioEnvironment.Default;
    /// <summary>Reports a missing or broken clip, once per asset.</summary>
    public Action<string> Warn { get; set; } = message => Log.Warn("audio: " + message);

    public AudioContext(AudioEngine engine, bool inlineSimulation = false)
    {
        Engine = engine;
        Simulator = new AcousticSimulator(inlineSimulation);
    }

    internal void Bind(IAssetSource? assets)
    {
        if (ReferenceEquals(assets, _assets)) return;
        if (_assets is not null) _assets.Changed -= OnChanged;
        _assets = assets;
        if (assets is not null) assets.Changed += OnChanged;
        foreach (var guid in _clips.Keys.ToArray()) if (_clips[guid] is null) _clips.Remove(guid); // retry what was missing
        foreach (var guid in _probes.Keys.ToArray()) if (_probes[guid] is null) _probes.Remove(guid);
    }

    /// <summary>The clip for an asset, loading it on first use; null (warned once) when it's missing or broken.</summary>
    public AudioClip? Clip(AssetRef<AudioAsset> asset)
    {
        if (asset.IsNone) return null;
        if (_clips.TryGetValue(asset.Guid, out var clip)) return clip;
        clip = Load(asset.Guid);
        _clips[asset.Guid] = clip;
        return clip;
    }

    /// <summary>Fades out everything playing and forgets positional one-shots.</summary>
    public void Silence()
    {
        Engine.StopAll();
        OneShots.Clear();
    }

    /// <summary>A baked probe grid, loading it on first use; null (warned once) when it's missing or broken.</summary>
    public AcousticProbeGrid? Probes(AssetRef<AcousticProbesAsset> asset)
    {
        if (asset.IsNone) return null;
        if (_probes.TryGetValue(asset.Guid, out var grid)) return grid;
        grid = LoadProbes(asset.Guid);
        _probes[asset.Guid] = grid;
        return grid;
    }

    /// <summary>Supplies a probe grid for an asset reference directly (a bake not yet imported, tests).</summary>
    public void RegisterProbes(AssetRef<AcousticProbesAsset> asset, AcousticProbeGrid grid) => _probes[asset.Guid] = grid;

    /// <summary>The baked reverb at a point, when a probe volume covers it.</summary>
    public bool SampleProbes(Vector3 point, out ReverbParams reverb)
    {
        foreach (var grid in Grids) if (grid.Sample(point, out reverb)) return true;
        reverb = ReverbParams.None;
        return false;
    }

    private AcousticProbeGrid? LoadProbes(Guid guid)
    {
        if (_assets is null || !_assets.TryRead(guid, out var kind, out var bytes) || kind != AssetKind.AcousticProbes)
        {
            Warn($"No acoustic probes asset {guid:N}; the volume is traced live.");
            return null;
        }
        try { return AcousticProbeGrid.FromBytes(bytes.Span); }
        catch (InvalidDataException e) { Warn($"Acoustic probes {guid:N} are broken: {e.Message}"); return null; }
    }

    /// <summary>Supplies a clip for an asset reference directly (generated sounds, tests).</summary>
    public void Register(AssetRef<AudioAsset> asset, AudioClip clip) => _clips[asset.Guid] = clip;

    /// <summary>Plays a clip that no entity owns (it runs to its end, or until <see cref="AudioEngine.Stop"/>).</summary>
    public VoiceHandle PlayOneShot(AssetRef<AudioAsset> asset, in PlayOptions options) =>
        Clip(asset) is { } clip ? Engine.Play(clip, options) : default;

    public VoiceHandle PlayOneShot(AssetRef<AudioAsset> asset) => PlayOneShot(asset, PlayOptions.Default);

    /// <summary>
    /// Plays a clip from a point in the world (an impact, an explosion); it keeps sounding from there as the listener
    /// moves, until it ends. <paramref name="options"/> sets volume, pitch and timing.
    /// </summary>
    public VoiceHandle PlayAt(AssetRef<AudioAsset> asset, Vector3 position, in AudioEmitter emitter, in PlayOptions options)
    {
        if (Clip(asset) is not { } clip) return default;
        var spatial = Placed(position, Vector3.Zero, Vector3.Zero, emitter, options.Gain, Occlusion(position, emitter), starting: true);
        var voice = Engine.Play(clip, options with { Positional = true, Spatial = spatial });
        if (voice.IsValid) OneShots.Add(new OneShot { Voice = voice, Position = position, Emitter = emitter, Volume = options.Gain });
        return voice;
    }

    public VoiceHandle PlayAt(AssetRef<AudioAsset> asset, Vector3 position) => PlayAt(asset, position, AudioEmitter.Default, PlayOptions.Default);

    /// <summary>
    /// Plays a clip from an entity (a gun in a hand, a shout, a footstep): it moves with the entity, at
    /// <paramref name="offset"/> in the entity's space, until it ends. If the entity goes, the sound stays where it last was.
    /// </summary>
    public VoiceHandle PlayOn(Entity entity, AssetRef<AudioAsset> asset, Vector3 offset, in AudioEmitter emitter, in PlayOptions options)
    {
        if (Clip(asset) is not { } clip) return default;
        var position = FollowPoint(entity, offset, offset);
        var spatial = Placed(position, Vector3.Zero, Vector3.Zero, emitter, options.Gain, Occlusion(position, emitter), starting: true);
        var voice = Engine.Play(clip, options with { Positional = true, Spatial = spatial });
        if (voice.IsValid) OneShots.Add(new OneShot { Voice = voice, Position = position, Emitter = emitter, Volume = options.Gain, Follow = entity, Offset = offset });
        return voice;
    }

    /// <summary>Where a point fixed to an entity is now (<paramref name="fallback"/> if the entity or its transform is gone).</summary>
    internal Vector3 FollowPoint(Entity entity, Vector3 offset, Vector3 fallback) =>
        World is { } w && !entity.IsNull && w.IsAlive(entity) && w.Has<LocalToWorld>(entity) ? Vector3.Transform(offset, w.Get<LocalToWorld>(entity).Value) : fallback;

    /// <summary>A voice starting now, fully placed: spatial parameters with HDR applied (a sound louder than the window raises it at once).</summary>
    internal SpatialParams Placed(Vector3 position, Vector3 velocity, Vector3 forward, in AudioEmitter emitter, float volume, Bands3 occlusion, bool starting)
    {
        var spatial = Spatialize(position, velocity, forward, emitter, volume, occlusion, out float loudness);
        if (Environment.Hdr && spatial.Gain > 0)
        {
            if (starting) Hdr.Raise(loudness);
            float hdr = HdrGain(loudness, volume);
            spatial.Reverb *= hdr / spatial.Gain;
            spatial.RemoteReverb *= hdr / spatial.Gain;
            spatial.Gain = hdr;
        }
        return spatial;
    }

    /// <summary>
    /// The spatial gain under HDR: the window's gain for the voice's loudness, less its volume (the voice's own gain
    /// applies that), times the listener's gain, less the headroom.
    /// </summary>
    internal float HdrGain(float loudness, float volume) =>
        volume > 1e-6f ? Hdr.Gain(loudness) / volume * ListenerGain * MathF.Pow(10, -Environment.HdrHeadroomDb / 20) : 0;

    /// <summary>
    /// Direction, distance gain, air absorption, directivity and Doppler for a source as heard by the current listener.
    /// <paramref name="loudness"/> is the source's level at the listener in dB (for HDR), counting <paramref name="volume"/>.
    /// The gain returned excludes HDR. <paramref name="forward"/> is the source's facing (zero: no cone).
    /// </summary>
    internal SpatialParams Spatialize(Vector3 position, Vector3 velocity, Vector3 forward, in AudioEmitter emitter, float volume, Bands3 occlusion, out float loudness) =>
        Spatialize(position, velocity, forward, emitter, volume, occlusion, false, default, out loudness);

    /// <summary>
    /// As above, with a diffraction path round whatever blocks the direct line: the sound through the walls and the sound
    /// round the edge add up by energy, per band, and it's heard from between their directions, weighted by energy.
    /// </summary>
    internal SpatialParams Spatialize(Vector3 position, Vector3 velocity, Vector3 forward, in AudioEmitter emitter, float volume, Bands3 occlusion,
        bool diffracted, in DiffractionPath diffraction, out float loudness)
    {
        var env = Environment;
        Matrix4x4.Invert(Listener, out var toListener);
        var listenerPos = Listener.Translation;
        var local = Vector3.Transform(position, toListener);
        var offset = position - listenerPos;
        float distance = offset.Length();
        var direction = local.LengthSquared() > 1e-8f ? Vector3.Normalize(local) : -Vector3.UnitZ;
        var away = distance > 1e-4f ? offset / distance : Vector3.Zero; // from the listener toward the source
        if (diffracted && occlusion.Mid < 0.999f)
        {
            float ratio = DistanceLaw(diffraction.Length, emitter) / MathF.Max(DistanceLaw(distance, emitter), 1e-6f);
            var around = diffraction.Gain * ratio;
            static float Sum(float a, float b) => MathF.Sqrt(a * a + b * b);
            var combined = new Bands3(Sum(occlusion.Low, around.Low), Sum(occlusion.Mid, around.Mid), Sum(occlusion.High, around.High));
            var edge = Vector3.Transform(diffraction.First, toListener);
            if (edge.LengthSquared() > 1e-8f)
            {
                float wThrough = occlusion.Mid * occlusion.Mid, wAround = around.Mid * around.Mid;
                var blend = direction * wThrough + Vector3.Normalize(edge) * wAround;
                if (blend.LengthSquared() > 1e-12f) direction = Vector3.Normalize(blend);
            }
            occlusion = combined;
        }
        float gain = Propagation.DistanceGain(distance, emitter.MinDistance, emitter.MaxDistance, emitter.Rolloff);
        if (forward != Vector3.Zero && distance > 1e-4f)
            gain *= Propagation.ConeGain(Vector3.Dot(Vector3.Normalize(forward), -away), emitter.ConeInner, emitter.ConeOuter, emitter.ConeOuterGain);
        var (low, mid, high) = Propagation.AirBands(distance, env.TemperatureC, env.HumidityPercent);
        low *= occlusion.Low; mid *= occlusion.Mid; high *= occlusion.High;
        // Doppler: speeds along the line between them (the source's away from the listener, the listener's toward the source).
        float doppler = Propagation.Doppler(Vector3.Dot(velocity, away), Vector3.Dot(ListenerVelocity, away), Propagation.SpeedOfSound(env.TemperatureC), env.DopplerScale * emitter.DopplerScale);
        float heard = gain * volume * occlusion.Mid; // occlusion counts toward loudness (HDR, which voices render)
        loudness = heard > 0 ? emitter.LoudnessDb + 20 * MathF.Log10(heard) : float.NegativeInfinity;
        float spread = emitter.Radius > 0 ? Math.Clamp(emitter.Radius / MathF.Max(distance, 1e-3f), 0, 1) : 0;
        // The reverb hears the source at its own level, not its distance: the reverberant field is even through a room.
        float send = env.Reverb ? Propagation.DistanceGain(MathF.Max(distance, emitter.MinDistance), emitter.MinDistance, float.MaxValue, emitter.Rolloff) : 0;
        // A hidden source excites the listener's room only as much as reaches it (through walls, round edges).
        float own = send > 0 && distance < emitter.MaxDistance ? gain / send * ListenerGain : 0;
        float reverb = own * MathF.Min(1, occlusion.Mid);
        // A hidden source also excites its own room (the remote reverb couples it back through the opening).
        float remote = occlusion.Mid < 0.999f && env.RemoteReverb ? own : 0;
        return new SpatialParams { Direction = direction, Gain = gain * ListenerGain, Spread = spread, Doppler = doppler, Low = low, Mid = mid, High = high, Reverb = reverb, RemoteReverb = remote };
    }

    /// <summary>The direct path's per-band transmission from a source to the listener (all ones without geometry or with occlusion off).</summary>
    internal Bands3 Occlusion(Vector3 position, in AudioEmitter emitter) =>
        emitter.Occlusion && Scene.TriangleCount > 0 ? Scene.DirectPath(Listener.Translation, position, emitter.Radius, emitter.Radius > 0 ? 8 : 0) : Bands3.All(1);

    private AudioClip? Load(Guid guid)
    {
        if (_assets is null || !_assets.TryRead(guid, out var kind, out var bytes) || kind != AssetKind.Audio)
        {
            Warn($"No audio asset {guid:N}; sources using it stay silent.");
            return null;
        }
        try { return Wav.Read(bytes.Span, guid.ToString("N")); }
        catch (InvalidDataException e) { Warn($"Audio asset {guid:N} is broken: {e.Message}"); return null; }
    }

    private void OnChanged(Guid guid)
    {
        if (_clips.ContainsKey(guid)) _clips[guid] = Load(guid);
        if (_probes.ContainsKey(guid)) _probes[guid] = LoadProbes(guid);
    }

    public void Dispose()
    {
        if (_assets is not null) _assets.Changed -= OnChanged;
        Simulator.Dispose();
        Engine.Dispose();
    }
}

/// <summary>
/// Audio: adds the <see cref="AudioContext"/> and <see cref="AudioEnvironment"/> resources (an engine on the output
/// device) and the system that drives <see cref="AudioSource"/>s as heard by the <see cref="AudioListener"/>.
/// <c>GAMEGRL_AUDIO</c> overrides the default device output (<c>null</c>, <c>offline</c>, or an ALSA device name such as
/// <c>hw:1,0</c>); <c>GAMEGRL_HEADPHONES=1</c> renders binaurally for headphones. Leaving editor play mode stops every
/// voice play started.
/// </summary>
public sealed class AudioModule(AudioOutput output, string? device = null, bool headphones = false) : IModule
{
    public AudioModule() : this(AudioOutput.Device) { }

    public void Build(EngineBuilder builder)
    {
        builder.AddModule<CoreModule>();
        // Rebuilding over a world that already has audio (the editor reloading game code) keeps its engine and device.
        var audio = builder.World.HasResource<AudioContext>() ? builder.World.Resource<AudioContext>() : null;
        if (audio is null)
        {
            var engine = CreateEngine();
            audio = new AudioContext(engine, inlineSimulation: engine.Device is null);
        }
        // Clips can be asked for before the first audio update (a fixed-step system playing a sound): know the assets now.
        if (builder.World.HasResource<IAssetSource>()) audio.Bind(builder.World.Resource<IAssetSource>());
        audio.World = builder.World;
        builder.SetResource(audio, a => { a.Silence(); return a; });
        builder.SetResource(builder.World.HasResource<AudioEnvironment>() ? builder.World.Resource<AudioEnvironment>() : AudioEnvironment.Default);
        builder.AddSystem(new AcousticGeometryUpdate());
        builder.AddSystem(new AudioUpdate());
        // Stopping (the app, or the editor's play mode) silences; the world disposes the context, closing the device, when it goes.
        builder.OnStop(_ => audio.Silence());
    }

    private AudioEngine CreateEngine()
    {
        var mode = output;
        string? name = device;
        bool phones = headphones || System.Environment.GetEnvironmentVariable("GAMEGRL_HEADPHONES") == "1";
        switch (output == AudioOutput.Device ? System.Environment.GetEnvironmentVariable("GAMEGRL_AUDIO") : null)
        {
            case null or "": break;
            case "null" or "none": mode = AudioOutput.Null; break;
            case "offline": mode = AudioOutput.Offline; break;
            case var other: mode = AudioOutput.Device; name = other; break;
        }
        var settings = AudioEngineSettings.Default with { Headphones = phones };
        if (mode == AudioOutput.Offline) return AudioEngine.Offline(48000, ChannelLayout.Stereo, settings);
        if (mode == AudioOutput.Device && AlsaDevice.IsAvailable)
        {
            try { return AudioEngine.Start(AlsaDevice.Open(AlsaDeviceOptions.Default with { Device = name ?? "default" }), settings); }
            catch (AudioDeviceException e) { Log.Warn($"audio: {e.Message}; using the null device"); }
        }
        return AudioEngine.Start(new NullAudioDevice(), settings);
    }
}

/// <summary>
/// Once per frame, after transforms: finds the listener, then starts, updates and stops the voices of
/// <see cref="AudioSource"/>s and keeps positional voices (sources and one-shots) placed — direction, distance, air,
/// directivity, Doppler from how far things moved since last frame, and HDR from everything audible. Each voice slot
/// remembers its owner; owned voices not visited this frame lost their entity or component and are stopped, with no
/// per-entity lookups.
/// </summary>
/// <summary>
/// Keeps each reflection in the same mixer tap from frame to frame. Paths come back from each search strongest first,
/// so their order shuffles as the listener moves; a tap that switched paths would glide from one delay to another, a
/// pitch sweep (heard as chorusing). A path keeps its tap while it lasts; a tap whose path is gone fades out and rests
/// two updates before a new path takes it, which then starts at its own delay.
/// </summary>
internal sealed class TapSlots(int voices)
{
    private const int K = ReflectionTaps.Count, Rest = 2;
    private readonly int[] _voice = new int[voices];
    private readonly int[] _key = new int[voices * K], _resting = new int[voices * K];
    private readonly Reflection[] _last = new Reflection[voices * K];

    /// <summary>Forgets a voice's taps (it left the reflecting set): next time they all start afresh.</summary>
    public void Forget(VoiceHandle voice) { if (_voice[voice.Slot] == voice.Value) _voice[voice.Slot] = 0; }

    /// <summary>Places this frame's taps (strongest first, with their path keys) into slots; returns how many slots to send.</summary>
    public int Assign(VoiceHandle voice, ReadOnlySpan<Reflection> taps, ReadOnlySpan<int> keys, Span<Reflection> slots)
    {
        int b = voice.Slot * K;
        var key = _key.AsSpan(b, K); var resting = _resting.AsSpan(b, K); var last = _last.AsSpan(b, K);
        if (_voice[voice.Slot] != voice.Value)
        {
            _voice[voice.Slot] = voice.Value;
            key.Clear(); resting.Clear();
        }
        Span<bool> placed = stackalloc bool[K];
        for (int k = 0; k < K; k++)
        {
            slots[k] = default;
            if (key[k] == 0) { if (resting[k] > 0) { resting[k]--; slots[k] = last[k] with { Gain = 0, Restart = false }; } continue; }
            int j = keys.IndexOf(key[k]);
            if (j < 0) { key[k] = 0; resting[k] = Rest; slots[k] = last[k] with { Gain = 0, Restart = false }; continue; } // fades out where it was
            slots[k] = last[k] = taps[j];
            placed[j] = true;
        }
        for (int j = 0; j < taps.Length; j++)
        {
            if (placed[j]) continue;
            int free = -1;
            for (int k = 0; k < K && free < 0; k++) if (key[k] == 0 && resting[k] == 0) free = k;
            if (free < 0) break; // weaker paths wait for a free tap
            key[free] = keys[j];
            slots[free] = last[free] = taps[j] with { Restart = true };
        }
        int count = 0;
        for (int k = 0; k < K; k++) if (key[k] != 0 || resting[k] > 0) count = k + 1;
        return count;
    }
}

internal sealed class AudioUpdate : ISystem
{
    private Query _sources = null!, _listeners = null!, _volumes = null!;
    private Entity[] _owner = [];
    private int[] _ownerVoice = [], _stamps = [];
    private float[] _sentVolume = [], _sentPitch = [], _sentPan = [];
    private Vector3[] _lastPosition = [];
    private bool[] _hasLast = [];
    private Bands3[] _occlusion = [];
    private int[] _occlusionVoice = []; // which voice each cached occlusion belongs to
    // This frame's positional voices: computed first, sent once the HDR window has moved.
    private VoiceHandle[] _pending = [];
    private SpatialParams[] _pendingParams = [];
    private float[] _pendingLoudness = [], _pendingVolume = [];
    private Vector3[] _pendingPosition = [];
    private AudioEmitter[] _pendingEmitter = [];
    private float[] _pendingBlocked = []; // the mid band's direct-path transmission (1: in plain view)
    private int _pendingCount, _stamp;
    /// <summary>How many positional voices get early reflections: the loudest rendered ones.</summary>
    public const int ReflectionVoices = 8;
    private readonly ReflectionRequest[] _requests = new ReflectionRequest[ReflectionVoices * 2];
    private readonly int[] _requestIndex = new int[ReflectionVoices];
    private readonly List<VoiceHandle> _reflecting = [];
    private TapSlots _slots = null!;
    private AudioDiagnostics? _report; // this frame's, when diagnostics are on
    private ReflectionResult? _paths;
    private readonly ReflectionPath[] _quick = new ReflectionPath[ReflectionTaps.Count];
    private Vector3 _lastListener;
    private bool _haveListener;

    public string Name => "Audio.Update";
    public Phase Phase => Phase.PostUpdate;
    public int Order => 1000;

    public void Initialize(World world)
    {
        _sources = world.Query(new QueryDesc().With<AudioSource>());
        _listeners = world.Query(new QueryDesc().With<AudioListener>().With<LocalToWorld>());
        _volumes = world.Query(new QueryDesc().With<AcousticProbeVolume>());
        int n = world.Resource<AudioContext>().Engine.MaxVoices;
        _owner = new Entity[n]; _ownerVoice = new int[n]; _stamps = new int[n];
        _sentVolume = new float[n]; _sentPitch = new float[n]; _sentPan = new float[n];
        _lastPosition = new Vector3[n]; _hasLast = new bool[n]; _occlusion = new Bands3[n]; _occlusionVoice = new int[n];
        _pending = new VoiceHandle[n]; _pendingParams = new SpatialParams[n]; _pendingLoudness = new float[n]; _pendingVolume = new float[n];
        _pendingPosition = new Vector3[n]; _pendingEmitter = new AudioEmitter[n]; _pendingBlocked = new float[n];
        _slots = new TapSlots(n);
    }

    public void Run(World world)
    {
        var audio = world.Resource<AudioContext>();
        audio.Bind(world.HasResource<IAssetSource>() ? world.Resource<IAssetSource>() : null);
        var env = audio.Environment = world.Resource<AudioEnvironment>();
        audio.Hdr.RangeDb = env.HdrRangeDb; audio.Hdr.ReleaseDbPerSecond = env.HdrReleaseDbPerSecond; audio.Hdr.FloorDb = env.HdrFloorDb;
        var engine = audio.Engine;
        engine.Update();
        _diffraction = env.Diffraction;
        float dt = world.HasResource<Time>() ? MathF.Max(world.Resource<Time>().Delta, 1e-4f) : 1 / 60f;
        UpdateListener(audio, dt);
        _report = audio.Diagnostics.Enabled ? audio.Diagnostics : null;
        audio.Simulator.Tracing = _report is not null;
        _report?.Begin(audio.Listener);
        audio.Grids.Clear();
        foreach (var a in _volumes.Archetypes)
            foreach (ref readonly var volume in a.GetSpan<AcousticProbeVolume>())
                if (audio.Probes(volume.Probes) is { } grid) audio.Grids.Add(grid);
        if (env.Reverb)
        {
            if (audio.SampleProbes(audio.Listener.Translation, out var baked))
            {
                // Inside a baked volume: interpolate the probes instead of tracing.
                if (Differs(baked, audio.Reverb)) { audio.Reverb = baked; engine.SetReverb(baked, 0.1f); }
                if (_report is not null) { _report.ReverbSource = ReverbSource.Baked; _report.Reverb = null; }
            }
            else
            {
                audio.Simulator.Submit(audio.Scene, audio.Listener.Translation, env.TemperatureC, env.HumidityPercent);
                if (audio.Simulator.TryTake(out var reverb)) { audio.Reverb = reverb; engine.SetReverb(reverb); }
                if (_report is not null) { _report.ReverbSource = ReverbSource.Traced; _report.Reverb = audio.Simulator.LatestTrace; }
            }
        }
        else
        {
            if (!audio.Reverb.IsSilent) { audio.Reverb = ReverbParams.None; engine.SetReverb(ReverbParams.None); }
            if (_report is not null) { _report.ReverbSource = ReverbSource.None; _report.Reverb = null; }
        }
        var orientation = Quaternion.CreateFromRotationMatrix(Orthonormal(audio.Listener));
        int stamp = ++_stamp;
        _pendingCount = 0;

        foreach (var a in _sources.Archetypes)
        {
            var sources = a.GetSpan<AudioSource>();
            var transforms = a.Has<LocalToWorld>() ? a.GetSpan<LocalToWorld>() : default;
            var entities = a.Entities;
            for (int r = 0; r < sources.Length; r++)
            {
                ref var s = ref sources[r];
                var voice = new VoiceHandle(s.Voice);
                bool playing = engine.IsPlaying(voice);
                bool positional = s.Positional && !transforms.IsEmpty;
                var world4 = transforms.IsEmpty ? Matrix4x4.Identity : transforms[r].Value;
                var forward = -ZAxis(world4);
                if (s.Stop)
                {
                    s.Stop = false;
                    if (playing) { engine.Stop(voice); _owner[voice.Slot] = default; }
                    playing = false;
                }
                if (s.Play || (s.PlayOnStart && !s.Started))
                {
                    s.Play = false;
                    s.Started = true;
                    if (playing) { engine.Stop(voice); _owner[voice.Slot] = default; }
                    playing = false;
                    var options = PlayOptions.Default with { Gain = s.Volume, Pitch = s.Pitch, Pan = s.Pan, Loop = s.Loop, ListenerOrientation = orientation };
                    var occlusion = positional ? audio.Occlusion(world4.Translation, s.Emitter) : Bands3.All(1);
                    if (positional) options = options with { Positional = true, Spatial = audio.Placed(world4.Translation, Vector3.Zero, forward, s.Emitter, s.Volume, occlusion, starting: true) };
                    voice = audio.Clip(s.Clip) is { } clip ? engine.Play(clip, options) : default;
                    s.Voice = voice.Value;
                    if (voice.IsValid)
                    {
                        int slot = voice.Slot;
                        _owner[slot] = entities[r]; _ownerVoice[slot] = voice.Value;
                        _sentVolume[slot] = s.Volume; _sentPitch[slot] = s.Pitch; _sentPan[slot] = s.Pan;
                        _lastPosition[slot] = world4.Translation; _hasLast[slot] = true;
                        _occlusion[slot] = occlusion; _occlusionVoice[slot] = voice.Value;
                        playing = true;
                        // Placed (and given reflections) this very frame: a short sound may be over by the next.
                        if (positional) Queue(voice, audio.Spatialize(world4.Translation, Vector3.Zero, forward, s.Emitter, s.Volume, occlusion, out float startLoudness), startLoudness, s.Volume, world4.Translation, s.Emitter, occlusion.Mid);
                    }
                }
                else if (playing)
                {
                    int slot = voice.Slot;
                    if (s.Volume != _sentVolume[slot]) { engine.SetGain(voice, s.Volume); _sentVolume[slot] = s.Volume; }
                    if (s.Pitch != _sentPitch[slot]) { engine.SetPitch(voice, s.Pitch); _sentPitch[slot] = s.Pitch; }
                    if (s.Pan != _sentPan[slot]) { engine.SetPan(voice, s.Pan); _sentPan[slot] = s.Pan; }
                    if (positional)
                    {
                        var p = world4.Translation;
                        var velocity = _hasLast[slot] ? (p - _lastPosition[slot]) / dt : Vector3.Zero;
                        _lastPosition[slot] = p; _hasLast[slot] = true;
                        var occluded = Occluded(audio, voice, p, s.Emitter);
                        bool around = TryDiffraction(voice, occluded, out var diffraction);
                        Queue(voice, audio.Spatialize(p, velocity, forward, s.Emitter, s.Volume, occluded, around, diffraction, out float loudness), loudness, s.Volume, p, s.Emitter, occluded.Mid);
                    }
                    else engine.SetListenerOrientation(voice, orientation, rampSeconds: Math.Clamp(dt, 0.002f, 0.05f));
                }
                if (playing) _stamps[voice.Slot] = stamp;
                s.IsPlaying = playing;
            }
        }

        // Positional one-shots: placed like sources until they end; those on an entity move with it.
        var shots = audio.OneShots;
        audio.World = world;
        for (int i = shots.Count - 1; i >= 0; i--)
        {
            var shot = shots[i];
            if (!engine.IsPlaying(shot.Voice)) { shots[i] = shots[^1]; shots.RemoveAt(shots.Count - 1); continue; }
            var velocity = Vector3.Zero;
            if (!shot.Follow.IsNull)
            {
                var now = audio.FollowPoint(shot.Follow, shot.Offset, shot.Position);
                velocity = (now - shot.Position) / dt;
                shot.Position = now;
                shots[i] = shot;
            }
            var occluded = Occluded(audio, shot.Voice, shot.Position, shot.Emitter);
            bool around = TryDiffraction(shot.Voice, occluded, out var diffraction);
            Queue(shot.Voice, audio.Spatialize(shot.Position, velocity, Vector3.Zero, shot.Emitter, shot.Volume, occluded, around, diffraction, out float loudness), loudness, shot.Volume, shot.Position, shot.Emitter, occluded.Mid);
        }

        RemoteRoom(audio, env, dt);

        // HDR: the window follows the loudest audible positional voice; then every one gets its gain.
        if (env.Hdr)
        {
            float loudest = float.NegativeInfinity;
            for (int i = 0; i < _pendingCount; i++) loudest = MathF.Max(loudest, _pendingLoudness[i]);
            audio.Hdr.Update(loudest, dt);
        }
        for (int i = 0; i < _pendingCount; i++)
        {
            var p = _pendingParams[i];
            if (env.Hdr && p.Gain > 0)
            {
                float hdr = audio.HdrGain(_pendingLoudness[i], _pendingVolume[i]);
                p.Reverb *= hdr / p.Gain;
                p.RemoteReverb *= hdr / p.Gain;
                p.Gain = hdr;
            }
            // Only hidden voices around the remote room's anchor send to it.
            if (!_remoteActive || Vector3.DistanceSquared(_pendingPosition[i], _remoteAnchor) > _remoteRadius * _remoteRadius) p.RemoteReverb = 0;
            // Ramp over one frame, so each update lands just as the next arrives (a ramp longer than the frame would
            // restart before finishing and lag behind forever).
            engine.SetSpatial(_pending[i], p, rampSeconds: Math.Clamp(dt, 0.002f, 0.05f));
            if (_report is not null)
            {
                bool around = TryDiffraction(_pending[i], Bands3.All(_pendingBlocked[i]), out var path);
                _report.Voices.Add(new VoiceReport
                {
                    Voice = _pending[i], Clip = engine.ClipOf(_pending[i]), Position = _pendingPosition[i], LoudnessDb = _pendingLoudness[i], Spatial = p,
                    Transmission = _pendingBlocked[i], Rendered = engine.IsRendered(_pending[i]), Diffracted = around, Diffraction = path,
                });
            }
        }

        Reflections(audio, env, dt);

        for (int slot = 0; slot < _owner.Length; slot++)
        {
            if (_owner[slot].IsNull || _stamps[slot] == stamp) continue;
            engine.Stop(new VoiceHandle(_ownerVoice[slot]));
            _owner[slot] = default;
        }
    }

    /// <summary>
    /// A voice's occlusion: recomputed every frame while it renders, every eighth frame (staggered) while it's virtual,
    /// so thousands of quiet voices don't each cast rays every frame.
    /// </summary>
    private Bands3 Occluded(AudioContext audio, VoiceHandle voice, Vector3 position, in AudioEmitter emitter)
    {
        int slot = voice.Slot;
        if (_occlusionVoice[slot] != voice.Value || audio.Engine.IsRendered(voice) || (slot + _stamp) % 8 == 0)
        {
            _occlusion[slot] = audio.Occlusion(position, emitter);
            _occlusionVoice[slot] = voice.Value;
        }
        return _occlusion[slot];
    }

    private void Queue(VoiceHandle voice, in SpatialParams p, float loudness, float volume, Vector3 position, in AudioEmitter emitter, float blocked)
    {
        if (_pendingCount == _pending.Length) return;
        _pending[_pendingCount] = voice; _pendingParams[_pendingCount] = p; _pendingLoudness[_pendingCount] = loudness; _pendingVolume[_pendingCount] = volume;
        _pendingPosition[_pendingCount] = position; _pendingEmitter[_pendingCount] = emitter; _pendingBlocked[_pendingCount] = blocked;
        _pendingCount++;
    }

    private bool _remoteActive;
    private Vector3 _remoteAnchor, _remoteOpening;
    private float _remoteRadius, _remoteCoupling, _remoteHold;

    /// <summary>
    /// The other room: the hidden source with a way round that would be loudest in plain view anchors it. Its reverb is
    /// estimated there, and plays from the opening (the first edge of that source's path, as seen from the listener)
    /// at the level its sound has on the way out; hidden voices near the anchor send to it.
    /// </summary>
    private void RemoteRoom(AudioContext audio, in AudioEnvironment env, float dt)
    {
        var engine = audio.Engine;
        int anchor = -1;
        float best = float.NegativeInfinity;
        DiffractionPath way = default;
        var listenerAt = audio.Listener.Translation;
        if (env.RemoteReverb && env.Diffraction && audio.Scene.TriangleCount > 0)
            for (int i = 0; i < _pendingCount; i++)
            {
                if (_pendingBlocked[i] >= 0.999f || _paths is null || !_paths.TryDiffraction(_pending[i].Value, out var path)) continue;
                ref readonly var emitter = ref _pendingEmitter[i];
                float open = Propagation.DistanceGain(Vector3.Distance(_pendingPosition[i], listenerAt), emitter.MinDistance, emitter.MaxDistance, emitter.Rolloff) * _pendingVolume[i];
                float score = open > 0 ? emitter.LoudnessDb + 20 * MathF.Log10(open) : float.NegativeInfinity;
                if (score > best) { best = score; anchor = i; way = path; }
            }
        if (anchor >= 0)
        {
            _remoteAnchor = _pendingPosition[anchor];
            _remoteOpening = way.First;
            // The room's reverberant sound leaves the way the source's own sound does.
            _remoteCoupling = _pendingParams[anchor].Mid;
            if (audio.SampleProbes(_remoteAnchor, out var bakedRoom)) audio.RemoteReverb = bakedRoom;
            else audio.Simulator.SubmitRemote(audio.Scene, _remoteAnchor, env.TemperatureC, env.HumidityPercent);
        }
        if (audio.Simulator.TryTakeRemote(out var remote)) audio.RemoteReverb = remote;
        var room = audio.RemoteReverb;
        if (anchor >= 0) _remoteHold = 1.2f * MathF.Max(room.DecaySeconds.Low, room.DecaySeconds.Mid);
        else _remoteHold -= dt; // the room keeps ringing after its sources stop
        bool active = !room.IsSilent && (anchor >= 0 || _remoteHold > 0);
        if (_report is not null)
        {
            _report.RemoteActive = active; _report.RemoteOpening = _remoteOpening;
            _report.Remote = active ? audio.Simulator.LatestRemoteTrace : null;
        }
        if (!active)
        {
            if (_remoteActive) { engine.SetRemoteReverb(ReverbParams.None, -Vector3.UnitZ, 0); audio.RemoteReverb = ReverbParams.None; }
            _remoteActive = false;
            return;
        }
        _remoteActive = true;
        _remoteRadius = MathF.Max(3, 0.6f * MathF.Cbrt(MathF.Max(room.Volume, 1)));
        Matrix4x4.Invert(audio.Listener, out var toListener);
        var opening = Vector3.Transform(_remoteOpening, toListener);
        var direction = opening.LengthSquared() > 1e-8f ? Vector3.Normalize(opening) : -Vector3.UnitZ;
        engine.SetRemoteReverb(room with { Level = room.Level * _remoteCoupling * audio.ListenerGain }, direction, 0.2f, Math.Clamp(dt, 0.002f, 0.05f));
    }

    /// <summary>The latest diffraction path round whatever hides a voice's source, when it's hidden and one was found.</summary>
    private bool TryDiffraction(VoiceHandle voice, Bands3 occlusion, out DiffractionPath path)
    {
        path = default;
        return _diffraction && occlusion.Mid < 0.999f && _paths is not null && _paths.TryDiffraction(voice.Value, out path);
    }
    private bool _diffraction;

    /// <summary>
    /// Early reflections: the loudest rendered positional voices that want them are sent to the simulator; the paths
    /// that come back become taps in listener space every frame (directions follow the head as it turns). Voices that
    /// drop out of the set lose theirs.
    /// </summary>
    private void Reflections(AudioContext audio, in AudioEnvironment env, float dt)
    {
        var engine = audio.Engine;
        int count = 0;
        if ((env.Reflections || env.Diffraction) && audio.Scene.TriangleCount > 0)
        {
            for (int i = 0; i < _pendingCount && env.Reflections; i++)
            {
                if (!_pendingEmitter[i].Reflections || _pendingParams[i].Gain <= 0 || !engine.IsRendered(_pending[i])) continue;
                float loudness = _pendingLoudness[i];
                int at = count;
                while (at > 0 && _pendingLoudness[_requestIndex[at - 1]] < loudness) at--;
                if (at >= ReflectionVoices) continue;
                for (int k = Math.Min(count, ReflectionVoices - 1); k > at; k--) _requestIndex[k] = _requestIndex[k - 1];
                _requestIndex[at] = i;
                count = Math.Min(count + 1, ReflectionVoices);
            }
            for (int k = 0; k < count; k++) _requests[k] = new ReflectionRequest { Key = _pending[_requestIndex[k]].Value, Source = _pendingPosition[_requestIndex[k]] };
            // Plus the hidden voices that would be loudest in plain view: they need their way round found to be heard at all
            // (behind an opaque wall their loudness is nothing, so rank them by distance and volume alone).
            int hidden = 0;
            Span<int> hiddenIndex = stackalloc int[ReflectionVoices];
            Span<float> potential = stackalloc float[ReflectionVoices];
            var listenerAt = audio.Listener.Translation;
            for (int i = 0; i < _pendingCount && env.Diffraction; i++)
            {
                if (_pendingBlocked[i] >= 0.999f) continue;
                bool listed = false;
                for (int k = 0; k < count && !listed; k++) listed = _requestIndex[k] == i;
                if (listed) continue;
                ref readonly var emitter = ref _pendingEmitter[i];
                float open = Propagation.DistanceGain(Vector3.Distance(_pendingPosition[i], listenerAt), emitter.MinDistance, emitter.MaxDistance, emitter.Rolloff) * _pendingVolume[i];
                if (open <= 0) continue;
                float score = emitter.LoudnessDb + 20 * MathF.Log10(open);
                int at = hidden;
                while (at > 0 && potential[at - 1] < score) at--;
                if (at >= ReflectionVoices) continue;
                for (int k = Math.Min(hidden, ReflectionVoices - 1); k > at; k--) { hiddenIndex[k] = hiddenIndex[k - 1]; potential[k] = potential[k - 1]; }
                hiddenIndex[at] = i; potential[at] = score;
                hidden = Math.Min(hidden + 1, ReflectionVoices);
            }
            for (int k = 0; k < hidden; k++) _requests[count + k] = new ReflectionRequest { Key = _pending[hiddenIndex[k]].Value, Source = _pendingPosition[hiddenIndex[k]] };
            audio.Simulator.SubmitReflections(audio.Scene, audio.Listener.Translation, _requests.AsSpan(0, count + hidden), Propagation.SpeedOfSound(env.TemperatureC));
            if (audio.Simulator.TryTakeReflections(out var result)) _paths = result;
        }

        float c = Propagation.SpeedOfSound(env.TemperatureC);
        Span<Reflection> taps = stackalloc Reflection[ReflectionTaps.Count];
        Span<Reflection> slots = stackalloc Reflection[ReflectionTaps.Count];
        Span<int> keys = stackalloc int[ReflectionTaps.Count];
        foreach (var held in _reflecting)
        {
            bool still = false;
            for (int k = 0; k < count && !still; k++) still = _pending[_requestIndex[k]] == held;
            if (!still) { engine.SetReflections(held, default, Bands3.All(1)); _slots.Forget(held); }
        }
        _reflecting.Clear();
        int quickSearches = 0;
        for (int k = 0; k < count; k++)
        {
            int i = _requestIndex[k];
            var voice = _pending[i];
            ReadOnlySpan<ReflectionPath> paths = _paths is null ? default : _paths.For(voice.Value);
            // A new sound the simulation thread hasn't reached yet (a gunshot is over before it would): first order now,
            // off the few largest planes, for a few voices a frame.
            if (paths.IsEmpty && !audio.Simulator.Inline && quickSearches < 4)
            {
                quickSearches++;
                paths = _quick.AsSpan(0, ImageSources.Find(audio.Scene, audio.Listener.Translation, _pendingPosition[i], _quick, maxOrder: 1, maxPlanes: 16));
            }
            if (paths.IsEmpty) continue;
            int n = AudioContext.Taps(paths, _pendingPosition[i], audio.Listener, _pendingEmitter[i], c, taps, out var tone, keys);
            int sent = _slots.Assign(voice, taps[..n], keys[..n], slots);
            if (sent == 0) continue;
            // Ramped over one frame, like the direct sound: each tap's delay then glides at its true Doppler rate.
            engine.SetReflections(voice, slots[..sent], tone, Math.Clamp(dt, 0.002f, 0.05f));
            _reflecting.Add(voice);
            if (_report is not null) _report.SetPaths(i, paths, slots[..sent]);
        }
    }

    private void UpdateListener(AudioContext audio, float dt)
    {
        foreach (var a in _listeners.Archetypes)
        {
            var transforms = a.GetSpan<LocalToWorld>();
            if (transforms.Length == 0) continue;
            var m = transforms[0].Value;
            audio.Listener = m;
            audio.ListenerGain = a.GetSpan<AudioListener>()[0].Gain;
            audio.ListenerVelocity = _haveListener ? (m.Translation - _lastListener) / dt : Vector3.Zero;
            _lastListener = m.Translation;
            _haveListener = true;
            return;
        }
        audio.ListenerVelocity = Vector3.Zero;
    }

    /// <summary>Whether two reverb settings differ enough (half a percent) to send the mixer new ones.</summary>
    private static bool Differs(in ReverbParams a, in ReverbParams b)
    {
        static bool Far(float x, float y) => MathF.Abs(x - y) > 0.005f * MathF.Max(MathF.Abs(x), MathF.Abs(y)) + 1e-6f;
        return Far(a.DecaySeconds.Low, b.DecaySeconds.Low) || Far(a.DecaySeconds.Mid, b.DecaySeconds.Mid) || Far(a.DecaySeconds.High, b.DecaySeconds.High)
            || Far(a.Level, b.Level) || Far(a.PreDelaySeconds, b.PreDelaySeconds);
    }

    /// <summary>A world matrix's +Z axis (row 3); an entity faces along its negation.</summary>
    private static Vector3 ZAxis(in Matrix4x4 m) => new(m.M31, m.M32, m.M33);

    /// <summary>The rotation part of a world matrix with scale removed.</summary>
    private static Matrix4x4 Orthonormal(in Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = Vector3.Normalize(new Vector3(m.M21, m.M22, m.M23));
        var z = Vector3.Normalize(new Vector3(m.M31, m.M32, m.M33));
        if (!float.IsFinite(x.X + y.Y + z.Z)) return Matrix4x4.Identity;
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }
}
