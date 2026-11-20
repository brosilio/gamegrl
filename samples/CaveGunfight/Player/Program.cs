using CaveGunfight;
using Gamegrl;
using Gamegrl.Audio;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

// Cave Gunfight: walk into a cave under fire and hear it.
//   CaveGunfight [--seconds N]                       the game, in a window (GAMEGRL_HEADPHONES=1 for binaural)
//   CaveGunfight --tour                              the same walk as --wav, in the window, hands off
//   CaveGunfight --wav walk.wav [--headphones]       a scripted 52 s walk rendered offline, for listening

if (args.Length > 0 && args[0] == "--wav")
{
    if (args.Length < 2) { Console.Error.WriteLine("Usage: CaveGunfight --wav walk.wav [--headphones]"); return 1; }
    bool phones = args.Contains("--headphones");
    using var tour = EngineBuilder.Create()
        .AddModule(new AudioModule(AudioOutput.Offline, headphones: phones))
        .AddModule(new CaveModule(tour: true))
        .Build();
    var engine = tour.World.Resource<AudioContext>().Engine;
    const int seconds = 54, rate = 48000, frame = rate / 60;
    var samples = new float[seconds * rate * 2];
    for (int f = 0; f + frame <= seconds * rate; f += frame)
    {
        tour.Tick(1.0 / 60);
        engine.Render(samples.AsSpan(f * 2, frame * 2));
    }
    Wav.Write(args[1], new AudioClip("cave walk", rate, ChannelLayout.Stereo, samples));
    Console.WriteLine($"Recorded the cave walk to {args[1]}{(phones ? " (binaural)" : "")}");
    return 0;
}

double limit = 0;
bool watch = args.Contains("--tour");
var rest = args.Where(a => a != "--tour").ToArray();
if (rest.Length != 0 && !(rest is ["--seconds", var value] && double.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out limit) && limit > 0))
{
    Console.Error.WriteLine("Usage: CaveGunfight [--tour] [--seconds N] | --wav walk.wav [--headphones]");
    return 1;
}
using var app = EngineBuilder.Create()
    .AddModule(new Sdl3PlatformModule(WindowDesc.Default("Cave Gunfight")))
    .AddModule<VulkanModule>()
    .AddModule(new RenderingModule(vsync: true))
    .AddModule(new CaveModule(tour: watch, quitAfter: limit))
    .AddModule(new CaveHudModule())
    .Build();
var audio = app.World.Resource<AudioContext>().Engine;
Console.WriteLine($"Cave Gunfight on {audio.Device?.Name ?? "offline"}{(audio.Headphones ? ", binaural" : "")}");
DesktopHost.Run(app);
return 0;
