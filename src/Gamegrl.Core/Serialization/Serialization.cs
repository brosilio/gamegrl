using System.Numerics;

namespace Gamegrl;

/// <summary>
/// Walks a component's serialized fields. The generator emits one visit method per component; the same method
/// drives every format (text scenes, binary scenes) and, later, the Inspector. Fields arrive in declaration order;
/// nested structs are bracketed by <see cref="Begin"/> and <see cref="End"/>.
/// </summary>
public interface IFieldVisitor
{
    void Begin(string name);
    void End();
    void Field(string name, ref bool value);
    void Field(string name, ref byte value);
    void Field(string name, ref sbyte value);
    void Field(string name, ref short value);
    void Field(string name, ref ushort value);
    void Field(string name, ref int value);
    void Field(string name, ref uint value);
    void Field(string name, ref long value);
    void Field(string name, ref ulong value);
    void Field(string name, ref float value);
    void Field(string name, ref double value);
    void Field(string name, ref Vector2 value);
    void Field(string name, ref Vector3 value);
    void Field(string name, ref Vector4 value);
    void Field(string name, ref Quaternion value);
    void Field(string name, ref Matrix4x4 value);
    void Field(string name, ref Guid value);
    void Field(string name, ref FixedString64 value);
    /// <summary>A reference to another entity; formats write the target's scene id.</summary>
    void Field(string name, ref Entity value);
    /// <summary>A reference to an asset by GUID (an <see cref="AssetRef{T}"/>).</summary>
    void Asset(string name, ref Guid value);
    /// <summary>An enum, as its underlying value, with the generator's name table for text formats.</summary>
    void Enum(string name, ref long value, ReadOnlySpan<string> names, ReadOnlySpan<long> values);
}

/// <summary>Generated per component type; registered with <see cref="ComponentRegistry"/> by a module initializer.</summary>
public unsafe interface IComponentSerializer
{
    void Visit(void* component, IFieldVisitor visitor);
}

/// <summary>Excludes a public field from serialization (derived or runtime-only data).</summary>
[AttributeUsage(AttributeTargets.Field)]
public sealed class NoSerializeAttribute : Attribute;

/// <summary>
/// A reference to an asset by GUID: plain data, so components stay unmanaged and scenes refer to assets the
/// way .meta files name them. Systems resolve it through the asset database.
/// </summary>
public struct AssetRef<T> : IEquatable<AssetRef<T>> where T : class
{
    public Guid Guid;

    public AssetRef(Guid guid) => Guid = guid;

    public readonly bool IsNone => Guid == Guid.Empty;

    public readonly bool Equals(AssetRef<T> other) => Guid == other.Guid;
    public override readonly bool Equals(object? obj) => obj is AssetRef<T> r && Equals(r);
    public override readonly int GetHashCode() => Guid.GetHashCode();
    public override readonly string ToString() => $"asset:{Guid:N}";
}

/// <summary>
/// An entity's identity in a scene: random, assigned once, never reused. Scene files name entities (and the
/// references between them) by it, so unrelated edits never conflict in a merge.
/// </summary>
public struct SceneId : IComponent
{
    public ulong Value;

    public static SceneId New() => new() { Value = (ulong)Random.Shared.NextInt64(1, long.MaxValue) | ((ulong)Random.Shared.Next(0, 2) << 63) };
}
