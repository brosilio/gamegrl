using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Dsp;
using Gamegrl.Audio.Spatial;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>Spatial audio checked against reference answers: VBAP, ISO 9613-1, Woodworth, Doppler, HDR and ambisonics.</summary>
internal static class AudioSpatialTests
{
    private const int Rate = 48000;

    private static float[] Gains(Panner p, Vector3 dir, float spread = 0)
    {
        var g = new float[p.Layout.Count];
        p.Pan(dir, spread, g);
        return g;
    }

    private static float Power(float[] g) { float s = 0; foreach (var x in g) s += x * x; return s; }
    private static float G(float[] g, ChannelLayout l, Speaker s) => g[l.IndexOf(s)];

    private static double Rms(float[] interleaved, int channels, int channel, int from, int to)
    {
        double sum = 0;
        for (int i = from; i < to; i++) { double x = interleaved[i * channels + channel]; sum += x * x; }
        return Math.Sqrt(sum / (to - from));
    }

    private static AudioEngine Engine(ChannelLayout layout, bool headphones = false) =>
        AudioEngine.Offline(Rate, layout, AudioEngineSettings.Default with { Limiter = false, Headphones = headphones });

    private static float[] Render(AudioEngine e, int frames)
    {
        var buffer = new float[frames * e.Layout.Count];
        e.Render(buffer);
        e.Update();
        return buffer;
    }

    public static void Register()
    {
        Test("audio spatial: VBAP lands on speakers exactly, splits evenly between them, keeps power, and covers stereo to 7.1.4", () =>
        {
            var l51 = ChannelLayout.Surround51;
            var p51 = new Panner(l51);
            var g = Gains(p51, Directions.FromAngles(30, 0));
            Assert.True(MathF.Abs(G(g, l51, Speaker.FrontLeft) - 1) < 1e-5f && Power(g) - 1 < 1e-5f, $"on FL: {string.Join(",", g)}");
            g = Gains(p51, Directions.FromAngles(15, 0));
            Assert.True(MathF.Abs(G(g, l51, Speaker.FrontLeft) - G(g, l51, Speaker.FrontCenter)) < 1e-5f && MathF.Abs(G(g, l51, Speaker.FrontLeft) - 0.70710678f) < 1e-4f, "halfway FL–FC");
            var random = new Random(9);
            foreach (var layout in (ReadOnlySpan<ChannelLayout>)[ChannelLayout.Stereo, ChannelLayout.Quad, ChannelLayout.Surround51, ChannelLayout.Surround71, ChannelLayout.Surround714])
            {
                var p = new Panner(layout);
                for (int i = 0; i < 300; i++)
                {
                    var dir = Vector3.Normalize(new Vector3((float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1, (float)random.NextDouble() * 2 - 1));
                    var gains = Gains(p, dir);
                    Assert.True(MathF.Abs(Power(gains) - 1) < 1e-4f, $"{layout}: power {Power(gains)} for {dir}");
                    if (layout.Has(Speaker.Lfe)) Assert.True(G(gains, layout, Speaker.Lfe) == 0, "LFE takes no share");
                    foreach (var x in gains) Assert.True(x >= 0 && float.IsFinite(x), "gains are finite and non-negative");
                }
            }
            var l71 = ChannelLayout.Surround71;
            g = Gains(new Panner(l71), Directions.FromAngles(120, 0));
            for (int c = 0; c < l71.Count; c++)
                Assert.True((l71.Speakers[c] is Speaker.SideLeft or Speaker.BackLeft) == (g[c] > 1e-4f), $"120° uses only SL and BL ({l71.Speakers[c]} = {g[c]})");

            var st = ChannelLayout.Stereo;
            var ps = new Panner(st);
            g = Gains(ps, Directions.FromAngles(0, 0));
            Assert.True(MathF.Abs(g[0] - g[1]) < 1e-5f, "stereo centre");
            Assert.True(MathF.Abs(Gains(ps, Directions.FromAngles(90, 0))[0] - 1) < 1e-5f, "stereo: hard left at 90°");
            Assert.True(MathF.Abs(Gains(ps, Directions.FromAngles(150, 0))[0] - 1) < 1e-5f, "stereo: 150° mirrors to front left");
            g = Gains(ps, Directions.FromAngles(180, 0));
            Assert.True(MathF.Abs(g[0] - g[1]) < 1e-5f, "stereo: behind mirrors to the centre");
            Assert.True(MathF.Abs(Gains(ps, Directions.FromAngles(-60, 0))[1] - 1) < 1e-5f, "stereo: past the right speaker clamps to it");

            var l714 = ChannelLayout.Surround714;
            var p714 = new Panner(l714);
            g = Gains(p714, Vector3.UnitY);
            for (int c = 0; c < l714.Count; c++)
            {
                bool top = l714.Speakers[c] is Speaker.TopFrontLeft or Speaker.TopFrontRight or Speaker.TopBackLeft or Speaker.TopBackRight;
                Assert.True(top ? g[c] > 0.2f : g[c] < 1e-4f, $"straight up plays on the tops only ({l714.Speakers[c]} = {g[c]})");
            }
            g = Gains(p714, Directions.FromAngles(45, 45));
            Assert.True(MathF.Abs(G(g, l714, Speaker.TopFrontLeft) - 1) < 1e-4f, "on TFL");
            g = Gains(p714, -Vector3.UnitY);
            for (int c = 0; c < l714.Count; c++)
                if (l714.Speakers[c] is Speaker.TopFrontLeft or Speaker.TopFrontRight or Speaker.TopBackLeft or Speaker.TopBackRight)
                    Assert.True(g[c] < 1e-4f, "straight down never uses the tops");
            g = Gains(p51, Directions.FromAngles(30, 0), spread: 1);
            for (int c = 0; c < l51.Count; c++)
                if (l51.Speakers[c] != Speaker.Lfe) Assert.True(MathF.Abs(g[c] - MathF.Sqrt(1 / 5f)) < 1e-5f, "full spread is equal power everywhere");
        });

        Test("audio spatial: air absorption matches ISO 9613-1; distance, cone, Doppler and the HDR window follow their formulas", () =>
        {
            // ISO 9613-2 Table 2 (octave-band attenuation in dB/km from ISO 9613-1's formula).
            (double T, double H, double F, double Expected)[] table =
            [
                (20, 70, 500, 2.8), (20, 70, 1000, 5.0), (20, 70, 2000, 9.0), (20, 70, 4000, 22.9), (20, 70, 8000, 76.6),
                (10, 70, 1000, 3.7), (10, 70, 2000, 9.7), (10, 70, 4000, 32.8), (10, 70, 8000, 117),
                (15, 20, 1000, 8.2), (15, 20, 4000, 88.8), (15, 20, 8000, 202),
            ];
            foreach (var (t, h, f, expected) in table)
            {
                double got = Propagation.AirAbsorptionDbPerMetre(f, t, h) * 1000;
                Assert.True(Math.Abs(got - expected) <= 0.05 * expected + 0.05, $"{f} Hz at {t} °C, {h}%: {got:0.00} dB/km, table {expected}");
            }
            Assert.True(MathF.Abs(Propagation.SpeedOfSound(20) - 343.2f) < 0.3f, "c at 20 °C");

            Assert.Equal(1f, Propagation.DistanceGain(0.5f, 1, 100));
            Assert.True(MathF.Abs(Propagation.DistanceGain(2, 1, 100) - 0.5f) < 1e-6f, "−6 dB per doubling");
            Assert.True(MathF.Abs(Propagation.DistanceGain(8, 2, 100) - 0.25f) < 1e-6f, "from the minimum distance");
            Assert.Equal(0f, Propagation.DistanceGain(150, 1, 100));

            Assert.Equal(1f, Propagation.ConeGain(1, 90, 180, 0.25f));
            Assert.True(MathF.Abs(Propagation.ConeGain(0, 90, 180, 0.25f) - 0.25f) < 1e-5f, "90° off axis is outside a 180° cone");
            Assert.True(MathF.Abs(Propagation.ConeGain(MathF.Cos(67.5f * MathF.PI / 180), 90, 180, 0.25f) - 0.625f) < 1e-4f, "halfway through the cone's edge");

            float c = 343;
            Assert.True(MathF.Abs(Propagation.Doppler(-c / 10, 0, c) - 1 / 0.9f) < 1e-5f, "a source approaching at c/10 sounds 11% high");
            Assert.True(MathF.Abs(Propagation.Doppler(c / 10, 0, c) - 1 / 1.1f) < 1e-5f, "receding, 9% low");
            Assert.True(MathF.Abs(Propagation.Doppler(0, c / 10, c) - 1.1f) < 1e-5f, "a listener approaching at c/10 hears 10% high");

            var hdr = new HdrWindow { RangeDb = 60, ReleaseDbPerSecond = 10, FloorDb = 40 };
            hdr.Update(130, 1 / 60f); // a gunshot
            Assert.True(hdr.TopDb == 130 && MathF.Abs(hdr.Gain(130) - 1) < 1e-6f, "the loudest sound plays at full scale");
            Assert.True(MathF.Abs(hdr.Gain(90) - 0.01f) < 1e-5f && hdr.Gain(60) == 0, "40 dB down plays at 1%; below the 60 dB window it's culled");
            for (int i = 0; i < 60; i++) hdr.Update(80, 1 / 60f);
            Assert.True(MathF.Abs(hdr.TopDb - 120) < 0.01f, $"after it, the top falls at 10 dB/s ({hdr.TopDb})");
            for (int i = 0; i < 600; i++) hdr.Update(0, 1 / 60f);
            Assert.True(hdr.TopDb == 40, "and rests at the floor");
        });

        Test("audio spatial: positional voices pan with VBAP, ramp between directions, filter by band and shift with Doppler", () =>
        {
            var l = ChannelLayout.Surround71;
            var e = Engine(l);
            var dc = AudioClip.Generate("dc", Rate, 4800, _ => 1);
            var v = e.Play(dc, PlayOptions.Default with { Loop = true, Positional = true, Spatial = SpatialParams.Default with { Direction = Directions.FromAngles(90, 0), Gain = 0.5f } });
            var o = Render(e, 512);
            for (int c = 0; c < l.Count; c++)
                Assert.True(MathF.Abs(o[511 * l.Count + c] - (l.Speakers[c] == Speaker.SideLeft ? 0.5f : 0)) < 1e-5f, $"only SL at 0.5 ({l.Speakers[c]} = {o[511 * l.Count + c]})");
            e.SetSpatial(v, SpatialParams.Default with { Direction = Directions.FromAngles(30, 0), Gain = 1 }, rampSeconds: 0.01f);
            o = Render(e, 1024);
            Assert.True(MathF.Abs(o[1023 * l.Count + l.IndexOf(Speaker.FrontLeft)] - 1) < 1e-5f && MathF.Abs(o[1023 * l.Count + l.IndexOf(Speaker.SideLeft)]) < 1e-5f, "moved to FL");
            float worst = 0;
            for (int f = 1; f < 1024; f++) worst = MathF.Max(worst, MathF.Abs(o[f * l.Count] - o[(f - 1) * l.Count]));
            Assert.True(worst < 1.5f / 480, $"the move is a ramp ({worst})");

            // Bands: a 16 kHz tone through a cut high band, a 200 Hz tone through it unchanged.
            var mono = ChannelLayout.Mono;
            foreach (var (hz, expectLow) in new[] { (16000f, true), (200f, false) })
            {
                var eq = Engine(mono);
                eq.Play(AudioClip.Sine(hz, 0.5f, amplitude: 1), PlayOptions.Default with { Positional = true, Spatial = SpatialParams.Default with { High = 0, Mid = 0.1f } });
                var y = Render(eq, Rate / 4);
                double rms = Rms(y, 1, 0, 2000, Rate / 4) * Math.Sqrt(2);
                Assert.True(expectLow ? rms < 0.2 : rms > 0.85, $"{hz} Hz through the cut bands: amplitude {rms:0.000}");
            }

            var d = Engine(mono);
            d.Play(AudioClip.Sine(1000, 1, amplitude: 1), PlayOptions.Default with { Positional = true, Spatial = SpatialParams.Default with { Doppler = 1 / 0.9f } });
            var shifted = Render(d, Rate / 4);
            int crossings = 0; double first = -1, last = 0;
            for (int i = 1001; i < Rate / 4; i++)
                if (shifted[i - 1] < 0 && shifted[i] >= 0) { double t = i - 1 + shifted[i - 1] / (shifted[i - 1] - shifted[i]); if (first < 0) first = t; else crossings++; last = t; }
            double hz2 = crossings * (double)Rate / (last - first);
            Assert.True(Math.Abs(hz2 - 1111.11) < 0.5, $"Doppler 1/0.9 on 1 kHz: {hz2:0.00} Hz");
        });

        Test("audio spatial: headphones — the head model's interaural delay is Woodworth's, rendered to the sample; the far ear is shadowed", () =>
        {
            for (int deg = 0; deg <= 90; deg += 15)
            {
                var dir = Directions.FromAngles(deg, 0);
                float itd = HeadModel.EarDelay(dir, HeadModel.RightEar) - HeadModel.EarDelay(dir, HeadModel.LeftEar);
                float woodworth = HeadModel.Woodworth(float.DegreesToRadians(deg));
                Assert.True(MathF.Abs(itd - woodworth) < 1e-7f, $"{deg}°: {itd * 1e6:0.0} µs, Woodworth {woodworth * 1e6:0.0} µs");
            }

            // A click from the left virtual speaker (90°): the left ear hears it first, by (a/c)(π/2 + 1) = 0.656 ms.
            var e = Engine(ChannelLayout.Stereo, headphones: true);
            Assert.True(e.BedLayout == ChannelLayout.Surround714, "headphones mix into a 7.1.4 bed");
            var click = AudioClip.Generate("click", Rate, 1, _ => 1);
            e.Play(click, PlayOptions.Default with { StartTime = 100, Positional = true, Spatial = SpatialParams.Default with { Direction = Directions.FromAngles(90, 0) } });
            var o = Render(e, 512);
            double Centroid(int ear) { double s = 0, w = 0; for (int f = 0; f < 512; f++) { double x = Math.Abs(o[f * 2 + ear]); s += f * x; w += x; } return s / w; }
            // The shadow filter smears the far ear's click; its leading edge is where to measure the delay.
            double Onset(int ear) { double peak = 0; for (int f = 0; f < 512; f++) peak = Math.Max(peak, Math.Abs(o[f * 2 + ear])); for (int f = 0; f < 512; f++) if (Math.Abs(o[f * 2 + ear]) > 0.2 * peak) return f; return -1; }
            double expected = HeadModel.Woodworth(MathF.PI / 2) * Rate;
            double measured = Onset(1) - Onset(0);
            Assert.True(Math.Abs(measured - expected) <= 1.5, $"interaural delay {measured} samples, Woodworth {expected:0.0}");
            Assert.True(Centroid(0) < Centroid(1), "the left ear leads");

            // Head shadow at 10 kHz from the left: the right ear is ~16 dB down (α 0.28 behind the head, 2.0 facing the left ear).
            var shadow = Engine(ChannelLayout.Stereo, headphones: true);
            shadow.Play(AudioClip.Sine(10000, 0.5f, amplitude: 0.5f), PlayOptions.Default with { Positional = true, Spatial = SpatialParams.Default with { Direction = Directions.FromAngles(90, 0) } });
            var s = Render(shadow, Rate / 4);
            double ild = 20 * Math.Log10(Rms(s, 2, 1, 2000, Rate / 4) / Rms(s, 2, 0, 2000, Rate / 4));
            Assert.True(ild < -14 && ild > -19, $"interaural level difference at 10 kHz: {ild:0.0} dB");

            var front = Engine(ChannelLayout.Stereo, headphones: true);
            front.Play(AudioClip.Sine(3000, 0.5f, amplitude: 0.5f), PlayOptions.Default with { Positional = true });
            var fr = Render(front, Rate / 4);
            double balance = 20 * Math.Log10(Rms(fr, 2, 1, 2000, Rate / 4) / Rms(fr, 2, 0, 2000, Rate / 4));
            Assert.True(Math.Abs(balance) < 0.05, $"straight ahead is balanced ({balance:0.000} dB)");
        });

        Test("audio spatial: entities — the listener hears sources from where they are, turning with the camera; Doppler from motion; HDR", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            var engine = context.Engine;
            var dc = new AssetRef<AudioAsset>(Guid.NewGuid());
            var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(dc, AudioClip.Generate("dc", Rate, 4800, _ => 1));
            context.Register(tone, AudioClip.Sine(1000, 4, amplitude: 1));
            var half = new AssetRef<AudioAsset>(Guid.NewGuid()); // half scale, so the master limiter stays out of the HDR checks
            context.Register(half, AudioClip.Generate("half", Rate, 4800, _ => 0.5f));
            var longHalf = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(longHalf, AudioClip.Generate("long half", Rate, 2 * Rate, _ => 0.5f));
            w.Resource<AudioEnvironment>().Hdr = false;
            const int frame = Rate / 60;
            float[] Tick() { app.Tick(1 / 60.0); var o = new float[frame * 2]; engine.Render(o); return o; }

            var listener = w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            var source = w.Spawn(LocalTransform.At(new Vector3(-5, 0, 0)), new LocalToWorld(), AudioSource.At(dc, loop: true));
            Tick();
            var o = Tick();
            float l = o[^2], r = o[^1];
            Assert.True(MathF.Abs(l - 0.2f) < 1e-3f && r < 1e-4f, $"5 m to the left: −14 dB, left only ({l}, {r})");
            w.Get<LocalTransform>(source).Position = new Vector3(5, 0, 0);
            Tick(); o = Tick();
            Assert.True(o[^1] > 0.199f && o[^2] < 1e-4f, $"moved to the right ({o[^2]}, {o[^1]})");
            w.Get<LocalTransform>(listener).Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI);
            Tick(); o = Tick();
            Assert.True(o[^2] > 0.199f && o[^1] < 1e-4f, $"the listener turned round: now on the left ({o[^2]}, {o[^1]})");
            w.Destroy(source);
            w.Get<LocalTransform>(listener).Rotation = Quaternion.Identity;
            Tick(); Tick();

            // A tone flying straight at the listener at c/10 (34.3 m/s at 20 °C) sounds 11% high.
            float c = Propagation.SpeedOfSound(20);
            var plane = w.Spawn(LocalTransform.At(new Vector3(0, 0, -300)), new LocalToWorld(),
                AudioSource.At(tone) with { Emitter = AudioEmitter.Default with { MaxDistance = 1000 } });
            var all = new List<float>();
            for (int i = 0; i < 60; i++)
            {
                w.Get<LocalTransform>(plane).Position += new Vector3(0, 0, c / 10 / 60);
                all.AddRange(Tick());
            }
            var mono = new float[all.Count / 2];
            for (int i = 0; i < mono.Length; i++) mono[i] = all[2 * i] + all[2 * i + 1];
            int crossings = 0; double first = -1, last = 0;
            for (int i = mono.Length / 2 + 1; i < mono.Length; i++)
                if (mono[i - 1] < 0 && mono[i] >= 0) { double t = i - 1 + mono[i - 1] / (mono[i - 1] - mono[i]); if (first < 0) first = t; else crossings++; last = t; }
            double hz = crossings * (double)Rate / (last - first);
            Assert.True(Math.Abs(hz - 1000 / 0.9) < 2, $"approaching at c/10: {hz:0.0} Hz, expected {1000 / 0.9:0.0}");
            w.Destroy(plane);
            Tick(); Tick();

            // HDR: a 110 dB shot 10 m away puts a 70 dB hum, also 10 m away, 40 dB down; then the window releases.
            w.Resource<AudioEnvironment>().Hdr = true;
            w.Resource<AudioEnvironment>().HdrHeadroomDb = 0; // the window's top at full scale, for exact levels
            w.Resource<AudioEnvironment>().HdrReleaseDbPerSecond = 30;
            var hum = w.Spawn(LocalTransform.At(new Vector3(-10, 0, 0)), new LocalToWorld(),
                AudioSource.At(half, loop: true) with { Emitter = AudioEmitter.Default with { LoudnessDb = 70 } });
            Tick(); o = Tick();
            float air = Propagation.AirBands(10, 20, 50).Low; // the low band (taken at 400 Hz) loses ~0.02 dB over 10 m
            Assert.True(MathF.Abs(o[^2] - 0.5f * air) < 1e-4f, $"alone, the hum fills the window ({o[^2]})");
            var shot = context.PlayAt(longHalf, new Vector3(-10, 0, 0), AudioEmitter.Default with { LoudnessDb = 110 }, PlayOptions.Default);
            o = Tick();
            Assert.True(MathF.Abs(context.Hdr.TopDb - 90) < 0.01f, $"the shot sets the top: 110 − 20 dB at 10 m = {context.Hdr.TopDb}");
            // The hum ducks over one frame while the shot is already at full scale, so the master limiter catches the
            // overlap; measure once its release (80 ms, exponential) has passed.
            for (int i = 0; i < 40; i++) o = Tick();
            Assert.True(MathF.Abs(o[^2] - (0.5f + 0.005f) * air) < 1e-4f, $"the shot at full scale, the hum 40 dB down ({o[^2]})");
            engine.Stop(shot);
            for (int i = 0; i < 30; i++) Tick();
            Assert.True(context.Hdr.TopDb < 90 - 10 && context.Hdr.TopDb >= 50, $"after half a second the window has come down ({context.Hdr.TopDb})");
            w.Destroy(hum);
        });

        Test("audio spatial: virtual voices — the loudest render, swaps follow loudness and priority, virtual voices keep time", () =>
        {
            var e = AudioEngine.Offline(Rate, ChannelLayout.Mono, AudioEngineSettings.Default with { Limiter = false, MaxRealVoices = 8 });
            var dc = AudioClip.Generate("dc", Rate, 4800, _ => 0.01f);
            var voices = new VoiceHandle[100];
            for (int k = 0; k < 100; k++)
                voices[k] = e.Play(dc, PlayOptions.Default with { Loop = true, Positional = true, Spatial = SpatialParams.Default with { Gain = (k + 1) / 100f } });
            // Starting 100 voices churns: louder ones swap in while the mixer's spare slots hold voices fading out. Once
            // those fades end, the next update settles on the eight loudest, and the render after it plays just them.
            Render(e, 1024);
            var o = Render(e, 1024);
            Assert.True(e.ActiveVoices == 100 && e.RealVoices == 8, $"100 playing, {e.RealVoices} rendered");
            for (int k = 0; k < 100; k++) Assert.True(e.IsRendered(voices[k]) == (k >= 92), $"voice {k} rendered: {e.IsRendered(voices[k])}");
            float top8 = 0;
            for (int k = 92; k < 100; k++) top8 += 0.01f * (k + 1) / 100f;
            Assert.True(MathF.Abs(o[^1] - top8) < 1e-5f, $"the output is the eight loudest ({o[^1]} vs {top8})");

            // The quietest becomes the loudest: it takes the weakest rendered voice's place, with fades.
            e.SetSpatial(voices[0], SpatialParams.Default with { Gain = 2 });
            e.Update();
            o = Render(e, 2048);
            Assert.True(e.IsRendered(voices[0]) && !e.IsRendered(voices[92]) && e.RealVoices == 8, "voice 0 in, voice 92 out");
            float swapped = top8 - 0.01f * 0.93f + 0.01f * 2;
            Assert.True(MathF.Abs(o[^1] - swapped) < 1e-5f, $"after the fades ({o[^1]} vs {swapped})");

            // Priority beats loudness.
            var music = e.Play(dc, PlayOptions.Default with { Loop = true, Gain = 0.001f, Priority = 100 });
            Render(e, 1024);
            Assert.True(e.IsRendered(music), "a high-priority voice renders however quiet it is");
            Assert.True(e.RealVoices == 8, "still eight");

            // A one-shot that never renders still ends on time.
            var small = AudioEngine.Offline(Rate, ChannelLayout.Mono, AudioEngineSettings.Default with { Limiter = false, MaxRealVoices = 1 });
            small.Play(dc, PlayOptions.Default with { Loop = true, Priority = 1 });
            var blip = small.Play(AudioClip.Generate("blip", Rate, 4800, _ => 0.5f));
            Assert.True(!small.IsRendered(blip) && small.IsPlaying(blip), "the one-shot is virtual");
            for (int t = 0; t < 4608; t += 512) Render(small, 512);
            Assert.True(small.IsPlaying(blip), "still playing at 4608 frames");
            Render(small, 512);
            Assert.True(!small.IsPlaying(blip) && small.ActiveVoices == 1, "ended once its 4800 frames passed");

            // A voice that comes back joins at the right place in its clip: a sawtooth tells the position.
            var saw = AudioClip.Generate("saw", Rate, 48000, i => i / 48000f);
            var back = AudioEngine.Offline(Rate, ChannelLayout.Mono, AudioEngineSettings.Default with { Limiter = false, MaxRealVoices = 1 });
            var hog = back.Play(dc, PlayOptions.Default with { Loop = true, Priority = 1 });
            var waiting = back.Play(saw, PlayOptions.Default with { Loop = true });
            for (int t = 0; t < 10240; t += 512) Render(back, 512);   // virtual for 10240 frames
            back.Stop(hog, fadeSeconds: 0.001f);
            Render(back, 512);                                       // the hog fades and frees its slot
            back.Update();
            long promoted = back.Time;
            var joined = Render(back, 1024);
            // After the 10 ms swap fade, the sawtooth reads its position: frames since it started.
            float expected = (promoted + 600) / 48000f, got = joined[600];
            Assert.True(back.IsRendered(waiting) && MathF.Abs(got - expected) < 2f / 48000, $"rejoined at {got * 48000:0} frames, expected {expected * 48000:0}");
        });

        Test("audio spatial: first-order ambisonics decodes toward its direction and stays fixed in the world as the listener turns", () =>
        {
            var l = ChannelLayout.Surround51;
            // A DC plane wave from the front left speaker's direction, encoded in AmbiX.
            var w = Ambisonics.Encode(Directions.FromAngles(30, 0));
            var samples = new float[4 * 2400];
            for (int f = 0; f < 2400; f++) { samples[f * 4] = 0.5f * w.X; samples[f * 4 + 1] = 0.5f * w.Y; samples[f * 4 + 2] = 0.5f * w.Z; samples[f * 4 + 3] = 0.5f * w.W; }
            var field = new AudioClip("field", Rate, ChannelLayout.AmbisonicFirstOrder, samples);
            var round = new MemoryStream();
            Wav.Write(round, field.Samples, Rate, field.Layout);
            Assert.True(Wav.Read(round.ToArray()).Layout == ChannelLayout.AmbisonicFirstOrder, "the ambi chunk survives a WAV round trip");

            int Loudest(float[] o, int frame)
            {
                int best = 0;
                for (int c = 1; c < l.Count; c++) if (o[frame * l.Count + c] > o[frame * l.Count + best]) best = c;
                return best;
            }
            var e = Engine(l);
            e.Play(field, PlayOptions.Default with { Loop = true });
            var o = Render(e, 512);
            Assert.Equal(Speaker.FrontLeft, l.Speakers[Loudest(o, 500)]);
            Assert.True(o[500 * l.Count + l.IndexOf(Speaker.Lfe)] == 0, "no LFE");

            // The listener turns 30° to the left: the source is now straight ahead.
            var turned = Engine(l);
            var voice = turned.Play(field, PlayOptions.Default with { Loop = true, ListenerOrientation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 6) });
            o = Render(turned, 512);
            Assert.Equal(Speaker.FrontCenter, l.Speakers[Loudest(o, 500)]);
            // Turning 30° to the right puts it at 60° left, between FL (30°) and BL (110°).
            turned.SetListenerOrientation(voice, Quaternion.CreateFromAxisAngle(Vector3.UnitY, -MathF.PI / 6), rampSeconds: 0);
            o = Render(turned, 512);
            float fl = o[500 * l.Count + l.IndexOf(Speaker.FrontLeft)], fc = o[500 * l.Count + l.IndexOf(Speaker.FrontCenter)], bl = o[500 * l.Count + l.IndexOf(Speaker.BackLeft)];
            Assert.True(fl > fc && bl > o[500 * l.Count + l.IndexOf(Speaker.FrontRight)], $"turned right: FL {fl}, FC {fc}, BL {bl}");
        });
    }
}
