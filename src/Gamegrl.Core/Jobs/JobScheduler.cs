using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>A unit of parallel work over the index range [start, end).</summary>
public interface IJobFor
{
    void Execute(int start, int end);
}

/// <summary>Work run once on each of several threads at the same time (they may wait on each other, at barriers).</summary>
public interface IJobThreads
{
    void Execute(int thread, int threadCount);
}

/// <summary>
/// A barrier for the threads of one <see cref="JobScheduler.RunOnThreads{T}"/> call: each waits until all have arrived.
/// Spins (the phases between barriers are microseconds long), yielding if a thread falls behind.
/// </summary>
public sealed class SpinBarrier
{
    private int _arrived, _generation, _threads = 1;
    public void Reset(int threads) { _threads = threads; _arrived = 0; }
    public void Wait()
    {
        if (_threads == 1) return;
        int generation = Volatile.Read(ref _generation);
        if (Interlocked.Increment(ref _arrived) == _threads)
        {
            _arrived = 0;
            Interlocked.Increment(ref _generation); // full fence: everything before is visible after
            return;
        }
        var spin = new SpinWait();
        while (Volatile.Read(ref _generation) == generation) spin.SpinOnce(-1);
    }
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
    private bool _threadMode; // the current dispatch is RunOnThreads: each worker takes one thread index
    private int _threadNext, _threadCount;
    private int _next;        // next unclaimed index
    private int _pending;     // workers that haven't finished the current loop
    private int _active;      // 1 while a loop runs

    [ThreadStatic] private static bool _isWorker;
    private static readonly ProfileMarker Dispatch = ProfileMarker.Get("Jobs.ParallelFor", ProfileCategory.Engine);
    private static readonly ProfileMarker Batches = ProfileMarker.Get("Jobs.Batches", ProfileCategory.Engine);

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
    /// Run <paramref name="job"/> on <paramref name="threads"/> threads at once (the caller is thread 0), each on its own
    /// thread, so they can synchronize with a <see cref="SpinBarrier"/>. Clamped to the workers available; runs on the
    /// caller alone (threadCount 1) when nested or when another loop is running.
    /// </summary>
    public void RunOnThreads<T>(int threads, ref T job) where T : struct, IJobThreads
    {
        threads = Math.Clamp(threads, 1, _workers.Length + 1);
        if (threads == 1 || _isWorker || Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            job.Execute(0, 1);
            return;
        }
        using var zone = Dispatch.Scope();
        _invoke = &InvokeThread<T>;
        _job = Unsafe.AsPointer(ref job);
        _threadMode = true;
        _threadNext = 1;
        _threadCount = threads;
        _pending = threads - 1;
        _wake.Release(threads - 1);
        job.Execute(0, threads);
        var spin = new SpinWait();
        while (Volatile.Read(ref _pending) != 0) spin.SpinOnce(-1);
        _threadMode = false;
        _job = null;
        Volatile.Write(ref _active, 0);
    }

    private static void InvokeThread<T>(void* job, int thread, int threadCount) where T : struct, IJobThreads =>
        Unsafe.AsRef<T>(job).Execute(thread, threadCount);

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

        using var zone = Dispatch.Scope();
        _invoke = &Invoke<T>;
        _job = Unsafe.AsPointer(ref job);
        _count = count;
        _batch = batchSize;
        _next = 0;
        // Wake only the workers there's work for: the caller takes a batch too, and every woken worker must check out
        // before this returns, so waking 15 threads for two batches costs more than the batches.
        int batches = (count + batchSize - 1) / batchSize;
        int helpers = Math.Min(_workers.Length, batches - 1);
        _pending = helpers;
        _wake.Release(helpers);

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
            if (_threadMode)
            {
                int thread = Interlocked.Increment(ref _threadNext) - 1;
                using (Batches.Scope()) _invoke(_job, thread, _threadCount);
            }
            else
                using (Batches.Scope()) RunBatches();
            Interlocked.Decrement(ref _pending);
        }
    }

    public void Dispose()
    {
        _stopping = true;
        if (_workers.Length > 0) _wake.Release(_workers.Length);
        foreach (var t in _workers) t.Join();
        _wake.Dispose();
    }
}
