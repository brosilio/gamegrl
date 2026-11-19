using System.Globalization;
using System.Numerics;
using System.Text;
using Gamegrl.Platform;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

public struct Agent : IComponent
{
    public Vector2 Position, Velocity, Goal;
    public float Heading;
    public int Age, Lifetime;
    public SimRandom Random;
}

/// <summary>The determinism scenario's simulation: only <see cref="SimMath"/>, seeded random and fixed-tick time.</summary>
public static partial class SwarmSystems
{
    [System(Phase.FixedUpdate)]
    private static void Steer(ref Agent a, [Res] in Time time)
    {
        var toGoal = a.Goal - a.Position;
        float desired = SimMath.Atan2(toGoal.Y, toGoal.X);
        float turn = SimMath.Atan2(SimMath.Sin(desired - a.Heading), SimMath.Cos(desired - a.Heading));
        a.Heading += turn * 0.2f + a.Random.Range(-0.05f, 0.05f);
        var (s, c) = SimMath.SinCos(a.Heading);
        float speed = SimMath.Sqrt(SimMath.Length(toGoal) + 1) * SimMath.Exp(-0.002f * a.Age) + SimMath.Log(1 + a.Age) * 0.1f;
        a.Velocity = SimMath.Lerp(a.Velocity, new Vector2(c, s) * speed, 0.3f);
        a.Velocity *= SimMath.Pow(0.99f, 1 + (a.Age & 3));
    }

    [System(Phase.FixedUpdate, Order = 1, Parallel = true)]
    private static void Move(ref Agent a, [Res] in Time time) => a.Position += a.Velocity * time.FixedDelta;

    [System(Phase.FixedUpdate, Order = 2)]
    private static void Expire(Entity e, ref Agent a, CommandBuffer commands)
    {
        if (++a.Age >= a.Lifetime) commands.Destroy(e);
    }
}

/// <summary>Spawns agents from a seeded generator and steers every goal with the arrow keys and the mouse.</summary>
internal sealed class SwarmDriver : ISystem
{
    private SimRandom _random = new(42);
    private Vector2 _goal;
    private Query _agents = null!;
    public string Name => "Test.SwarmDriver";
    public Phase Phase => Phase.FixedUpdate;
    public int Order => -1;
    public void Initialize(World world) => _agents = world.Query(new QueryDesc().With<Agent>());

    public void Run(World world)
    {
        var input = world.Resource<Input>();
        if (input.IsDown(Key.Right)) _goal.X += 0.5f;
        if (input.IsDown(Key.Left)) _goal.X -= 0.5f;
        if (input.IsDown(Key.Up)) _goal.Y += 0.5f;
        if (input.IsDown(MouseButton.Left)) _goal = input.MousePosition * 0.1f;
        foreach (var a in _agents.Archetypes)
            foreach (ref var agent in a.GetSpan<Agent>()) agent.Goal = _goal;
        var commands = world.Commands;
        int spawn = 8 + _random.Range(0, 8);
        for (int i = 0; i < spawn; i++)
        {
            var e = commands.Spawn();
            commands.Add(e, new Agent
            {
                Position = new Vector2(_random.Range(-50f, 50f), _random.Range(-50f, 50f)),
                Heading = _random.Range(-SimMath.Pi, SimMath.Pi),
                Lifetime = _random.Range(120, 400),
                Random = new SimRandom(_random.NextUInt()),
            });
        }
    }
}

internal static class DeterminismTests
{
    private const int Frames = 360;
    // Frame times that exercise the fixed-step accumulator: several steps, zero steps, the step cap.
    private static readonly double[] Deltas = [1 / 60.0, 1 / 144.0, 1 / 30.0, 1 / 75.0, 0.1, 1 / 240.0, 1 / 59.0];

    private static App Build(out TickHashRecorder hashes, int workers = 3)
    {
        var b = EngineBuilder.Create();
        b.WorkerThreads = workers;
        b.SetResource(new Input());
        Gamegrl.Generated.GamegrlTestsSystems.AddTo(b.Schedule);
        b.SetResource(new Gravity()).SetResource(new Tally()); // the other generated test systems' resources
        b.AddSystem(new SwarmDriver());
        b.AddSystem(hashes = new TickHashRecorder());
        return b.Build();
    }

    /// <summary>Scripted input: held arrows, clicks and mouse moves at fixed frames.</summary>
    private static void Script(int frame, Input input)
    {
        if (frame == 20) input.OnKey((int)Key.Right, true, false);
        if (frame == 90) input.OnKey((int)Key.Right, false, false);
        if (frame == 60) input.OnKey((int)Key.Up, true, false);
        if (frame == 200) input.OnKey((int)Key.Up, false, false);
        if (frame % 50 == 7) input.OnMouseMove(new Vector2(frame * 3 % 640, frame * 7 % 360), new Vector2(3, 7));
        if (frame == 150) input.OnMouseButton(0, true);
        if (frame == 155) input.OnMouseButton(0, false);
        if (frame == 300) { input.OnText("hi"); input.OnKey((int)Key.Left, true, false); }
        if (frame == 330) input.OnKey((int)Key.Left, false, false);
    }

    private static List<ulong> Live(out InputRecording recording, int workers = 3)
    {
        using var app = Build(out var hashes, workers);
        var input = app.World.Resource<Input>();
        recording = new InputRecording();
        input.Recording = recording;
        for (int f = 0; f < Frames; f++)
        {
            double dt = Deltas[f % Deltas.Length];
            recording.BeginFrame(dt);
            input.BeginFrame();
            Script(f, input);
            app.Tick(dt);
        }
        return hashes.Hashes;
    }

    private static List<ulong> Replay(InputRecording recording)
    {
        using var app = Build(out var hashes);
        var input = app.World.Resource<Input>();
        for (int f = 0; f < recording.FrameCount; f++)
        {
            input.BeginFrame();
            recording.Apply(f, input);
            app.Tick(recording.Delta(f));
        }
        return hashes.Hashes;
    }

    private static int Ulps(float a, float b)
    {
        if (float.IsNaN(a) || float.IsNaN(b)) return float.IsNaN(a) && float.IsNaN(b) ? 0 : int.MaxValue;
        int ia = BitConverter.SingleToInt32Bits(a), ib = BitConverter.SingleToInt32Bits(b);
        if (ia < 0) ia = int.MinValue - ia;
        if (ib < 0) ib = int.MinValue - ib;
        return (int)Math.Min(int.MaxValue, Math.Abs((long)ia - ib));
    }

    /// <summary>Every strict function over a fixed set of inputs, hashed: identical on every platform or it's a bug.</summary>
    private static ulong MathDigest()
    {
        ulong h = 0;
        var r = new SimRandom(7);
        for (int i = 0; i < 20000; i++)
        {
            float x = r.Range(-1000f, 1000f), y = r.Range(-1000f, 1000f), u = r.Range(-1f, 1f), p = r.Range(0.001f, 100f);
            var (s, c) = SimMath.Strict.SinCos(x);
            h = WorldHash.Combine(h, (s, c, SimMath.Strict.Atan2(y, x), SimMath.Strict.Atan(u * 50), SimMath.Strict.Asin(u), SimMath.Strict.Acos(u)));
            h = WorldHash.Combine(h, (SimMath.Strict.Exp(u * 80), SimMath.Strict.Log(p), SimMath.Strict.Pow(p, u * 4)));
        }
        return h;
    }

    // ------------------------------------------------------------------ golden file

    private const string GoldenName = "determinism.golden";

    private static Dictionary<string, string> ReadGolden()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Data", GoldenName);
        var golden = new Dictionary<string, string>();
        if (!File.Exists(path)) return golden;
        foreach (string line in File.ReadAllLines(path))
        {
            if (line.StartsWith('#') || !line.Contains(' ')) continue;
            golden[line[..line.IndexOf(' ')]] = line[(line.IndexOf(' ') + 1)..];
        }
        return golden;
    }

    /// <summary>GAMEGRL_UPDATE_GOLDEN=1 rewrites the file in the source tree (after an intended simulation change).</summary>
    private static void CheckGolden(string key, string value)
    {
        if (Environment.GetEnvironmentVariable("GAMEGRL_UPDATE_GOLDEN") == "1")
        {
            string? dir = AppContext.BaseDirectory;
            while (dir is not null && !File.Exists(Path.Combine(dir, "gamegrl.slnx"))) dir = Path.GetDirectoryName(dir);
            if (dir is null) throw new Exception("GAMEGRL_UPDATE_GOLDEN needs to run inside the repository.");
            string path = Path.Combine(dir, "tests", "Gamegrl.Tests", "Data", GoldenName);
            var lines = File.Exists(path) ? File.ReadAllLines(path).Where(l => !l.StartsWith(key + " ", StringComparison.Ordinal)).ToList() : ["# CrossPlatform determinism: must match on every platform, runtime and instruction set. Regenerate with GAMEGRL_UPDATE_GOLDEN=1 only for an intended change."];
            lines.Add(key + " " + value);
            File.WriteAllLines(path, lines);
            return;
        }
        var golden = ReadGolden();
        if (!golden.TryGetValue(key, out var expected)) throw new Exception($"No golden value for {key}; run once with GAMEGRL_UPDATE_GOLDEN=1.");
        if (expected != value)
        {
            if (key == "ticks")
            {
                var e = expected.Split(','); var v = value.Split(',');
                int first = Enumerable.Range(0, Math.Min(e.Length, v.Length)).FirstOrDefault(i => e[i] != v[i], -1);
                throw new Exception($"Simulation diverged from the golden run at tick {(first < 0 ? Math.Min(e.Length, v.Length) : first)} ({e.Length} golden ticks, {v.Length} now).");
            }
            throw new Exception($"{key}: {value} differs from golden {expected}");
        }
    }

    public static void Register()
    {
        Test("determinism: this binary runs in CrossPlatform mode (set by <GamegrlDeterminism>)", () =>
            Assert.True(Determinism.CrossPlatform, "Gamegrl.Tests sets GamegrlDeterminism=CrossPlatform"));

        Test("math: strict transcendentals stay within 2 ulps of the true value and handle edge cases", () =>
        {
            var r = new SimRandom(1);
            int worst = 0; string worstAt = "";
            void Check(string name, float got, double truth)
            {
                int u = Ulps(got, (float)truth);
                if (u > worst) { worst = u; worstAt = $"{name} = {got} vs {(float)truth}"; }
            }
            for (int i = 0; i < 200000; i++)
            {
                float x = r.Range(-2000f, 2000f), y = r.Range(-2000f, 2000f), u = r.Range(-1f, 1f), p = MathF.Exp(r.Range(-60f, 60f));
                Check($"sin({x})", SimMath.Strict.Sin(x), Math.Sin(x));
                Check($"cos({x})", SimMath.Strict.Cos(x), Math.Cos(x));
                Check($"atan2({y},{x})", SimMath.Strict.Atan2(y, x), Math.Atan2(y, x));
                Check($"atan({x})", SimMath.Strict.Atan(x), Math.Atan(x));
                Check($"asin({u})", SimMath.Strict.Asin(u), Math.Asin(u));
                Check($"acos({u})", SimMath.Strict.Acos(u), Math.Acos(u));
                Check($"exp({u * 85})", SimMath.Strict.Exp(u * 85), Math.Exp(u * 85.0f));
                Check($"log({p})", SimMath.Strict.Log(p), Math.Log(p));
                Check($"pow({p},{u * 2})", SimMath.Strict.Pow(p, u * 2), Math.Pow(p, u * 2));
            }
            Assert.True(worst <= 2, $"worst error {worst} ulps: {worstAt}");
            Assert.True(float.IsNaN(SimMath.Strict.Sin(float.PositiveInfinity)) && float.IsNaN(SimMath.Strict.Log(-1)) && SimMath.Strict.Log(0) == float.NegativeInfinity);
            Assert.True(SimMath.Strict.Exp(200) == float.PositiveInfinity && SimMath.Strict.Exp(-200) == 0 && SimMath.Strict.Pow(-2, 3) == -8 && float.IsNaN(SimMath.Strict.Pow(-2, 0.5f)));
            Assert.True(SimMath.Strict.Atan2(0, -1) == MathF.PI && SimMath.Strict.Atan2(1, 0) == MathF.PI / 2 && SimMath.Strict.Sin(0) == 0 && SimMath.Strict.Cos(0) == 1);
        });

        Test("math: strict results match the golden digest bit for bit; SimRandom matches its golden sequence", () =>
        {
            CheckGolden("math", MathDigest().ToString("x16", CultureInfo.InvariantCulture));
            var r = new SimRandom(12345);
            var seq = new StringBuilder();
            for (int i = 0; i < 8; i++) seq.Append(r.NextUInt().ToString("x8", CultureInfo.InvariantCulture)).Append(i < 7 ? "," : "");
            CheckGolden("random", seq.ToString());
            var b = new SimRandom(3);
            for (int i = 0; i < 10000; i++) { int v = b.Range(-3, 4); Assert.True(v >= -3 && v < 4); float f = b.NextFloat(); Assert.True(f >= 0 && f < 1); }
        });

        Test("determinism: a recorded session replays bit for bit and matches the golden per-tick world hashes", () =>
        {
            var live = Live(out var recording);
            Assert.True(live.Count > Frames, $"{live.Count} fixed ticks");
            var buffer = new MemoryStream();
            recording.Save(buffer);
            buffer.Position = 0;
            var replayed = Replay(InputRecording.Load(buffer));
            Assert.Equal(live.Count, replayed.Count);
            for (int i = 0; i < live.Count; i++)
                if (live[i] != replayed[i]) throw new Exception($"replay diverged at tick {i}");
            var serial = Live(out _, workers: 0);
            Assert.True(serial.SequenceEqual(live), "a run without worker threads agrees with one with three (parallel systems don't depend on thread count)");
            CheckGolden("ticks", string.Join(',', live.Select(h => h.ToString("x16", CultureInfo.InvariantCulture))));
        });
    }
}
