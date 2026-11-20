using System.Diagnostics;
using System.Globalization;
using Gamegrl;
using Gamegrl.Physics2D;
using Gamegrl.PhysicsBench;

// Box2D v3's benchmark (benchmark/main.c) for gamegrl's Physics2D: the same scenes, step counts, time step, substeps
// and timing (the first step is left out; per-scene work in the step function is timed, as Box2D times it). Usage:
//   Gamegrl.PhysicsBench [-b=<name>] [-w=<workers>] [-t=<max threads>] [-r=<repeats>] [-l]
// Prints Box2D's lines ("run 0 : 123.4 (ms)") and a final "result <scene> <threads> <best ms>" per configuration.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
string? single = null;
int singleWorkers = -1, maxThreads = Math.Min(Environment.ProcessorCount, 32), repeats = 4;
bool profile = false;
foreach (var arg in args)
{
    string? Value(string shortName, string longName) =>
        arg.StartsWith(shortName, StringComparison.Ordinal) ? arg[shortName.Length..] : arg.StartsWith(longName, StringComparison.Ordinal) ? arg[longName.Length..] : null;
    if (Value("-b=", "--benchmark=") is { } b) single = b;
    else if (Value("-w=", "--workers=") is { } w) singleWorkers = int.Parse(w);
    else if (Value("-t=", "--threads=") is { } t) maxThreads = Math.Clamp(int.Parse(t), 1, maxThreads);
    else if (Value("-r=", "--repeats=") is { } r) repeats = Math.Clamp(int.Parse(r), 1, 1000);
    else if (arg is "-p" or "--profile") profile = true;
    else if (arg is "-l" or "--list") { foreach (var s in Scenes.All) Console.WriteLine(s.Name); return 0; }
    else if (arg is "-simd=4" or "--simd-width=4") { } // accepted for symmetry with Box2D's command line
    else Console.WriteLine($"Unknown option \"{arg}\"");
}
if (single is not null && Scenes.All.All(s => s.Name != single)) { Console.WriteLine($"No benchmark named \"{single}\""); return 1; }

Console.WriteLine("Starting gamegrl Physics2D benchmarks");
Console.WriteLine("======================================");
foreach (var scene in Scenes.All)
{
    if (single is not null && scene.Name != single) continue;
    Console.WriteLine($"benchmark: {scene.Name}, steps = {scene.Steps}");
    for (int threads = 1; threads <= maxThreads; threads++)
    {
        if (singleWorkers != -1 && singleWorkers != threads) continue;
        Console.WriteLine($"thread count: {threads}");
        double best = double.MaxValue;
        for (int run = 0; run < repeats; run++)
        {
            using var jobs = threads > 1 ? new JobScheduler(threads - 1) : null;
            var world = new PhysicsWorld2D { Jobs = jobs };
            world.Settings.Substeps = 4;
            var instance = scene.Make();
            instance.Create(world);
            const float timeStep = 1 / 60f;
            instance.Step(world, 0);
            world.Step(timeStep); // the first step can be expensive; Box2D leaves it out
            if (profile) { Profiler.Enabled = true; Profiler.Clear(); PhysicsWorld2D.StageTicks = new long[32]; }
            long start = Stopwatch.GetTimestamp();
            for (int step = 1; step < scene.Steps; step++)
            {
                if (profile) Profiler.BeginFrame();
                instance.Step(world, step);
                world.Step(timeStep);
                if (profile) Profiler.EndFrame();
            }
            double ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            Console.WriteLine($"run {run} : {ms:0.###} (ms)");
            if (profile) PrintProfile(scene.Steps - 1, world);
            best = Math.Min(best, ms);
        }
        Console.WriteLine($"result {scene.Name} {threads} {best:0.###}");
    }
}
return 0;

// Per-step milliseconds by profiler marker and solver stage (with -p; profiling costs a little time itself).
static void PrintProfile(int steps, PhysicsWorld2D world)
{
    var tree = new ProfileTree();
    tree.Build(Math.Max(0, Profiler.FrameCount - steps), steps);
    var line = "  markers:";
    for (int n = 0; n < tree.Nodes.Length; n++)
        if (tree.Nodes[n].Marker >= 0 && tree.UnderRoot(n, ProfileTree.Root)) line += $" {tree.Name(n).Replace("Physics2D.", "")} {tree.Ms(n):0.000}";
    Console.WriteLine(line);
    if (PhysicsWorld2D.StageTicks is { } ticks)
    {
        line = "  stages:";
        for (int k = 0; k < ticks.Length; k++) if (ticks[k] > 0) line += $" {PhysicsWorld2D.StageName(k)} {ticks[k] * 1000.0 / Stopwatch.Frequency / steps:0.000}";
        Console.WriteLine(line);
    }
    Console.WriteLine($"  bodies {world.BodyCount} awake {world.AwakeBodyCount} contacts {world.ContactTotal} awake {world.AwakeContactCount} touching {world.TouchingCount} colours {world.ColorsUsed} overflow {world.OverflowConstraints} islands {world.IslandCount} moved {world.MovedProxies}");
    Console.WriteLine("  " + world.TreeStats);
    Profiler.Enabled = false;
    PhysicsWorld2D.StageTicks = null;
}
