using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Audio;
using Gamegrl.Audio.Acoustics;
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
