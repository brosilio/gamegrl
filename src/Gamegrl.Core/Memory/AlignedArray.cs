using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl.Core.Memory;

/// <summary>
/// A fixed-length array of unmanaged values whose first element starts on a cache line (64 bytes). Managed arrays are
/// only 8-byte aligned, so 16- and 32-byte records in them straddle cache lines; SIMD loops over such records pay for
/// it. The storage lives on the pinned object heap and never moves, so <see cref="Pointer"/> stays valid for the
/// array's life, and the GC frees it like any other object.
/// </summary>
public sealed unsafe class AlignedArray<T> where T : unmanaged
{
    private const int Alignment = 64;
    private readonly byte[] _storage;
    private readonly T* _first;

    /// <summary>Zeroed, <paramref name="length"/> elements long.</summary>
    public AlignedArray(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        Length = length;
        _storage = GC.AllocateArray<byte>(checked(length * sizeof(T) + Alignment), pinned: true);
        nint start = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_storage));
        _first = (T*)((start + Alignment - 1) & ~(nint)(Alignment - 1));
    }

    public int Length { get; }

    /// <summary>The first element, aligned to 64 bytes. Valid while this array is reachable.</summary>
    public T* Pointer => _first;

    public ref T this[int index]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            if ((uint)index >= (uint)Length) ThrowIndex();
            return ref _first[index];
        }
    }

    public Span<T> AsSpan() => new(_first, Length);

    public AlignedArray<T> Clone()
    {
        var copy = new AlignedArray<T>(Length);
        AsSpan().CopyTo(copy.AsSpan());
        return copy;
    }

    /// <summary>A new array of <paramref name="length"/> elements starting with this one's (the rest zero).</summary>
    public AlignedArray<T> Resized(int length)
    {
        var copy = new AlignedArray<T>(length);
        AsSpan()[..Math.Min(Length, length)].CopyTo(copy.AsSpan());
        return copy;
    }

    private static void ThrowIndex() => throw new IndexOutOfRangeException();
}
