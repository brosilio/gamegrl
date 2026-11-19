using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// A 64-bit hash of a world's simulation state: every entity handle and every component's bytes, archetype by
/// archetype in creation order, columns in stable-id order (so component registration order doesn't matter).
/// Two worlds that took the same structural changes and computed the same values hash equal; replay tests compare it
/// per tick. Not cryptographic, not stable across engine versions. Allocation-free.
/// </summary>
public static unsafe class WorldHash
{
    private static readonly HashSet<ulong> Ignored = new();
    private static readonly Lock Gate = new();

    static WorldHash() => Ignore<LocalToWorld>();

    /// <summary>
    /// Leave a component out of the hash: derived, presentation-only data (like <see cref="LocalToWorld"/>, which blends
    /// with the frame's interpolation factor and uses platform matrix math) that the simulation never reads.
    /// </summary>
    public static void Ignore<T>() where T : unmanaged, IComponent
    {
        lock (Gate) Ignored.Add(ComponentRegistry.Get(Component<T>.Id).StableId);
    }

    public static ulong Compute(World world)
    {
        var h = new Hasher(0x6767726c);
        Span<int> order = stackalloc int[ComponentSet.Capacity];
        foreach (var archetype in world.Archetypes)
        {
            int count = archetype.Count;
            if (count == 0) continue;
            int columns = archetype.Types.Length;
            for (int c = 0; c < columns; c++) order[c] = c;
            // Insertion sort by stable id: archetypes have a handful of columns.
            for (int i = 1; i < columns; i++)
            {
                int c = order[i], j = i - 1;
                ulong id = ComponentRegistry.Get(archetype.Types[c]).StableId;
                while (j >= 0 && ComponentRegistry.Get(archetype.Types[order[j]]).StableId > id) { order[j + 1] = order[j]; j--; }
                order[j + 1] = c;
            }
            h.Add((ulong)count);
            var entities = archetype.Entities;
            fixed (Entity* e = entities) h.Add(new ReadOnlySpan<byte>(e, count * sizeof(Entity)));
            for (int i = 0; i < columns; i++)
            {
                int c = order[i];
                ulong stable = ComponentRegistry.Get(archetype.Types[c]).StableId;
                if (Ignored.Contains(stable)) continue;
                h.Add(stable);
                byte* column = archetype.Columns[c];
                int size = archetype.Sizes[c];
                if (ComponentRegistry.Get(archetype.Types[c]).DataRanges is { } ranges)
                {
                    // Skip padding: its bytes depend on how the compiler wrote the struct, not on the simulation.
                    for (int row = 0; row < count; row++)
                        for (int k = 0; k < ranges.Length; k += 2) h.Add(new ReadOnlySpan<byte>(column + row * size + ranges[k], ranges[k + 1]));
                }
                else h.Add(new ReadOnlySpan<byte>(column, count * size));
            }
        }
        return h.Finish();
    }

    /// <summary>Hash an unmanaged value (a resource, say) into a running hash.</summary>
    public static ulong Combine<T>(ulong hash, in T value) where T : unmanaged
    {
        var h = new Hasher(hash);
        fixed (T* p = &value) h.Add(new ReadOnlySpan<byte>(p, sizeof(T)));
        return h.Finish();
    }

    /// <summary>A streaming 64-bit hash in the style of xxHash64's lane round: one multiply-rotate-multiply per word.</summary>
    private struct Hasher(ulong seed)
    {
        private const ulong P1 = 11400714785074694791UL, P2 = 14029467366897019727UL, P3 = 1609587929392839161UL, P4 = 9650029242287828579UL, P5 = 2870177450012600261UL;
        private ulong _h = seed + P5;
        private ulong _length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static ulong Round(ulong acc, ulong word) => BitOperations.RotateLeft(acc ^ BitOperations.RotateLeft(word * P2, 31) * P1, 27) * P1 + P4;

        public void Add(ulong word) { _h = Round(_h, word); _length += 8; }

        public void Add(ReadOnlySpan<byte> bytes)
        {
            ulong h = _h;
            int i = 0;
            for (; i + 8 <= bytes.Length; i += 8) h = Round(h, BinaryPrimitives.ReadUInt64LittleEndian(bytes[i..]));
            ulong tail = 0;
            for (int shift = 0; i < bytes.Length; i++, shift += 8) tail |= (ulong)bytes[i] << shift;
            if ((bytes.Length & 7) != 0) h = Round(h, tail ^ P3);
            _h = h;
            _length += (ulong)bytes.Length;
        }

        public readonly ulong Finish()
        {
            ulong h = _h ^ _length;
            h ^= h >> 33; h *= P2;
            h ^= h >> 29; h *= P3;
            return h ^ (h >> 32);
        }
    }
}

/// <summary>Records <see cref="WorldHash"/> after every fixed step (add last in <see cref="Phase.FixedUpdate"/>).</summary>
public sealed class TickHashRecorder : ISystem
{
    public readonly List<ulong> Hashes = new();
    public string Name => "Determinism.TickHash";
    public Phase Phase => Phase.FixedUpdate;
    public int Order => int.MaxValue;
    public void Initialize(World world) { }
    public void Run(World world) => Hashes.Add(WorldHash.Compute(world));
}
