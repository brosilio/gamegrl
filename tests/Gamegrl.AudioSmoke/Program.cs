using System.Diagnostics;
using Gamegrl.Audio;
using Gamegrl.Audio.Devices;

// Gamegrl.AudioSmoke: a speaker walk (a burst on each speaker in turn) and then a 120 BPM metronome scheduled on the
// beat clock, played on an ALSA device or rendered offline.
//
//   Gamegrl.AudioSmoke [--device default] [--layout stereo|quad|5.1|7.1|7.1.4] [--seconds 4] [--period 256]
//   Gamegrl.AudioSmoke --wav out.wav [--layout 5.1] [--seconds 4]      render offline instead
//   Gamegrl.AudioSmoke --bench N                                       N moving positional voices, offline: real-time factor
//   Gamegrl.AudioSmoke --analyze recording.wav [--expect-spacing N]     click onsets and their spacing (fails without 3 in a row N apart)

string? device = null, wav = null, analyze = null;
int expectSpacing = 0, bench = 0;
var layout = ChannelLayout.Stereo;
double seconds = 4;
int period = 256;
for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(args[i] + " needs a value");
    switch (args[i])
    {
        case "--device": device = Next(); break;
        case "--layout":
            layout = Next() switch { "mono" => ChannelLayout.Mono, "stereo" => ChannelLayout.Stereo, "quad" => ChannelLayout.Quad, "5.1" => ChannelLayout.Surround51, "7.1" => ChannelLayout.Surround71, "7.1.4" => ChannelLayout.Surround714, var x => throw new ArgumentException("unknown layout " + x) };
            break;
        case "--seconds": seconds = double.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--period": period = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--wav": wav = Next(); break;
        case "--analyze": analyze = Next(); break;
        case "--bench": bench = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        case "--expect-spacing": expectSpacing = int.Parse(Next(), System.Globalization.CultureInfo.InvariantCulture); break;
        default: throw new ArgumentException("unknown argument " + args[i]);
    }
}

if (analyze is not null) return Analyze(analyze, expectSpacing);
if (bench > 0) { Bench(bench, headphones: false); Bench(bench, headphones: true); return 0; }

const int Rate = 48000;
AudioEngine engine;
if (wav is not null) engine = AudioEngine.Offline(Rate, layout);
else
{
    var dev = AlsaDevice.Open(AlsaDeviceOptions.Default with { Device = device ?? "default", Layout = layout, PeriodFrames = period });
    Console.WriteLine($"device '{dev.Name}': {dev.SampleRate} Hz, {dev.Layout} ({dev.Layout.Count} ch), period {dev.PeriodFrames}, buffer {dev.BufferFrames} frames ({1000.0 * dev.BufferFrames / dev.SampleRate:0.0} ms)");
    engine = AudioEngine.Start(dev);
}
using var _ = engine;
int rate = engine.SampleRate;
var outLayout = engine.Layout;

// One burst per speaker, in the output layout itself so it lands on exactly that speaker.
var bursts = new AudioClip[outLayout.Count];
var noise = new Random(1);
for (int s = 0; s < outLayout.Count; s++)
{
    int frames = rate / 4, ch = outLayout.Count;
    var data = new float[frames * ch];
    for (int f = 0; f < frames; f++)
    {
        float env = MathF.Sin(MathF.PI * f / frames);
        data[f * ch + s] = 0.3f * env * (float)(noise.NextDouble() * 2 - 1);
    }
    bursts[s] = new AudioClip(outLayout.Speakers[s].ToString(), rate, outLayout, data);
}
AudioClip Blip(float hz) => AudioClip.Generate($"blip {hz}", rate, rate / 20, i => 0.5f * MathF.Exp(-i / (0.006f * rate)) * MathF.Sin(2 * MathF.PI * hz * i / rate));
var accent = Blip(1500);
var tick = Blip(1000);

// Everything is scheduled ahead on exact samples, a little past the device's latency.
long start = engine.Time + engine.LatencyFrames + rate / 10;
for (int s = 0; s < bursts.Length; s++)
{
    engine.Play(bursts[s], PlayOptions.Default with { StartTime = start + s * (rate * 3 / 10) });
    Console.WriteLine($"  speaker {s}: {outLayout.Speakers[s]}");
}
var clock = new BeatClock(new TempoMap(rate, 120), start + bursts.Length * (rate * 3 / 10) + rate / 2);
long end = clock.Origin + (long)(seconds * rate);
long nextBeat = 0;
int beats = 0;

if (wav is not null)
{
    var all = new List<float>();
    var block = new float[512 * outLayout.Count];
    while (engine.Time < end + rate / 2)
    {
        Schedule(engine.Time + rate / 5);
        engine.Render(block);
        engine.Update();
        all.AddRange(block);
    }
    Wav.Write(wav, new AudioClip("smoke", rate, outLayout, all.ToArray()));
    Console.WriteLine($"wrote {wav}: {all.Count / outLayout.Count} frames, {beats} beats");
    return 0;
}

var watch = Stopwatch.StartNew();
while (engine.Time < end + rate / 2)
{
    Schedule(engine.Time + engine.LatencyFrames + rate / 5); // look ahead 200 ms past what's audible
    engine.Update();
    Thread.Sleep(10);
}
var d = engine.Device!;
Console.WriteLine($"played {beats} beats in {watch.Elapsed.TotalSeconds:0.00} s; latency {d.LatencyFrames} frames ({1000.0 * d.LatencyFrames / rate:0.0} ms); underruns {d.Underruns}; dropped commands {engine.DroppedCommands}; real-time thread {(d is AlsaDevice a ? a.RealTimeThread : false)}");
return d.Underruns == 0 ? 0 : 1;

void Schedule(long horizon)
{
    while (true)
    {
        long at = clock.SampleAt(nextBeat * TempoMap.TicksPerBeat);
        if (at > horizon || at >= end) return;
        engine.Play(nextBeat % 4 == 0 ? accent : tick, PlayOptions.Default with { StartTime = at });
        nextBeat++;
        beats++;
    }
}

// Onsets: the first sample of each burst above a threshold after a stretch of quiet; prints the spacing between them.
static int Analyze(string path, int expectSpacing)
{
    var clip = Wav.Read(path);
    int ch = clip.Channels;
    float peak = 0;
    for (int i = 0; i < clip.Samples.Length; i++) peak = MathF.Max(peak, MathF.Abs(clip.Samples[i]));
    float threshold = 0.25f * peak;
    var onsets = new List<long>();
    long quietSince = 0;
    for (int f = 0; f < clip.Frames; f++)
    {
        float m = 0;
        for (int c = 0; c < ch; c++) m = MathF.Max(m, MathF.Abs(clip.Samples[f * ch + c]));
        if (m >= threshold) { if (f - quietSince > clip.SampleRate / 40) onsets.Add(f); quietSince = f; }
    }
    Console.WriteLine($"{path}: {clip.SampleRate} Hz, {clip.Layout}, {clip.Frames} frames, peak {peak:0.000}, {onsets.Count} onsets");
    for (int i = 0; i < onsets.Count; i++)
    {
        // The loudest channel over the 50 ms after the onset.
        var energy = new double[ch];
        for (long f = onsets[i]; f < Math.Min(clip.Frames, onsets[i] + clip.SampleRate / 20); f++)
            for (int c = 0; c < ch; c++) energy[c] += clip.Samples[f * ch + c] * clip.Samples[f * ch + c];
        int loudest = Array.IndexOf(energy, energy.Max());
        string channels = energy.Max() * 0.5 < energy.Where((_, c) => c != loudest).DefaultIfEmpty(0).Max() ? "several channels" : clip.Layout.Speakers[loudest].ToString();
        Console.WriteLine($"  onset {onsets[i]}  {(i > 0 ? "+" + (onsets[i] - onsets[i - 1]) : "")}  {channels}");
    }
    if (expectSpacing == 0) return 0;
    int run = 0, best = 0;
    for (int i = 1; i < onsets.Count; i++) { run = onsets[i] - onsets[i - 1] == expectSpacing ? run + 1 : 0; best = Math.Max(best, run); }
    Console.WriteLine($"longest run spaced exactly {expectSpacing}: {best}");
    return best >= 3 ? 0 : 1;
}

// N looping positional voices whose directions change every 60 Hz frame, rendered offline for 10 s.
static void Bench(int voices, bool headphones)
{
    const int rate = 48000, seconds = 10, frame = rate / 60;
    var engine = AudioEngine.Offline(rate, headphones ? ChannelLayout.Stereo : ChannelLayout.Surround71, AudioEngineSettings.Default with { Headphones = headphones, MaxVoices = Math.Max(256, voices) });
    var clip = AudioClip.Sine(220, 1.37f, rate);
    var handles = new VoiceHandle[voices];
    var random = new Random(4);
    for (int i = 0; i < voices; i++) handles[i] = engine.Play(clip, PlayOptions.Default with { Loop = true, Positional = true, Gain = 0.01f });
    var block = new float[frame * engine.Layout.Count];
    var watch = Stopwatch.StartNew();
    for (int f = 0; f < seconds * 60; f++)
    {
        for (int i = 0; i < voices; i++)
        {
            float az = (f * 0.7f + i * 37) % 360;
            engine.SetSpatial(handles[i], SpatialParams.Default with { Direction = Gamegrl.Audio.Spatial.Directions.FromAngles(az, (i % 7) * 10 - 20), Gain = 0.5f, Low = 1, Mid = 0.8f, High = 0.5f }, 1f / 60);
        }
        engine.Render(block);
        engine.Update();
    }
    double elapsed = watch.Elapsed.TotalSeconds;
    Console.WriteLine($"{voices} positional voices, {(headphones ? "headphones (7.1.4 bed, binaural)" : "7.1 speakers")}: {seconds / elapsed:0.0}× real time, {elapsed / (seconds * 60) * 1000:0.000} ms per 60 Hz frame of audio");
}
