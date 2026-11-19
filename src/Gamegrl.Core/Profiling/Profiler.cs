using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl;

/// <summary>Who a zone's time belongs to, so a view can split the game's cost from the engine's and the editor's.</summary>
public enum ProfileCategory : byte
{
    Engine,
    Game,
    Editor,
    /// <summary>Blocked rather than working: waiting on the GPU, vsync or present.</summary>
    Wait,
    Gpu,
}

/// <summary>
/// A named zone. Create one per call site, once, and keep it in a static field:
/// <code>static readonly ProfileMarker Think = ProfileMarker.Get("AI.Think");
/// using (Think.Scope()) { ... }</code>
/// Markers are interned by name and category, so code that reloads (the editor's game code) gets the same id back.
/// </summary>
public sealed class ProfileMarker
{
    private ProfileMarker(string name, ProfileCategory category, int id) { Name = name; Category = category; Id = id; }

    public string Name { get; }
    public ProfileCategory Category { get; }
    public int Id { get; }

    /// <summary>The marker for <paramref name="name"/>, created on first use. Not for hot paths: keep the result.</summary>
    public static ProfileMarker Get(string name, ProfileCategory category = ProfileCategory.Game) => Profiler.Intern(name, category, (n, c, id) => new ProfileMarker(n, c, id));

    /// <summary>Time the enclosing <c>using</c> block on this thread. One branch when profiling is off.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ProfileScope Scope() => new(Id);

    public override string ToString() => Name;
}

/// <summary>An open zone; dispose it (with <c>using</c>) to close it.</summary>
public readonly ref struct ProfileScope
{
    private readonly long _start, _allocated;
    private readonly int _marker; // id + 1; 0 when profiling was off at entry

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ProfileScope(int marker)
    {
        if (!Profiler.Enabled) { _marker = 0; _start = _allocated = 0; return; }
        _marker = marker + 1;
        _start = Profiler.Enter(out _allocated);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        if (_marker != 0) Profiler.Exit(_marker - 1, _start, _allocated);
    }
}

/// <summary>One closed zone. Times are <see cref="Stopwatch"/> ticks; GPU zones are mapped onto the CPU frame's start.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ProfileEvent
{
    public long Start, End;
    /// <summary>Bytes the thread allocated inside the zone, children included.</summary>
    public long Allocated;
    public int Marker;
    public ushort Depth;
    /// <summary>Index into <see cref="Profiler.ThreadName"/>, or <see cref="Profiler.GpuThread"/>.</summary>
    public byte Thread;
    public readonly long Duration => End - Start;
}

/// <summary>What one frame cost. Frames are measured end to end: from the previous frame's end to this one's.</summary>
public struct ProfileFrame
{
    public long Number;
    /// <summary>Previous frame's end, this frame's <see cref="App.Tick()"/> entry, and its end (Stopwatch ticks).</summary>
    public long Start, TickStart, End;
    public long EventStart; public int EventCount;
    public long GpuEventStart; public int GpuEventCount;
    /// <summary>GPU time for the frame's command buffer, in Stopwatch ticks; 0 until the GPU reports (a frame or two late).</summary>
    public long GpuTicks;
    /// <summary>The RHI frame number this frame recorded, to match GPU results; 0 if it didn't render.</summary>
    public ulong GpuFrame;
    /// <summary>Bytes the frame thread allocated (exact), and the whole process allocated (coarse: flushed per 8 KB context).</summary>
    public long Allocated, AllocatedAllThreads;
    public int Gen0, Gen1, Gen2;
    public long GcPauseTicks;
    public long HeapBytes;
    /// <summary>Zones lost because a thread wrote more than its ring holds between two frames.</summary>
    public int Dropped;

    public readonly double Milliseconds => (End - Start) * Profiler.TicksToMs;
    public readonly double TickMilliseconds => (End - TickStart) * Profiler.TicksToMs;
    public readonly double GpuMilliseconds => GpuTicks * Profiler.TicksToMs;
}

/// <summary>A GPU zone as a backend reports it: nanoseconds from the start of the frame's command buffer.</summary>
public struct GpuZone
{
    public int Marker;
    public ushort Depth;
    public long StartNs, EndNs;
}

/// <summary>
/// The engine's profiler: zones (<see cref="ProfileMarker"/>), per-system timing from the <see cref="Schedule"/>,
/// per-frame GC and allocation figures, GPU zones from the RHI, and a history of recent frames for views to read.
/// Each thread writes closed zones to its own lock-free ring; the frame thread drains them once per frame, so a zone
/// costs two timestamp reads and a store. Nothing allocates after warm-up. Process-wide: one profiler, every App.
/// </summary>
public static class Profiler
{
    public const byte GpuThread = 255;
    private const int FrameCapacity = 1024, EventCapacity = 1 << 18, GpuEventCapacity = 1 << 15;
    private const int TrackCapacity = 1 << 14, FrameTrackCapacity = 1 << 16;

    public static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

    /// <summary>Record zones and frames. Off by default; GAMEGRL_PROFILE=1 turns it on at startup; the editor turns it on.</summary>
    public static bool Enabled = Environment.GetEnvironmentVariable("GAMEGRL_PROFILE") == "1";

    /// <summary>Keep draining zones but stop adding frames to the history (a view inspecting a frame freezes it).</summary>
    public static bool Paused;

    private static readonly Lock _gate = new();
    private static readonly Dictionary<(string, ProfileCategory), ProfileMarker> _byName = new();
    private static ProfileMarker[] _markers = [];
    private static int _markerCount;
    private static ProfileTrack[] _tracks = [];
    private static int _trackCount;
    [ThreadStatic] private static ProfileTrack? _track;

    private static ProfileFrame[]? _frames;
    private static ProfileEvent[]? _events, _gpuEvents;
    private static long _frameCount, _eventsWritten, _gpuEventsWritten;
    private static ProfileFrame _current;
    private static bool _inFrame;
    private static long _lastEnd;
    private static long _lastAllocated, _lastAllocatedAll, _lastPause;
    private static int _lastGen0, _lastGen1, _lastGen2;
    private static int _frameThread = -1;

    // ------------------------------------------------------------------ markers

    internal static ProfileMarker Intern(string name, ProfileCategory category, Func<string, ProfileCategory, int, ProfileMarker> create)
    {
        lock (_gate)
        {
            if (_byName.TryGetValue((name, category), out var existing)) return existing;
            if (_markerCount == _markers.Length) Array.Resize(ref _markers, Math.Max(64, _markerCount * 2));
            var marker = create(name, category, _markerCount);
            _markers[_markerCount++] = marker;
            _byName[(name, category)] = marker;
            return marker;
        }
    }

    public static int MarkerCount => Volatile.Read(ref _markerCount);
    public static ProfileMarker Marker(int id) => _markers[id];

    // ------------------------------------------------------------------ zones

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static long Enter(out long allocated)
    {
        var track = _track ?? RegisterThread();
        track.Depth++;
        allocated = GC.GetAllocatedBytesForCurrentThread();
        return Stopwatch.GetTimestamp();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Exit(int marker, long start, long allocatedAtStart)
    {
        long end = Stopwatch.GetTimestamp();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedAtStart;
        var track = _track!;
        int depth = --track.Depth;
        long written = track.Written;
        ref var e = ref track.Ring[written & track.Mask];
        e.Start = start; e.End = end; e.Allocated = allocated; e.Marker = marker; e.Depth = (ushort)Math.Max(0, depth); e.Thread = track.Index;
        Volatile.Write(ref track.Written, written + 1);
    }

    /// <summary>Record an already-measured zone on this thread (for work timed some other way).</summary>
    public static void Record(ProfileMarker marker, long start, long end)
    {
        if (!Enabled) return;
        var track = _track ?? RegisterThread();
        long written = track.Written;
        ref var e = ref track.Ring[written & track.Mask];
        e.Start = start; e.End = end; e.Allocated = 0; e.Marker = marker.Id; e.Depth = (ushort)track.Depth; e.Thread = track.Index;
        Volatile.Write(ref track.Written, written + 1);
    }

    private static ProfileTrack RegisterThread()
    {
        lock (_gate)
        {
            if (_trackCount >= GpuThread) throw new InvalidOperationException("Too many profiled threads.");
            var thread = Thread.CurrentThread;
            bool frameThread = _frameThread < 0 || _frameThread == thread.ManagedThreadId;
            var track = new ProfileTrack((byte)_trackCount, thread.Name ?? (frameThread ? "main" : "thread " + thread.ManagedThreadId), frameThread ? FrameTrackCapacity : TrackCapacity);
            if (_trackCount == _tracks.Length) Array.Resize(ref _tracks, Math.Max(8, _trackCount * 2));
            _tracks[_trackCount] = track;
            Volatile.Write(ref _trackCount, _trackCount + 1);
            return _track = track;
        }
    }

    public static int ThreadCount => Volatile.Read(ref _trackCount);
    public static string ThreadName(byte thread) => thread == GpuThread ? "GPU" : _tracks[thread].Name;

    /// <summary>The track index of the thread that runs frames, once one has.</summary>
    public static byte FrameThread { get; private set; }

    // ------------------------------------------------------------------ frames

    /// <summary>Called by <see cref="App.Tick(double)"/> on entry.</summary>
    public static void BeginFrame()
    {
        if (!Enabled) { _inFrame = false; return; }
        EnsureStorage();
        if (_frameThread < 0) _frameThread = Environment.CurrentManagedThreadId;
        FrameThread = (_track ?? RegisterThread()).Index;
        long now = Stopwatch.GetTimestamp();
        if (_lastEnd == 0) { _lastEnd = now; SampleGc(out _, out _, out _, out _, out _, out _); }
        _current = new ProfileFrame { Start = _lastEnd, TickStart = now };
        _inFrame = true;
    }

    /// <summary>Called by a renderer when the frame records GPU frame <paramref name="gpuFrame"/>.</summary>
    public static void MarkGpuFrame(ulong gpuFrame)
    {
        if (_inFrame) _current.GpuFrame = gpuFrame + 1; // 0 means none
    }

    /// <summary>Called by <see cref="App.Tick(double)"/> on exit: drains every thread's zones into the history.</summary>
    public static void EndFrame()
    {
        if (!_inFrame) return;
        _inFrame = false;
        long end = Stopwatch.GetTimestamp();
        ref var f = ref _current;
        f.End = end;
        _lastEnd = end;
        SampleGc(out f.Allocated, out f.AllocatedAllThreads, out f.Gen0, out f.Gen1, out f.Gen2, out f.GcPauseTicks);
        f.HeapBytes = GC.GetTotalMemory(false);
        f.EventStart = _eventsWritten;
        int tracks = Volatile.Read(ref _trackCount);
        var events = _events!;
        for (int t = 0; t < tracks; t++)
        {
            var track = _tracks[t];
            long written = Volatile.Read(ref track.Written);
            long from = Math.Max(track.Read, written - track.Ring.Length);
            f.Dropped += (int)(from - track.Read);
            if (!Paused)
                for (long i = from; i < written; i++)
                    events[_eventsWritten++ & (EventCapacity - 1)] = track.Ring[i & track.Mask];
            track.Read = written;
        }
        f.EventCount = (int)(_eventsWritten - f.EventStart);
        if (Paused) return;
        f.Number = _frameCount;
        _frames![_frameCount & (FrameCapacity - 1)] = f;
        _frameCount++;
    }

    private static void SampleGc(out long allocated, out long allocatedAll, out int gen0, out int gen1, out int gen2, out long pause)
    {
        long a = GC.GetAllocatedBytesForCurrentThread(), all = GC.GetTotalAllocatedBytes(false);
        int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
        long p = GC.GetTotalPauseDuration().Ticks;
        allocated = a - _lastAllocated; allocatedAll = all - _lastAllocatedAll;
        gen0 = g0 - _lastGen0; gen1 = g1 - _lastGen1; gen2 = g2 - _lastGen2;
        pause = (long)((p - _lastPause) * (Stopwatch.Frequency / (double)TimeSpan.TicksPerSecond));
        _lastAllocated = a; _lastAllocatedAll = all; _lastGen0 = g0; _lastGen1 = g1; _lastGen2 = g2; _lastPause = p;
    }

    /// <summary>
    /// GPU results for an RHI frame, from the backend once the GPU has finished it. Attached to the CPU frame that
    /// recorded it, laid out from that frame's tick start (GPU and CPU clocks aren't calibrated against each other).
    /// </summary>
    public static void SubmitGpu(ulong gpuFrame, ReadOnlySpan<GpuZone> zones, long frameNs)
    {
        if (!Enabled || _frames is null) return;
        double toTicks = Stopwatch.Frequency / 1e9;
        for (long n = _frameCount - 1; n >= 0 && n >= _frameCount - 16; n--)
        {
            ref var f = ref _frames[n & (FrameCapacity - 1)];
            if (f.GpuFrame != gpuFrame + 1) continue;
            f.GpuTicks = (long)(frameNs * toTicks);
            f.GpuEventStart = _gpuEventsWritten;
            foreach (var z in zones)
                _gpuEvents![_gpuEventsWritten++ & (GpuEventCapacity - 1)] = new ProfileEvent
                {
                    Start = f.TickStart + (long)(z.StartNs * toTicks), End = f.TickStart + (long)(z.EndNs * toTicks),
                    Marker = z.Marker, Depth = z.Depth, Thread = GpuThread,
                };
            f.GpuEventCount = (int)(_gpuEventsWritten - f.GpuEventStart);
            return;
        }
    }

    private static void EnsureStorage()
    {
        if (_frames is not null) return;
        _frames = new ProfileFrame[FrameCapacity];
        _events = new ProfileEvent[EventCapacity];
        _gpuEvents = new ProfileEvent[GpuEventCapacity];
    }

    // ------------------------------------------------------------------ reading

    /// <summary>Frames recorded since startup (or <see cref="Clear"/>). The history keeps the most recent ones.</summary>
    public static long FrameCount => _frameCount;

    /// <summary>The oldest frame number still fully in the history.</summary>
    public static long OldestFrame
    {
        get
        {
            long oldest = Math.Max(0, _frameCount - FrameCapacity + 1);
            while (oldest < _frameCount && _frames![oldest & (FrameCapacity - 1)].EventStart < _eventsWritten - EventCapacity) oldest++;
            return oldest;
        }
    }

    public static ref readonly ProfileFrame Frame(long number)
    {
        if (_frames is null || number < 0 || number >= _frameCount || number < _frameCount - FrameCapacity) throw new ArgumentOutOfRangeException(nameof(number));
        return ref _frames[number & (FrameCapacity - 1)];
    }

    /// <summary>The <paramref name="i"/>th CPU zone of <paramref name="frame"/> (in the order threads closed them).</summary>
    public static ref readonly ProfileEvent Event(in ProfileFrame frame, int i) => ref _events![(frame.EventStart + i) & (EventCapacity - 1)];
    public static ref readonly ProfileEvent GpuEvent(in ProfileFrame frame, int i) => ref _gpuEvents![(frame.GpuEventStart + i) & (GpuEventCapacity - 1)];

    /// <summary>Forget recorded frames (markers and threads stay).</summary>
    public static void Clear()
    {
        _frameCount = 0;
        _lastEnd = 0;
        _inFrame = false;
    }
}

internal sealed class ProfileTrack(byte index, string name, int capacity)
{
    public readonly byte Index = index;
    public readonly string Name = name;
    public readonly ProfileEvent[] Ring = new ProfileEvent[capacity];
    public readonly int Mask = capacity - 1;
    public long Written; // the owning thread's
    public long Read;    // the frame thread's
    public int Depth;
}
