using System.Text.Json;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>The profiler: zones, per-system timing, the frame history, worker threads, allocation figures, export.</summary>
internal static class ProfilerTests
{
    private static readonly ProfileMarker Outer = ProfileMarker.Get("Test.Outer");
    private static readonly ProfileMarker Inner = ProfileMarker.Get("Test.Inner");

    private static void Profiled(Action body)
    {
        Profiler.Clear();
        Profiler.Enabled = true;
        try { body(); }
        finally { Profiler.Enabled = false; Profiler.Paused = false; Profiler.Clear(); }
    }

    private static int Find(ProfileTree tree, string path)
    {
        for (int i = 0; i < tree.Nodes.Length; i++) if (tree.Nodes[i].Marker >= 0 && tree.Path(i) == path) return i;
        throw new Exception($"no node {path}; have: {string.Join(", ", Enumerable.Range(0, tree.Nodes.Length).Where(i => tree.Nodes[i].Marker >= 0).Select(tree.Path))}");
    }

    public static void Register()
    {
        Test("profiler: systems and nested zones form a per-frame call tree under their phase", () => Profiled(() =>
        {
            var builder = EngineBuilder.Create();
            builder.WorkerThreads = 0;
            builder.AddSystem(new Probe("Test.Probe", _ =>
            {
                using (Outer.Scope())
                {
                    using (Inner.Scope()) Thread.SpinWait(2000);
                    using (Inner.Scope()) Thread.SpinWait(2000);
                }
            }));
            using var app = builder.Build();
            for (int i = 0; i < 5; i++) app.Tick(1 / 60.0);
            Assert.Equal(5L, Profiler.FrameCount);
            var tree = new ProfileTree();
            tree.Build(1, 4);
            Assert.Equal(4, tree.Frames);
            int probe = Find(tree, "Phase.Update/Test.Probe"), outer = Find(tree, "Phase.Update/Test.Probe/Test.Outer"), inner = Find(tree, "Phase.Update/Test.Probe/Test.Outer/Test.Inner");
            Assert.Equal(4L, tree[outer].Calls);
            Assert.Equal(8L, tree[inner].Calls);
            Assert.True(tree[probe].Ticks >= tree[outer].Ticks && tree[outer].Ticks >= tree[inner].Ticks && tree[inner].Ticks > 0, "a parent contains its children");
            Assert.True(tree[outer].SelfTicks == tree[outer].Ticks - tree[inner].Ticks);
            Assert.True(tree.Stats.MeanMs > 0 && tree.Stats.MaxMs >= tree.Stats.MedianMs && tree.Stats.TickMeanMs <= tree.Stats.MeanMs + 1e-9);
            Assert.Equal(ProfileCategory.Game, Profiler.Marker(tree[outer].Marker).Category);
        }));

        Test("profiler: worker threads' zones are drained into the frame and gathered under Workers", () => Profiled(() =>
        {
            var builder = EngineBuilder.Create();
            builder.WorkerThreads = 2;
            builder.AddSystem(new Probe("Test.Parallel", world =>
            {
                var job = new SpinJob();
                world.Jobs!.ParallelFor(64, 1, ref job);
            }));
            using var app = builder.Build();
            for (int i = 0; i < 4; i++) app.Tick(1 / 60.0);
            var tree = new ProfileTree();
            tree.Build(0, 4);
            Find(tree, "Phase.Update/Test.Parallel/Jobs.ParallelFor");
            int batches = Find(tree, "Workers/Jobs.Batches");
            Assert.True(tree[batches].Calls >= 1 && tree[batches].Ticks > 0);
        }));

        Test("profiler: a profiled frame allocates nothing, and allocating frames are reported", () => Profiled(() =>
        {
            var builder = EngineBuilder.Create();
            builder.WorkerThreads = 0;
            bool allocate = false;
            object? sink = null;
            builder.AddSystem(new Probe("Test.Alloc", _ =>
            {
                using (Outer.Scope()) if (allocate) sink = new byte[4096];
            }));
            using var app = builder.Build();
            var tree = new ProfileTree();
            for (int i = 0; i < 8; i++) app.Tick(1 / 60.0);
            tree.Build(0, 8);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 64; i++) app.Tick(1 / 60.0);
            tree.Build(Profiler.FrameCount - 64, 64);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated == 0, $"64 profiled frames and a tree build allocated {allocated} bytes");
            Assert.True(tree.Stats.AllocatedPerFrame == 0, $"reported {tree.Stats.AllocatedPerFrame} bytes per frame");
            allocate = true;
            for (int i = 0; i < 4; i++) app.Tick(1 / 60.0);
            tree.Build(Profiler.FrameCount - 4, 4);
            Assert.True(tree.Stats.AllocatedPerFrame >= 4096, $"a 4 KB allocation per frame reported as {tree.Stats.AllocatedPerFrame}");
            GC.KeepAlive(sink);
        }));

        Test("profiler: off records nothing, paused keeps the history, export is a valid Chrome trace", () =>
        {
            Profiler.Clear();
            var builder = EngineBuilder.Create();
            builder.WorkerThreads = 0;
            builder.AddSystem(new Probe("Test.Export", _ => { using (Outer.Scope()) Thread.SpinWait(100); }));
            using var app = builder.Build();
            app.Tick(1 / 60.0);
            Assert.Equal(0L, Profiler.FrameCount);
            Profiled(() =>
            {
                for (int i = 0; i < 3; i++) app.Tick(1 / 60.0);
                Profiler.Paused = true;
                app.Tick(1 / 60.0);
                Assert.Equal(3L, Profiler.FrameCount);
                var text = new StringWriter();
                ProfileTree.WriteChromeTrace(text, 0, 3);
                using var json = JsonDocument.Parse(text.ToString());
                var names = json.RootElement.GetProperty("traceEvents").EnumerateArray()
                    .Where(e => e.GetProperty("ph").GetString() == "X").Select(e => e.GetProperty("name").GetString()).ToHashSet();
                Assert.True(names.Contains("Test.Outer") && names.Contains("Test.Export") && names.Contains("Phase.Update"), "zones exported");
            });
        });
    }

    private struct SpinJob : IJobFor
    {
        public void Execute(int start, int end) { for (int i = start; i < end; i++) Thread.SpinWait(500); }
    }

    private sealed class Probe(string name, Action<World> run) : ISystem
    {
        public string Name => name;
        public Phase Phase => Phase.Update;
        public int Order => 0;
        public void Initialize(World world) { }
        public void Run(World world) => run(world);
    }
}
