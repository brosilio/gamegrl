using System.Numerics;
using Gamegrl.Physics2D;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;
using Gamegrl.UI;

namespace Gamegrl.Bench;

public struct Velocity : IComponent { public Vector3 Value; }
public struct Spin : IComponent { public Vector3 Axis; public float Speed; }
public struct Lifetime : IComponent { public int Frames; }

/// <summary>The work the scenarios time. Generated like any game's systems; the bench's assembly profiles as Engine.</summary>
public static partial class BenchSystems
{
    [System(Phase.Update)]
    private static void Integrate(ref LocalTransform t, in Velocity v, [Res] in Time time) => t.Position += v.Value * time.Delta;

    [System(Phase.Update, Parallel = true)]
    private static void Rotate(ref LocalTransform t, in Spin s, [Res] in Time time) =>
        t.Rotation = Quaternion.Normalize(t.Rotation * Quaternion.CreateFromAxisAngle(s.Axis, s.Speed * time.Delta));

    [System(Phase.Update)]
    private static void Expire(Entity e, ref Lifetime life, CommandBuffer commands)
    {
        if (--life.Frames <= 0) commands.Destroy(e);
    }
}

/// <summary>Spawns a fixed number of short-lived entities per frame through the command buffer.</summary>
internal sealed class Spawner(int perFrame) : ISystem
{
    public string Name => "Bench.Spawner";
    public Phase Phase => Phase.Update;
    public int Order => -10;
    public void Initialize(World world) { }
    public void Run(World world)
    {
        var commands = world.Commands;
        for (int i = 0; i < perFrame; i++)
        {
            var e = commands.Spawn();
            commands.Add(e, LocalTransform.At(new Vector3(i, 0, 0)));
            commands.Add(e, new LocalToWorld());
            commands.Add(e, new Lifetime { Frames = 8 });
        }
    }
}

/// <summary>A debug-tool-sized pixel UI frame: three windows of widgets and a few hundred world labels.</summary>
internal sealed class UiLoad : ISystem
{
    private float _a = 1, _b = 2, _c = 3;
    private bool _flag;
    private Vector3 _v = new(1, 2, 3);
    private int _choice;
    private static readonly string[] Items = ["one", "two", "three"];
    public string Name => "Bench.UiLoad";
    public Phase Phase => Phase.Update;
    public int Order => 0;
    public void Initialize(World world) { }
    public void Run(World world)
    {
        var ui = world.Resource<Ui>();
        ulong frame = world.Resource<Time>().Frame;
        for (int w = 0; w < 3; w++)
        {
            ui.PushId(w);
            if (ui.BeginWindow(w == 0 ? "Stats" : w == 1 ? "Tuning" : "Entities", new Rect(10 + w * 270, 10, 260, 560)))
            {
                for (int i = 0; i < 8; i++)
                {
                    ui.PushId(i);
                    ui.Label($"row {i} · frame {frame} · {_a * i:0.00}", dim: i % 2 == 0);
                    ui.Slider(ui.Property("A"), "a", ref _a, 0, 4);
                    ui.DragFloat(ui.Property("B"), "b", ref _b);
                    ui.Checkbox("Flag", ref _flag);
                    ui.Vector3(ui.Property("V"), "v", ref _v);
                    if (i == 0) ui.Combo(ui.Property("Choice"), "choice", ref _choice, Items);
                    ui.ProgressBar(ui.Next(), (i + 1) / 8f, ui.Format($"{(i + 1) * 12.5f:0}%"));
                    ui.PopId();
                }
            }
            ui.EndWindow();
            ui.PopId();
        }
        for (int i = 0; i < 300; i++)
            ui.TextAt(new Vector2(40 + i % 30 * 40, 600 + i / 30 * 12), ui.Format($"e{i}"), ui.Theme.Accent, new Vector2(0.5f, 1));
        _c += 0.01f;
    }
}

/// <summary>A window that renders offscreen, so the GPU scenarios run without a display.</summary>
internal sealed class HeadlessPlatform(int width, int height) : IPlatform
{
    private readonly Window _window = new(width, height);
    public string Name => "Headless";
    public bool QuitRequested => false;
    public IWindow CreateWindow(in WindowDesc desc) => _window;
    public void PumpEvents(Input input) { }
    public void CancelQuitRequest() { }
    public string GetClipboardText() => "";
    public void SetClipboardText(string text) { }
    public void SetCursor(CursorShape shape) { }
    public void Dispose() { }

    private sealed class Window(int width, int height) : IOffscreenWindow
    {
        public (int Width, int Height) PixelSize => (width, height);
        public bool CloseRequested => false;
        public bool Minimized => false;
        public string[] VulkanInstanceExtensions => [];
        public void CancelCloseRequest() { }
        public ulong CreateVulkanSurface(nint instance) => throw new NotSupportedException();
        public void Dispose() { }
    }
}

public static class Scenarios
{
    public static App Build(Action<EngineBuilder> configure, int workers = -1)
    {
        var builder = EngineBuilder.Create().AddModule<CoreModule>();
        builder.WorkerThreads = workers;
        configure(builder);
        return builder.Build();
    }

    /// <summary>100k entities: a serial integrate and a parallel rotate over the same transforms, then propagation.</summary>
    public static App Iterate() => Build(b =>
    {
        Gamegrl.Generated.GamegrlBenchSystems.AddTo(b.Schedule);
        var rng = new Random(1);
        for (int i = 0; i < 100_000; i++)
            b.World.Spawn(LocalTransform.At(new Vector3(i % 100, 0, i / 100)), new LocalToWorld(),
                new Velocity { Value = new Vector3(rng.NextSingle(), 0, rng.NextSingle()) },
                new Spin { Axis = Vector3.UnitY, Speed = rng.NextSingle() * 3 });
    });

    /// <summary>A transform hierarchy: 10k spinning roots with four children each.</summary>
    public static App Hierarchy() => Build(b =>
    {
        Gamegrl.Generated.GamegrlBenchSystems.AddTo(b.Schedule);
        var world = b.World;
        for (int i = 0; i < 10_000; i++)
        {
            var root = world.Spawn(LocalTransform.At(new Vector3(i % 100, 0, i / 100)), new LocalToWorld(), new Spin { Axis = Vector3.UnitY, Speed = 1 });
            for (int c = 0; c < 4; c++)
            {
                var child = world.Spawn(LocalTransform.At(new Vector3(c, 1, 0), 0.5f), new LocalToWorld());
                world.SetParent(child, root);
            }
        }
    });

    /// <summary>Structural churn: 2k spawns and 2k despawns a frame through the command buffer.</summary>
    public static App Structural() => Build(b =>
    {
        Gamegrl.Generated.GamegrlBenchSystems.AddTo(b.Schedule);
        b.AddSystem(new Spawner(2_000));
    });

    /// <summary>The game UI, headless: layout, widgets, formatting and draw-list building.</summary>
    public static App Interface() => Build(b =>
    {
        b.SetResource(new ScreenSize { Width = 1280, Height = 720 });
        b.AddModule(new UiModule());
        b.AddSystem(new UiLoad());
    });

    private static void Body2D(World w, Vector2 p, Collider2D c, RigidBody2D body) =>
        w.Spawn(new LocalTransform { Position = new Vector3(p, 0), Rotation = Quaternion.Identity, Scale = Vector3.One }, new LocalToWorld(), body, c);

    /// <summary>A 30-row pyramid of boxes resting on the ground (465 bodies in contact): solver and contact cost.</summary>
    public static App Pyramid() => Build(b =>
    {
        b.AddModule(new Physics2DModule());
        Body2D(b.World, new Vector2(0, -0.5f), Collider2D.Box(new Vector2(200, 1)), RigidBody2D.Static);
        for (int row = 0; row < 30; row++)
            for (int i = 0; i < 30 - row; i++)
                Body2D(b.World, new Vector2((i - (30 - row) / 2f) * 1.05f, 0.5f + row), Collider2D.Box(Vector2.One), RigidBody2D.Default);
    }, workers: 0);

    /// <summary>2000 circles poured into a bin: broadphase, many contacts, bodies in motion.</summary>
    public static App Rain() => Build(b =>
    {
        b.AddModule(new Physics2DModule());
        Body2D(b.World, new Vector2(0, -0.5f), Collider2D.Box(new Vector2(40, 1)), RigidBody2D.Static);
        Body2D(b.World, new Vector2(-20.5f, 20), Collider2D.Box(new Vector2(1, 40)), RigidBody2D.Static);
        Body2D(b.World, new Vector2(20.5f, 20), Collider2D.Box(new Vector2(1, 40)), RigidBody2D.Static);
        var r = new SimRandom(3);
        for (int i = 0; i < 2000; i++)
            Body2D(b.World, new Vector2(r.Range(-19f, 19f), 2 + i * 0.05f), Collider2D.Circle(r.Range(0.2f, 0.4f)), RigidBody2D.Default);
    }, workers: 0);

    /// <summary>10k lit cubes drawn offscreen at 1280×720 through the renderer, with GPU timings.</summary>
    public static App Render() => Build(b =>
    {
        var platform = new HeadlessPlatform(1280, 720);
        b.SetResource(new Input()).SetResource(new PlatformContext(platform, platform.CreateWindow(WindowDesc.Default("bench"))));
        b.AddModule<VulkanModule>().AddModule(new RenderingModule(vsync: false));
        Gamegrl.Generated.GamegrlBenchSystems.AddTo(b.Schedule);
        var world = b.World;
        world.Spawn(LocalTransform.At(new Vector3(0, 40, -90)) with { Rotation = Quaternion.CreateFromYawPitchRoll(MathF.PI, -0.4f, 0) }, new LocalToWorld(), Camera.Default);
        for (int i = 0; i < 10_000; i++)
            world.Spawn(LocalTransform.At(new Vector3(i % 100 - 50, 0, i / 100 - 50), 0.8f), new LocalToWorld(),
                MeshRenderer.Default with { Color = new Vector4(0.4f + i % 7 * 0.08f, 0.5f, 0.9f - i % 5 * 0.1f, 1) },
                new Spin { Axis = Vector3.Normalize(new Vector3(1, 2, 0)), Speed = 1 + i % 3 });
    });
}
