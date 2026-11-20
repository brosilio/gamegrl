using System.Numerics;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// Runs the expensive acoustic estimates off the game thread: the game submits the scene and the listener every frame
/// (cheap), and the simulator re-estimates the reverb when the scene changed, the listener moved more than half a metre,
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

    private const float MoveThreshold = 0.5f;
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

    /// <summary>Game thread: the newest estimate, if one arrived since the last call.</summary>
    public bool TryTake(out ReverbParams result)
    {
        int version = Volatile.Read(ref _resultVersion);
        lock (_wake) result = _result;
        if (version == _takenVersion) return false;
        _takenVersion = version;
        return true;
    }

    private ReverbParams Compute(Request r) => ReverbEstimator.Estimate(r.Scene, r.Listener, Rays, Bounces, r.Temperature, r.Humidity);

    private void Publish(in ReverbParams result)
    {
        lock (_wake) _result = result;
        Interlocked.Increment(ref _resultVersion);
    }

    private void Run()
    {
        while (!_stopping)
        {
            _wake.WaitOne();
            var request = Interlocked.Exchange(ref _pending, null);
            if (request is null || _stopping) continue;
            Publish(Compute(request));
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
