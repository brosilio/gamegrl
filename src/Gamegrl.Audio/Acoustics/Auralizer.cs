using System.Numerics;
using Gamegrl.Audio.Spatial;

namespace Gamegrl.Audio.Acoustics;

/// <summary>What to render with <see cref="Auralizer.Render"/>: where the listener and the source are, in which scene, and how.</summary>
public struct AuralizeSetup
{
    public AcousticScene Scene;
    /// <summary>The listener's world transform (it faces −Z).</summary>
    public Matrix4x4 Listener;
    public Vector3 Source;
    public AudioEmitter Emitter;
    public AudioEnvironment Environment;
    public int SampleRate;
    /// <summary>Speakers to render for; with <see cref="Headphones"/>, stereo binaural.</summary>
    public ChannelLayout Output;
    public bool Headphones;
    /// <summary>The direct sound alone, as in an anechoic room (for comparing).</summary>
    public bool Dry;
    /// <summary>How long to keep rendering after the sound ends, for the room's tail.</summary>
    public float TailSeconds;
    /// <summary>A reverb estimate already made (from a measurement); estimated from the scene when null.</summary>
    public ReverbParams? Reverb;

    public static AuralizeSetup For(AcousticScene scene, Matrix4x4 listener, Vector3 source) => new()
    {
        Scene = scene, Listener = listener, Source = source, Emitter = AudioEmitter.Default, Environment = AudioEnvironment.Default with { Hdr = false },
        SampleRate = 48000, Output = ChannelLayout.Stereo, TailSeconds = 1.5f,
    };
}

/// <summary>
/// Renders a sound as heard by a listener in a scene, offline, through the same pipeline a running game uses: the direct
/// path (distance, air, occlusion and transmission), early reflections from image sources, and the room's reverb, for
/// speakers or headphones. For auditioning a space in the editor, exporting impulse responses, and tests.
/// </summary>
public static class Auralizer
{
    public static AudioClip Render(AudioClip sound, in AuralizeSetup setup)
    {
        var settings = AudioEngineSettings.Default with { Limiter = false, Headphones = setup.Headphones, MaxVoices = 16, MaxRealVoices = 4 };
        var output = setup.Headphones ? ChannelLayout.Stereo : setup.Output;
        using var engine = AudioEngine.Offline(setup.SampleRate, output, settings);
        using var context = new AudioContext(engine, inlineSimulation: true)
        {
            Listener = setup.Listener,
            Scene = setup.Dry ? AcousticScene.Empty : setup.Scene,
        };
        context.Environment = setup.Environment with { Hdr = false, Reverb = !setup.Dry && setup.Environment.Reverb };
        var env = context.Environment;
        if (env.Reverb)
        {
            var reverb = setup.Reverb ?? ReverbEstimator.Estimate(setup.Scene, setup.Listener.Translation, 4096, 128, env.TemperatureC, env.HumidityPercent);
            engine.SetReverb(reverb, 0);
        }
        var emitter = setup.Emitter;
        var occlusion = context.Occlusion(setup.Source, emitter);
        var spatial = context.Spatialize(setup.Source, Vector3.Zero, Vector3.Zero, emitter, 1, occlusion, out _);
        var voice = engine.Play(sound, PlayOptions.Default with { Positional = true, Spatial = spatial });
        if (!setup.Dry && env.Reflections && emitter.Reflections && setup.Scene.TriangleCount > 0)
        {
            Span<ReflectionPath> paths = stackalloc ReflectionPath[ReflectionTaps.Count];
            int found = ImageSources.Find(setup.Scene, setup.Listener.Translation, setup.Source, paths);
            Span<Reflection> taps = stackalloc Reflection[ReflectionTaps.Count];
            int n = AudioContext.Taps(paths[..found], setup.Source, setup.Listener, emitter, Propagation.SpeedOfSound(env.TemperatureC), taps, out var tone);
            if (n > 0) engine.SetReflections(voice, taps[..n], tone, 0);
        }
        int frames = (int)((sound.Duration + Math.Max(0, setup.TailSeconds)) * setup.SampleRate);
        var samples = new float[frames * output.Count];
        engine.Render(samples);
        return new AudioClip(setup.Dry ? sound.Name + " (dry)" : sound.Name + " (room)", setup.SampleRate, output, samples);
    }

    /// <summary>The impulse response from the source to the listener: a unit click rendered through the room.</summary>
    public static AudioClip ImpulseResponse(in AuralizeSetup setup) =>
        Render(new AudioClip("impulse", setup.SampleRate, ChannelLayout.Mono, [1f]), setup);
}
