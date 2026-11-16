using System.Numerics;
using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// Marks a struct as a component. Components are unmanaged (no references), so a world's state is plain memory:
/// it can be copied, snapshotted for rollback and play mode, and handed to jobs as raw pointers.
/// </summary>
public interface IComponent;

/// <summary>Process-wide registry of component types: id, size, name.</summary>
public static class ComponentRegistry
{
    private static readonly Lock Gate = new();
    private static ComponentInfo[] _infos = new ComponentInfo[64];
    private static int _count;

    public static int Count => Volatile.Read(ref _count);

    public static ComponentInfo Get(int id) => _infos[id];

    internal static int Register(string name, int size)
    {
        lock (Gate)
        {
            if (_count >= ComponentSet.Capacity)
                throw new InvalidOperationException($"More than {ComponentSet.Capacity} component types are registered.");
            if (_count == _infos.Length) Array.Resize(ref _infos, _infos.Length * 2);
            _infos[_count] = new ComponentInfo(_count, name, size);
            return _count++;
        }
    }
}

public readonly record struct ComponentInfo(int Id, string Name, int Size);

/// <summary>
/// The id of component type <typeparamref name="T"/>, assigned on first use.
/// TODO(milestone 2): ids assigned in first-use order differ between runs; serialization and networking need
/// stable ids (a hash of the full type name), which the generator will emit.
/// </summary>
public static class Component<T> where T : unmanaged, IComponent
{
    public static readonly int Id = ComponentRegistry.Register(typeof(T).FullName ?? typeof(T).Name, Unsafe.SizeOf<T>());
}

[InlineArray(4)]
internal struct Bits256
{
    private ulong _element;
}

/// <summary>A set of component ids (a 256-bit mask): an archetype's signature, or a query's filter.</summary>
public struct ComponentSet : IEquatable<ComponentSet>
{
    public const int Capacity = 256;

    private Bits256 _bits;

    public static ComponentSet Of<T>() where T : unmanaged, IComponent
    {
        var s = default(ComponentSet);
        s.Add(Component<T>.Id);
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Add(int id) => _bits[id >> 6] |= 1UL << (id & 63);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove(int id) => _bits[id >> 6] &= ~(1UL << (id & 63));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Contains(int id) => (_bits[id >> 6] & (1UL << (id & 63))) != 0;

    public readonly ComponentSet With(int id)
    {
        var s = this;
        s.Add(id);
        return s;
    }

    public readonly ComponentSet Without(int id)
    {
        var s = this;
        s.Remove(id);
        return s;
    }

    /// <summary>True if every id in <paramref name="other"/> is in this set.</summary>
    public readonly bool ContainsAll(in ComponentSet other)
    {
        for (int i = 0; i < 4; i++)
            if ((_bits[i] & other._bits[i]) != other._bits[i]) return false;
        return true;
    }

    public readonly bool Intersects(in ComponentSet other)
    {
        for (int i = 0; i < 4; i++)
            if ((_bits[i] & other._bits[i]) != 0) return true;
        return false;
    }

    public readonly int Count
    {
        get
        {
            int n = 0;
            for (int i = 0; i < 4; i++) n += BitOperations.PopCount(_bits[i]);
            return n;
        }
    }

    public readonly bool IsEmpty => _bits[0] == 0 && _bits[1] == 0 && _bits[2] == 0 && _bits[3] == 0;

    /// <summary>The ids in ascending order.</summary>
    public readonly int[] ToArray()
    {
        var ids = new int[Count];
        int n = 0;
        for (int w = 0; w < 4; w++)
        {
            ulong bits = _bits[w];
            while (bits != 0)
            {
                ids[n++] = (w << 6) | BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
            }
        }
        return ids;
    }

    public readonly bool Equals(ComponentSet other) =>
        _bits[0] == other._bits[0] && _bits[1] == other._bits[1] && _bits[2] == other._bits[2] && _bits[3] == other._bits[3];

    public override readonly bool Equals(object? obj) => obj is ComponentSet s && Equals(s);
    public override readonly int GetHashCode() => HashCode.Combine(_bits[0], _bits[1], _bits[2], _bits[3]);
    public static bool operator ==(ComponentSet a, ComponentSet b) => a.Equals(b);
    public static bool operator !=(ComponentSet a, ComponentSet b) => !a.Equals(b);
}
