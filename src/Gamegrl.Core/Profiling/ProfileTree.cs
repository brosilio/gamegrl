using System.Globalization;
using System.Text;

namespace Gamegrl;

/// <summary>
/// The profiler's frames over a range, merged into a call tree: the frame thread's zones nest by depth, other threads'
/// zones gather under a "Workers" root and GPU zones under a "GPU" root. Identical paths merge, so a range of frames
/// reads as averages per frame. Reusable and allocation-free once its arrays have grown.
/// </summary>
public sealed class ProfileTree
{
    public struct Node
    {
        public int Marker;      // -1 for the synthetic roots
        public int Parent, FirstChild, NextSibling;
        public int Depth;
        public long Ticks, Calls;
        public long ChildTicks;
        /// <summary>Bytes allocated inside the zone (children included), and by its children.</summary>
        public long Allocated, ChildAllocated;
        public readonly long SelfTicks => Ticks - ChildTicks;
        public readonly long SelfAllocated => Allocated - ChildAllocated;
    }

    private Node[] _nodes = new Node[256];
    private int _count;
    private ProfileEvent[] _scratch = new ProfileEvent[1024];
    private readonly int[] _stack = new int[256];
    private readonly double[] _frameMs = new double[1024];

    /// <summary>Index of the frame thread's root; its children are the top-level zones.</summary>
    public const int Root = 0;
    public int Workers { get; private set; }
    public int Gpu { get; private set; }

    public ReadOnlySpan<Node> Nodes => _nodes.AsSpan(0, _count);
    public ref readonly Node this[int index] => ref _nodes[index];

    public int Frames { get; private set; }
    public long FirstFrame { get; private set; }
    public FrameStats Stats;

    /// <summary>Frame time and memory figures over the range.</summary>
    public struct FrameStats
    {
        public double MeanMs, MedianMs, P95Ms, P99Ms, MaxMs;
        public double TickMeanMs, GpuMeanMs;
        public double AllocatedPerFrame, AllocatedAllThreadsPerFrame;
        public int Gen0, Gen1, Gen2;
        public double GcPauseMs;
        public long HeapBytes;
        public int Dropped;
    }

    /// <summary>Merge frames [<paramref name="first"/>, first + <paramref name="count"/>) of the history.</summary>
    public void Build(long first, int count)
    {
        _count = 0;
        New(-1, -1, 0);
        Workers = New(-1, -1, 0);
        Gpu = New(-1, -1, 0);
        first = Math.Max(first, Profiler.OldestFrame);
        count = (int)Math.Clamp(Profiler.FrameCount - first, 0, count);
        FirstFrame = first; Frames = count;
        Stats = default;
        if (count == 0) return;
        byte frameThread = Profiler.FrameThread;
        long gpuFrames = 0;
        for (long n = first; n < first + count; n++)
        {
            ref readonly var frame = ref Profiler.Frame(n);
            _frameMs[(int)(n - first) % _frameMs.Length] = frame.Milliseconds;
            Stats.MeanMs += frame.Milliseconds;
            Stats.TickMeanMs += frame.TickMilliseconds;
            if (frame.GpuTicks > 0) { Stats.GpuMeanMs += frame.GpuMilliseconds; gpuFrames++; }
            Stats.AllocatedPerFrame += frame.Allocated;
            Stats.AllocatedAllThreadsPerFrame += frame.AllocatedAllThreads;
            Stats.Gen0 += frame.Gen0; Stats.Gen1 += frame.Gen1; Stats.Gen2 += frame.Gen2;
            Stats.GcPauseMs += frame.GcPauseTicks * Profiler.TicksToMs;
            Stats.HeapBytes = frame.HeapBytes;
            Stats.Dropped += frame.Dropped;
            Stats.MaxMs = Math.Max(Stats.MaxMs, frame.Milliseconds);

            // The frame thread's zones, opened in start order, nest by depth.
            int k = 0;
            if (_scratch.Length < frame.EventCount) Array.Resize(ref _scratch, Math.Max(frame.EventCount, _scratch.Length * 2));
            for (int i = 0; i < frame.EventCount; i++)
            {
                ref readonly var e = ref Profiler.Event(frame, i);
                if (e.Thread == frameThread) _scratch[k++] = e;
                else Add(Workers, e.Marker, e.Duration, e.Allocated);
            }
            Nest(_scratch.AsSpan(0, k), Root);
            k = 0;
            if (_scratch.Length < frame.GpuEventCount) Array.Resize(ref _scratch, Math.Max(frame.GpuEventCount, _scratch.Length * 2));
            for (int i = 0; i < frame.GpuEventCount; i++) _scratch[k++] = Profiler.GpuEvent(frame, i);
            Nest(_scratch.AsSpan(0, k), Gpu);
        }
        Stats.MeanMs /= count; Stats.TickMeanMs /= count;
        Stats.GpuMeanMs = gpuFrames > 0 ? Stats.GpuMeanMs / gpuFrames : 0;
        Stats.AllocatedPerFrame /= count; Stats.AllocatedAllThreadsPerFrame /= count;
        var times = _frameMs.AsSpan(0, Math.Min(count, _frameMs.Length));
        HeapSort(times);
        Stats.MedianMs = Percentile(times, 0.5); Stats.P95Ms = Percentile(times, 0.95); Stats.P99Ms = Percentile(times, 0.99);
        for (int i = 0; i < _count; i++)
            if (_nodes[i].Parent >= 0)
            {
                _nodes[_nodes[i].Parent].ChildTicks += _nodes[i].Ticks;
                _nodes[_nodes[i].Parent].ChildAllocated += _nodes[i].Allocated;
            }
        _nodes[Root].Ticks = _nodes[Root].ChildTicks;
        _nodes[Workers].Ticks = _nodes[Workers].ChildTicks;
        _nodes[Gpu].Ticks = _nodes[Gpu].ChildTicks;
    }

    private static double Percentile(ReadOnlySpan<double> sorted, double p) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];

    // Own sorts, not Span.Sort: the BCL's allocate under NativeAOT (comparer wrappers), and this runs every rebuild.

    /// <summary>Insertion sort by start, then depth. A thread's zones arrive nearly ordered (children close before their
    /// parent), so each parent moves back only past its own children.</summary>
    private static void SortByStart(Span<ProfileEvent> events)
    {
        for (int i = 1; i < events.Length; i++)
        {
            var e = events[i];
            int j = i - 1;
            while (j >= 0 && (events[j].Start > e.Start || (events[j].Start == e.Start && events[j].Depth > e.Depth)))
            {
                events[j + 1] = events[j];
                j--;
            }
            events[j + 1] = e;
        }
    }

    private static void HeapSort(Span<double> a)
    {
        for (int i = a.Length / 2 - 1; i >= 0; i--) SiftDown(a, i, a.Length);
        for (int end = a.Length - 1; end > 0; end--)
        {
            (a[0], a[end]) = (a[end], a[0]);
            SiftDown(a, 0, end);
        }
    }

    private static void SiftDown(Span<double> a, int i, int n)
    {
        while (true)
        {
            int child = i * 2 + 1;
            if (child >= n) return;
            if (child + 1 < n && a[child + 1] > a[child]) child++;
            if (a[i] >= a[child]) return;
            (a[i], a[child]) = (a[child], a[i]);
            i = child;
        }
    }

    private void Nest(Span<ProfileEvent> events, int root)
    {
        SortByStart(events);
        int baseDepth = int.MaxValue;
        foreach (var e in events) baseDepth = Math.Min(baseDepth, e.Depth);
        foreach (var e in events)
        {
            int depth = Math.Min(e.Depth - baseDepth, _stack.Length - 1);
            int parent = depth == 0 ? root : _stack[depth - 1];
            _stack[depth] = Add(parent, e.Marker, e.Duration, e.Allocated);
        }
    }

    private int Add(int parent, int marker, long ticks, long allocated)
    {
        int child = _nodes[parent].FirstChild;
        while (child >= 0 && _nodes[child].Marker != marker) child = _nodes[child].NextSibling;
        if (child < 0) child = New(parent, marker, _nodes[parent].Depth + 1);
        _nodes[child].Ticks += ticks;
        _nodes[child].Allocated += allocated;
        _nodes[child].Calls++;
        return child;
    }

    private int New(int parent, int marker, int depth)
    {
        if (_count == _nodes.Length) Array.Resize(ref _nodes, _count * 2);
        int i = _count++;
        _nodes[i] = new Node { Marker = marker, Parent = parent, FirstChild = -1, NextSibling = -1, Depth = depth };
        if (parent >= 0)
        {
            // Append, so children keep first-seen (execution) order.
            ref var p = ref _nodes[parent];
            if (p.FirstChild < 0) p.FirstChild = i;
            else
            {
                int last = p.FirstChild;
                while (_nodes[last].NextSibling >= 0) last = _nodes[last].NextSibling;
                _nodes[last].NextSibling = i;
            }
        }
        return i;
    }

    /// <summary>Mean milliseconds per frame for a node.</summary>
    public double Ms(int node) => Frames == 0 ? 0 : _nodes[node].Ticks * Profiler.TicksToMs / Frames;
    public double SelfMs(int node) => Frames == 0 ? 0 : _nodes[node].SelfTicks * Profiler.TicksToMs / Frames;
    /// <summary>Mean bytes allocated per frame inside a node (children included).</summary>
    public double AllocatedPerFrame(int node) => Frames == 0 ? 0 : (double)_nodes[node].Allocated / Frames;

    public string Name(int node) => node == Root ? "Frame" : node == Workers ? "Workers" : node == Gpu ? "GPU" : Profiler.Marker(_nodes[node].Marker).Name;

    /// <summary>A node's slash-separated path from its root ("Phase.Update/Spinners.Spin"), as baselines key it.</summary>
    public string Path(int node)
    {
        var parts = new List<string>();
        for (int n = node; n > Gpu; n = _nodes[n].Parent) parts.Add(Name(n));
        int root = node;
        while (root > Gpu) root = _nodes[root].Parent;
        if (root != Root) parts.Add(Name(root));
        parts.Reverse();
        return string.Join('/', parts);
    }

    /// <summary>Self milliseconds per frame across the nodes of <paramref name="category"/> under <paramref name="root"/>.</summary>
    public double CategoryMs(ProfileCategory category, int root = Root)
    {
        long ticks = 0;
        for (int i = 3; i < _count; i++)
            if (Profiler.Marker(_nodes[i].Marker).Category == category && UnderRoot(i, root)) ticks += _nodes[i].SelfTicks;
        return Frames == 0 ? 0 : ticks * Profiler.TicksToMs / Frames;
    }

    public bool UnderRoot(int node, int root)
    {
        while (node > Gpu) node = _nodes[node].Parent;
        return node == root;
    }

    /// <summary>
    /// Write the frames as a Chrome trace (JSON; opens in ui.perfetto.dev or chrome://tracing): every thread's zones,
    /// GPU zones on their own track, and frame time and allocation counters.
    /// </summary>
    public static void WriteChromeTrace(TextWriter w, long first, int count)
    {
        first = Math.Max(first, Profiler.OldestFrame);
        count = (int)Math.Clamp(Profiler.FrameCount - first, 0, count);
        var c = CultureInfo.InvariantCulture;
        long origin = count > 0 ? Profiler.Frame(first).Start : 0;
        double toUs = Profiler.TicksToMs * 1000;
        w.Write("{\"displayTimeUnit\":\"ms\",\"traceEvents\":[\n");
        bool firstEvent = true;
        void Sep() { if (!firstEvent) w.Write(",\n"); firstEvent = false; }
        for (byte t = 0; t < Profiler.ThreadCount; t++)
        { Sep(); w.Write($"{{\"ph\":\"M\",\"name\":\"thread_name\",\"pid\":1,\"tid\":{t},\"args\":{{\"name\":{Json(Profiler.ThreadName(t))}}}}}"); }
        Sep(); w.Write($"{{\"ph\":\"M\",\"name\":\"thread_name\",\"pid\":1,\"tid\":{Profiler.GpuThread},\"args\":{{\"name\":\"GPU (aligned to frame start)\"}}}}");
        void Zone(in ProfileEvent e)
        {
            var m = Profiler.Marker(e.Marker);
            Sep();
            w.Write(string.Create(c, $"{{\"ph\":\"X\",\"name\":{Json(m.Name)},\"cat\":\"{m.Category}\",\"pid\":1,\"tid\":{e.Thread},\"ts\":{(e.Start - origin) * toUs:0.###},\"dur\":{e.Duration * toUs:0.###}}}"));
        }
        for (long n = first; n < first + count; n++)
        {
            ref readonly var f = ref Profiler.Frame(n);
            for (int i = 0; i < f.EventCount; i++) Zone(Profiler.Event(f, i));
            for (int i = 0; i < f.GpuEventCount; i++) Zone(Profiler.GpuEvent(f, i));
            Sep();
            w.Write(string.Create(c, $"{{\"ph\":\"C\",\"name\":\"frame\",\"pid\":1,\"ts\":{(f.Start - origin) * toUs:0.###},\"args\":{{\"ms\":{f.Milliseconds:0.###},\"gpu ms\":{f.GpuMilliseconds:0.###},\"alloc bytes\":{f.Allocated}}}}}"));
        }
        w.Write("\n]}\n");
    }

    private static string Json(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (char ch in s)
            sb.Append(ch switch { '"' => "\\\"", '\\' => "\\\\", < ' ' => $"\\u{(int)ch:x4}", _ => ch.ToString() });
        return sb.Append('"').ToString();
    }
}
