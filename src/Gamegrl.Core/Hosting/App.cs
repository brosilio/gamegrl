using System.Diagnostics;

namespace Gamegrl;

/// <summary>
/// A feature, composed at compile time. A module registers systems, resources and startup work in
/// <see cref="Build"/>. Modules a game doesn't add are trimmed from its player.
/// </summary>
public interface IModule
{
    void Build(EngineBuilder builder);
}

/// <summary>Composes modules into an <see cref="App"/>.</summary>
public sealed class EngineBuilder
{
    private readonly List<IModule> _modules = new();
    private readonly HashSet<Type> _moduleTypes = new();
    internal readonly List<Action<App>> StartActions = new();
    internal readonly List<Action<App>> StopActions = new();

    private EngineBuilder() { }

    public static EngineBuilder Create() => new();

    public Schedule Schedule { get; } = new();
    public World World { get; } = new();

    /// <summary>The fixed simulation rate in Hz.</summary>
    public double FixedRate { get; set; } = 60;

    public int WorkerThreads { get; set; } = -1;

    /// <summary>Add a module once; a second add of the same type is ignored, so modules can depend on each other.</summary>
    public EngineBuilder AddModule<T>() where T : IModule, new() => AddModule(new T());

    public EngineBuilder AddModule(IModule module)
    {
        if (!_moduleTypes.Add(module.GetType())) return this;
        _modules.Add(module);
        module.Build(this);
        return this;
    }

    public bool HasModule<T>() where T : IModule => _moduleTypes.Contains(typeof(T));

    public EngineBuilder AddSystem(ISystem system)
    {
        Schedule.Add(system);
        return this;
    }

    public EngineBuilder SetResource<T>(T value)
    {
        World.SetResource(value);
        return this;
    }

    /// <summary>Run <paramref name="action"/> once the app is built, before the first tick.</summary>
    public EngineBuilder OnStart(Action<App> action)
    {
        StartActions.Add(action);
        return this;
    }

    /// <summary>Run <paramref name="action"/> when the app is disposed (in reverse registration order).</summary>
    public EngineBuilder OnStop(Action<App> action)
    {
        StopActions.Add(action);
        return this;
    }

    public App Build() => new(this);
}

/// <summary>App-level requests systems can make, as a world resource.</summary>
public sealed class AppControl
{
    /// <summary>Ask the host to stop calling Tick (e.g. a quit button or the Escape key).</summary>
    public bool QuitRequested;
}

/// <summary>
/// A running engine instance. It never owns a loop: whatever owns frame timing (a desktop window's vsync, the
/// browser, an XR runtime) calls <see cref="Tick"/> once per frame.
/// </summary>
public sealed class App : IDisposable
{
    private readonly List<Action<App>> _stop;
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _last;
    private long _accumulatorNs; // integer nanoseconds: no floating-point drift in the step count
    private readonly long _fixedStepNs;
    private bool _disposed;

    internal App(EngineBuilder b)
    {
        World = b.World;
        Schedule = b.Schedule;
        Jobs = new JobScheduler(b.WorkerThreads);
        World.Jobs = Jobs;
        _fixedStepNs = (long)Math.Round(1e9 / b.FixedRate);
        FixedStep = _fixedStepNs / 1e9;
        _stop = b.StopActions;
        if (!World.HasResource<Time>()) World.SetResource(new Time { FixedDelta = (float)FixedStep });
        World.SetResource(new AppControl());
        Schedule.Initialize(World);
        foreach (var a in b.StartActions) a(this);
        _last = Stopwatch.GetTimestamp();
    }

    public World World { get; }
    public Schedule Schedule { get; }
    public JobScheduler Jobs { get; }
    public double FixedStep { get; }

    /// <summary>Set by anything (a window close, a game menu) to ask the host to stop calling Tick.</summary>
    public bool QuitRequested
    {
        get => World.Resource<AppControl>().QuitRequested;
        set => World.Resource<AppControl>().QuitRequested = value;
    }

    /// <summary>The most fixed steps one frame may run; beyond this the simulation slows instead of spiralling.</summary>
    public int MaxFixedStepsPerFrame { get; set; } = 8;

    /// <summary>Advance one frame, measuring elapsed time with the system clock.</summary>
    public void Tick()
    {
        long now = Stopwatch.GetTimestamp();
        double dt = Stopwatch.GetElapsedTime(_last, now).TotalSeconds;
        _last = now;
        Tick(dt);
    }

    /// <summary>Advance one frame by <paramref name="deltaSeconds"/> (a host with its own clock passes it in).</summary>
    public void Tick(double deltaSeconds)
    {
        ref var time = ref World.Resource<Time>();
        time.Delta = (float)deltaSeconds;
        time.Elapsed += deltaSeconds;
        time.Frame++;

        Schedule.Run(Phase.First, World);

        _accumulatorNs += (long)Math.Round(deltaSeconds * 1e9);
        int steps = 0;
        while (_accumulatorNs >= _fixedStepNs && steps < MaxFixedStepsPerFrame)
        {
            Schedule.Run(Phase.FixedUpdate, World);
            World.Resource<Time>().Tick++;
            _accumulatorNs -= _fixedStepNs;
            steps++;
        }
        if (steps == MaxFixedStepsPerFrame && _accumulatorNs > _fixedStepNs) _accumulatorNs = _fixedStepNs;
        World.Resource<Time>().FixedAlpha = (float)((double)_accumulatorNs / _fixedStepNs);

        Schedule.Run(Phase.Update, World);
        Schedule.Run(Phase.PostUpdate, World);
        Schedule.Run(Phase.Render, World);
        Schedule.Run(Phase.Last, World);
    }

    public TimeSpan Uptime => Stopwatch.GetElapsedTime(_start);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _stop.Count - 1; i >= 0; i--) _stop[i](this);
        Jobs.Dispose();
        World.Dispose();
    }
}
