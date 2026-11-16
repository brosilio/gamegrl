using System.Collections.Concurrent;

namespace Gamegrl.Editor;

/// <summary>Workers produce values; only Pump applies them at a frame boundary.</summary>
public sealed class EditorJobs : IDisposable
{
    private readonly ConcurrentQueue<Action> _completed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<int, (string Name, CancellationTokenSource Stop)> _running = new();
    private int _next;
    public string[] Running => _running.Values.Select(v => v.Name).ToArray();
    public bool Busy => !_running.IsEmpty;
    public void Run<T>(string name, Func<CancellationToken, T> work, Action<T> apply, Action? finished = null)
    {
        int id = Interlocked.Increment(ref _next);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _running[id] = (name, stop);
        _ = Task.Run(() =>
        {
            try
            {
                T result = work(stop.Token);
                _completed.Enqueue(() => { if (!stop.IsCancellationRequested) apply(result); });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _completed.Enqueue(() => Log.Error($"{name}: {e.Message}")); }
            finally { _completed.Enqueue(() => { _running.TryRemove(id, out _); stop.Dispose(); finished?.Invoke(); }); }
        });
    }
    public void Pump()
    {
        // Do not include callbacks enqueued by callbacks in this frame's work budget.
        int count = _completed.Count;
        while (count-- > 0 && _completed.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Log.Error(e.ToString()); }
        }
    }
    public void CancelAll()
    {
        foreach (var (_, stop) in _running.Values) stop.Cancel();
    }
    public void Dispose() => _stop.Cancel();
}
