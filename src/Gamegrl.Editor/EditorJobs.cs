using System.Collections.Concurrent;

namespace Gamegrl.Editor;

/// <summary>
/// Workers produce values; only <see cref="Pump"/> applies them at a frame boundary. Jobs are started and
/// retired on the frame thread, so the running list needs no locking and reading it allocates nothing.
/// </summary>
public sealed class EditorJobs : IDisposable
{
    private readonly ConcurrentQueue<Action> _completed = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly List<(int Id, CancellationTokenSource Stop)> _running = new();
    private readonly List<string> _names = new();
    private int _next;

    /// <summary>Names of running jobs, in start order.</summary>
    public IReadOnlyList<string> Running => _names;
    public bool Busy => _running.Count > 0;

    public void Run<T>(string name, Func<CancellationToken, T> work, Action<T> apply, Action? finished = null)
    {
        int id = ++_next;
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _running.Add((id, stop));
        _names.Add(name);
        _ = Task.Run(() =>
        {
            try
            {
                T result = work(stop.Token);
                _completed.Enqueue(() =>
                {
                    if (!stop.IsCancellationRequested) apply(result);
                    else if (result is IDisposable disposable) disposable.Dispose();
                });
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { _completed.Enqueue(() => Log.Error($"{name}: {e.Message}")); }
            finally { _completed.Enqueue(() => Retire(id, stop, finished)); }
        });
    }

    private void Retire(int id, CancellationTokenSource stop, Action? finished)
    {
        for (int i = 0; i < _running.Count; i++)
        {
            if (_running[i].Id != id) continue;
            _running.RemoveAt(i);
            _names.RemoveAt(i);
            break;
        }
        stop.Dispose();
        finished?.Invoke();
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
        foreach (var (_, stop) in _running) stop.Cancel();
    }

    // The root source has no timer, so cancelling is enough; workers may still observe its linked tokens.
    public void Dispose() => _stop.Cancel();
}
