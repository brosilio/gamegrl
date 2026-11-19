using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Gamegrl.Bench;

/// <summary>One scenario's figures: the median of each metric over the runs.</summary>
public sealed class BenchResult(string name)
{
    public string Name { get; } = name;
    public double MedianMs, P95Ms, MaxMs, WorkMs, GpuMs, AllocatedPerFrame;
    public double Gen0PerKFrames;
    public readonly Dictionary<string, double> Zones = new(StringComparer.Ordinal);
    public string Tree = "";
}

/// <summary>
/// Runs scenarios under the profiler and compares them with a stored per-machine baseline. Allocation and GC figures
/// are exact and gate strictly; times gate on a relative tolerance plus a small absolute floor, since a busy machine
/// drifts by a few percent. Zone times are reported as warnings: they explain a regression, the frame totals catch it.
/// </summary>
public sealed class BenchSuite
{
    public int Runs = 3, Warmup = 120, Frames = 300;
    public double Tolerance = 0.25, P95Tolerance = 0.5;
    public const double FloorMs = 0.05;
    public bool PrintTrees;
    public readonly List<BenchResult> Results = new();

    /// <summary>
    /// "aot" or "jit": code generation differs enough that they're baselined separately. (PublishAot projects report no
    /// dynamic code even under the JIT, so ask whether anything was JIT-compiled instead.)
    /// </summary>
    public static string Mode => System.Runtime.JitInfo.GetCompiledMethodCount() > 0 ? "jit" : "aot";

    public static string Machine
    {
        get
        {
            string cpu = "cpu";
            try
            {
                foreach (string line in File.ReadLines("/proc/cpuinfo"))
                    if (line.StartsWith("model name", StringComparison.Ordinal)) { cpu = line[(line.IndexOf(':') + 1)..].Trim(); break; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var slug = new StringBuilder();
            foreach (char c in cpu.ToLowerInvariant()) slug.Append(char.IsAsciiLetterOrDigit(c) ? c : '-');
            string s = slug.ToString();
            while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
            return s.Trim('-') + "-" + Environment.ProcessorCount + "t";
        }
    }

    /// <summary>
    /// Tick <paramref name="app"/> through warm-up and measured frames <see cref="Runs"/> times. <paramref name="frame"/>
    /// replaces the plain fixed-step tick when a scenario drives its own host loop.
    /// </summary>
    public BenchResult Measure(string name, App app, Action? frame = null)
    {
        frame ??= () => app.Tick(1 / 60.0);
        var runs = new List<BenchResult>();
        var tree = new ProfileTree();
        bool wasEnabled = Profiler.Enabled;
        Profiler.Enabled = true;
        try
        {
            for (int run = 0; run < Runs; run++)
            {
                for (int i = 0; i < Warmup; i++) frame();
                Profiler.Clear();
                for (int i = 0; i < Frames; i++) frame();
                tree.Build(0, Frames);
                var r = new BenchResult(name)
                {
                    MedianMs = tree.Stats.MedianMs, P95Ms = tree.Stats.P95Ms, MaxMs = tree.Stats.MaxMs,
                    WorkMs = tree.Stats.MedianMs - tree.CategoryMs(ProfileCategory.Wait),
                    GpuMs = tree.Stats.GpuMeanMs, AllocatedPerFrame = tree.Stats.AllocatedPerFrame,
                    Gen0PerKFrames = tree.Stats.Gen0 * 1000.0 / Frames,
                };
                for (int i = 0; i < tree.Nodes.Length; i++)
                    if (tree.Nodes[i].Marker >= 0) r.Zones[tree.Path(i)] = tree.Ms(i);
                if (run == Runs - 1) r.Tree = TreeText(tree);
                runs.Add(r);
            }
        }
        finally { Profiler.Enabled = wasEnabled; Profiler.Clear(); }
        var result = new BenchResult(name)
        {
            MedianMs = Median(runs, r => r.MedianMs), P95Ms = Median(runs, r => r.P95Ms), MaxMs = Median(runs, r => r.MaxMs),
            WorkMs = Median(runs, r => r.WorkMs), GpuMs = Median(runs, r => r.GpuMs),
            AllocatedPerFrame = Median(runs, r => r.AllocatedPerFrame), Gen0PerKFrames = Median(runs, r => r.Gen0PerKFrames),
            Tree = runs[^1].Tree,
        };
        foreach (string zone in runs[^1].Zones.Keys) result.Zones[zone] = Median(runs, r => r.Zones.GetValueOrDefault(zone));
        Results.Add(result);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  {name,-22} median {result.MedianMs,7:0.000} ms  p95 {result.P95Ms,7:0.000}  work {result.WorkMs,7:0.000}  gpu {result.GpuMs,6:0.000}  alloc {result.AllocatedPerFrame,6:0} B/f  gen0 {result.Gen0PerKFrames:0.#}/1k"));
        if (PrintTrees) Console.WriteLine(result.Tree);
        return result;
    }

    private static double Median(List<BenchResult> runs, Func<BenchResult, double> metric)
    {
        var values = runs.Select(metric).Order().ToArray();
        return values[values.Length / 2];
    }

    private static string TreeText(ProfileTree tree)
    {
        var sb = new StringBuilder();
        void Walk(int node, int depth)
        {
            ref readonly var n = ref tree[node];
            if (node > tree.Gpu && tree.Ms(node) < 0.001) return;
            if (node <= tree.Gpu && n.FirstChild < 0) return;
            double alloc = tree.AllocatedPerFrame(node);
            sb.Append(CultureInfo.InvariantCulture, $"    {new string(' ', depth * 2)}{tree.Name(node),-40} {tree.Ms(node),8:0.000} ms  self {tree.SelfMs(node),8:0.000}{(alloc > 0 ? $"  alloc {alloc:0} B/f" : "")}\n");
            for (int c = n.FirstChild; c >= 0; c = tree[c].NextSibling) Walk(c, depth + 1);
        }
        Walk(ProfileTree.Root, 0); Walk(tree.Workers, 0); Walk(tree.Gpu, 0);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ baselines

    public static string BaselinePath(string repo) => Path.Combine(repo, "perf", "baselines", Machine + ".json");

    /// <summary>Compare with the baseline; returns the number of failures. Missing scenarios or files only warn.</summary>
    public int Compare(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"no baseline for this machine ({Path.GetFileName(path)}); run with --update to record one");
            return 0;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty(Mode, out var scenarios)) { Console.WriteLine($"baseline has no {Mode} section; run with --update"); return 0; }
        int failures = 0;
        foreach (var r in Results)
        {
            if (!scenarios.TryGetProperty(r.Name, out var b)) { Console.WriteLine($"  {r.Name}: not in the baseline"); continue; }
            void Time(string metric, double current, double tolerance)
            {
                if (!b.TryGetProperty(metric, out var v)) return;
                double baseline = v.GetDouble(), limit = baseline * (1 + tolerance) + FloorMs;
                if (current > limit)
                {
                    failures++;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  FAIL {r.Name} {metric}: {current:0.000} ms vs baseline {baseline:0.000} (limit {limit:0.000})"));
                }
            }
            void Exact(string metric, double current, double slack)
            {
                if (!b.TryGetProperty(metric, out var v)) return;
                double baseline = v.GetDouble();
                if (current > baseline + slack)
                {
                    failures++;
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  FAIL {r.Name} {metric}: {current:0.##} vs baseline {baseline:0.##}"));
                }
            }
            Time("median_ms", r.MedianMs, Tolerance);
            Time("work_ms", r.WorkMs, Tolerance);
            Time("p95_ms", r.P95Ms, P95Tolerance);
            Time("gpu_ms", r.GpuMs, Tolerance);
            // A few bytes of slack: the coarse all-thread counter isn't used, but a GC can land in a measured frame.
            Exact("alloc_bytes_per_frame", r.AllocatedPerFrame, 8);
            Exact("gen0_per_1k_frames", r.Gen0PerKFrames, 1);
            if (b.TryGetProperty("zones", out var zones))
                foreach (var z in zones.EnumerateObject())
                {
                    double current = r.Zones.GetValueOrDefault(z.Name), baseline = z.Value.GetDouble();
                    if (current > baseline * (1 + Tolerance * 2) + FloorMs * 2)
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  warn {r.Name} {z.Name}: {current:0.000} ms vs {baseline:0.000}"));
                }
        }
        return failures;
    }

    /// <summary>Write this mode's results into the machine's baseline file, keeping other modes and scenarios.</summary>
    public void Update(string path)
    {
        var sections = new SortedDictionary<string, SortedDictionary<string, string>>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var mode in doc.RootElement.EnumerateObject())
            {
                if (mode.Value.ValueKind != JsonValueKind.Object) continue;
                var scenarios = new SortedDictionary<string, string>(StringComparer.Ordinal);
                foreach (var s in mode.Value.EnumerateObject()) scenarios[s.Name] = s.Value.GetRawText();
                sections[mode.Name] = scenarios;
            }
        }
        if (!sections.TryGetValue(Mode, out var mine)) sections[Mode] = mine = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in Results) mine[r.Name] = ScenarioJson(r);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        w.WriteStartObject();
        w.WriteString("machine", Machine);
        foreach (var (mode, scenarios) in sections)
        {
            w.WritePropertyName(mode);
            w.WriteStartObject();
            foreach (var (name, json) in scenarios) { w.WritePropertyName(name); w.WriteRawValue(json); }
            w.WriteEndObject();
        }
        w.WriteEndObject();
        Console.WriteLine($"baseline written: {path}");
    }

    private static string ScenarioJson(BenchResult r)
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            static double R(double v) => Math.Round(v, 4);
            w.WriteStartObject();
            w.WriteNumber("median_ms", R(r.MedianMs));
            w.WriteNumber("work_ms", R(r.WorkMs));
            w.WriteNumber("p95_ms", R(r.P95Ms));
            if (r.GpuMs > 0) w.WriteNumber("gpu_ms", R(r.GpuMs));
            w.WriteNumber("alloc_bytes_per_frame", R(r.AllocatedPerFrame));
            w.WriteNumber("gen0_per_1k_frames", R(r.Gen0PerKFrames));
            w.WriteStartObject("zones");
            // Only zones big enough to mean something; the rest is noise.
            foreach (var (zone, ms) in r.Zones.OrderBy(z => z.Key, StringComparer.Ordinal))
                if (ms >= 0.01) w.WriteNumber(zone, R(ms));
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Parse the shared command line: scenario filters, --runs/--frames/--warmup, --quick, --tree, --update, --tolerance.</summary>
    public List<string> Configure(string[] args)
    {
        var filters = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException(a + " needs a value");
            switch (a)
            {
                case "--runs": Runs = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--frames": Frames = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--warmup": Warmup = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--tolerance": Tolerance = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--quick": Runs = 1; Warmup = 60; Frames = 120; break;
                case "--tree": PrintTrees = true; break;
                case "--update" or "--bench" or "--desktop" or "--no-gpu": break;
                default: if (!a.StartsWith("--", StringComparison.Ordinal)) filters.Add(a); break;
            }
        }
        return filters;
    }

    public static bool Selected(List<string> filters, string name) => filters.Count == 0 || filters.Any(f => name.StartsWith(f, StringComparison.Ordinal));

    public static string RepoRoot()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gamegrl.slnx"))) return d.FullName;
        for (var d = new DirectoryInfo(Environment.CurrentDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "gamegrl.slnx"))) return d.FullName;
        throw new InvalidOperationException("Run from inside the repository.");
    }
}
