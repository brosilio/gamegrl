using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Acoustics;
using Gamegrl.Audio.Dsp;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>Acoustic geometry: the BVH, materials, transmission and occlusion, in the world and through the ECS.</summary>
internal static class AcousticsTests
{
    private const int Rate = 48000;

    private static Matrix4x4 Box(Vector3 center, Vector3 size) => Matrix4x4.CreateScale(size) * Matrix4x4.CreateTranslation(center);

    private static float BruteForce(Vector3[] tris, Vector3 origin, Vector3 dir)
    {
        float best = float.MaxValue;
        for (int t = 0; t < tris.Length / 3; t++)
        {
            Vector3 a = tris[3 * t], e1 = tris[3 * t + 1] - a, e2 = tris[3 * t + 2] - a;
            var p = Vector3.Cross(dir, e2);
            float det = Vector3.Dot(e1, p);
            if (MathF.Abs(det) < 1e-12f) continue;
            var s = origin - a;
            float u = Vector3.Dot(s, p) / det;
            if (u < 0 || u > 1) continue;
            var q = Vector3.Cross(s, e1);
            float v = Vector3.Dot(dir, q) / det;
            if (v < 0 || u + v > 1) continue;
            float d = Vector3.Dot(e2, q) / det;
            if (d > 1e-5f && d < best) best = d;
        }
        return best;
    }

    /// <summary>T60 from an impulse response's energy (summed over channels): Schroeder backward integration, a line fitted from −5 to −35 dB.</summary>
    private static double SchroederT60(double[] energy, int rate)
    {
        var edc = new double[energy.Length];
        double acc = 0;
        for (int i = energy.Length - 1; i >= 0; i--) { acc += energy[i]; edc[i] = acc; }
        double sx = 0, sy = 0, sxx = 0, sxy = 0; int n = 0;
        for (int i = 0; i < edc.Length; i++)
        {
            double db = 10 * Math.Log10(edc[i] / edc[0]);
            if (db > -5) continue;
            if (db < -35) break;
            double t = (double)i / rate;
            sx += t; sy += db; sxx += t * t; sxy += t * db; n++;
        }
        double slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        return -60 / slope;
    }

    public static void Register()
    {
        Test("acoustics: the BVH finds the same closest hits as brute force", () =>
        {
            var random = new Random(11);
            Vector3 R() => new((float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10, (float)random.NextDouble() * 20 - 10);
            var tris = new Vector3[600 * 3];
            for (int t = 0; t < 600; t++) { var c = R(); tris[3 * t] = c; tris[3 * t + 1] = c + R() * 0.2f; tris[3 * t + 2] = c + R() * 0.2f; }
            var builder = new AcousticSceneBuilder();
            builder.AddMesh(tris, Matrix4x4.Identity, AcousticMaterial.Generic);
            var scene = builder.Build();
            Assert.Equal(600, scene.TriangleCount);
            int hits = 0;
            for (int i = 0; i < 3000; i++)
            {
                var origin = R();
                var dir = Vector3.Normalize(R());
                float expected = BruteForce(tris, origin, dir);
                bool hit = scene.Raycast(origin, dir, 100, out var h);
                Assert.True(hit == expected < 100, $"ray {i}: hit {hit}, brute force {expected}");
                if (hit) { hits++; Assert.True(MathF.Abs(h.Distance - expected) < 1e-4f, $"ray {i}: {h.Distance} vs {expected}"); }
            }
            Assert.True(hits > 300, $"enough rays hit something ({hits})");
        });

        Test("acoustics: walls transmit by their material — solid boxes once per path, mesh surfaces at every crossing", () =>
        {
            var builder = new AcousticSceneBuilder();
            builder.AddBox(Box(Vector3.Zero, new Vector3(10, 4, 8)), AcousticMaterial.Concrete); // a room around the origin
            var room = builder.Build();
            var concrete = AcousticMaterial.Concrete.Transmission;
            var inside = room.DirectPath(Vector3.Zero, new Vector3(2, 0, 1), 0, 0);
            Assert.True(inside.Low == 1 && inside.Mid == 1 && inside.High == 1, "inside the room, nothing in the way");
            var outside = room.DirectPath(Vector3.Zero, new Vector3(20, 0, 0), 0, 0);
            Assert.True(outside.Low == concrete.Low && outside.Mid == concrete.Mid && outside.High == concrete.High, $"through one wall: {outside}");

            builder.AddBox(Box(Vector3.Zero, new Vector3(30, 10, 30)), AcousticMaterial.Wood); // and a building around that
            var nested = builder.Build().DirectPath(Vector3.Zero, new Vector3(40, 0, 0), 0, 0);
            var wood = AcousticMaterial.Wood.Transmission;
            Assert.True(MathF.Abs(nested.Mid - concrete.Mid * wood.Mid) < 1e-9f, $"through both: {nested}");

            // A thick wall as a solid box counts once; the same wall as two open quads counts twice.
            var solid = new AcousticSceneBuilder();
            var half = new AcousticMaterial(Bands3.All(0.1f), 0, Bands3.All(0.5f));
            solid.AddBox(Box(new Vector3(0, 0, -3), new Vector3(10, 10, 0.5f)), half);
            Assert.True(MathF.Abs(solid.Build().DirectPath(Vector3.Zero, new Vector3(0, 0, -6), 0, 0).Mid - 0.5f) < 1e-6f, "a solid wall is one crossing");
            var open = new AcousticSceneBuilder();
            Vector3[] quad = [new(-5, -5, 0), new(5, -5, 0), new(5, 5, 0), new(-5, -5, 0), new(5, 5, 0), new(-5, 5, 0)];
            open.AddMesh(quad, Matrix4x4.CreateTranslation(0, 0, -2.75f), half);
            open.AddMesh(quad, Matrix4x4.CreateTranslation(0, 0, -3.25f), half);
            Assert.True(MathF.Abs(open.Build().DirectPath(Vector3.Zero, new Vector3(0, 0, -6), 0, 0).Mid - 0.25f) < 1e-6f, "two surfaces are two crossings");
        });

        Test("acoustics: a large source half behind a wall is half occluded; materials order by hardness", () =>
        {
            var builder = new AcousticSceneBuilder();
            var opaque = new AcousticMaterial(Bands3.All(0.1f), 0, Bands3.All(0));
            builder.AddBox(Box(new Vector3(-5, 0, -5), new Vector3(10, 20, 0.2f)), opaque); // covers x < 0 at z = −5
            var scene = builder.Build();
            float point = scene.DirectPath(Vector3.Zero, new Vector3(0.001f, 0, -10), 0, 0).Mid;
            Assert.Equal(1f, point);
            float hidden = scene.DirectPath(Vector3.Zero, new Vector3(-3, 0, -10), 0, 0).Mid;
            Assert.Equal(0f, hidden);
            float partly = scene.DirectPath(Vector3.Zero, new Vector3(0, 0, -10), 2, 32).Mid;
            Assert.True(partly > 0.3f && partly < 0.7f, $"a 2 m source straddling the wall's edge: {partly:0.00} of it is heard");

            float previous = float.MaxValue;
            for (float h = 0; h <= 1.001f; h += 0.25f)
            {
                var m = AcousticMaterial.FromHardness(h);
                float total = m.Absorption.Low + m.Absorption.Mid + m.Absorption.High;
                Assert.True(total < previous, $"harder absorbs less ({h}: {total})");
                Assert.True(m.Absorption.High >= m.Absorption.Low && m.Transmission.High <= m.Transmission.Low, "highs absorb more and pass less");
                previous = total;
            }
            foreach (var m in new[] { AcousticMaterial.Generic, AcousticMaterial.Brick, AcousticMaterial.Concrete, AcousticMaterial.Carpet, AcousticMaterial.Glass, AcousticMaterial.Rock, AcousticMaterial.Water, AcousticMaterial.Foliage, AcousticMaterial.Fabric })
                for (int b = 0; b < 3; b++) Assert.True(m.Absorption[b] is >= 0 and <= 1 && m.Transmission[b] is >= 0 and <= 1, "preset values are fractions");
        });

        Test("acoustics: ray-traced reverberation time in a shoebox matches Eyring; volume, surface and enclosure come out right", () =>
        {
            // 10 × 6 × 4 m: V = 240 m³, S = 248 m², fully scattering walls (a diffuse field). Eyring, T = 0.161·V / (−S·ln(1 − α)),
            // assumes every reflection one mean free path apart; Kuttruff's refinement accounts for the spread of path
            // lengths (relative variance γ² ≈ 0.4 in ordinary rooms): T = T_Eyring / (1 + γ²/2 · ln(1 − α)). Sabine is the upper bound.
            foreach (float alpha in new[] { 0.2f, 0.5f })
            {
                var builder = new AcousticSceneBuilder();
                builder.AddBox(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(Bands3.All(alpha), 1, Bands3.All(0)));
                var p = ReverbEstimator.Estimate(builder.Build(), new Vector3(1.3f, 0.4f, -0.7f), rays: 4096, bounces: 200, airAbsorption: false);
                double eyring = 0.161 * 240 / (-248 * Math.Log(1 - alpha)), sabine = 0.161 * 240 / (248 * alpha);
                double kuttruff = eyring / (1 + 0.2 * Math.Log(1 - alpha));
                float t = p.LateDecaySeconds.Mid;
                Assert.True(Math.Abs(t - kuttruff) < 0.05 * kuttruff && t > eyring && t < sabine,
                    $"α {alpha}: T60 {t:0.000} s; Eyring {eyring:0.000}, Kuttruff {kuttruff:0.000}, Sabine {sabine:0.000}");
                Assert.True(Math.Abs(p.Volume - 240) < 0.05 * 240, $"volume {p.Volume:0.0} m³");
                Assert.True(Math.Abs(p.Surface - 248) < 0.12 * 248, $"surface {p.Surface:0.0} m² (from the mean free path)");
                Assert.True(p.Enclosure == 1 && p.Level > 0, $"enclosed, level {p.Level:0.00}");
            }
            var bands = new AcousticSceneBuilder();
            bands.AddBox(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(new Bands3(0.1f, 0.3f, 0.6f), 0.5f, Bands3.All(0)));
            var d = ReverbEstimator.Estimate(bands.Build(), Vector3.Zero, airAbsorption: false).DecaySeconds;
            Assert.True(d.Low > d.Mid && d.Mid > d.High, $"absorbent highs decay faster: {d}");
            // Out in the open on a big floor: half the rays escape, little reverb.
            var floor = new AcousticSceneBuilder();
            floor.AddBox(Box(new Vector3(0, -2, 0), new Vector3(200, 0.1f, 200)), AcousticMaterial.Concrete);
            var open = ReverbEstimator.Estimate(floor.Build(), Vector3.Zero);
            Assert.True(Math.Abs(open.Enclosure - 0.5f) < 0.02f && open.Level < 0.2f, $"open air: enclosure {open.Enclosure:0.00}, level {open.Level:0.00}");
            Assert.True(ReverbEstimator.Estimate(AcousticScene.Empty, Vector3.Zero).IsSilent, "no geometry, no reverb");
        });

        Test("acoustics: the reverbs' structures differ — the same sound through two of them doesn't comb (no flanging as pre-delays move)", () =>
        {
            // The same impulse through each variant; with one structure, their outputs would match sample for sample at
            // the pre-delay offset and sum to a comb filter.
            float[] Tail(int variant, float preDelay)
            {
                var fdn = new Fdn(ChannelLayout.Mono, Rate, variant: variant);
                fdn.Set(new ReverbParams { DecaySeconds = Bands3.All(1.5f), Level = 1, PreDelaySeconds = preDelay }, 0);
                var output = new float[Rate];
                var input = new float[512];
                var bed = new float[512];
                for (int f = 0; f < output.Length; f += 512)
                {
                    Array.Clear(input); Array.Clear(bed);
                    if (f == 0) input[0] = 1;
                    fdn.Process(input, bed, 512, 512);
                    bed.AsSpan(0, Math.Min(512, output.Length - f)).CopyTo(output.AsSpan(f));
                }
                return output;
            }
            static double Correlation(float[] a, float[] b, int lag)
            {
                double ab = 0, aa = 0, bb = 0;
                for (int i = 0; i + lag < a.Length; i++) { ab += a[i + lag] * b[i]; aa += a[i + lag] * a[i + lag]; bb += b[i] * b[i]; }
                return ab / Math.Sqrt(aa * bb);
            }
            var same = Correlation(Tail(0, 0.047f), Tail(0, 0.042f), 0);
            var aligned = Correlation(Tail(0, 0.047f), Tail(0, 0.042f), (int)(0.005f * Rate));
            Assert.True(aligned > 0.99, $"one structure twice: correlated {aligned:0.000} at the pre-delay offset (a comb; {same:0.00} unaligned)");
            for (int a = 0; a < Fdn.Variants; a++)
            for (int b = a + 1; b < Fdn.Variants; b++)
            {
                var x = Tail(a, 0.047f); var y = Tail(b, 0.042f);
                double worst = 0;
                for (int lag = 0; lag < 0.02f * Rate; lag++) worst = Math.Max(worst, Math.Max(Math.Abs(Correlation(x, y, lag)), Math.Abs(Correlation(y, x, lag))));
                Assert.True(worst < 0.15, $"variants {a} and {b}: correlated up to {worst:0.000} within 20 ms");
            }
        });

        Test("acoustics: HDR counts occlusion once — the loudest sound, though behind a wall, plays at the window's top; a quieter one in view keeps its distance below", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            ref var env = ref w.Resource<AudioEnvironment>();
            env.Hdr = true; env.HdrHeadroomDb = 0; env.Reverb = false; env.Reflections = false; env.Diffraction = false;
            context.Diagnostics.Enabled = true;
            var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(tone, AudioClip.Generate("tone", Rate, Rate, i => MathF.Sin(i * 0.05f)));
            w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            // A wall passing 10% of the mid band between the listener and a 120 dB source 6 m away; a 90 dB one 3 m away in view.
            w.Spawn(new LocalTransform { Position = new Vector3(0, 0, -3), Rotation = Quaternion.Identity, Scale = new Vector3(10, 10, 0.3f) },
                new LocalToWorld(), AcousticSurface.Box(new AcousticMaterial(Bands3.All(0.1f), 0, Bands3.All(0.1f))));
            w.Spawn(LocalTransform.At(new Vector3(0, 0, -6)), new LocalToWorld(), AudioSource.At(tone, loop: true) with { Emitter = AudioEmitter.Default with { LoudnessDb = 120 } });
            w.Spawn(LocalTransform.At(new Vector3(3, 0, 0)), new LocalToWorld(), AudioSource.At(tone, loop: true) with { Emitter = AudioEmitter.Default with { LoudnessDb = 90 } });
            for (int f = 0; f < 5; f++) { app.Tick(1 / 60.0); context.Engine.Render(new float[Rate / 60 * 2]); }
            VoiceReport loud = default, quiet = default;
            foreach (var v in context.Diagnostics.Voices) if (Vector3.Distance(v.Position, new Vector3(0, 0, -6)) < 0.1f) loud = v; else quiet = v;
            float heard = loud.Spatial.Gain * loud.Spatial.Mid;
            float air = Gamegrl.Audio.Spatial.Propagation.AirBands(6, env.TemperatureC, env.HumidityPercent).Mid;
            Assert.True(MathF.Abs(loud.Transmission - 0.1f) < 0.01f && MathF.Abs(heard - air) < 0.02f * air,
                $"behind the wall (transmission {loud.Transmission:0.000}): mid band at {heard:0.000} of full scale, expected {air:0.000}");
            // The window's top is the loud one's level at the listener: 120 − 20·lg 6 − 20 = 84.4 dB; the quiet one is
            // 90 − 20·lg 3 = 80.5 dB there, 3.9 dB below.
            float below = 20 * MathF.Log10(quiet.Spatial.Gain * quiet.Spatial.Mid / heard);
            Assert.True(MathF.Abs(below - (80.46f - 84.44f)) < 0.3f, $"the one in view sits {below:0.0} dB below it, expected -4.0");
        });

        Test("acoustics: a source hidden behind a pillar in the listener's room fills that room's reverb, with no other room", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            ref var env = ref w.Resource<AudioEnvironment>();
            env.Hdr = false; env.Reflections = false;
            context.Diagnostics.Enabled = true;
            var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(tone, AudioClip.Generate("tone", Rate, Rate, i => MathF.Sin(i * 0.05f)));
            // A concrete hall 30 × 8 × 30 m with a 4 × 8 × 2 m pillar between the listener and the source.
            w.Spawn(new LocalTransform { Position = new Vector3(0, 3, 0), Rotation = Quaternion.Identity, Scale = new Vector3(30, 8, 30) }, new LocalToWorld(), AcousticSurface.Room(AcousticMaterial.Concrete));
            w.Spawn(new LocalTransform { Position = new Vector3(0, 3, -4), Rotation = Quaternion.Identity, Scale = new Vector3(4, 8, 2) }, new LocalToWorld(), AcousticSurface.Box(AcousticMaterial.Concrete));
            w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            w.Spawn(LocalTransform.At(new Vector3(0, 0, -9)), new LocalToWorld(), AudioSource.At(tone, loop: true) with { Emitter = AudioEmitter.Default with { LoudnessDb = 120 } });
            for (int f = 0; f < 6; f++) { app.Tick(1 / 60.0); context.Engine.Render(new float[Rate / 60 * 2]); }
            var v = context.Diagnostics.Voices[0];
            Assert.True(v.Transmission < 0.05f && v.Diffracted, $"hidden (transmission {v.Transmission:0.000}), heard round the pillar ({v.Diffracted})");
            Assert.True(context.HiddenInThisRoom && !context.Diagnostics.RemoteActive, $"known to be in this room ({context.HiddenInThisRoom}), no other room playing ({context.Diagnostics.RemoteActive})");
            Assert.True(v.Spatial.Reverb > 0.95f && v.Spatial.RemoteReverb == 0, $"its reverb send {v.Spatial.Reverb:0.000} (as if in view: 1), other room {v.Spatial.RemoteReverb:0.000}");
        });

        Test("acoustics: a new room — what's ringing keeps the old room's decay, new sound gets the new room's", () =>
        {
            double TailAfter(bool newRoom)
            {
                using var engine = AudioEngine.Offline(Rate, ChannelLayout.Stereo);
                engine.SetReverb(new ReverbParams { DecaySeconds = Bands3.All(2), Level = 1, PreDelaySeconds = 0.02f }, 0);
                var click = AudioClip.Generate("click", Rate, 1, _ => 1);
                engine.Play(click, PlayOptions.Default with { Positional = true, Spatial = SpatialParams.Default with { Reverb = 1 } });
                var block = new float[Rate / 5 * 2];
                engine.Render(block); // 0.2 s into a 2 s hall
                engine.SetReverb(new ReverbParams { DecaySeconds = Bands3.All(0.3f), Level = 1, PreDelaySeconds = 0.01f }, 0.05f, newRoom);
                var tail = new float[Rate * 2];
                engine.Render(tail);
                double late = 0;
                for (int i = Rate * 2 / 2; i < Rate * 2 * 3 / 4; i++) late += tail[i] * tail[i]; // 0.5 to 0.75 s after the change
                return late;
            }
            double kept = TailAfter(true), retuned = TailAfter(false);
            // 0.5 s into a 2 s decay is −15 dB; into a 0.3 s one, −100 dB.
            Assert.True(kept > 1e4 * retuned && kept > 1e-4, $"the hall's tail after walking out: {kept:0.0e0} kept against {retuned:0.0e0} retuned");
            Assert.True(!AudioUpdate.NewRoom(new ReverbParams { DecaySeconds = Bands3.All(2), Level = 0.2f }, new ReverbParams { DecaySeconds = Bands3.All(2.4f), Level = 0.25f })
                && AudioUpdate.NewRoom(new ReverbParams { DecaySeconds = Bands3.All(0.55f), Level = 0.4f }, new ReverbParams { DecaySeconds = Bands3.All(2.4f), Level = 0.16f })
                && AudioUpdate.NewRoom(new ReverbParams { DecaySeconds = Bands3.All(2.4f), Level = 0.16f }, ReverbParams.None),
                "walking about a room retunes it; a tunnel into a cave, or out into the open, is a new room");
        });

        Test("acoustics: the FDN reverb decays at its set time per band and puts out unit energy", () =>
        {
            var fdn = new Fdn(ChannelLayout.Stereo, Rate);
            fdn.Set(new ReverbParams { DecaySeconds = Bands3.All(1), Level = 1 }, 0);
            int frames = 3 * Rate;
            var input = new float[512];
            var bed = new float[2 * 512];
            var energy = new double[frames];
            double total = 0;
            for (int f = 0; f < frames; f += 512)
            {
                Array.Clear(input); Array.Clear(bed);
                if (f == 0) input[0] = 1;
                fdn.Process(input, bed, 512, 512);
                for (int i = 0; i < 512 && f + i < frames; i++) { double e = bed[i] * bed[i] + bed[512 + i] * bed[512 + i]; energy[f + i] = e; total += e; }
            }
            double t60 = SchroederT60(energy, Rate);
            Assert.True(Math.Abs(t60 - 1) < 0.1, $"set to 1 s, measured {t60:0.000} s");
            Assert.True(total > 0.7 && total < 1.4, $"a unit impulse's tail carries {total:0.00} of its energy");

            // Low 2 s, high 0.5 s: the tail darkens.
            var dark = new Fdn(ChannelLayout.Stereo, Rate);
            dark.Set(new ReverbParams { DecaySeconds = new Bands3(2, 1, 0.5f), Level = 1 }, 0);
            var lowE = new double[frames]; var highE = new double[frames];
            var lp = Biquad.LowPass(300, Rate); var lp2 = Biquad.LowPass(10000, Rate);
            for (int f = 0; f < frames; f += 512)
            {
                Array.Clear(input); Array.Clear(bed);
                if (f == 0) input[0] = 1;
                dark.Process(input, bed, 512, 512);
                for (int i = 0; i < 512 && f + i < frames; i++)
                {
                    float x = bed[i];
                    float low = lp.Process(x), high = x - lp2.Process(x);
                    lowE[f + i] = low * low; highE[f + i] = high * high;
                }
            }
            double tLow = SchroederT60(lowE, Rate), tHigh = SchroederT60(highE, Rate);
            Assert.True(Math.Abs(tLow - 2) < 0.3 && tHigh < 0.8, $"low band {tLow:0.00} s (set 2), high band {tHigh:0.00} s (set 0.5)");
        });

        Test("acoustics: in a concrete room a burst leaves a tail decaying at the estimated time; in the open, none", () =>
        {
            double[] Run(bool room, out ReverbParams reverb)
            {
                var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
                using var app = b.Build();
                var w = app.World;
                var context = w.Resource<AudioContext>();
                w.Resource<AudioEnvironment>().Hdr = false;
                var burst = new AssetRef<AudioAsset>(Guid.NewGuid());
                var noise = new Random(5);
                context.Register(burst, AudioClip.Generate("burst", Rate, Rate / 10, _ => 0.5f * (float)(noise.NextDouble() * 2 - 1)));
                w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
                if (room) w.Spawn(new LocalTransform { Position = new Vector3(0, 1, -1), Rotation = Quaternion.Identity, Scale = new Vector3(12, 5, 9) }, new LocalToWorld(), AcousticSurface.Box(AcousticMaterial.Concrete));
                app.Tick(1 / 60.0); app.Tick(1 / 60.0); // geometry and the reverb estimate settle (and the reverb's level ramps in)
                for (int i = 0; i < 20; i++) { app.Tick(1 / 60.0); context.Engine.Render(new float[Rate / 60 * 2]); }
                w.Spawn(LocalTransform.At(new Vector3(2, 0, -3)), new LocalToWorld(), AudioSource.At(burst));
                const int frame = Rate / 60;
                var energy = new double[3 * Rate];
                for (int f = 0; f < energy.Length; f += frame)
                {
                    app.Tick(1 / 60.0);
                    var o = new float[frame * 2];
                    context.Engine.Render(o);
                    for (int i = 0; i < frame && f + i < energy.Length; i++) energy[f + i] = o[2 * i] * o[2 * i] + o[2 * i + 1] * o[2 * i + 1];
                }
                reverb = context.Reverb;
                return energy;
            }
            var withRoom = Run(true, out var estimate);
            var open = Run(false, out var none);
            Assert.True(estimate.DecaySeconds.Mid > 1.5f && estimate.Enclosure == 1, $"concrete room: T60 {estimate.DecaySeconds}, enclosure {estimate.Enclosure}");
            Assert.True(none.IsSilent, "no surfaces, no reverb");
            double Sum(double[] e, int from, int to) { double s = 0; for (int i = from; i < to; i++) s += e[i]; return s; }
            double burstEnergy = Sum(withRoom, 0, Rate / 8), tail = Sum(withRoom, Rate / 2, Rate);
            Assert.True(tail > 1e-3 * burstEnergy && Sum(open, Rate / 2, Rate) < 1e-12,
                $"between 0.5 and 1 s: room {tail / burstEnergy:0.0e0} of the burst's energy, open {Sum(open, Rate / 2, Rate):0.0e0}");
            // The tail's decay, measured from the render after the burst (broadband, so between the low and high bands' times).
            var after = withRoom.AsSpan(Rate / 8).ToArray();
            double measured = SchroederT60(after, Rate);
            Assert.True(measured > estimate.DecaySeconds.High * 0.8 && measured < estimate.DecaySeconds.Low * 1.25,
                $"rendered tail decays in {measured:0.00} s; estimated {estimate.DecaySeconds}");
        });

        Test("acoustics: image sources in a shoebox — 6 first-order and 18 second-order paths at the analytic lengths", () =>
        {
            // Room x ∈ [−5, 5], y ∈ [−3, 3], z ∈ [−2, 2]; mirror images are per-axis reflections.
            var builder = new AcousticSceneBuilder();
            builder.AddBox(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(Bands3.All(0.19f), 0, Bands3.All(0)));
            var scene = builder.Build();
            Assert.Equal(6, scene.Planes.Length);
            var listener = new Vector3(1.2f, -0.7f, 0.4f);
            var source = new Vector3(-2.5f, 1.1f, -0.9f);
            var expected1 = new List<float>(); var expected2 = new List<float>();
            float[] lo = [-5, -3, -2], hi = [5, 3, 2];
            float Get(Vector3 v, int a) => a == 0 ? v.X : a == 1 ? v.Y : v.Z;
            Vector3 Set(Vector3 v, int a, float x) => a == 0 ? v with { X = x } : a == 1 ? v with { Y = x } : v with { Z = x };
            Vector3 Mirror(Vector3 v, int wall) { int a = wall / 2; float w = wall % 2 == 0 ? lo[a] : hi[a]; return Set(v, a, 2 * w - Get(v, a)); }
            for (int w = 0; w < 6; w++) expected1.Add(Vector3.Distance(listener, Mirror(source, w)));
            // Second order: distinct images (perpendicular walls commute; opposite walls give two images).
            var seen = new HashSet<(int, int, int)>();
            for (int w1 = 0; w1 < 6; w1++)
            for (int w2 = 0; w2 < 6; w2++)
            {
                if (w1 == w2) continue;
                var image = Mirror(Mirror(source, w1), w2);
                var key = ((int)MathF.Round(image.X * 1000), (int)MathF.Round(image.Y * 1000), (int)MathF.Round(image.Z * 1000));
                if (seen.Add(key)) expected2.Add(Vector3.Distance(listener, image));
            }
            Assert.Equal(18, expected2.Count);

            var paths = new ReflectionPath[64];
            int n = ImageSources.Find(scene, listener, source, paths);
            var found1 = paths[..n].Where(p => p.Order == 1).Select(p => p.Length).Order().ToArray();
            var found2 = paths[..n].Where(p => p.Order == 2).Select(p => p.Length).Order().ToArray();
            Assert.True(found1.Length == 6 && found2.Length == 18, $"found {found1.Length} first-order and {found2.Length} second-order paths");
            var e1 = expected1.Order().ToArray(); var e2 = expected2.Order().ToArray();
            for (int i = 0; i < 6; i++) Assert.True(MathF.Abs(found1[i] - e1[i]) < 1e-3f, $"first order {i}: {found1[i]} vs {e1[i]}");
            for (int i = 0; i < 18; i++) Assert.True(MathF.Abs(found2[i] - e2[i]) < 1e-3f, $"second order {i}: {found2[i]} vs {e2[i]}");
            float r1 = MathF.Sqrt(0.81f), r2 = 0.81f;
            foreach (var p in paths[..n])
            {
                Assert.True(MathF.Abs(p.Surfaces.Mid - (p.Order == 1 ? r1 : r2)) < 1e-5f, "each bounce keeps √(1 − α) of the amplitude");
                Assert.True(MathF.Abs(MathF.Abs(p.Point.X) - 5) < 1e-3f || MathF.Abs(MathF.Abs(p.Point.Y) - 3) < 1e-3f || MathF.Abs(MathF.Abs(p.Point.Z) - 2) < 1e-3f, "the reflection point is on a wall");
            }
            // A pillar between the source and a wall blocks that wall's reflection.
            builder.AddBox(Box(new Vector3(-3.75f, 1.1f, -0.9f), new Vector3(0.5f, 0.5f, 0.5f)), AcousticMaterial.Concrete);
            int blocked = ImageSources.Find(builder.Build(), listener, source, paths, maxOrder: 1);
            Assert.True(blocked < 6, $"a pillar beside the source hides at least one first-order path ({blocked} left)");
        });

        Test("acoustics: a click in a concrete room arrives with its floor reflection 152 samples behind", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            w.Resource<AudioEnvironment>().Hdr = false;
            w.Resource<AudioEnvironment>().Reverb = false; // just the early reflections
            var click = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(click, AudioClip.Generate("click", Rate, 1, _ => 1));
            w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            // Room x ∈ [−6, 6], y ∈ [−1.5, 3.5], z ∈ [−5.5, 3.5]: the floor is 1.5 m below the listener's ears.
            w.Spawn(new LocalTransform { Position = new Vector3(0, 1, -1), Rotation = Quaternion.Identity, Scale = new Vector3(12, 5, 9) }, new LocalToWorld(), AcousticSurface.Box(AcousticMaterial.Concrete));
            app.Tick(1 / 60.0);
            context.Engine.Render(new float[Rate / 60 * 2]);
            var source = new Vector3(2, 0, -3);
            w.Spawn(LocalTransform.At(source), new LocalToWorld(), AudioSource.At(click));
            const int frame = Rate / 60;
            app.Tick(1 / 60.0);
            var o = new float[frame * 2];
            context.Engine.Render(o);
            var energy = new double[frame];
            for (int i = 0; i < frame; i++) energy[i] = o[2 * i] * o[2 * i] + o[2 * i + 1] * o[2 * i + 1];
            int direct = Array.IndexOf(energy, energy.Max());
            // The floor's image is 3 m below the source: 4.690 m against 3.606 m direct, 1.084 m = 3.16 ms = 152 samples later.
            float c = Gamegrl.Audio.Spatial.Propagation.SpeedOfSound(20);
            int expected = direct + (int)MathF.Round((MathF.Sqrt(4 + 9 + 9) - MathF.Sqrt(4 + 9)) / c * Rate);
            int peak = expected - 3;
            for (int i = expected - 3; i <= expected + 3; i++) if (energy[i] > energy[peak]) peak = i;
            double around = 0; int count = 0;
            for (int i = direct + 20; i < expected - 10; i++) { around = Math.Max(around, energy[i]); count++; }
            Assert.True(Math.Abs(peak - expected) <= 1 && energy[peak] > 10 * around, $"floor reflection at {peak - direct} samples (expected {expected - direct}), {energy[peak]:0.0e0} against {around:0.0e0} before it");
            Assert.True(context.Scene.Planes.Length == 6, "the room's six walls");
        });

        Test("acoustics: measurement — the echogram is calibrated (free field and a mirror floor), T30 follows Kuttruff, clarity follows absorption", () =>
        {
            // Free field: just the direct sound, 1/(4πr²).
            var none = AcousticMeasurement.Measure(AcousticScene.Empty, new Vector3(0, 0, -4), Vector3.Zero, MeasureOptions.Default);
            // (Air takes its share over the 4 m, too: about 0.05 dB in the mid band.)
            float free = 1 / (4 * MathF.PI * 16) * MathF.Pow(10, -(float)Gamegrl.Audio.Spatial.Propagation.AirAbsorptionDbPerMetre(Gamegrl.Audio.Spatial.Bands.MidCenter) * 4 / 10);
            Assert.True(MathF.Abs(none.DirectEnergy.Mid - free) < 1e-3f * free && none.Mid.Sum() == 0, $"free field: {none.DirectEnergy.Mid} W/m², expected {free}");
            // A mirror floor 2 m below both: its image is 4 m down, so the reflection carries (1 − α)/(4π·(√(4² + 4²))²).
            var floor = new AcousticSceneBuilder();
            floor.AddBox(Box(new Vector3(0, -2.05f, 0), new Vector3(400, 0.1f, 400)), new AcousticMaterial(Bands3.All(0.2f), 0, Bands3.All(0)));
            var mirror = AcousticMeasurement.Measure(floor.Build(), new Vector3(0, 0, -4), Vector3.Zero, MeasureOptions.Default with { Rays = 200000, ReceiverRadius = 0.3f, TemperatureC = 20, HumidityPercent = 100 });
            float expected = 0.8f / (4 * MathF.PI * 32);
            float got = mirror.Mid.Sum();
            Assert.True(MathF.Abs(got - expected) < 0.15f * expected, $"floor reflection: ray-counted {got:0.0000e0}, image source {expected:0.0000e0}");
            Assert.True(mirror.Paths.Length == 1 && mirror.Paths[0].Order == 1, $"one image path ({mirror.Paths.Length})");

            var room = new AcousticSceneBuilder();
            room.AddBox(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(Bands3.All(0.2f), 1, Bands3.All(0)));
            var m = AcousticMeasurement.Measure(room.Build(), new Vector3(-2.5f, 1.1f, -0.9f), new Vector3(1.3f, 0.4f, -0.7f), MeasureOptions.Default);
            double kuttruff = 0.161 * 240 / (-248 * Math.Log(0.8)) / (1 + 0.2 * Math.Log(0.8));
            Assert.True(Math.Abs(m.T30.Mid - kuttruff) < 0.1 * kuttruff, $"T30 {m.T30.Mid:0.000} s, Kuttruff {kuttruff:0.000}");
            Assert.True(MathF.Abs(m.SabineSeconds - 0.779f) < 0.05f && MathF.Abs(m.EyringSeconds - 0.698f) < 0.05f, $"predictions: Sabine {m.SabineSeconds:0.000}, Eyring {m.EyringSeconds:0.000}");
            Assert.True(m.Edt.Mid > 0.3f && m.Paths.Length > 0 && m.InitialGapSeconds > 0, $"EDT {m.Edt.Mid:0.00}, {m.Paths.Length} paths, gap {m.InitialGapSeconds * 1000:0.0} ms");

            AcousticMeasurement In(AcousticMaterial material, Vector3 source)
            {
                var b = new AcousticSceneBuilder();
                b.AddBox(Box(Vector3.Zero, new Vector3(10, 4, 8)), material);
                return AcousticMeasurement.Measure(b.Build(), source, Vector3.Zero, MeasureOptions.Default with { Rays = 8000 });
            }
            var hard = In(AcousticMaterial.Concrete, new Vector3(3, 0, -2));
            var soft = In(AcousticMaterial.Carpet, new Vector3(3, 0, -2));
            var near = In(AcousticMaterial.Concrete, new Vector3(0.8f, 0, -0.6f));
            Assert.True(soft.C50.Mid > hard.C50.Mid + 5, $"carpet is clearer for speech: C50 {soft.C50.Mid:0.0} dB against concrete {hard.C50.Mid:0.0}");
            Assert.True(soft.T30.Mid < hard.T30.Mid / 3, $"and much less reverberant: {soft.T30.Mid:0.00} s against {hard.T30.Mid:0.00}");
            Assert.True(near.DirectToReverberantDb > hard.DirectToReverberantDb + 6, $"closer, more direct: DRR {near.DirectToReverberantDb:0.0} dB against {hard.DirectToReverberantDb:0.0}");
            Assert.True(hard.CriticalDistance > 0.3f && hard.CriticalDistance < 3, $"critical distance {hard.CriticalDistance:0.00} m");
        });

        Test("acoustics: the auralizer renders dry, room and binaural versions; the room's impulse response decays as estimated", () =>
        {
            var builder = new AcousticSceneBuilder();
            builder.AddBox(Box(new Vector3(0, 1, -1), new Vector3(12, 5, 9)), AcousticMaterial.Concrete);
            var scene = builder.Build();
            var setup = AuralizeSetup.For(scene, Matrix4x4.Identity, new Vector3(2, 0, -3)) with { TailSeconds = 2.5f };
            var dry = Auralizer.ImpulseResponse(setup with { Dry = true });
            var wet = Auralizer.ImpulseResponse(setup);
            double Energy(AudioClip c, int from, int to) { double e = 0; for (int i = from * c.Channels; i < Math.Min(to, c.Frames) * c.Channels; i++) e += c.Samples[i] * c.Samples[i]; return e; }
            Assert.True(Energy(dry, 10, dry.Frames) < 1e-9 && Energy(dry, 0, 10) > 0, "dry: one click and nothing after");
            Assert.True(Energy(wet, 2000, wet.Frames) > 1e-3 * Energy(wet, 0, 10), "room: reflections and a tail after the click");
            var energy = new double[wet.Frames];
            for (int f = 0; f < wet.Frames; f++) energy[f] = wet.Samples[2 * f] * wet.Samples[2 * f] + wet.Samples[2 * f + 1] * wet.Samples[2 * f + 1];
            double t60 = SchroederT60(energy, 48000);
            var estimate = ReverbEstimator.Estimate(scene, Vector3.Zero, 4096, 128);
            Assert.True(t60 > estimate.DecaySeconds.High && t60 < estimate.DecaySeconds.Low * 1.25, $"impulse response decays in {t60:0.00} s; estimated {estimate.DecaySeconds}");
            var phones = Auralizer.Render(TestSounds.Clap(), setup with { Headphones = true, TailSeconds = 0.5f });
            Assert.True(phones.Layout == ChannelLayout.Stereo && phones.Frames == (int)((0.1 + 0.5) * 48000), $"binaural stereo, clap plus tail ({phones.Frames} frames)");
            var gun = TestSounds.Gunshot();
            Assert.True(gun.Duration > 0.3 && gun.Samples.Max(MathF.Abs) <= 0.951f, "the gunshot is 0.4 s and normalised");
        });

        Test("acoustics: diffraction — over a wall top, over a thick barrier, round a door frame, with ISO 9613-2's losses", () =>
        {
            var listener = Vector3.Zero; var source = new Vector3(0, 0, -10);
            const float c = 343;
            // A free-standing thin wall: one quad at z = −5 reaching y = 2. Its top is an open edge.
            var thin = new AcousticSceneBuilder();
            Vector3[] quad = [new(-20, -10, 0), new(20, -10, 0), new(20, 2, 0), new(-20, -10, 0), new(20, 2, 0), new(-20, 2, 0)];
            thin.AddMesh(quad, Matrix4x4.CreateTranslation(0, 0, -5), AcousticMaterial.Concrete);
            Assert.True(Diffraction.Find(thin.Build(), listener, source, out var over, c), "a path over the wall");
            float expected = 2 * MathF.Sqrt(25 + 4), z = expected - 10;
            Assert.True(over.Edges == 1 && MathF.Abs(over.Length - expected) < 1e-3f && Vector3.Distance(over.First, new Vector3(0, 2, -5)) < 0.01f,
                $"over the top: {over.Length} m via {over.First}, expected {expected} via (0, 2, −5)");
            float low = MathF.Pow(10, -MathF.Min(20, 10 * MathF.Log10(3 + 40 * z / (c / 400))) / 20);
            Assert.True(MathF.Abs(over.Gain.Low - low) < 1e-4f && MathF.Abs(over.Gain.Mid - 0.1f) < 1e-4f && MathF.Abs(over.Gain.High - 0.1f) < 1e-4f,
                $"losses {over.Gain}: low {20 * MathF.Log10(over.Gain.Low):0.0} dB (expected {20 * MathF.Log10(low):0.0}), mid and high at the 20 dB cap");
            Assert.True(MathF.Abs(Diffraction.BarrierDb(0, 1) - 10 * MathF.Log10(3)) < 1e-5f, "at the shadow's edge, 4.8 dB");

            // A barrier 2 m thick: over its front and back top edges.
            var thick = new AcousticSceneBuilder();
            thick.AddBox(Box(new Vector3(0, -4, -5), new Vector3(40, 12, 2)), AcousticMaterial.Concrete);
            Assert.True(Diffraction.Find(thick.Build(), listener, source, out var both, c), "a path over the barrier");
            float expected2 = 2 * MathF.Sqrt(16 + 4) + 2;
            Assert.True(both.Edges == 2 && MathF.Abs(both.Length - expected2) < 2e-3f, $"over both edges: {both.Edges} edges, {both.Length} m, expected {expected2}");
            float c3 = Diffraction.DoubleC3(2, c / 400);
            float low2 = MathF.Pow(10, -Diffraction.BarrierDb(expected2 - 10, c / 400, c3, Diffraction.DoubleCapDb) / 20);
            Assert.True(MathF.Abs(both.Gain.Low - low2) < 1e-4f && both.Gain.Low < over.Gain.Low, $"double diffraction loses more: {20 * MathF.Log10(both.Gain.Low):0.0} dB low");

            // A doorway from x = 1 to 2 in a tall wall: round the nearer door frame, so the sound comes from the right.
            var door = new AcousticSceneBuilder();
            door.AddBox(Box(new Vector3(-9.5f, 0, -5), new Vector3(21, 40, 0.2f)), AcousticMaterial.Concrete);
            door.AddBox(Box(new Vector3(12, 0, -5), new Vector3(20, 40, 0.2f)), AcousticMaterial.Concrete);
            Assert.True(Diffraction.Find(door.Build(), listener, source, out var through, c), "a path through the door");
            Assert.True(MathF.Abs(through.First.X - 1) < 0.01f && through.First.Z > -5.2f && through.Length < 10.3f,
                $"round the frame at x = 1: {through.First}, {through.Length} m (straight through the doorway's edge: {2 * MathF.Sqrt(25 + 1):0.000})");
            Assert.True(!Diffraction.Find(door.Build(), listener, new Vector3(3, 0, -10), out _, c), "a source in plain view through the door needs none"); // the line crosses the wall at x = 1.5
        });

        Test("acoustics: a tone behind an opaque wall reaches the listener through a doorway on the right, at the diffraction loss", () =>
        {
            (double Left, double Right) Run(bool wall)
            {
                var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
                using var app = b.Build();
                var w = app.World;
                var context = w.Resource<AudioContext>();
                ref var env = ref w.Resource<AudioEnvironment>();
                env.Hdr = false; env.Reverb = false; env.Reflections = false;
                var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
                context.Register(tone, AudioClip.Sine(2530, 4, amplitude: 0.5f));
                w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
                if (wall)
                {
                    var opaque = new AcousticMaterial(Bands3.All(0.1f), 0, Bands3.All(0));
                    w.Spawn(new LocalTransform { Position = new Vector3(-9.5f, 0, -5), Rotation = Quaternion.Identity, Scale = new Vector3(21, 40, 0.2f) }, new LocalToWorld(), AcousticSurface.Box(opaque));
                    w.Spawn(new LocalTransform { Position = new Vector3(12, 0, -5), Rotation = Quaternion.Identity, Scale = new Vector3(20, 40, 0.2f) }, new LocalToWorld(), AcousticSurface.Box(opaque));
                }
                w.Spawn(LocalTransform.At(new Vector3(0, 0, -10)), new LocalToWorld(), AudioSource.At(tone, loop: true) with { Emitter = AudioEmitter.Default with { Reflections = false } });
                double l = 0, r = 0; int n = 0;
                for (int k = 0; k < 12; k++)
                {
                    app.Tick(1 / 60.0);
                    var o = new float[Rate / 60 * 2];
                    context.Engine.Render(o);
                    if (k < 6) continue; // the path search and ramps settle
                    for (int i = 0; i < o.Length; i += 2) { l += o[i] * o[i]; r += o[i + 1] * o[i + 1]; n++; }
                }
                return (Math.Sqrt(l / n), Math.Sqrt(r / n));
            }
            var open = Run(false);
            var door = Run(true);
            double ratio = Math.Sqrt((door.Left * door.Left + door.Right * door.Right) / (open.Left * open.Left + open.Right * open.Right));
            // Round the door frame's two edges (the wall is 0.2 m thick): about 18 dB down in the mid band, 10/10.2 for distance.
            Assert.True(ratio > 0.09 && ratio < 0.2, $"through the doorway: {20 * Math.Log10(ratio):0.0} dB against open air");
            Assert.True(door.Right > 1.5 * door.Left && Math.Abs(open.Right - open.Left) < 1e-3, $"from the right, where the door is: left {door.Left:0.0000}, right {door.Right:0.0000}");
        });

        Test("acoustics: a burst inside a concrete room, heard from outside, rings on through the door it comes out of", () =>
        {
            (double Tail, double Left, double Right, ReverbParams Remote) Run(bool remote)
            {
                var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
                using var app = b.Build();
                var w = app.World;
                var context = w.Resource<AudioContext>();
                ref var env = ref w.Resource<AudioEnvironment>();
                env.Hdr = false; env.RemoteReverb = remote;
                var burst = new AssetRef<AudioAsset>(Guid.NewGuid());
                var noise = new Random(9);
                context.Register(burst, AudioClip.Generate("burst", Rate, Rate / 10, _ => 0.5f * (float)(noise.NextDouble() * 2 - 1)));
                w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
                // Interior x ∈ [−6, 6], y ∈ [−1.5, 3.5], z ∈ [−20, −8]; the front wall has a doorway from x = 1 to 3.
                void Wall(Vector3 center, Vector3 size) =>
                    w.Spawn(new LocalTransform { Position = center, Rotation = Quaternion.Identity, Scale = size }, new LocalToWorld(), AcousticSurface.Box(AcousticMaterial.Concrete));
                Wall(new Vector3(0, -1.65f, -14), new Vector3(12.6f, 0.3f, 12.6f));
                Wall(new Vector3(0, 3.65f, -14), new Vector3(12.6f, 0.3f, 12.6f));
                Wall(new Vector3(0, 1, -20.15f), new Vector3(12.6f, 5, 0.3f));
                Wall(new Vector3(-6.15f, 1, -14), new Vector3(0.3f, 5, 12));
                Wall(new Vector3(6.15f, 1, -14), new Vector3(0.3f, 5, 12));
                Wall(new Vector3(-2.65f, 1, -7.85f), new Vector3(7.3f, 5, 0.3f));
                Wall(new Vector3(4.65f, 1, -7.85f), new Vector3(3.3f, 5, 0.3f));
                w.Spawn(LocalTransform.At(new Vector3(-2, 0, -14)), new LocalToWorld(), AudioSource.At(burst));
                const int frame = Rate / 60;
                var left = new double[2 * Rate]; var right = new double[2 * Rate];
                for (int f = 0; f < left.Length; f += frame)
                {
                    app.Tick(1 / 60.0);
                    var o = new float[frame * 2];
                    context.Engine.Render(o);
                    for (int i = 0; i < frame && f + i < left.Length; i++) { left[f + i] = o[2 * i] * o[2 * i]; right[f + i] = o[2 * i + 1] * o[2 * i + 1]; }
                }
                double l = 0, r = 0;
                for (int i = Rate / 2; i < Rate; i++) { l += left[i]; r += right[i]; }
                return (l + r, l, r, context.RemoteReverb);
            }
            var with = Run(true);
            var without = Run(false);
            Assert.True(with.Remote.DecaySeconds.Mid > 1 && with.Remote.Enclosure > 0.9f, $"the room inside: T60 {with.Remote.DecaySeconds}, enclosed {with.Remote.Enclosure:0.00}");
            Assert.True(with.Tail > 5 * without.Tail, $"its tail between 0.5 and 1 s: {with.Tail:0.0e0} with the room's reverb, {without.Tail:0.0e0} without");
            Assert.True(with.Right > 1.5 * with.Left, $"from the door, on the right: left {with.Left:0.0e0}, right {with.Right:0.0e0}");
        });

        Test("acoustics: outdoors, a shout slaps back off a facade 60 m away at the image-source delay, after its ground bounce", () =>
        {
            var builder = new AcousticSceneBuilder();
            builder.AddBox(Box(new Vector3(0, -0.1f, 0), new Vector3(400, 0.2f, 400)), AcousticMaterial.Concrete); // the ground, top at y = 0
            builder.AddBox(Box(new Vector3(0, 10, -62), new Vector3(200, 20, 4)), AcousticMaterial.Concrete);   // a facade, its face at z = −60
            var listener = Matrix4x4.CreateTranslation(0, 1.7f, 0);
            var source = new Vector3(0, 1.7f, -10);
            var setup = AuralizeSetup.For(builder.Build(), listener, source) with
            {
                Environment = AudioEnvironment.Default with { Hdr = false, Reverb = false }, TailSeconds = 0.5f,
            };
            var ir = Auralizer.ImpulseResponse(setup);
            var e = new double[ir.Frames];
            for (int f = 0; f < ir.Frames; f++) e[f] = ir.Samples[2 * f] * ir.Samples[2 * f] + ir.Samples[2 * f + 1] * ir.Samples[2 * f + 1];
            int direct = Array.IndexOf(e, e.Max());
            float c = Gamegrl.Audio.Spatial.Propagation.SpeedOfSound(20);
            int Peak(int at) { int p = at - 3; for (int i = at - 3; i <= at + 3; i++) if (e[i] > e[p]) p = i; return p; }
            int ground = Peak(direct + (int)MathF.Round((MathF.Sqrt(100 + 3.4f * 3.4f) - 10) / c * Rate));
            int slap = Peak(direct + (int)MathF.Round(100 / c * Rate));
            double quietBefore = 0;
            for (int i = direct + 2000; i < slap - 50; i++) quietBefore = Math.Max(quietBefore, e[i]);
            Assert.True(e[ground] > 1e-3 * e[direct], $"the ground bounce at +{ground - direct} samples");
            Assert.True(e[slap] > 100 * quietBefore, $"the slapback at +{slap - direct} samples ({(slap - direct) * 1000.0 / Rate:0.0} ms), clear of what's before it");
            // The echo sits at a fractional delay, interpolated across neighbouring samples: compare energy around each, not peaks.
            double Around(int at) { double sum = 0; for (int i = Math.Max(0, at - 4); i <= Math.Min(e.Length - 1, at + 8); i++) sum += e[i]; return sum; }
            double ratio = Math.Sqrt(Around(slap) / Around(direct));
            double expected = 10.0 / 110 * Math.Sqrt((1 - 0.07) * (1 - 0.05));
            Assert.True(Math.Abs(ratio - expected) < 0.25 * expected, $"at {ratio:0.000} of the direct sound, expected {expected:0.000} (distance and the facade)");
        });

        Test("acoustics: baked probes — a grid through a room bakes, interpolates like a live estimate, skips probes in solids, round-trips", () =>
        {
            var builder = new AcousticSceneBuilder();
            builder.AddRoom(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(Bands3.All(0.2f), 1, Bands3.All(0)));
            var room = builder.Build();
            var volume = Box(Vector3.Zero, new Vector3(8, 4, 2.4f));
            var grid = AcousticProbeGrid.Bake(room, volume, spacing: 2, rays: 1024, bounces: 128, temperatureC: 20, humidityPercent: 50);
            Assert.True(grid.CountX == 5 && grid.CountY == 3 && grid.CountZ == 3 && grid.ValidCount == grid.Count, $"{grid.CountX}×{grid.CountY}×{grid.CountZ}, {grid.ValidCount} valid");
            var between = new Vector3(1, 1, 0.3f);
            Assert.True(grid.Sample(between, out var baked), "a point inside samples");
            var live = ReverbEstimator.Estimate(room, between, 4096, 128);
            Assert.True(MathF.Abs(baked.DecaySeconds.Mid - live.DecaySeconds.Mid) < 0.1f * live.DecaySeconds.Mid && MathF.Abs(baked.Level - live.Level) < 0.15f * live.Level,
                $"interpolated {baked.DecaySeconds.Mid:0.000} s level {baked.Level:0.00}; live {live.DecaySeconds.Mid:0.000} s level {live.Level:0.00}");
            Assert.True(!grid.Sample(new Vector3(4.5f, 0, 0), out _), "outside the volume, nothing");
            var bytes = grid.ToBytes();
            var back = AcousticProbeGrid.FromBytes(bytes);
            Assert.True(back.ToBytes().AsSpan().SequenceEqual(bytes), "the stored form round-trips");
            var imported = new Gamegrl.AssetPipeline.AcousticProbesImporter().Import(new Gamegrl.AssetPipeline.ImportContext("Assets/room.acprobes", bytes, new Dictionary<string, string>(), _ => [], _ => null));
            Assert.True(imported.AsSpan().SequenceEqual(bytes), "and imports as it is");

            // A pillar through the middle: the probes inside it are skipped, and points beside it still sample from the rest.
            builder.AddBox(Box(Vector3.Zero, new Vector3(1.2f, 6, 1.2f)), AcousticMaterial.Concrete);
            var pillared = AcousticProbeGrid.Bake(builder.Build(), volume, spacing: 2, rays: 256, bounces: 64);
            Assert.True(pillared.ValidCount < pillared.Count, $"{pillared.Count - pillared.ValidCount} probes inside the pillar");
            Assert.True(pillared.Sample(new Vector3(0.9f, 0.2f, 0.1f), out var beside) && beside.DecaySeconds.Mid > 0, "beside the pillar");
        });

        Test("acoustics: inside a baked volume the listener's reverb comes from the probes, with no surfaces traced", () =>
        {
            var builder = new AcousticSceneBuilder();
            builder.AddRoom(Box(new Vector3(0, 1, -1), new Vector3(12, 5, 9)), AcousticMaterial.Concrete);
            var volumeMatrix = Box(new Vector3(0, 1, -1), new Vector3(10, 4, 8));
            var grid = AcousticProbeGrid.Bake(builder.Build(), volumeMatrix, spacing: 3, rays: 512, bounces: 128);
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            var probes = new AssetRef<AcousticProbesAsset>(Guid.NewGuid());
            context.RegisterProbes(probes, grid);
            // No acoustic surfaces in this world at all: only the baked volume.
            w.Spawn(new LocalTransform { Position = new Vector3(0, 1, -1), Rotation = Quaternion.Identity, Scale = new Vector3(10, 4, 8) }, new LocalToWorld(),
                AcousticProbeVolume.Default with { Probes = probes });
            var listener = w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            app.Tick(1 / 60.0);
            Assert.True(context.Scene.TriangleCount == 0 && context.Reverb.DecaySeconds.Mid > 1.5f && context.Reverb.Enclosure > 0.99f,
                $"baked reverb {context.Reverb.DecaySeconds} with {context.Scene.TriangleCount} triangles traced");
            grid.Sample(Vector3.Zero, out var expected);
            Assert.True(MathF.Abs(context.Reverb.Level - expected.Level) < 1e-5f, "the probes' value at the listener");
            w.Get<LocalTransform>(listener).Position = new Vector3(30, 0, 0);
            app.Tick(1 / 60.0);
            Assert.True(context.Reverb.IsSilent, "outside the volume, with nothing to trace, there's no reverb");
        });

        Test("acoustics: an entity behind a wall entity is heard through the wall's transmission", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            w.Resource<AudioEnvironment>().Hdr = false;
            var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(tone, AudioClip.Sine(2530, 4, amplitude: 0.5f)); // the middle of the mid band
            const int frame = Rate / 60;
            double Level()
            {
                double sum = 0; int n = 0;
                for (int k = 0; k < 6; k++)
                {
                    app.Tick(1 / 60.0);
                    var o = new float[frame * 2];
                    context.Engine.Render(o);
                    if (k < 3) continue; // let ramps settle
                    foreach (var x in o) { sum += x * x; n++; }
                }
                return Math.Sqrt(sum / n);
            }
            w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            w.Spawn(LocalTransform.At(new Vector3(0, 0, -6)), new LocalToWorld(), AudioSource.At(tone, loop: true));
            double clear = Level();
            var wallMaterial = new AcousticMaterial(Bands3.All(0.1f), 0, new Bands3(0.5f, 0.1f, 0.05f));
            var wall = w.Spawn(new LocalTransform { Position = new Vector3(0, 0, -3), Rotation = Quaternion.Identity, Scale = new Vector3(10, 10, 0.3f) },
                new LocalToWorld(), AcousticSurface.Box(wallMaterial));
            double behind = Level();
            // The tone sits mid-band, 1.7 octaves above the low shelf's corner: a little of the low band's 0.5 remains.
            double ratio = behind / clear;
            Assert.True(ratio > 0.098 && ratio < 0.12, $"through a wall passing 10% mid-band: {ratio:0.000}");
            Assert.True(context.Scene.TriangleCount == 12, "the wall entity became twelve triangles");
            w.Destroy(wall);
            Assert.True(Math.Abs(Level() / clear - 1) < 0.01, "removing the wall clears the path");
        });
        Test("acoustics: reflections glide at their Doppler rate as the listener walks past walls (no jumps when paths are found again, no taps swapping)", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            ref var env = ref w.Resource<AudioEnvironment>();
            env.Hdr = false; env.Reverb = false; env.Diffraction = false;
            context.Diagnostics.Enabled = true;
            var tone = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(tone, AudioClip.Generate("tone", Rate, Rate, i => MathF.Sin(i * 0.05f)));
            // A concrete room 20 × 6 × 10 m; the listener walks 9 m along it at 4.5 m/s past a fixed source.
            w.Spawn(new LocalTransform { Position = new Vector3(0, 1, 0), Rotation = Quaternion.Identity, Scale = new Vector3(20, 6, 10) }, new LocalToWorld(), AcousticSurface.Room(AcousticMaterial.Concrete));
            w.Spawn(LocalTransform.At(new Vector3(1, 0.5f, -2)), new LocalToWorld(), AudioSource.At(tone, loop: true));
            var listener = w.Spawn(LocalTransform.At(new Vector3(-4.5f, 0, 1)), new LocalToWorld(), AudioListener.Default);
            const float speed = 4.5f, dt = 1 / 60f;
            float c = Gamegrl.Audio.Spatial.Propagation.SpeedOfSound(20);
            var previous = new float[ReflectionTaps.Count];
            var previousStep = new float[ReflectionTaps.Count];
            var had = new bool[ReflectionTaps.Count];
            var stepped = new bool[ReflectionTaps.Count];
            int compared = 0, restarts = 0;
            float worst = 0;
            for (int f = 0; f < 120; f++)
            {
                w.Get<LocalTransform>(listener).Position.X = -4.5f + speed * dt * f;
                app.Tick(dt);
                context.Engine.Render(new float[Rate / 60 * 2]);
                var d = context.Diagnostics;
                Assert.True(d.Voices.Count == 1, "one voice");
                var v = d.Voices[0];
                var has = new bool[ReflectionTaps.Count];
                var steps = new bool[ReflectionTaps.Count];
                for (int k = 0; k < v.TapCount; k++)
                {
                    var tap = d.Taps[v.TapStart + k];
                    if (tap.Gain <= 0) continue;
                    has[k] = true;
                    if (tap.Restart) { restarts++; }
                    else if (had[k])
                    {
                        // A reflection's path changes by at most twice the listener's step (toward one image, away from the
                        // source), and at constant speed its rate of change drifts slowly: successive steps are nearly
                        // equal. (Delays recomputed only when a search lands step small, small, small, then big.)
                        float step = tap.Delay - previous[k];
                        Assert.True(MathF.Abs(step) <= 2 * speed * dt / c + 2e-6f, $"frame {f}, tap {k}: delay moved {step * 1000:0.000} ms in a frame (at most {2 * speed * dt / c * 1000:0.000})");
                        if (stepped[k])
                        {
                            float change = MathF.Abs(step - previousStep[k]);
                            worst = MathF.Max(worst, change);
                            Assert.True(change < 3e-5f, $"frame {f}, tap {k}: delay step went from {previousStep[k] * 1000:0.000} to {step * 1000:0.000} ms");
                            compared++;
                        }
                        previousStep[k] = step;
                        steps[k] = true;
                    }
                    previous[k] = tap.Delay;
                }
                had = has;
                stepped = steps;
            }
            Assert.True(compared > 500 && restarts < 40, $"{compared} tap steps compared, {restarts} restarts; worst change of step {worst * 1000:0.0000} ms");
        });

        Test("audio: a one-shot played on an entity moves with it; one played at a point stays behind", () =>
        {
            var b = EngineBuilder.Create().AddModule(new AudioModule(AudioOutput.Offline));
            using var app = b.Build();
            var w = app.World;
            var context = w.Resource<AudioContext>();
            w.Resource<AudioEnvironment>().Hdr = false;
            context.Diagnostics.Enabled = true;
            var shot = new AssetRef<AudioAsset>(Guid.NewGuid());
            context.Register(shot, AudioClip.Generate("shot", Rate, Rate, i => MathF.Sin(i * 0.05f)));
            var walker = w.Spawn(LocalTransform.Identity, new LocalToWorld(), AudioListener.Default);
            app.Tick(1 / 60.0);
            var offset = new Vector3(0.2f, -0.3f, -0.6f);
            var held = context.PlayOn(walker, shot, offset, AudioEmitter.Default, PlayOptions.Default);
            var dropped = context.PlayAt(shot, Vector3.Transform(offset, w.Get<LocalToWorld>(walker).Value), AudioEmitter.Default, PlayOptions.Default);
            for (int f = 1; f <= 30; f++)
            {
                w.Get<LocalTransform>(walker).Position = new Vector3(5f * f / 60, 0, 0); // 5 m/s to the right
                app.Tick(1 / 60.0);
                context.Engine.Render(new float[Rate / 60 * 2]);
            }
            VoiceReport On = default, At = default;
            foreach (var v in context.Diagnostics.Voices) { if (v.Voice == held) On = v; if (v.Voice == dropped) At = v; }
            var expected = new Vector3(2.5f, 0, 0) + offset;
            Assert.True(Vector3.Distance(On.Position, expected) < 1e-4f, $"the held shot is at {On.Position}, expected {expected}");
            Assert.True(MathF.Abs(On.Spatial.Doppler - 1) < 1e-4f && On.Spatial.Direction.X > 0.2f, $"moving together: no Doppler ({On.Spatial.Doppler:0.0000}), still to the right ({On.Spatial.Direction})");
            Assert.True(At.Spatial.Doppler < 0.99f && At.Spatial.Direction.X < -0.5f, $"left behind: lower ({At.Spatial.Doppler:0.0000}) and to the left ({At.Spatial.Direction})");
        });

        Test("acoustics: the reverb is what comes back — a shoebox's level matches 16π/R; in the open before a cliff it's short and faint", () =>
        {
            // 10 × 6 × 4 m, α = 0.2, fully scattering: R = S·α/(1 − α) = 62 m².
            var room = new AcousticSceneBuilder();
            room.AddRoom(Box(Vector3.Zero, new Vector3(10, 6, 4)), new AcousticMaterial(Bands3.All(0.2f), 1, Bands3.All(0)));
            var inside = ReverbEstimator.Estimate(room.Build(), new Vector3(1.3f, 0.4f, -0.7f), airAbsorption: false);
            float theory = MathF.Sqrt(16 * MathF.PI / 62);
            Assert.True(MathF.Abs(inside.Level - theory) < 0.1f * theory, $"level {inside.Level:0.000}, diffuse-field theory {theory:0.000}");
            Assert.True(MathF.Abs(inside.DecaySeconds.Mid - inside.LateDecaySeconds.Mid) < 0.12f * inside.LateDecaySeconds.Mid,
                $"a diffuse room's early decay is its late decay: EDT {inside.DecaySeconds.Mid:0.000} s, T30 {inside.LateDecaySeconds.Mid:0.000} s");
            // Gravel ground and a rock cliff 12 m ahead with a 6 m doorway; behind it solid hill, or a rock chamber whose
            // energy rings on (a long late decay) while little of it comes back out to where the listener stands.
            static AcousticSceneBuilder Cliff()
            {
                var b = new AcousticSceneBuilder();
                b.AddBox(Box(new Vector3(0, -1, 0), new Vector3(200, 2, 200)), AcousticMaterial.Gravel);
                b.AddBox(Box(new Vector3(-23, 7, -14), new Vector3(40, 14, 4)), AcousticMaterial.Rock);
                b.AddBox(Box(new Vector3(23, 7, -14), new Vector3(40, 14, 4)), AcousticMaterial.Rock);
                b.AddBox(Box(new Vector3(0, 10, -14), new Vector3(6, 8, 4)), AcousticMaterial.Rock);
                return b;
            }
            var solid = Cliff();
            solid.AddBox(Box(new Vector3(0, 8, -36), new Vector3(40, 16, 40)), AcousticMaterial.Rock);
            var hill = solid.Build();
            var carved = Cliff(); // a chamber x ∈ [−15, 15], y ∈ [0, 10], z ∈ [−52, −16]
            carved.AddBox(Box(new Vector3(0, -0.99f, -34), new Vector3(30, 2, 36)), AcousticMaterial.Rock);
            carved.AddBox(Box(new Vector3(-20, 7, -34), new Vector3(10, 14, 36)), AcousticMaterial.Rock);
            carved.AddBox(Box(new Vector3(20, 7, -34), new Vector3(10, 14, 36)), AcousticMaterial.Rock);
            carved.AddBox(Box(new Vector3(0, 7, -57), new Vector3(50, 14, 10)), AcousticMaterial.Rock);
            carved.AddBox(Box(new Vector3(0, 12, -34), new Vector3(30, 4, 36)), AcousticMaterial.Rock);
            var hollow = carved.Build();
            var open = ReverbEstimator.Estimate(hill, new Vector3(0, 1.7f, 0));
            var cave = ReverbEstimator.Estimate(hollow, new Vector3(0, 1.7f, -30));
            var outside = ReverbEstimator.Estimate(hollow, new Vector3(0, 1.7f, 0));
            Assert.True(open.Level < 0.05f && open.DecaySeconds.Mid < 0.6f, $"before a cliff: level {20 * MathF.Log10(open.Level):0} dB, EDT {open.DecaySeconds.Mid:0.00} s");
            Assert.True(cave.Level > 0.1f && cave.DecaySeconds.Mid > 1.5f, $"in the chamber: level {20 * MathF.Log10(cave.Level):0} dB, EDT {cave.DecaySeconds.Mid:0.00} s");
            Assert.True(outside.Level < 0.3f * cave.Level && outside.LateDecaySeconds.Mid > 1, $"outside its doorway: level {20 * MathF.Log10(outside.Level):0} dB against {20 * MathF.Log10(cave.Level):0} inside (late T30 {outside.LateDecaySeconds.Mid:0.0} s)");
        });

    }
}
