using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// Entities, their components (in archetype storage) and the world's resources.
/// Structural changes (spawn, destroy, add or remove a component) are not allowed while a system iterates;
/// record them in <see cref="Commands"/> instead, which the schedule plays back at sync points.
/// Not thread-safe: structural changes happen on one thread; jobs may read and write component data in place.
/// </summary>
public sealed unsafe class World : IDisposable
{
    private struct Record
    {
        public Archetype? Archetype; // null: reserved by a command buffer, not placed yet
        public int Row;
        public uint Generation;
    }

    private Record[] _records = new Record[1024];
    private int _recordCount;
    private int[] _free = new int[256];
    private int _freeCount;
    private int _alive;

    private readonly List<Archetype> _archetypes = new();
    private readonly Dictionary<ComponentSet, Archetype> _bySet = new();
    private readonly Dictionary<QueryDesc, Query> _queries = new();
    private readonly Archetype _empty;
    private int _iterating;

    private object?[] _resources = new object?[16];

    public World()
    {
        _empty = GetOrCreateArchetype(default);
        Commands = new CommandBuffer(this);
    }

    /// <summary>The world's default command buffer, played back by the schedule after each system.</summary>
    public CommandBuffer Commands { get; }

    /// <summary>The job scheduler parallel systems use; set by the app.</summary>
    public JobScheduler? Jobs { get; set; }

    public int EntityCount => _alive;

    public IReadOnlyList<Archetype> Archetypes => _archetypes;

    // ------------------------------------------------------------------ entities

    public Entity Spawn()
    {
        AssertNotIterating();
        var e = Allocate();
        Place(e, _empty);
        return e;
    }

    public Entity Spawn<T1>(in T1 c1)
        where T1 : unmanaged, IComponent
    {
        AssertNotIterating();
        var set = default(ComponentSet);
        set.Add(Component<T1>.Id);
        var a = GetOrCreateArchetype(set);
        var e = Allocate();
        int row = Place(e, a);
        Write(a, row, c1);
        return e;
    }

    public Entity Spawn<T1, T2>(in T1 c1, in T2 c2)
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
    {
        AssertNotIterating();
        var set = default(ComponentSet);
        set.Add(Component<T1>.Id);
        set.Add(Component<T2>.Id);
        var a = GetOrCreateArchetype(set);
        var e = Allocate();
        int row = Place(e, a);
        Write(a, row, c1);
        Write(a, row, c2);
        return e;
    }

    public Entity Spawn<T1, T2, T3>(in T1 c1, in T2 c2, in T3 c3)
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent where T3 : unmanaged, IComponent
    {
        AssertNotIterating();
        var set = default(ComponentSet);
        set.Add(Component<T1>.Id);
        set.Add(Component<T2>.Id);
        set.Add(Component<T3>.Id);
        var a = GetOrCreateArchetype(set);
        var e = Allocate();
        int row = Place(e, a);
        Write(a, row, c1);
        Write(a, row, c2);
        Write(a, row, c3);
        return e;
    }

    public Entity Spawn<T1, T2, T3, T4>(in T1 c1, in T2 c2, in T3 c3, in T4 c4)
        where T1 : unmanaged, IComponent where T2 : unmanaged, IComponent
        where T3 : unmanaged, IComponent where T4 : unmanaged, IComponent
    {
        AssertNotIterating();
        var set = default(ComponentSet);
        set.Add(Component<T1>.Id);
        set.Add(Component<T2>.Id);
        set.Add(Component<T3>.Id);
        set.Add(Component<T4>.Id);
        var a = GetOrCreateArchetype(set);
        var e = Allocate();
        int row = Place(e, a);
        Write(a, row, c1);
        Write(a, row, c2);
        Write(a, row, c3);
        Write(a, row, c4);
        return e;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool IsAlive(Entity e) =>
        e.Index < (uint)_recordCount && _records[e.Index].Generation == e.Generation && !e.IsNull;

    public void Destroy(Entity e)
    {
        AssertNotIterating();
        ref var r = ref RecordOf(e);
        if (r.Archetype is { } a) Unplace(a, r.Row);
        r.Archetype = null;
        r.Generation = r.Generation == uint.MaxValue ? 1 : r.Generation + 1;
        if (_freeCount == _free.Length) Array.Resize(ref _free, _free.Length * 2);
        _free[_freeCount++] = (int)e.Index;
        _alive--;
    }

    // ------------------------------------------------------------------ components

    public bool Has<T>(Entity e) where T : unmanaged, IComponent =>
        RecordOf(e).Archetype is { } a && a.ColumnOf(Component<T>.Id) >= 0;

    /// <summary>A reference to <paramref name="e"/>'s <typeparamref name="T"/>; valid until the next structural change.</summary>
    public ref T Get<T>(Entity e) where T : unmanaged, IComponent
    {
        ref var r = ref RecordOf(e);
        var a = r.Archetype;
        int c = a is null ? -1 : a.ColumnOf(Component<T>.Id);
        if (c < 0) ThrowMissing(e, typeof(T));
        return ref *(T*)a!.Ptr(c, r.Row);
    }

    /// <summary>Add <typeparamref name="T"/> to <paramref name="e"/>, or overwrite it if present.</summary>
    public void Add<T>(Entity e, in T value) where T : unmanaged, IComponent
    {
        T copy = value;
        AddRaw(e, Component<T>.Id, &copy);
    }

    public void Remove<T>(Entity e) where T : unmanaged, IComponent => RemoveRaw(e, Component<T>.Id);

    internal void AddRaw(Entity e, int componentId, void* data)
    {
        AssertNotIterating();
        ref var r = ref RecordOf(e);
        if (r.Archetype is null) Place(e, _empty);
        var src = r.Archetype!;
        int c = src.ColumnOf(componentId);
        if (c < 0)
        {
            if (!src.AddEdges.TryGetValue(componentId, out var dst))
            {
                dst = GetOrCreateArchetype(src.Set.With(componentId));
                src.AddEdges[componentId] = dst;
                dst.RemoveEdges[componentId] = src;
            }
            Move(e, ref r, dst);
            c = dst.ColumnOf(componentId);
        }
        var a = r.Archetype!;
        int size = a.Sizes[c];
        Buffer.MemoryCopy(data, a.Ptr(c, r.Row), size, size);
    }

    internal void RemoveRaw(Entity e, int componentId)
    {
        AssertNotIterating();
        ref var r = ref RecordOf(e);
        var src = r.Archetype;
        if (src is null || src.ColumnOf(componentId) < 0) return;
        if (!src.RemoveEdges.TryGetValue(componentId, out var dst))
        {
            dst = GetOrCreateArchetype(src.Set.Without(componentId));
            src.RemoveEdges[componentId] = dst;
            dst.AddEdges[componentId] = src;
        }
        Move(e, ref r, dst);
    }

    // ------------------------------------------------------------------ queries

    /// <summary>The cached query for <paramref name="desc"/> (one per distinct description).</summary>
    public Query Query(in QueryDesc desc)
    {
        if (_queries.TryGetValue(desc, out var q)) return q;
        q = new Query(this, desc);
        foreach (var a in _archetypes) q.Consider(a);
        _queries[desc] = q;
        return q;
    }

    /// <summary>Called by generated systems around iteration: structural changes inside throw.</summary>
    public void BeginIteration() => _iterating++;

    public void EndIteration() => _iterating--;

    // ------------------------------------------------------------------ resources

    private sealed class ResourceBox<T>
    {
        public T Value = default!;
    }

    private static class ResourceId<T>
    {
        public static readonly int Value = Interlocked.Increment(ref _nextResourceId) - 1;
    }

    private static int _nextResourceId;

    /// <summary>Set (or replace) the world's resource of type <typeparamref name="T"/>.</summary>
    public void SetResource<T>(T value)
    {
        int id = ResourceId<T>.Value;
        if (id >= _resources.Length) Array.Resize(ref _resources, Math.Max(id + 1, _resources.Length * 2));
        if (_resources[id] is ResourceBox<T> box) box.Value = value;
        else _resources[id] = new ResourceBox<T> { Value = value };
    }

    public bool HasResource<T>()
    {
        int id = ResourceId<T>.Value;
        return id < _resources.Length && _resources[id] is not null;
    }

    /// <summary>A reference to the resource of type <typeparamref name="T"/>; throws if it was never set.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Resource<T>()
    {
        int id = ResourceId<T>.Value;
        if (id >= _resources.Length || _resources[id] is null) ThrowNoResource(typeof(T));
        return ref Unsafe.As<ResourceBox<T>>(_resources[id]!).Value;
    }

    // ------------------------------------------------------------------ internals

    /// <summary>Allocate an entity id without placing it: what command buffers hand out for deferred spawns.</summary>
    internal Entity Reserve()
    {
        var e = Allocate();
        _records[e.Index].Archetype = null;
        return e;
    }

    internal void PlaceReserved(Entity e)
    {
        if (_records[e.Index].Archetype is null) Place(e, _empty);
    }

    private Entity Allocate()
    {
        int index;
        if (_freeCount > 0) index = _free[--_freeCount];
        else
        {
            if (_recordCount == _records.Length) Array.Resize(ref _records, _records.Length * 2);
            index = _recordCount++;
        }
        ref var r = ref _records[index];
        if (r.Generation == 0) r.Generation = 1;
        _alive++;
        return new Entity((uint)index, r.Generation);
    }

    private int Place(Entity e, Archetype a)
    {
        int row = a.AddRow(e);
        ref var r = ref _records[e.Index];
        r.Archetype = a;
        r.Row = row;
        return row;
    }

    private void Unplace(Archetype a, int row)
    {
        var moved = a.RemoveRow(row);
        if (!moved.IsNull) _records[moved.Index].Row = row;
    }

    private void Move(Entity e, ref Record r, Archetype dst)
    {
        var src = r.Archetype!;
        int srcRow = r.Row;
        int dstRow = dst.AddRow(e);
        for (int c = 0; c < src.Types.Length; c++)
        {
            int dc = dst.ColumnOf(src.Types[c]);
            if (dc >= 0) Buffer.MemoryCopy(src.Ptr(c, srcRow), dst.Ptr(dc, dstRow), src.Sizes[c], src.Sizes[c]);
        }
        Unplace(src, srcRow);
        r.Archetype = dst;
        r.Row = dstRow;
    }

    private static void Write<T>(Archetype a, int row, in T value) where T : unmanaged, IComponent =>
        *(T*)a.Ptr(a.ColumnOf(Component<T>.Id), row) = value;

    private Archetype GetOrCreateArchetype(in ComponentSet set)
    {
        if (_bySet.TryGetValue(set, out var a)) return a;
        a = new Archetype(_archetypes.Count, set);
        _archetypes.Add(a);
        _bySet[set] = a;
        foreach (var q in _queries.Values) q.Consider(a);
        return a;
    }

    private ref Record RecordOf(Entity e)
    {
        if (!IsAlive(e)) ThrowDead(e);
        return ref _records[e.Index];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void AssertNotIterating()
    {
        if (_iterating != 0) ThrowIterating();
    }

    private static void ThrowIterating() =>
        throw new InvalidOperationException("Structural change during iteration: use World.Commands (a CommandBuffer) instead.");

    private static void ThrowDead(Entity e) => throw new InvalidOperationException($"{e} is not alive.");

    private static void ThrowMissing(Entity e, Type t) => throw new InvalidOperationException($"{e} has no {t.Name}.");

    private static void ThrowNoResource(Type t) => throw new InvalidOperationException($"The world has no {t.Name} resource.");

    public void Dispose()
    {
        foreach (var a in _archetypes) a.Dispose();
        _archetypes.Clear();
        _bySet.Clear();
    }
}
