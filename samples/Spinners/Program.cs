using System.Numerics;
using Gamegrl;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi;
using Gamegrl.Rhi.Vulkan;
using Spinners;

// Milestone 1 sample: a field of spinning cubes (3D, instanced) under bouncing sprites (2D), all driven by
// generated systems. Escape quits. --seconds N quits after N seconds (for automated runs).
double? runFor = args.Length == 2 && args[0] == "--seconds" ? double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture) : null;

using var app = EngineBuilder.Create()
    .AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · spinners")))
    .AddModule<VulkanModule>()
    .AddModule(new RenderingModule(vsync: true))
    .AddModule(new SpinnersModule(runFor))
    .Build();

var device = app.World.Resource<GpuDevice>();
Console.WriteLine($"gamegrl: {device.BackendName} on {device.AdapterName}, {app.Jobs.WorkerCount} worker threads");
DesktopHost.Run(app);
var t = app.World.Resource<Time>();
Console.WriteLine($"gamegrl: {t.Frame} frames in {t.Elapsed:F1} s ({t.Frame / Math.Max(t.Elapsed, 1e-9):F0} fps), {t.Tick} fixed ticks");

namespace Spinners
{
    public struct Spinner : IComponent
    {
        public Vector3 Axis;
        public float DegreesPerSecond;
    }

    public struct Bouncer : IComponent
    {
        public Vector2 Velocity;
        public float Spin;
    }

    public struct Orbit : IComponent
    {
        public float Radius, Height, Speed;
    }

    public struct QuitAfter
    {
        public double Seconds;
    }

    public static partial class Gameplay
    {
        [System(Phase.Update, Parallel = true)]
        private static void Spin(ref LocalTransform t, in Spinner s, [Res] in Time time) =>
            t.Rotation = Quaternion.Normalize(t.Rotation * Quaternion.CreateFromAxisAngle(s.Axis, float.DegreesToRadians(s.DegreesPerSecond) * time.Delta));

        [System(Phase.FixedUpdate)]
        private static void Bounce(ref LocalTransform t, ref Bouncer b, in Sprite sprite, [Res] in Time time, [Res] in ScreenSize screen)
        {
            var p = new Vector2(t.Position.X, t.Position.Y) + b.Velocity * time.FixedDelta;
            var half = sprite.Size * 0.5f * new Vector2(t.Scale.X, t.Scale.Y);
            if (p.X < half.X || p.X > screen.Width - half.X) b.Velocity.X = -b.Velocity.X;
            if (p.Y < half.Y || p.Y > screen.Height - half.Y) b.Velocity.Y = -b.Velocity.Y;
            p = Vector2.Clamp(p, half, Vector2.Max(half, new Vector2(screen.Width, screen.Height) - half));
            t.Position = new Vector3(p, 0);
            t.Rotation = Quaternion.Normalize(t.Rotation * Quaternion.CreateFromAxisAngle(Vector3.UnitZ, b.Spin * time.FixedDelta));
        }

        [System(Phase.Update)]
        private static void OrbitCamera(ref LocalTransform t, in Orbit o, [Res] in Time time)
        {
            float a = (float)time.Elapsed * o.Speed;
            t.Position = new Vector3(MathF.Cos(a) * o.Radius, o.Height, MathF.Sin(a) * o.Radius);
            var forward = Vector3.Normalize(-t.Position);
            t.Rotation = LookRotation(forward, Vector3.UnitY);
        }

        [System(Phase.First)]
        private static void QuitKeys([Res] in Input input, [Res] in Time time, [Res] in QuitAfter quit, [Res] ref AppControl app)
        {
            if (input.WasPressed(Key.Escape) || (quit.Seconds > 0 && time.Elapsed >= quit.Seconds)) app.QuitRequested = true;
        }

        /// <summary>A rotation whose −Z axis points along <paramref name="forward"/> (right-handed, cameras look down −Z).</summary>
        private static Quaternion LookRotation(Vector3 forward, Vector3 up)
        {
            Matrix4x4.Invert(Matrix4x4.CreateLookAt(Vector3.Zero, forward, up), out var world);
            return Quaternion.CreateFromRotationMatrix(world);
        }
    }

    public sealed class SpinnersModule(double? runFor) : IModule
    {
        public void Build(EngineBuilder builder)
        {
            Gamegrl.Generated.SpinnersSystems.AddTo(builder.Schedule);
            builder.SetResource(new QuitAfter { Seconds = runFor ?? 0 });
            builder.OnStart(Populate);
        }

        private static void Populate(App app)
        {
            var world = app.World;
            var ctx = world.Resource<RenderContext>();
            var rng = new Random(1234);

            // Camera.
            world.Spawn(Camera.Perspective(60), LocalTransform.Identity, new LocalToWorld(), new Orbit { Radius = 34, Height = 14, Speed = 0.15f });

            // A 24 × 24 field of cubes (576 instances, one draw call).
            var (vertices, indices) = Primitives.Cube();
            var cube = ctx.CreateMesh(vertices, indices);
            const int n = 24;
            for (int x = 0; x < n; x++)
            for (int z = 0; z < n; z++)
            {
                var pos = new Vector3((x - n / 2f + 0.5f) * 1.6f, MathF.Sin(x * 0.5f) * MathF.Cos(z * 0.5f) * 1.5f, (z - n / 2f + 0.5f) * 1.6f);
                var color = new Vector4(0.35f + 0.65f * x / n, 0.45f, 0.35f + 0.65f * z / n, 1);
                var axis = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, 1, rng.NextSingle() - 0.5f));
                world.Spawn(
                    LocalTransform.At(pos, 0.9f),
                    new LocalToWorld(),
                    new MeshRenderer { Mesh = cube, Color = color },
                    new Spinner { Axis = axis, DegreesPerSecond = 30 + rng.NextSingle() * 150 });
            }

            // Sprites: a generated soft ball texture.
            var ball = ctx.CreateTexture(64, 64, BallTexture(64));
            var screen = world.Resource<ScreenSize>();
            for (int i = 0; i < 40; i++)
            {
                float size = 24 + rng.NextSingle() * 40;
                var tint = new Vector4(0.6f + rng.NextSingle() * 0.4f, 0.5f + rng.NextSingle() * 0.5f, 0.3f + rng.NextSingle() * 0.7f, 0.9f);
                var e = world.Spawn(
                    LocalTransform.At(new Vector3(rng.NextSingle() * screen.Width, rng.NextSingle() * screen.Height, 0)),
                    Sprite.Of(ball, new Vector2(size)).Tinted(tint),
                    new Bouncer { Velocity = new Vector2(rng.NextSingle() - 0.5f, rng.NextSingle() - 0.5f) * 500, Spin = rng.NextSingle() * 4 - 2 });
                _ = e;
            }
        }

        /// <summary>A white disc with a soft edge and a highlight, RGBA8.</summary>
        private static byte[] BallTexture(int size)
        {
            var px = new byte[size * size * 4];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2 - 1, dy = (y + 0.5f) / size * 2 - 1;
                float d = MathF.Sqrt(dx * dx + dy * dy);
                float alpha = Math.Clamp((1 - d) * size * 0.5f, 0, 1);
                float shade = 0.75f + 0.25f * Math.Clamp(1 - MathF.Sqrt((dx + 0.35f) * (dx + 0.35f) + (dy + 0.35f) * (dy + 0.35f)), 0, 1);
                int o = (y * size + x) * 4;
                byte c = (byte)(255 * shade);
                px[o] = c; px[o + 1] = c; px[o + 2] = c; px[o + 3] = (byte)(255 * alpha);
            }
            return px;
        }
    }
}
