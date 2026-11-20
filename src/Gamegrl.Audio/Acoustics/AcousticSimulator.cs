using System.Numerics;

namespace Gamegrl.Audio.Acoustics;

/// <summary>A voice that wants early reflections, and where its source is.</summary>
internal struct ReflectionRequest
{
    public int Key;
    public Vector3 Source;
}

/// <summary>
/// Paths found for a set of voices: early reflections (up to <see cref="ReflectionTaps.Count"/> each) and, for voices
/// whose direct line is blocked, a diffraction path round the obstacle. Immutable.
/// </summary>
internal sealed class ReflectionResult
{
    public required int[] Keys;
    public required int[] Counts;
    public required ReflectionPath[] Paths; // voice i's at [i * ReflectionTaps.Count ..]
    public required DiffractionPath[] Diffraction;
    public required bool[] Diffracted;

    public ReadOnlySpan<ReflectionPath> For(int key)
    {
        int i = Array.IndexOf(Keys, key);
        return i < 0 ? default : Paths.AsSpan(i * ReflectionTaps.Count, Counts[i]);
    }

    public bool TryDiffraction(int key, out DiffractionPath path)
    {
        int i = Array.IndexOf(Keys, key);
        path = i >= 0 ? Diffraction[i] : default;
        return i >= 0 && Diffracted[i];
    }
}

/// <summary>
/// Runs the expensive acoustic estimates off the game thread: the game submits the scene and the listener every frame
/// (cheap), and the simulator re-estimates the reverb when the scene changed, the listener moved more than 30 cm,
/// or the air changed; the game picks up the newest result without waiting. Inline mode (offline engines, tests) does
/// the work in <see cref="Submit"/> instead, deterministically.
/// </summary>
internal sealed class AcousticSimulator : IDisposable
{
    private sealed class Request
    {
        public required AcousticScene Scene;
        public Vector3 Listener;
        public float Temperature, Humidity;
    }

    private sealed class ReflectionJob
    {
        public required AcousticScene Scene;
        public Vector3 Listener;
        public float SpeedOfSound;
        public required ReflectionRequest[] Requests;
    }

    private const float MoveThreshold = 0.3f, ReflectionMoveThreshold = 0.25f, RemoteMoveThreshold = 1;
    private Request? _pendingRemote;
    private ReverbParams _remote;
    private int _remoteVersion, _takenRemoteVersion;
    private AcousticScene? _lastRemoteScene;
    private Vector3 _lastRemote = new(float.NaN);
    private ReflectionJob? _pendingReflections;
    private ReflectionResult? _reflections;
    private int _reflectionVersion, _takenReflectionVersion;
    private AcousticScene? _lastReflectionScene;
    private Vector3 _lastReflectionListener;
    private ReflectionRequest[] _lastRequests = [];
    private readonly bool _inline;
    private readonly Thread? _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile bool _stopping;
    private Request? _pending;                 // written by the game, taken by the worker
    private ReverbParams _result;
    private int _resultVersion, _takenVersion; // written by the worker / read by the game (Interlocked)
    private AcousticScene? _lastScene;
    private Vector3 _lastListener;
    private float _lastTemperature, _lastHumidity;

    /// <summary>Whether estimates are made in <see cref="Submit"/> (offline engines, tests) rather than on the simulation thread.</summary>
    public bool Inline => _inline;
    public int Rays { get; init; } = 2048;
    public int Bounces { get; init; } = 64;

    public AcousticSimulator(bool inline)
    {
        _inline = inline;
        if (inline) return;
        _thread = new Thread(Run) { Name = "gamegrl acoustics", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Game thread, every frame: hands over the current state when it differs enough to need a new estimate.</summary>
    public void Submit(AcousticScene scene, Vector3 listener, float temperature, float humidity)
    {
        bool changed = !ReferenceEquals(scene, _lastScene) || Vector3.DistanceSquared(listener, _lastListener) > MoveThreshold * MoveThreshold
            || temperature != _lastTemperature || humidity != _lastHumidity;
        if (!changed) return;
        _lastScene = scene; _lastListener = listener; _lastTemperature = temperature; _lastHumidity = humidity;
        var request = new Request { Scene = scene, Listener = listener, Temperature = temperature, Humidity = humidity };
        if (_inline) { Publish(Compute(request)); return; }
        Volatile.Write(ref _pending, request);
        _wake.Set();
    }

    /// <summary>
    /// Game thread, every frame: the voices that want reflections. A new search starts when the scene or the set of voices
    /// changed, or the listener or any source moved a quarter of a metre.
    /// </summary>
    public void SubmitReflections(AcousticScene scene, Vector3 listener, ReadOnlySpan<ReflectionRequest> requests, float speedOfSound = 343)
    {
        bool changed = !ReferenceEquals(scene, _lastReflectionScene) || requests.Length != _lastRequests.Length
            || Vector3.DistanceSquared(listener, _lastReflectionListener) > ReflectionMoveThreshold * ReflectionMoveThreshold;
        for (int i = 0; i < requests.Length && !changed; i++)
            changed = requests[i].Key != _lastRequests[i].Key || Vector3.DistanceSquared(requests[i].Source, _lastRequests[i].Source) > ReflectionMoveThreshold * ReflectionMoveThreshold;
        if (!changed) return;
        _lastReflectionScene = scene; _lastReflectionListener = listener; _lastRequests = requests.ToArray();
        var job = new ReflectionJob { Scene = scene, Listener = listener, Requests = _lastRequests, SpeedOfSound = speedOfSound };
        if (_inline) { PublishReflections(FindReflections(job)); return; }
        Volatile.Write(ref _pendingReflections, job);
        _wake.Set();
    }

    /// <summary>Game thread: the newest reflection paths, if a search finished since the last call.</summary>
    public bool TryTakeReflections(out ReflectionResult? result)
    {
        int version = Volatile.Read(ref _reflectionVersion);
        result = Volatile.Read(ref _reflections);
        if (version == _takenReflectionVersion) return false;
        _takenReflectionVersion = version;
        return true;
    }

    private static ReflectionResult FindReflections(ReflectionJob job)
    {
        int n = job.Requests.Length, k = ReflectionTaps.Count;
        var result = new ReflectionResult
        {
            Keys = new int[n], Counts = new int[n], Paths = new ReflectionPath[n * k], Diffraction = new DiffractionPath[n], Diffracted = new bool[n],
        };
        // Voices are independent, each writing its own slots: searched in parallel, the same result either way.
        void Search(int i)
        {
            var source = job.Requests[i].Source;
            result.Keys[i] = job.Requests[i].Key;
            result.Counts[i] = ImageSources.Find(job.Scene, job.Listener, source, result.Paths.AsSpan(i * k, k));
            result.Diffracted[i] = Diffraction.Find(job.Scene, job.Listener, source, out result.Diffraction[i], job.SpeedOfSound);
        }
        if (n > 1) Parallel.For(0, n, Search);
        else if (n == 1) Search(0);
        return result;
    }

    private void PublishReflections(ReflectionResult result)
    {
        Volatile.Write(ref _reflections, result);
        Interlocked.Increment(ref _reflectionVersion);
    }

    /// <summary>Game thread: where the other room's reverb should be estimated (a hidden source); a new estimate when it moves a metre.</summary>
    public void SubmitRemote(AcousticScene scene, Vector3 anchor, float temperature, float humidity)
    {
        if (ReferenceEquals(scene, _lastRemoteScene) && Vector3.DistanceSquared(anchor, _lastRemote) < RemoteMoveThreshold * RemoteMoveThreshold) return;
        _lastRemoteScene = scene; _lastRemote = anchor;
        var request = new Request { Scene = scene, Listener = anchor, Temperature = temperature, Humidity = humidity };
        if (_inline) { PublishRemote(Compute(request)); return; }
        Volatile.Write(ref _pendingRemote, request);
        _wake.Set();
    }

    /// <summary>Game thread: the newest remote estimate, if one arrived since the last call.</summary>
    public bool TryTakeRemote(out ReverbParams result)
    {
        int version = Volatile.Read(ref _remoteVersion);
        lock (_wake) result = _remote;
        if (version == _takenRemoteVersion) return false;
        _takenRemoteVersion = version;
        return true;
    }

    private void PublishRemote((ReverbParams Result, ReverbTrace? Trace) estimate)
    {
        lock (_wake) { _remote = estimate.Result; _remoteTrace = estimate.Trace; }
        Interlocked.Increment(ref _remoteVersion);
    }

    /// <summary>Game thread: the newest estimate, if one arrived since the last call.</summary>
    public bool TryTake(out ReverbParams result)
    {
        int version = Volatile.Read(ref _resultVersion);
        lock (_wake) result = _result;
        if (version == _takenVersion) return false;
        _takenVersion = version;
        return true;
    }

    /// <summary>Whether estimates keep a <see cref="ReverbTrace"/> (rays and decay curve), for diagnostics.</summary>
    public bool Tracing { get => _tracing; set => _tracing = value; }
    private volatile bool _tracing;
    private ReverbTrace? _trace, _remoteTrace;

    /// <summary>The traces of the latest reverb and remote estimates (null unless <see cref="Tracing"/> was on for them).</summary>
    public ReverbTrace? LatestTrace { get { lock (_wake) return _trace; } }
    public ReverbTrace? LatestRemoteTrace { get { lock (_wake) return _remoteTrace; } }

    private (ReverbParams, ReverbTrace?) Compute(Request r)
    {
        var trace = _tracing ? new ReverbTrace() : null;
        return (ReverbEstimator.Estimate(r.Scene, r.Listener, Rays, Bounces, r.Temperature, r.Humidity, trace: trace), trace);
    }

    private void Publish((ReverbParams Result, ReverbTrace? Trace) estimate)
    {
        lock (_wake) { _result = estimate.Result; _trace = estimate.Trace; }
        Interlocked.Increment(ref _resultVersion);
    }

    private void Run()
    {
        while (!_stopping)
        {
            _wake.WaitOne();
            if (_stopping) break;
            // Reflections first: they follow moving sources and want to be fresh; the reverb changes slowly.
            var reflections = Interlocked.Exchange(ref _pendingReflections, null);
            if (reflections is not null) PublishReflections(FindReflections(reflections));
            var request = Interlocked.Exchange(ref _pending, null);
            if (request is not null) Publish(Compute(request));
            var remote = Interlocked.Exchange(ref _pendingRemote, null);
            if (remote is not null) PublishRemote(Compute(remote));
            if (Volatile.Read(ref _pendingReflections) is not null || Volatile.Read(ref _pending) is not null || Volatile.Read(ref _pendingRemote) is not null) _wake.Set();
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Set();
        _thread?.Join();
        _wake.Dispose();
    }
}
