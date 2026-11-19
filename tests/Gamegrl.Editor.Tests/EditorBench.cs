using AnsiDotNet;
using Gamegrl.Assets;
using Gamegrl.Bench;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.Platform.Sdl3;
using Gamegrl.Rendering;
using Gamegrl.Rhi.Vulkan;

namespace Gamegrl.Tests;

/// <summary>
/// The editor's own frame cost, with the Spinners sample loaded: idle on the Scene view, playing, and playing with the
/// Profiler panel open. The terminal host (headless Vulkan) by default, so it runs anywhere CI does; --desktop uses an
/// SDL window instead (without vsync numbers mean little: compare "work").
/// Usage: Gamegrl.Editor.Tests --bench [scenario-prefix...] [--desktop] [--quick] [--tree] [--update]
/// </summary>
internal static class EditorBench
{
    public static int Run(string[] args)
    {
        var suite = new BenchSuite();
        var filters = suite.Configure(args);
        bool desktop = args.Contains("--desktop");
        string host = desktop ? "desktop" : "terminal";
        Console.WriteLine($"gamegrl editor bench · {BenchSuite.Machine} · {BenchSuite.Mode} · {host} · {suite.Runs}×{suite.Frames} frames after {suite.Warmup} warm-up");
        string repo = BenchSuite.RepoRoot();
        string source = Path.Combine(repo, "samples/Spinners");
        string root = Path.Combine(Path.GetTempPath(), "gamegrl-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        foreach (string file in Directory.EnumerateFiles(Path.Combine(source, "Assets"))) File.Copy(file, Path.Combine(root, "Assets", Path.GetFileName(file)));
        Profiler.Enabled = true;
        try
        {
            var assets = new EditorAssets();
            using var backend = desktop ? null : new NullBackend();
            using var terminal = backend is not null ? Terminal.Open(backend, new TerminalOptions { Mouse = MouseMode.AnyMotion }) : null;
            using var display = terminal is not null ? new TerminalDisplay(terminal) : null;
            var builder = EngineBuilder.Create();
            if (terminal is not null)
            {
                var platform = new TerminalPlatform(terminal);
                builder.SetResource(new Input()).SetResource(new PlatformContext(platform, platform.CreateWindow(WindowDesc.Default("bench"))));
            }
            else builder.AddModule(new Sdl3PlatformModule(WindowDesc.Default("gamegrl · bench")));
            using var app = builder.AddModule(new AssetsModule(assets)).AddModule<VulkanModule>().AddModule(new RenderingModule(vsync: false)).Build();
            using var editor = new EditorHost(app, assets, root, Path.Combine(source, "Game/Spinners.Game.csproj"),
                desktop ? Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf" : null, display);
            var context = app.World.Resource<PlatformContext>();
            var input = app.World.Resource<Input>();
            void Frame() { input.BeginFrame(); context.Platform.PumpEvents(input); editor.Pump(); app.Tick(1 / 60.0); }
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (editor.Jobs.Busy || app.World.EntityCount == 0)
            {
                if (timer.Elapsed.TotalSeconds > 120) throw new TimeoutException("The editor didn't load the sample.");
                Frame();
            }
            string prefix = "editor." + host + ".";
            if (BenchSuite.Selected(filters, prefix + "idle")) suite.Measure(prefix + "idle", app, Frame);
            if (!editor.RunCommand("play.toggle") || !editor.Document.Playing) throw new Exception("Play didn't start.");
            if (BenchSuite.Selected(filters, prefix + "play")) suite.Measure(prefix + "play", app, Frame);
            editor.RunCommand("profiler.show");
            if (BenchSuite.Selected(filters, prefix + "profiler")) suite.Measure(prefix + "profiler", app, Frame);
            editor.StopPlay();
        }
        finally
        {
            Profiler.Enabled = false;
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
        string path = BenchSuite.BaselinePath(repo);
        if (args.Contains("--update")) { suite.Update(path); return 0; }
        int failures = suite.Compare(path);
        Console.WriteLine(failures == 0 ? "bench ok" : $"bench: {failures} regression(s)");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>A 120×40 terminal that discards output, so terminal writes don't distort the editor's numbers.</summary>
    private sealed class NullBackend : ITerminalBackend
    {
        public TerminalSize Size => new(120, 40);
        public void Start(ITerminalInput input) { }
        public void Write(ReadOnlySpan<byte> bytes) { }
        public void Dispose() { }
    }
}
