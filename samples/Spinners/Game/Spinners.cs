using System.Numerics;
using Gamegrl;
using Gamegrl.Assets;
using Gamegrl.Physics2D;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;
using Gamegrl.UI;

namespace Spinners;

public struct Spinner : IComponent
{
    [Header("Rotation"), Tooltip("Local axis around which this entity rotates.")]
    public Vector3 Axis;
    [Range(0, 720), Tooltip("Angular speed in degrees per second.")]
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

/// <summary>Live tuning, edited from the debug window.</summary>
public struct SpinTuning
{
    public float Speed;
    public bool Frozen;
}

public struct QuitAfter
{
    public double Seconds;
}

/// <summary>How long a raining box has existed, in fixed ticks; boxes go after four seconds, so the pile stays a steady size.</summary>
public struct BoxAge : IComponent
{
    public int Ticks;
}

/// <summary>The box rain: a seeded generator (simulation state, so it snapshots and replays) and a tick counter.</summary>
public struct BoxRain
{
    public SimRandom Random;
    public int Ticks;
}

public static partial class Gameplay
{
    [System(Phase.Update, Parallel = true)]
    private static void Spin(ref LocalTransform t, in Spinner s, [Res] in Time time, [Res] in SpinTuning tuning)
    {
        if (tuning.Frozen) return;
        float angle = float.DegreesToRadians(s.DegreesPerSecond * tuning.Speed) * time.Delta;
        t.Rotation = Quaternion.Normalize(t.Rotation * Quaternion.CreateFromAxisAngle(s.Axis, angle));
    }

    /// <summary>A debug window drawn with the engine's pixel UI; it shows in the player and in the editor's Game view.</summary>
    [System(Phase.Update)]
    private static void DebugWindow([Res] in Ui ui, [Res] in Time time, [Res] ref SpinTuning tuning)
    {
        if (ui.BeginWindow("Spinners", new Rect(12, 12, 230, 120)))
        {
            ui.Label($"{1 / MathF.Max(time.Delta, 1e-6f):0} fps · {time.Delta * 1000:0.0} ms", dim: true);
            ui.Slider(ui.Property("Speed"), "speed", ref tuning.Speed, 0, 4);
            ui.Checkbox("Frozen", ref tuning.Frozen);
        }
        ui.EndWindow();
    }

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
        Matrix4x4.Invert(Matrix4x4.CreateLookAt(t.Position, new Vector3(0, 2, 0), Vector3.UnitY), out var world);
        t.Rotation = Quaternion.CreateFromRotationMatrix(world);
    }

    /// <summary>Drops a box onto the shelf every quarter second (a fixed-tick system: seeded random, no clocks).</summary>
    [System(Phase.FixedUpdate)]
    private static void RainBoxes([Res] ref BoxRain rain, CommandBuffer commands)
    {
        if (rain.Ticks++ % 15 != 0) return;
        float size = rain.Random.Range(0.35f, 0.8f);
        var e = commands.Spawn();
        commands.Add(e, new LocalTransform
        {
            Position = new Vector3(rain.Random.Range(-3.5f, 3.5f), 14, -2),
            Rotation = Rot.FromAngle(rain.Random.Range(-SimMath.Pi, SimMath.Pi)).ToQuaternion(),
            Scale = new Vector3(size),
        });
        commands.Add(e, new LocalToWorld());
        commands.Add(e, new InterpolatedTransform());
        commands.Add(e, new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = new Vector4(1, rain.Random.Range(0.35f, 0.75f), rain.Random.Range(0.4f, 0.8f), 1) });
        commands.Add(e, RigidBody2D.Default with { AngularVelocity = rain.Random.Range(-3f, 3f) });
        commands.Add(e, Collider2D.Box(new Vector2(size)) with { Restitution = 0.1f });
        commands.Add(e, new BoxAge());
    }

    /// <summary>Boxes go after four seconds, or once they've fallen below the field.</summary>
    [System(Phase.FixedUpdate)]
    private static void FallOut(Entity e, in LocalTransform t, ref BoxAge age, CommandBuffer commands)
    {
        if (++age.Ticks > 240 || t.Position.Y < -8) commands.Destroy(e);
    }

    [System(Phase.First)]
    private static void QuitKeys([Res] in Input input, [Res] in Time time, [Res] in QuitAfter quit, [Res] ref AppControl app)
    {
        if (input.WasPressed(Key.Escape) || (quit.Seconds > 0 && time.Elapsed >= quit.Seconds)) app.QuitRequested = true;
    }
}

/// <summary>Game systems and play-mode startup; the host chooses the startup scene.</summary>
public sealed class SpinnersModule : IModule
{
    public SpinnersModule() { }
    public SpinnersModule(double? runFor) => _runFor = runFor;
    private readonly double? _runFor;
    public const string MainScene = "Assets/main.scene";

    public void Build(EngineBuilder builder)
    {
        builder.AddModule(new UiModule());
        builder.AddModule(new Physics2DModule());
        builder.SetResource(new BoxRain { Random = new SimRandom(7) });
        Gamegrl.Generated.SpinnersGameSystems.AddTo(builder.Schedule);
        builder.SetResource(new SpinTuning { Speed = 1 });
        builder.SetResource(new QuitAfter { Seconds = _runFor ?? 0 });
        builder.OnStart(Start);
    }

    private static void Start(App app)
    {
        var world = app.World;
        // A 24 × 24 field of cubes (576 instances, one draw call).
        var rng = new Random(1234);
        const int n = 24;
        for (int x = 0; x < n; x++)
        for (int z = 0; z < n; z++)
        {
            var pos = new Vector3((x - n / 2f + 0.5f) * 1.6f, MathF.Sin(x * 0.5f) * MathF.Cos(z * 0.5f) * 1.5f - 3, (z - n / 2f + 0.5f) * 1.6f);
            var color = new Vector4(0.35f + 0.65f * x / n, 0.45f, 0.35f + 0.65f * z / n, 1);
            var axis = Vector3.Normalize(new Vector3(rng.NextSingle() - 0.5f, 1, rng.NextSingle() - 0.5f));
            world.Spawn(
                LocalTransform.At(pos, 0.9f),
                new LocalToWorld(),
                new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = color },
                new Spinner { Axis = axis, DegreesPerSecond = 30 + rng.NextSingle() * 150 });
        }
        // The shelf the box rain lands on (2D physics in the x–y plane, in front of the field).
        world.Spawn(
            new LocalTransform { Position = new Vector3(0, 4, -2), Rotation = Quaternion.Identity, Scale = new Vector3(8, 0.4f, 1.5f) },
            new LocalToWorld(),
            new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = new Vector4(0.3f, 0.3f, 0.36f, 1) },
            RigidBody2D.Static,
            Collider2D.Box(new Vector2(8, 0.4f)));
    }
}

/// <summary>Composes the app; the player and the dev tool differ only in the asset source.</summary>
public static class SpinnersApp
{
    public static App Build(IAssetSource assets, double? runFor) =>
        EngineBuilder.Create()
            .AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · spinners")))
            .AddModule(new AssetsModule(assets))
            .AddModule<VulkanModule>()
            .AddModule(new RenderingModule(vsync: true))
            .AddModule(new SceneBootModule(SpinnersModule.MainScene))
            .AddModule(new SpinnersModule(runFor))
            .Build();

    public static void Run(App app)
    {
        var device = app.World.Resource<Gamegrl.Rhi.GpuDevice>();
        Console.WriteLine($"gamegrl: {device.BackendName} on {device.AdapterName}, {app.Jobs.WorkerCount} worker threads");
        DesktopHost.Run(app);
        var t = app.World.Resource<Time>();
        Console.WriteLine($"gamegrl: {t.Frame} frames in {t.Elapsed:F1} s ({t.Frame / Math.Max(t.Elapsed, 1e-9):F0} fps), {t.Tick} fixed ticks");
    }

    public static double? ParseRunFor(string[] args)
    {
        int i = Array.IndexOf(args, "--seconds");
        return i >= 0 && i + 1 < args.Length ? double.Parse(args[i + 1], System.Globalization.CultureInfo.InvariantCulture) : null;
    }
}
