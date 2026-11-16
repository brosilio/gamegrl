using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// A handle to an entity: an index into the world's entity table plus a generation that changes every time the
/// index is reused. A destroyed entity's handle never aliases a new one. The default value is never alive
/// (generations start at 1). Check with <see cref="World.IsAlive"/>; there is no fake-null.
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    private readonly ulong _value;

    internal Entity(uint index, uint generation) => _value = ((ulong)generation << 32) | index;

    public uint Index
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)_value;
    }

    public uint Generation
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => (uint)(_value >> 32);
    }

    /// <summary>True for <c>default(Entity)</c>, which no world ever hands out.</summary>
    public bool IsNull => _value == 0;

    public ulong RawValue => _value;

    public bool Equals(Entity other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Entity e && e._value == _value;
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(Entity a, Entity b) => a._value == b._value;
    public static bool operator !=(Entity a, Entity b) => a._value != b._value;
    public override string ToString() => IsNull ? "Entity(null)" : $"Entity({Index}v{Generation})";
}
