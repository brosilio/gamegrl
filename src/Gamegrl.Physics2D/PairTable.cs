using System.Runtime.CompilerServices;

namespace Gamegrl.Physics2D;

/// <summary>
/// Shape pair → contact, as an open-addressing hash table with linear probing (Box2D keeps a hash set for the same
/// job). The broadphase asks "is this pair already a contact?" for every overlap a moved proxy finds, so the lookup is
/// a multiply, a shift and usually one probe, with no interface calls. Keys are never zero (a pair's lower shape index
/// is stored plus one), so zero marks an empty slot; removal shifts later entries back, leaving no tombstones.
/// Lookups are read-only and safe from several threads while nothing writes.
/// </summary>
internal sealed class PairTable
{
    private ulong[] _keys = new ulong[64];
    private int[] _values = new int[64];
    private int _count, _shift = 64 - 6;

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Stored(ulong key) => key + (1UL << 32); // the lower index plus one: never zero

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Slot(ulong stored) => (int)((stored * 0x9E3779B97F4A7C15UL) >> _shift);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(ulong key)
    {
        ulong stored = Stored(key);
        var keys = _keys;
        int mask = keys.Length - 1;
        for (int i = Slot(stored); ; i = (i + 1) & mask)
        {
            ulong k = keys[i];
            if (k == stored) return true;
            if (k == 0) return false;
        }
    }

    public void Add(ulong key, int value)
    {
        if ((_count + 1) * 2 > _keys.Length) Grow();
        ulong stored = Stored(key);
        int mask = _keys.Length - 1;
        int i = Slot(stored);
        while (_keys[i] != 0) i = (i + 1) & mask;
        _keys[i] = stored;
        _values[i] = value;
        _count++;
    }

    public void Remove(ulong key)
    {
        ulong stored = Stored(key);
        int mask = _keys.Length - 1;
        int i = Slot(stored);
        while (_keys[i] != stored) { if (_keys[i] == 0) return; i = (i + 1) & mask; }
        // Shift back any entry after the hole that would no longer be reachable from its home slot.
        int hole = i;
        for (int j = (i + 1) & mask; _keys[j] != 0; j = (j + 1) & mask)
        {
            int home = Slot(_keys[j]);
            // Movable when its home isn't in (hole, j] (cyclically).
            if (hole <= j ? (home <= hole || home > j) : (home <= hole && home > j))
            {
                _keys[hole] = _keys[j];
                _values[hole] = _values[j];
                hole = j;
            }
        }
        _keys[hole] = 0;
        _count--;
    }

    private void Grow()
    {
        var oldKeys = _keys; var oldValues = _values;
        _keys = new ulong[oldKeys.Length * 2];
        _values = new int[oldKeys.Length * 2];
        _shift--;
        int mask = _keys.Length - 1;
        for (int k = 0; k < oldKeys.Length; k++)
        {
            ulong stored = oldKeys[k];
            if (stored == 0) continue;
            int i = Slot(stored);
            while (_keys[i] != 0) i = (i + 1) & mask;
            _keys[i] = stored;
            _values[i] = oldValues[k];
        }
    }

    public PairTable Clone()
    {
        var t = (PairTable)MemberwiseClone();
        t._keys = (ulong[])_keys.Clone();
        t._values = (int[])_values.Clone();
        return t;
    }
}
