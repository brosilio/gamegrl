using System.Runtime.InteropServices;

namespace Gamegrl.Audio;

/// <summary>A counter alone on its cache line (nested in a generic type it couldn't have explicit layout).</summary>
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct PaddedIndex { [FieldOffset(64)] public long Value; }

/// <summary>
/// A bounded lock-free queue for exactly one producer thread and one consumer thread (the game and the audio thread).
/// Push and pop never allocate, lock or wait. The indices live on separate cache lines so the two threads don't share one.
/// </summary>
public sealed class SpscRing<T> where T : struct
{
    private readonly T[] _items;
    private readonly int _mask;
    private PaddedIndex _head; // next slot to read: written by the consumer
    private PaddedIndex _tail; // next slot to write: written by the producer

    public SpscRing(int capacity)
    {
        int size = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)Math.Max(2, capacity));
        _items = new T[size];
        _mask = size - 1;
    }

    public int Capacity => _items.Length;
    /// <summary>Approximate when read by a thread that's neither side.</summary>
    public int Count => (int)(Volatile.Read(ref _tail.Value) - Volatile.Read(ref _head.Value));

    /// <summary>Producer only. False when full.</summary>
    public bool TryPush(in T item)
    {
        long tail = _tail.Value;
        if (tail - Volatile.Read(ref _head.Value) == _items.Length) return false;
        _items[tail & _mask] = item;
        Volatile.Write(ref _tail.Value, tail + 1); // publishes the item
        return true;
    }

    /// <summary>Consumer only. False when empty.</summary>
    public bool TryPop(out T item)
    {
        long head = _head.Value;
        if (head == Volatile.Read(ref _tail.Value)) { item = default; return false; }
        ref var slot = ref _items[head & _mask];
        item = slot;
        slot = default; // drop references (clips) the queue would otherwise keep alive
        Volatile.Write(ref _head.Value, head + 1); // frees the slot
        return true;
    }
}
