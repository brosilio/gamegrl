using System.Globalization;
using CaveGunfight;
using Gamegrl;
using Gamegrl.Audio;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

// Cave Gunfight: walk into a cave under fire and hear it.
//   CaveGunfight [--tour] [--overlay] [--seconds N] [--capture-at T,...]   the game in a window (GAMEGRL_HEADPHONES=1
//                                                                for binaural); --tour plays the scripted walk hands off,
//                                                                --overlay starts with the acoustic overlay (F1) on
//   CaveGunfight --wav walk.wav [--headphones] [--capture-at T,...]   the scripted walk rendered offline
// F12 (or each --capture-at time, in seconds) writes a capture to artifacts/captures/<time>/: the last 12 s of output,
// a report of every voice's paths, taps and gains and of the reverb estimate, and its decay curves.

const string Usage = "Usage: CaveGunfight [--tour] [--overlay] [--seconds N] [--capture-at T,...] | --wav walk.wav [--headphones] [--capture-at T,...]";
string? wav = null;
bool tour = false, phones = false, overlay = false;
double limit = 0;
var captures = new CaveCaptures();
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--wav" when i + 1 < args.Length: wav = args[++i]; break;
        case "--headphones": phones = true; break;
        case "--tour": tour = true; break;
        case "--overlay": overlay = true; break;
        case "--seconds" when i + 1 < args.Length && double.TryParse(args[i + 1], CultureInfo.InvariantCulture, out limit) && limit > 0: i++; break;
        case "--capture-at" when i + 1 < args.Length:
            var times = args[++i].Split(',');
            captures.At = new double[times.Length];
            for (int k = 0; k < times.Length; k++)
                if (!double.TryParse(times[k], CultureInfo.InvariantCulture, out captures.At[k])) { Console.Error.WriteLine(Usage); return 1; }
            Array.Sort(captures.At);
            break;
        default: Console.Error.WriteLine(Usage); return 1;
    }
}

if (wav is not null)
{
    using var walk = EngineBuilder.Create()
        .AddModule(new AudioModule(AudioOutput.Offline, headphones: phones))
        .AddModule(new CaveModule(tour: true, captures: captures))
        .Build();
    var engine = walk.World.Resource<AudioContext>().Engine;
    const int seconds = 54, rate = 48000, frame = rate / 60;
    var samples = new float[seconds * rate * 2];
    for (int f = 0; f + frame <= seconds * rate; f += frame)
    {
        walk.Tick(1.0 / 60);
        engine.Render(samples.AsSpan(f * 2, frame * 2));
    }
    Wav.Write(wav, new AudioClip("cave walk", rate, ChannelLayout.Stereo, samples));
    Console.WriteLine($"Recorded the cave walk to {wav}{(phones ? " (binaural)" : "")}");
    return 0;
}

using var app = EngineBuilder.Create()
    .AddModule(new Sdl3PlatformModule(WindowDesc.Default("Cave Gunfight")))
    .AddModule<VulkanModule>()
    .AddModule(new RenderingModule(vsync: true))
    .AddModule(new CaveModule(tour: tour, quitAfter: limit, captures: captures))
    .AddModule(new CaveHudModule(overlay))
    .Build();
var audio = app.World.Resource<AudioContext>().Engine;
Console.WriteLine($"Cave Gunfight on {audio.Device?.Name ?? "offline"}{(audio.Headphones ? ", binaural" : "")}");
DesktopHost.Run(app);
return 0;
