using Gamegrl.AssetPipeline;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Devices;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>The audio core, rendered offline: exact sample timing, resampling, ramps, buses, the limiter, WAV and the tempo map.</summary>
internal static class AudioTests
{
    private const int Rate = 48000;
    private const float Minus3dB = 0.70710678f;

    private static AudioEngine Engine(ChannelLayout? layout = null, bool limiter = false) =>
        AudioEngine.Offline(Rate, layout ?? ChannelLayout.Stereo, AudioEngineSettings.Default with { Limiter = limiter });

    private static float[] Render(AudioEngine e, int frames)
    {
        var buffer = new float[frames * e.Layout.Count];
        e.Render(buffer);
        e.Update();
        return buffer;
    }

    private static AudioClip Dc(int frames = 4800) => AudioClip.Generate("dc", Rate, frames, _ => 1);

    /// <summary>Frequency from positive-going zero crossings of one channel, interpolated between samples.</summary>
    private static double Frequency(float[] interleaved, int channels, int channel, int from, int to, int rate)
    {
        double first = -1, last = -1;
        int count = 0;
        for (int i = from + 1; i < to; i++)
        {
            float a = interleaved[(i - 1) * channels + channel], b = interleaved[i * channels + channel];
            if (a < 0 && b >= 0)
            {
                double t = i - 1 + a / (a - b);
                if (first < 0) first = t; else count++;
                last = t;
            }
        }
        return count * (double)rate / (last - first);
    }

    /// <summary>An asset source holding artifacts in memory.</summary>
    private sealed class MemoryAssets : IAssetSource
    {
        public readonly Dictionary<Guid, (AssetKind Kind, byte[] Bytes)> Items = [];
        public bool TryRead(Guid guid, out AssetKind kind, out ReadOnlyMemory<byte> artifact)
        {
            if (Items.TryGetValue(guid, out var item)) { kind = item.Kind; artifact = item.Bytes; return true; }
            kind = AssetKind.Unknown; artifact = default; return false;
        }
        public Guid FindByPath(string path) => Guid.Empty;
        public event Action<Guid>? Changed;
        public void Raise(Guid guid) => Changed?.Invoke(guid);
        public void PumpChanges() { }
    }

    /// <summary>A WAV source file run through the importer, as the editor would.</summary>
    private static byte[] Imported(AudioClip clip, WavSampleFormat format)
    {
        var source = new MemoryStream();
        Wav.Write(source, clip.Samples, clip.SampleRate, clip.Layout, format);
        return new AudioImporter().Import(new ImportContext("Assets/test.wav", source.ToArray(), new Dictionary<string, string>(), _ => [], _ => null));
    }

    public static void Register()
    {
        Test("audio: WAV round trips in float, 16- and 24-bit PCM, with 5.1 and 7.1.4 channel masks", () =>
        {
            var random = new Random(3);
            var samples = new float[600 * 6];
            for (int i = 0; i < samples.Length; i++) samples[i] = (float)(random.NextDouble() * 2 - 1);
            foreach (var format in (ReadOnlySpan<WavSampleFormat>)[WavSampleFormat.Float32, WavSampleFormat.Pcm24, WavSampleFormat.Pcm16])
            {
                var stream = new MemoryStream();
                Wav.Write(stream, samples, 44100, ChannelLayout.Surround51, format);
                var clip = Wav.Read(stream.ToArray());
                Assert.True(clip.Layout == ChannelLayout.Surround51 && clip.SampleRate == 44100 && clip.Frames == 600, $"{format}: {clip.Layout} {clip.SampleRate} {clip.Frames}");
                float tolerance = format switch { WavSampleFormat.Float32 => 0, WavSampleFormat.Pcm24 => 1.5f / 8388607, _ => 1.5f / 32767 };
                float worst = 0;
                for (int i = 0; i < samples.Length; i++) worst = MathF.Max(worst, MathF.Abs(clip.Samples[i] - samples[i]));
                Assert.True(worst <= tolerance, $"{format} error {worst}");
            }
            var tall = new MemoryStream();
            Wav.Write(tall, new float[12 * 10], Rate, ChannelLayout.Surround714);
            Assert.True(Wav.Read(tall.ToArray()).Layout == ChannelLayout.Surround714, "7.1.4 keeps its mask");
            var stereo = new MemoryStream();
            Wav.Write(stereo, new float[20], Rate, ChannelLayout.Stereo, WavSampleFormat.Pcm16);
            Assert.Equal(44 + 40, (int)stereo.Length); // plain 44-byte header for stereo PCM
        });

        Test("audio: downmixes fold centre and surrounds at −3 dB and drop LFE; ALSA channel order maps to ours", () =>
        {
            var m = ChannelLayout.MixMatrix(ChannelLayout.Surround51, ChannelLayout.Stereo);
            // Rows: FL, FR; columns: FL FR FC LFE BL BR.
            float[] expected = [1, 0, Minus3dB, 0, Minus3dB, 0, 0, 1, Minus3dB, 0, 0, Minus3dB];
            for (int i = 0; i < expected.Length; i++) Assert.True(MathF.Abs(m[i] - expected[i]) < 1e-6f, $"5.1 → stereo [{i}] = {m[i]}");
            var mono = ChannelLayout.MixMatrix(ChannelLayout.Stereo, ChannelLayout.Mono);
            Assert.True(MathF.Abs(mono[0] - Minus3dB) < 1e-6f && MathF.Abs(mono[1] - Minus3dB) < 1e-6f, "stereo → mono at −3 dB each");
            var up = ChannelLayout.MixMatrix(ChannelLayout.Stereo, ChannelLayout.Surround71);
            Assert.True(up[0] == 1 && up[2 + 1] == 1 && up.Sum() == 2, "stereo → 7.1 uses the fronts only");
            // ALSA's 5.1 order is FL FR RL RR FC LFE; ours is FL FR FC LFE BL BR.
            var perm = Alsa.Permutation(Alsa.DefaultChmap(6), ChannelLayout.Surround51);
            Assert.True(perm.SequenceEqual(new[] { 0, 1, 4, 5, 2, 3 }), $"permutation {string.Join(",", perm)}");
            var perm71 = Alsa.Permutation(Alsa.DefaultChmap(8), ChannelLayout.Surround71);
            Assert.True(perm71.SequenceEqual(new[] { 0, 1, 4, 5, 2, 3, 6, 7 }), $"7.1 permutation {string.Join(",", perm71)}");
        });

        Test("audio: voices start on their exact sample, mid-block, and mono pans at equal power", () =>
        {
            var e = Engine();
            var click = AudioClip.Generate("click", Rate, 1, _ => 1);
            e.Play(click, PlayOptions.Default with { StartTime = 1000 });                // block 2, 488 frames in
            e.Play(click, PlayOptions.Default with { StartTime = 1700, Pan = -1 });
            e.Play(click, PlayOptions.Default with { StartTime = 1701, Pan = 0.5f });
            var output = Render(e, 2048);
            for (int f = 0; f < 2048; f++)
            {
                float l = output[2 * f], r = output[2 * f + 1];
                if (f == 1000) Assert.True(MathF.Abs(l - Minus3dB) < 1e-6f && MathF.Abs(r - Minus3dB) < 1e-6f, $"centre at −3 dB: {l}, {r}");
                else if (f == 1700) Assert.True(l == 1 && MathF.Abs(r) < 1e-7f, $"hard left: {l}, {r}");
                else if (f == 1701) Assert.True(MathF.Abs(l * l + r * r - 1) < 1e-6f && r > l, $"equal power at 0.5: {l}, {r}");
                else Assert.True(l == 0 && r == 0, $"silence at frame {f}: {l}, {r}");
            }
            Assert.Equal(0, e.ActiveVoices);
            // A start in the past starts at once.
            e.Play(click, PlayOptions.Default with { StartTime = 10 });
            var late = Render(e, 16);
            Assert.True(late[0] > 0.7f, "a late start plays immediately");
        });

        Test("audio: resampling keeps pitch — a 44.1 kHz clip plays at its own pitch at 48 kHz; pitch 1.5 raises a fifth", () =>
        {
            var e = Engine(ChannelLayout.Mono);
            var cd = AudioClip.Sine(1000, 1, sampleRate: 44100, amplitude: 1);
            e.Play(cd);
            var output = Render(e, Rate / 2);
            double f = Frequency(output, 1, 0, 4800, Rate / 2, Rate);
            Assert.True(Math.Abs(f - 1000) < 0.5, $"44.1 kHz clip at {f} Hz");
            // Catmull-Rom against the ideal sine (the mono clip goes to the mono output at unity).
            double step = 1000.0 / 48000;
            float worst = 0;
            for (int i = 100; i < 20000; i++) worst = MathF.Max(worst, MathF.Abs(output[i] - (float)Math.Sin(2 * Math.PI * step * i)));
            Assert.True(worst < 2e-3f, $"interpolation error {worst}");

            var e2 = Engine(ChannelLayout.Mono);
            var voice = e2.Play(AudioClip.Sine(1000, 1, amplitude: 1), PlayOptions.Default with { Pitch = 1.5f });
            var up = Render(e2, Rate / 4);
            Assert.True(Math.Abs(Frequency(up, 1, 0, 1000, Rate / 4, Rate) - 1500) < 0.5, "pitch 1.5");
            e2.SetPitch(voice, 0.5f);
            var down = Render(e2, Rate / 4);
            Assert.True(Math.Abs(Frequency(down, 1, 0, 1000, Rate / 4, Rate) - 500) < 0.5, "pitch 0.5");
        });

        Test("audio: gain ramps are smooth, loops are seamless, stops fade and free the voice", () =>
        {
            var e = Engine();
            var voice = e.Play(Dc(), PlayOptions.Default with { Loop = true, Pan = -1 });
            Render(e, 1000);
            e.SetGain(voice, 0, rampSeconds: 0.01f); // 480 frames
            var fade = Render(e, 1000);
            float prev = 1, worstStep = 0;
            for (int f = 0; f < 1000; f++)
            {
                float l = fade[2 * f];
                Assert.True(l <= prev + 1e-6f, $"gain only falls ({l} after {prev} at {f})");
                worstStep = MathF.Max(worstStep, prev - l);
                prev = l;
            }
            Assert.True(worstStep <= 1f / 480 + 1e-5f && fade[2 * 999] == 0, $"linear over 480 frames (largest step {worstStep})");
            Assert.True(e.IsPlaying(voice), "a silent voice keeps playing");

            // 100 whole cycles in 4800 frames: looping continues the sine exactly.
            var e2 = Engine(ChannelLayout.Mono);
            e2.Play(AudioClip.Sine(1000, 0.1f, amplitude: 1), PlayOptions.Default with { Loop = true });
            var loop = Render(e2, 9600);
            float seam = 0;
            for (int i = 4700; i < 4900; i++) seam = MathF.Max(seam, MathF.Abs(loop[i] - MathF.Sin(2 * MathF.PI * 1000 * (i % 4800) / Rate)));
            Assert.True(seam < 1e-6f, $"seamless loop ({seam})");

            var e3 = Engine();
            var held = e3.Play(Dc(), PlayOptions.Default with { Loop = true, Pan = -1 });
            Render(e3, 512);
            e3.Stop(held, fadeSeconds: 0.005f, at: 1000); // 240-frame fade from sample 1000
            var tail = Render(e3, 1536);
            for (int f = 512; f < 2048; f++)
            {
                float l = tail[2 * (f - 512)];
                if (f < 1000) Assert.True(l == 1, $"full level before the stop at {f}: {l}");
                else if (f >= 1240) Assert.True(l == 0, $"silent after the fade at {f}: {l}");
            }
            Assert.True(!e3.IsPlaying(held) && e3.ActiveVoices == 0, "the stopped voice is freed");

            var e4 = Engine();
            var shortVoice = e4.Play(AudioClip.Generate("blip", Rate, 100, _ => 0.5f));
            Render(e4, 512);
            Assert.True(!e4.IsPlaying(shortVoice), "a one-shot ends by itself");
            e4.Stop(shortVoice); // stale: ignored
        });

        Test("audio: bus gains multiply down the tree and voices sum", () =>
        {
            var e = Engine();
            var music = e.CreateBus(e.Master, 0.5f);
            var drums = e.CreateBus(music, 0.5f);
            e.Play(Dc(), PlayOptions.Default with { Bus = drums, Loop = true, Pan = -1 });
            e.Play(Dc(), PlayOptions.Default with { Gain = 0.5f, Loop = true, Pan = -1 });
            var output = Render(e, 256);
            Assert.True(MathF.Abs(output[2 * 100] - 0.75f) < 1e-6f, $"0.5 × 0.5 + 0.5 = {output[2 * 100]}");
            e.SetBusGain(music, 0, rampSeconds: 0);
            output = Render(e, 256);
            Assert.True(MathF.Abs(output[2 * 100] - 0.5f) < 1e-6f, $"muting the parent mutes its children: {output[2 * 100]}");
        });

        Test("audio: the limiter holds peaks under its ceiling and passes quiet signals unchanged, delayed", () =>
        {
            var limiter = new Limiter(2, Rate, ceilingDb: -1);
            int n = 4800, stride = 4800;
            var planar = new float[2 * stride];
            for (int i = 0; i < n; i++)
            {
                float x = 4 * MathF.Sin(2 * MathF.PI * 440 * i / Rate);
                if (i == 3000) x = 20; // a lone spike
                planar[i] = x; planar[stride + i] = -0.5f * x;
            }
            limiter.Process(planar, stride, n);
            float ceiling = MathF.Pow(10, -1 / 20f), peak = 0;
            foreach (var x in planar) { Assert.True(float.IsFinite(x), "finite"); peak = MathF.Max(peak, MathF.Abs(x)); }
            Assert.True(peak <= ceiling + 1e-5f, $"peak {peak} over the ceiling {ceiling}");

            var quiet = new Limiter(1, Rate);
            var signal = new float[2000];
            for (int i = 0; i < signal.Length; i++) signal[i] = 0.5f * MathF.Sin(i * 0.05f);
            var copy = (float[])signal.Clone();
            quiet.Process(copy, copy.Length, copy.Length);
            int d = quiet.LatencyFrames;
            for (int i = d; i < signal.Length; i++) Assert.True(copy[i] == signal[i - d], $"quiet passes untouched at {i}");

            var e = Engine(limiter: true);
            e.Play(AudioClip.Sine(200, 0.5f, amplitude: 1), PlayOptions.Default with { Gain = 8 });
            var loud = Render(e, Rate / 4);
            float loudPeak = 0;
            foreach (var x in loud) loudPeak = MathF.Max(loudPeak, MathF.Abs(x));
            Assert.True(loudPeak <= ceiling + 1e-5f && loudPeak > 0.85f, $"the master limiter holds a voice at gain 8 to {loudPeak}");
        });

        Test("audio: tempo maps are exact — 120 BPM, a change to 174.5 BPM in 7/8, tick ↔ sample round trips", () =>
        {
            var map = new TempoMap(Rate, 120);
            Assert.Equal(24000L, map.SampleAtBeat(1));
            Assert.Equal(96000L, map.SampleAtBeat(4));
            Assert.Equal(960L, map.TickAt(24000));
            Assert.Equal(959L, map.TickAt(23999));
            map.Change(2, 349, 2, beatsPerBar: 7); // bar 2 starts at beat 8 = sample 192000
            Assert.Equal(192000L, map.SampleAt(map.TickAtBar(2)));
            // One beat at 174.5 BPM is 60 × 48000 × 2 ÷ 349 = 16504.3 samples: the beat starts on the next whole sample.
            Assert.Equal(192000L + 16505, map.SampleAt(map.TickAtBar(2) + TempoMap.TicksPerBeat));
            Assert.Equal(new BarBeat(3, 1, 5), map.BarBeatAt(map.TickAtBar(2) + 8 * TempoMap.TicksPerBeat + 5));
            Assert.Equal(new BarBeat(1, 3, 0), map.BarBeatAt(7 * TempoMap.TicksPerBeat));
            for (long t = 0; t < 400_000; t += 37) Assert.True(map.TickAt(map.SampleAt(t)) == t, $"round trip at tick {t}");
            for (long s = 0; s < 2_000_000; s += 997) { long t = map.TickAt(s); Assert.True(map.SampleAt(t) <= s && map.SampleAt(t + 1) > s, $"tick {t} holds sample {s}"); }

            var clock = new BeatClock(new TempoMap(Rate, 120), origin: 1000);
            Assert.Equal((1L, 25000L), clock.NextBeat(1001));
            Assert.Equal((0L, 1000L), clock.NextBeat(1000));
            Assert.Equal(new BarBeat(0, 2, 0), clock.PositionAt(1000 + 48000));
        });

        Test("audio: AudioSource entities play on start, follow volume, restart on request, stop when despawned or stripped", () =>
        {
            var assets = new MemoryAssets();
            var hum = new AssetRef<AudioAsset>(Guid.NewGuid());
            var blip = new AssetRef<AudioAsset>(Guid.NewGuid());
            // 16-bit sources: 0.5 survives PCM exactly (16384 / 32768).
            assets.Items[hum.Guid] = (AssetKind.Audio, Imported(AudioClip.Generate("hum", Rate, 4800, _ => 0.5f), WavSampleFormat.Pcm16));
            assets.Items[blip.Guid] = (AssetKind.Audio, Imported(AudioClip.Generate("blip", Rate, 100, _ => 0.5f), WavSampleFormat.Pcm16));
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            b.World.SetResource<IAssetSource>(assets);
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            var warnings = new List<string>();
            context.Warn = warnings.Add;
            var engine = context.Engine;
            float Left(int frames) { var o = new float[frames * 2]; engine.Render(o); return o[^2]; }

            var looping = w.Spawn(AudioSource.Of(hum, loop: true) with { Pan = -1 });
            var once = w.Spawn(AudioSource.Of(blip) with { Pan = 1 });
            var broken = w.Spawn(AudioSource.Of(new AssetRef<AudioAsset>(Guid.NewGuid())));
            app.Tick(1 / 60.0);
            Assert.True(w.Get<AudioSource>(looping).IsPlaying && w.Get<AudioSource>(once).IsPlaying, "both started");
            Assert.True(!w.Get<AudioSource>(broken).IsPlaying && warnings.Count == 1, $"a missing clip warns once and stays silent ({warnings.Count})");
            Assert.True(Left(256) == 0.5f, "the hum plays hard left at full volume");
            app.Tick(1 / 60.0);
            Assert.True(!w.Get<AudioSource>(once).IsPlaying, "the 100-frame blip ended by itself");

            w.Get<AudioSource>(looping).Volume = 0.5f;
            app.Tick(1 / 60.0);
            Assert.True(MathF.Abs(Left(1024) - 0.25f) < 1e-6f, "volume changes reach the voice");

            w.Get<AudioSource>(once).Play = true;
            app.Tick(1 / 60.0);
            Assert.True(w.Get<AudioSource>(once).IsPlaying && !w.Get<AudioSource>(once).Play, "Play restarts it and is cleared");
            Left(256);

            w.Destroy(looping);
            app.Tick(1 / 60.0);
            Assert.True(Left(1024) == 0, "despawning stops the voice");
            app.Tick(1 / 60.0);
            Assert.Equal(0, engine.ActiveVoices);

            var stripped = w.Spawn(AudioSource.Of(hum, loop: true));
            app.Tick(1 / 60.0);
            Assert.Equal(1, engine.ActiveVoices);
            w.Remove<AudioSource>(stripped);
            app.Tick(1 / 60.0);
            Left(1024);
            app.Tick(1 / 60.0);
            Assert.Equal(0, engine.ActiveVoices);

            // Reimporting a clip reloads it for the next play.
            assets.Items[hum.Guid] = (AssetKind.Audio, Imported(AudioClip.Generate("hum", Rate, 4800, _ => 0.25f), WavSampleFormat.Pcm16));
            assets.Raise(hum.Guid);
            w.Spawn(AudioSource.Of(hum, loop: true) with { Pan = -1 });
            app.Tick(1 / 60.0);
            Assert.True(Left(256) == 0.25f, "the reimported clip plays");
        });

        Test("audio: the SPSC ring carries a million messages between threads in order", () =>
        {
            var ring = new SpscRing<long>(256);
            const long count = 1_000_000;
            var producer = new Thread(() => { for (long i = 0; i < count; i++) while (!ring.TryPush(i)) Thread.SpinWait(4); });
            producer.Start();
            long expected = 0;
            while (expected < count)
            {
                if (ring.TryPop(out long v)) { if (v != expected) throw new Exception($"got {v}, expected {expected}"); expected++; }
                else Thread.SpinWait(4);
            }
            producer.Join();
            Assert.True(!ring.TryPop(out _), "empty at the end");
        });
    }
}
