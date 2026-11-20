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
                float t = p.DecaySeconds.Mid;
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
    }
}
