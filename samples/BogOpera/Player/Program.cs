using BogOpera;
using Gamegrl;
using Gamegrl.Audio;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

if (args is ["--wav", var path])
{
    using var engine = AudioEngine.Offline(48000, ChannelLayout.Stereo);
    var choir = new Choir(engine);
    // Four bars of music, then room for the last singer and its echoes to decay.
    int music = engine.Frames(0.08 + 16 * 60.0 / 112);
    var samples = new float[(music + engine.Frames(0.6)) * 2];
    for (int frame = 0; frame < samples.Length / 2;)
    {
        int count = Math.Min(256, samples.Length / 2 - frame);
        choir.Conduct(endStep: 32);
        engine.Render(samples.AsSpan(frame * 2, count * 2));
        engine.Update();
        frame += count;
    }
    using var file = File.Create(path);
    Wav.Write(file, samples, engine.SampleRate, engine.Layout, WavSampleFormat.Pcm16);
    Console.WriteLine($"Recorded Bog Opera to {path}");
    return 0;
}

double seconds = 0;
if (args.Length != 0 && !(args is ["--seconds", var limit] && double.TryParse(limit, out seconds) && double.IsFinite(seconds) && seconds > 0))
{
    Console.Error.WriteLine("Usage: BogOpera [--seconds N | --wav recording.wav]");
    return 1;
}

using var app = EngineBuilder.Create()
    .AddModule(new Sdl3PlatformModule(WindowDesc.Default("Bog Opera")))
    .AddModule<VulkanModule>()
    .AddModule(new RenderingModule())
    .AddModule<BogOperaModule>()
    .SetResource(new Curtain(seconds))
    .Build();
Console.WriteLine($"Bog Opera is playing on {app.World.Resource<AudioContext>().Engine.Device?.Name ?? "offline"}");
DesktopHost.Run(app);
return 0;
