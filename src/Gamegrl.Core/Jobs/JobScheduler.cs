using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>A unit of parallel work over the index range [start, end).</summary>
public interface IJobFor
{
    void Execute(int start, int end);
}

/// <summary>
/// A fixed pool of worker threads for data-parallel loops. <see cref="ParallelFor{T}"/> splits a range into
/// batches that the workers and the calling thread claim with an atomic counter. It allocates nothing: the job
/// struct stays on the caller's stack and the workers reach it through a pointer and a generic trampoline.
/// One parallel loop runs at a time; a nested call (or a call from a worker) runs inline.
/// TODO(milestone 3): a task graph with dependencies, so independent systems also run in parallel.
/// </summary>
public sealed unsafe class JobScheduler : IDisposable
{
    private readonly Thread[] _workers;
    private readonly SemaphoreSlim _wake = new(0);
    private volatile bool _stopping;

    // The current loop. Published before the workers are woken (Release is a full fence).
    private delegate*<void*, int, int, void> _invoke;
    private void* _job;
    private int _count, _batch;
    private int _next;        // next unclaimed index
    private int _pending;     // workers that haven't finished the current loop
    private int _active;      // 1 while a loop runs

    [ThreadStatic] private static bool _isWorker;

    /// <param name="workerCount">Worker threads besides the caller; default: logical cores − 1.</param>
    public JobScheduler(int workerCount = -1)
    {
        if (workerCount < 0) workerCount = Math.Max(0, Environment.ProcessorCount - 1);
        _workers = new Thread[workerCount];
        for (int i = 0; i < workerCount; i++)
        {
            _workers[i] = new Thread(WorkerLoop) { IsBackground = true, Name = $"gamegrl-worker-{i}" };
            _workers[i].Start();
        }
    }

    public int WorkerCount => _workers.Length;

    /// <summary>
    /// Run <paramref name="job"/> over [0, <paramref name="count"/>) in batches of <paramref name="batchSize"/>.
    /// Returns when every batch has run. Batches must not depend on each other.
    /// </summary>
    public void ParallelFor<T>(int count, int batchSize, ref T job) where T : struct, IJobFor
    {
        if (count <= 0) return;
        if (batchSize < 1) batchSize = 1;
        if (_workers.Length == 0 || count <= batchSize || _isWorker || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            job.Execute(0, count);
            return;
        }

        _invoke = &Invoke<T>;
        _job = Unsafe.AsPointer(ref job);
        _count = count;
        _batch = batchSize;
        _next = 0;
        _pending = _workers.Length;
        _wake.Release(_workers.Length);

        RunBatches();

        // Every worker must check out before the job (on our stack) goes away or the next loop's fields change.
        var spin = new SpinWait();
        while (Volatile.Read(ref _pending) != 0) spin.SpinOnce(-1);

        _job = null;
        Volatile.Write(ref _active, 0);
    }

    private static void Invoke<T>(void* job, int start, int end) where T : struct, IJobFor =>
        Unsafe.AsRef<T>(job).Execute(start, end);

    private void RunBatches()
    {
        int count = _count, batch = _batch;
        var invoke = _invoke;
        var job = _job;
        while (true)
        {
            int start = Interlocked.Add(ref _next, batch) - batch;
            if (start >= count) return;
            invoke(job, start, Math.Min(start + batch, count));
        }
    }

    private void WorkerLoop()
    {
        _isWorker = true;
        while (true)
        {
            _wake.Wait();
            if (_stopping) return;
            RunBatches();
            Interlocked.Decrement(ref _pending);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        _wake.Release(_workers.Length);
        foreach (var t in _workers) t.Join();
        _wake.Dispose();
    }
}
