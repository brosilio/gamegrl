namespace Gamegrl;

/// <summary>When in a frame a system runs. Phases run in this order every <see cref="App.Tick"/>.</summary>
public enum Phase
{
    /// <summary>Start of the frame: input, platform events.</summary>
    First,
    /// <summary>The fixed-rate simulation step; runs zero or more times per frame.</summary>
    FixedUpdate,
    /// <summary>Variable-rate gameplay.</summary>
    Update,
    /// <summary>After gameplay: transform propagation and other derived data.</summary>
    PostUpdate,
    /// <summary>Rendering (extract and submit).</summary>
    Render,
    /// <summary>End of the frame.</summary>
    Last,
}

/// <summary>
/// Marks a static method as a system. The method's parameters say what it touches, and the source generator
/// writes the loop and the registration:
/// <list type="bullet">
/// <item><c>ref T</c>: a component it writes; <c>in T</c>: a component it reads. Each runs once per entity that has all of them.</item>
/// <item><c>[Res] ref T</c> / <c>[Res] in T</c>: a world resource.</item>
/// <item><c>Entity</c>: the current entity. <c>CommandBuffer</c>: the world's deferred structural changes.</item>
/// </list>
/// The containing type must be <c>static partial</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SystemAttribute(Phase phase = Phase.Update) : Attribute
{
    public Phase Phase { get; } = phase;

    /// <summary>Split the entities across worker threads. Only for systems without Entity/CommandBuffer parameters.</summary>
    public bool Parallel { get; set; }

    /// <summary>Order within the phase; lower runs first. Ties keep registration order.</summary>
    public int Order { get; set; }
}

/// <summary>Marks a system parameter as a world resource rather than a component.</summary>
[AttributeUsage(AttributeTargets.Parameter)]
public sealed class ResAttribute : Attribute;

/// <summary>Excludes entities that have <typeparamref name="T"/> from a system's query.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithoutAttribute<T> : Attribute where T : unmanaged, IComponent;

/// <summary>Requires <typeparamref name="T"/> on matched entities without passing it to the system (a tag filter).</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class WithAttribute<T> : Attribute where T : unmanaged, IComponent;

/// <summary>A unit of per-frame logic, normally generated from a <see cref="SystemAttribute"/> method.</summary>
public interface ISystem
{
    string Name { get; }
    Phase Phase { get; }
    int Order { get; }
    /// <summary>Whose time the system's zone counts as. Generated systems outside the engine's assemblies say Game.</summary>
    ProfileCategory Category => ProfileCategory.Engine;
    void Initialize(World world);
    void Run(World world);
}

/// <summary>Holds the systems per phase and runs them in order, playing back commands after each.</summary>
public sealed class Schedule
{
    private readonly List<ISystem>[] _phases;
    private readonly List<ProfileMarker>[] _markers;
    private static readonly ProfileMarker Playback = ProfileMarker.Get("Commands.Playback", ProfileCategory.Engine);
    private bool _initialized;
    private World? _world;

    public Schedule()
    {
        _phases = new List<ISystem>[Enum.GetValues<Phase>().Length];
        _markers = new List<ProfileMarker>[_phases.Length];
        for (int i = 0; i < _phases.Length; i++) { _phases[i] = new List<ISystem>(); _markers[i] = new List<ProfileMarker>(); }
    }

    /// <summary>Add a system; after the app is built it's initialized immediately (the editor adds game systems this way).</summary>
    public void Add(ISystem system)
    {
        var list = _phases[(int)system.Phase];
        int at = list.Count;
        while (at > 0 && list[at - 1].Order > system.Order) at--;
        list.Insert(at, system);
        _markers[(int)system.Phase].Insert(at, ProfileMarker.Get(system.Name, system.Category));
        if (_initialized) system.Initialize(_world!);
    }

    public bool Remove(ISystem system)
    {
        var list = _phases[(int)system.Phase];
        int at = list.IndexOf(system);
        if (at < 0) return false;
        list.RemoveAt(at);
        _markers[(int)system.Phase].RemoveAt(at);
        return true;
    }

    public IReadOnlyList<ISystem> SystemsIn(Phase phase) => _phases[(int)phase];

    internal void Initialize(World world)
    {
        foreach (var list in _phases)
            foreach (var s in list) s.Initialize(world);
        _world = world;
        _initialized = true;
    }

    public void Run(Phase phase, World world)
    {
        var list = _phases[(int)phase];
        var markers = _markers[(int)phase];
        for (int i = 0; i < list.Count; i++)
        {
            using (markers[i].Scope()) list[i].Run(world);
            if (!world.Commands.IsEmpty)
                using (Playback.Scope()) world.Commands.Playback();
        }
    }
}

/// <summary>Frame and simulation timing, available to systems as a resource.</summary>
public struct Time
{
    /// <summary>Seconds since the previous frame (variable rate).</summary>
    public float Delta;
    /// <summary>Seconds since the app started.</summary>
    public double Elapsed;
    public ulong Frame;

    /// <summary>The fixed simulation step in seconds; <see cref="Phase.FixedUpdate"/> systems should use this.</summary>
    public float FixedDelta;
    /// <summary>Fixed steps run since the app started (the simulation tick).</summary>
    public ulong Tick;
    /// <summary>How far the current frame is between the last fixed step and the next (0..1), for interpolation.</summary>
    public float FixedAlpha;
}
