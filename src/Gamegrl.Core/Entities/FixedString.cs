using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

namespace Gamegrl;

[InlineArray(63)]
internal struct Bytes63
{
    private byte _element;
}

/// <summary>
/// Up to 63 bytes of UTF-8 in a 64-byte unmanaged struct, so components can hold short text (names, tags) and
/// stay plain memory. Longer strings are truncated at a character boundary.
/// </summary>
public struct FixedString64 : IEquatable<FixedString64>
{
    public const int MaxBytes = 63;

    private byte _length;
    private Bytes63 _bytes;

    public FixedString64(string value)
    {
        Span<byte> buffer = stackalloc byte[Encoding.UTF8.GetMaxByteCount(value.Length)];
        int n = Encoding.UTF8.GetBytes(value, buffer);
        if (n > MaxBytes)
        {
            n = MaxBytes;
            while (n > 0 && (buffer[n] & 0xC0) == 0x80) n--; // don't split a multi-byte character
        }
        buffer[..n].CopyTo(_bytes);
        _length = (byte)n;
    }

    public readonly int Length => _length;

    [UnscopedRef]
    public readonly ReadOnlySpan<byte> Utf8 => ((ReadOnlySpan<byte>)_bytes)[.._length];

    public override readonly string ToString() => Encoding.UTF8.GetString(Utf8);

    public static implicit operator FixedString64(string value) => new(value);

    public readonly bool Equals(FixedString64 other) => Utf8.SequenceEqual(other.Utf8);
    public override readonly bool Equals(object? obj) => obj is FixedString64 f && Equals(f);
    public override readonly int GetHashCode()
    {
        var h = new HashCode();
        h.AddBytes(Utf8);
        return h.ToHashCode();
    }
    public static bool operator ==(FixedString64 a, FixedString64 b) => a.Equals(b);
    public static bool operator !=(FixedString64 a, FixedString64 b) => !a.Equals(b);
}

/// <summary>A human-readable name, shown in the Hierarchy and written to scenes.</summary>
public struct Name : IComponent
{
    public FixedString64 Value;

    public Name(string value) => Value = value;

    public override readonly string ToString() => Value.ToString();
}
