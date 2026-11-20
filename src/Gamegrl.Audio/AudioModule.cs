using Gamegrl.Assets;
using Gamegrl.Audio.Devices;

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
/// A sound on an entity: its clip, level, pitch and pan (mono clips; positions come with spatial audio). It starts when
/// the entity first runs with <see cref="PlayOnStart"/>, or whenever gameplay sets <see cref="Play"/>; <see cref="Stop"/>
/// fades it out. Despawning the entity or removing the component stops its voice. Changes to volume, pitch and pan
/// apply while it plays, smoothed.
/// </summary>
public struct AudioSource : IComponent
{
    public AssetRef<AudioAsset> Clip;
    [Range(0, 4)] public float Volume;
    [Range(0.25, 4)] public float Pitch;
    [Range(-1, 1)] public float Pan;
    public bool Loop;
    public bool PlayOnStart;
    /// <summary>Set to start (or restart) the clip; the audio system clears it.</summary>
    public bool Play;
    /// <summary>Set to fade the clip out; the audio system clears it.</summary>
    public bool Stop;
    [NoSerialize] public bool IsPlaying;
    [NoSerialize] public bool Started;
    [NoSerialize] public int Voice;

    public static AudioSource Default => new() { Volume = 1, Pitch = 1, PlayOnStart = true };
    public static AudioSource Of(AssetRef<AudioAsset> clip, bool loop = false) => Default with { Clip = clip, Loop = loop };
}

/// <summary>
/// The audio resource: the <see cref="AudioEngine"/> and the clips loaded from the asset source (reloaded when their
/// asset is reimported; voices already playing keep the clip they started with).
/// </summary>
public sealed class AudioContext : IDisposable
{
    private readonly Dictionary<Guid, AudioClip?> _clips = [];
    private IAssetSource? _assets;

    public AudioEngine Engine { get; }
    /// <summary>Reports a missing or broken clip, once per asset.</summary>
    public Action<string> Warn { get; set; } = message => Log.Warn("audio: " + message);

    public AudioContext(AudioEngine engine) => Engine = engine;

    internal void Bind(IAssetSource? assets)
    {
        if (ReferenceEquals(assets, _assets)) return;
        if (_assets is not null) _assets.Changed -= OnChanged;
        _assets = assets;
        if (assets is not null) assets.Changed += OnChanged;
        foreach (var guid in _clips.Keys.ToArray()) if (_clips[guid] is null) _clips.Remove(guid); // retry what was missing
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

    /// <summary>Supplies a clip for an asset reference directly (generated sounds, tests).</summary>
    public void Register(AssetRef<AudioAsset> asset, AudioClip clip) => _clips[asset.Guid] = clip;

    /// <summary>Plays a clip that no entity owns (it runs to its end, or until <see cref="AudioEngine.Stop"/>).</summary>
    public VoiceHandle PlayOneShot(AssetRef<AudioAsset> asset, in PlayOptions options) =>
        Clip(asset) is { } clip ? Engine.Play(clip, options) : default;

    public VoiceHandle PlayOneShot(AssetRef<AudioAsset> asset) => PlayOneShot(asset, PlayOptions.Default);

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
    }

    public void Dispose()
    {
        if (_assets is not null) _assets.Changed -= OnChanged;
        Engine.Dispose();
    }
}

/// <summary>
/// Audio: adds the <see cref="AudioContext"/> resource (an engine on the output device) and the system that drives
/// <see cref="AudioSource"/>s. <c>GAMEGRL_AUDIO</c> overrides the default device output: <c>null</c>, <c>offline</c>, or
/// an ALSA device name such as <c>hw:1,0</c>. Leaving editor play mode stops every voice play started.
/// </summary>
public sealed class AudioModule(AudioOutput output, string? device = null) : IModule
{
    public AudioModule() : this(AudioOutput.Device) { }

    public void Build(EngineBuilder builder)
    {
        builder.AddModule<CoreModule>();
        var audio = new AudioContext(CreateEngine());
        builder.SetResource(audio, a => { a.Engine.StopAll(); return a; });
        builder.AddSystem(new AudioUpdate());
        builder.OnStop(_ => audio.Dispose());
    }

    private AudioEngine CreateEngine()
    {
        var mode = output;
        string? name = device;
        switch (output == AudioOutput.Device ? Environment.GetEnvironmentVariable("GAMEGRL_AUDIO") : null)
        {
            case null or "": break;
            case "null" or "none": mode = AudioOutput.Null; break;
            case "offline": mode = AudioOutput.Offline; break;
            case var other: mode = AudioOutput.Device; name = other; break;
        }
        if (mode == AudioOutput.Offline) return AudioEngine.Offline(48000, ChannelLayout.Stereo);
        if (mode == AudioOutput.Device && AlsaDevice.IsAvailable)
        {
            try { return AudioEngine.Start(AlsaDevice.Open(AlsaDeviceOptions.Default with { Device = name ?? "default" })); }
            catch (AudioDeviceException e) { Log.Warn($"audio: {e.Message}; using the null device"); }
        }
        return AudioEngine.Start(new NullAudioDevice());
    }
}

/// <summary>
/// Starts, updates and stops the voices of <see cref="AudioSource"/>s once per frame, after gameplay. Each voice slot
/// remembers its owner; owned voices not visited this frame lost their entity or component and are stopped, with no
/// per-entity lookups.
/// </summary>
internal sealed class AudioUpdate : ISystem
{
    private Query _sources = null!;
    private Entity[] _owner = [];
    private int[] _ownerVoice = [], _stamps = [];
    private float[] _sentVolume = [], _sentPitch = [], _sentPan = [];
    private int _stamp;

    public string Name => "Audio.Update";
    public Phase Phase => Phase.PostUpdate;
    public int Order => 1000;

    public void Initialize(World world)
    {
        _sources = world.Query(new QueryDesc().With<AudioSource>());
        int n = world.Resource<AudioContext>().Engine.MaxVoices;
        _owner = new Entity[n]; _ownerVoice = new int[n]; _stamps = new int[n];
        _sentVolume = new float[n]; _sentPitch = new float[n]; _sentPan = new float[n];
    }

    public void Run(World world)
    {
        var audio = world.Resource<AudioContext>();
        audio.Bind(world.HasResource<IAssetSource>() ? world.Resource<IAssetSource>() : null);
        var engine = audio.Engine;
        engine.Update();
        int stamp = ++_stamp;

        foreach (var a in _sources.Archetypes)
        {
            var sources = a.GetSpan<AudioSource>();
            var entities = a.Entities;
            for (int r = 0; r < sources.Length; r++)
            {
                ref var s = ref sources[r];
                var voice = new VoiceHandle(s.Voice);
                bool playing = engine.IsPlaying(voice);
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
                    voice = audio.Clip(s.Clip) is { } clip
                        ? engine.Play(clip, PlayOptions.Default with { Gain = s.Volume, Pitch = s.Pitch, Pan = s.Pan, Loop = s.Loop })
                        : default;
                    s.Voice = voice.Value;
                    if (voice.IsValid)
                    {
                        int slot = voice.Slot;
                        _owner[slot] = entities[r]; _ownerVoice[slot] = voice.Value;
                        _sentVolume[slot] = s.Volume; _sentPitch[slot] = s.Pitch; _sentPan[slot] = s.Pan;
                        playing = true;
                    }
                }
                else if (playing)
                {
                    int slot = voice.Slot;
                    if (s.Volume != _sentVolume[slot]) { engine.SetGain(voice, s.Volume); _sentVolume[slot] = s.Volume; }
                    if (s.Pitch != _sentPitch[slot]) { engine.SetPitch(voice, s.Pitch); _sentPitch[slot] = s.Pitch; }
                    if (s.Pan != _sentPan[slot]) { engine.SetPan(voice, s.Pan); _sentPan[slot] = s.Pan; }
                }
                if (playing) _stamps[voice.Slot] = stamp;
                s.IsPlaying = playing;
            }
        }

        for (int slot = 0; slot < _owner.Length; slot++)
        {
            if (_owner[slot].IsNull || _stamps[slot] == stamp) continue;
            engine.Stop(new VoiceHandle(_ownerVoice[slot]));
            _owner[slot] = default;
        }
    }
}
