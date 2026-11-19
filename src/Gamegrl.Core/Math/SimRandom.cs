using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// A seeded random generator for the simulation (xoshiro128**): same seed, same sequence on every platform, and a
/// 16-byte value type, so it snapshots and rolls back with the component or resource that holds it.
/// <see cref="System.Random"/> is neither specified across runtimes nor cheap to copy.
/// </summary>
public struct SimRandom
{
    private uint _s0, _s1, _s2, _s3;

    public SimRandom(ulong seed)
    {
        // SplitMix64 spreads any seed (including 0) over the state.
        _s0 = (uint)Next(ref seed); _s1 = (uint)(seed >> 32);
        _s2 = (uint)Next(ref seed); _s3 = (uint)(seed >> 32);
        if ((_s0 | _s1 | _s2 | _s3) == 0) _s0 = 1;

        static ulong Next(ref ulong x)
        {
            ulong z = x += 0x9E3779B97F4A7C15;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EB;
            return z ^ (z >> 31);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt()
    {
        uint result = uint.RotateLeft(_s1 * 5, 7) * 9;
        uint t = _s1 << 9;
        _s2 ^= _s0; _s3 ^= _s1; _s1 ^= _s2; _s0 ^= _s3;
        _s2 ^= t;
        _s3 = uint.RotateLeft(_s3, 11);
        return result;
    }

    /// <summary>A float in [0, 1) with 24 random bits (exact, no rounding differences).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / (1 << 24));

    public float Range(float min, float max) => min + (max - min) * NextFloat();

    /// <summary>An integer in [min, max), without modulo bias.</summary>
    public int Range(int min, int max)
    {
        uint span = (uint)(max - min);
        if (span == 0) return min;
        ulong m = (ulong)NextUInt() * span;
        if ((uint)m < span)
        {
            uint threshold = (uint)-span % span;
            while ((uint)m < threshold) m = (ulong)NextUInt() * span;
        }
        return min + (int)(m >> 32);
    }
}
