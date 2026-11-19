using Gamegrl;
using Gamegrl.Bench;

// Engine performance scenarios under the profiler. Usage:
//   Gamegrl.Bench [scenario-prefix...] [--quick] [--runs N --frames N --warmup N] [--tree] [--update] [--tolerance 0.25] [--no-gpu]
// Compares with perf/baselines/<machine>.json (the aot or jit section) and exits non-zero on a regression.
var suite = new BenchSuite();
var filters = suite.Configure(args);
bool gpu = !args.Contains("--no-gpu");
Console.WriteLine($"gamegrl bench · {BenchSuite.Machine} · {BenchSuite.Mode} · {suite.Runs}×{suite.Frames} frames after {suite.Warmup} warm-up");
(string Name, Func<App> Build, bool Gpu)[] scenarios =
[
    ("ecs.iterate", Scenarios.Iterate, false),
    ("ecs.hierarchy", Scenarios.Hierarchy, false),
    ("ecs.structural", Scenarios.Structural, false),
    ("ui.frame", Scenarios.Interface, false),
    ("render.cubes", Scenarios.Render, true),
];
foreach (var (name, build, needsGpu) in scenarios)
{
    if (!BenchSuite.Selected(filters, name)) continue;
    if (needsGpu && !gpu) { Console.WriteLine($"  {name,-22} skipped (--no-gpu)"); continue; }
    App app;
    try { app = build(); }
    catch (Exception e) when (needsGpu) { Console.WriteLine($"  {name,-22} skipped: {e.Message}"); continue; }
    using (app) suite.Measure(name, app);
}
string path = BenchSuite.BaselinePath(BenchSuite.RepoRoot());
if (args.Contains("--update")) { suite.Update(path); return 0; }
int failures = suite.Compare(path);
Console.WriteLine(failures == 0 ? "bench ok" : $"bench: {failures} regression(s)");
return failures == 0 ? 0 : 1;
