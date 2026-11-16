using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl;

/// <summary>
/// All entities with exactly one set of components. Each component type is a column of tightly packed values in
/// 64-byte-aligned native memory (struct-of-arrays), so iterating a component is a linear walk and columns can be
/// handed to jobs as raw pointers. Rows are dense: removing one moves the last row into its place.
/// </summary>
public sealed unsafe class Archetype : IDisposable
{
    private const int InitialCapacity = 16;

    public readonly int Id;
    public readonly ComponentSet Set;

    /// <summary>The component ids of the columns, ascending.</summary>
    internal readonly int[] Types;
    internal readonly int[] Sizes;
    internal readonly byte*[] Columns;
    private readonly short[] _columnOf = new short[ComponentSet.Capacity];

    private Entity[] _entities = new Entity[InitialCapacity];
    private int _count;
    private int _capacity;

    // Cached structural edges: the archetype reached by adding or removing one component.
    internal readonly Dictionary<int, Archetype> AddEdges = new();
    internal readonly Dictionary<int, Archetype> RemoveEdges = new();

    internal Archetype(int id, in ComponentSet set)
    {
        Id = id;
        Set = set;
        Types = set.ToArray();
        Sizes = new int[Types.Length];
        Columns = new byte*[Types.Length];
        _columnOf.AsSpan().Fill(-1);
        _capacity = InitialCapacity;
        for (int c = 0; c < Types.Length; c++)
        {
            _columnOf[Types[c]] = (short)c;
            Sizes[c] = ComponentRegistry.Get(Types[c]).Size;
            Columns[c] = (byte*)NativeMemory.AlignedAlloc((nuint)(_capacity * Sizes[c]), 64);
        }
    }

    /// <summary>The number of entities (rows).</summary>
    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _count;
    }

    public ReadOnlySpan<Entity> Entities => new(_entities, 0, _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ColumnOf(int componentId) => _columnOf[componentId];

    public bool Has<T>() where T : unmanaged, IComponent => _columnOf[Component<T>.Id] >= 0;

    /// <summary>The column of <typeparamref name="T"/> as a pointer; valid until the next structural change.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T* GetPointer<T>() where T : unmanaged, IComponent
    {
        int c = _columnOf[Component<T>.Id];
        if (c < 0) ThrowMissing(typeof(T));
        return (T*)Columns[c];
    }

    /// <summary>The column of <typeparamref name="T"/>; valid until the next structural change.</summary>
    public Span<T> GetSpan<T>() where T : unmanaged, IComponent => new(GetPointer<T>(), _count);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal byte* Ptr(int column, int row) => Columns[column] + (nint)row * Sizes[column];

    /// <summary>Append a zeroed row for <paramref name="e"/>; returns its index.</summary>
    internal int AddRow(Entity e)
    {
        if (_count == _capacity) Grow();
        int row = _count++;
        _entities[row] = e;
        for (int c = 0; c < Columns.Length; c++)
            NativeMemory.Clear(Ptr(c, row), (nuint)Sizes[c]);
        return row;
    }

    /// <summary>
    /// Remove <paramref name="row"/> by moving the last row into it. Returns the entity that moved into
    /// <paramref name="row"/>, or default if the removed row was the last.
    /// </summary>
    internal Entity RemoveRow(int row)
    {
        int last = --_count;
        if (row == last)
        {
            _entities[last] = default;
            return default;
        }
        for (int c = 0; c < Columns.Length; c++)
            Buffer.MemoryCopy(Ptr(c, last), Ptr(c, row), Sizes[c], Sizes[c]);
        var moved = _entities[last];
        _entities[row] = moved;
        _entities[last] = default;
        return moved;
    }

    private void Grow()
    {
        int capacity = _capacity * 2;
        for (int c = 0; c < Columns.Length; c++)
            Columns[c] = (byte*)NativeMemory.AlignedRealloc(Columns[c], (nuint)(capacity * Sizes[c]), 64);
        Array.Resize(ref _entities, capacity);
        _capacity = capacity;
    }

    private static void ThrowMissing(Type t) => throw new InvalidOperationException($"The archetype has no {t.Name} column.");

    public void Dispose()
    {
        for (int c = 0; c < Columns.Length; c++)
        {
            if (Columns[c] != null) NativeMemory.AlignedFree(Columns[c]);
            Columns[c] = null;
        }
        _count = 0;
    }
}
