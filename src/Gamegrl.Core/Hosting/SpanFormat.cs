using System.Globalization;
using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// Interpolated strings written into a caller's buffer, without allocating:
/// <code>Span&lt;char&gt; buffer = stackalloc char[64];
/// var text = SpanFormat.Write(buffer, $"{ms:0.00} ms");</code>
/// Numbers format with the invariant culture. Text past the buffer's end is cut off. (The BCL's
/// <c>MemoryExtensions.TryWrite</c> boxes values that have a format string; this doesn't.)
/// </summary>
public static class SpanFormat
{
    public static ReadOnlySpan<char> Write(Span<char> destination, [InterpolatedStringHandlerArgument(nameof(destination))] ref SpanFormatHandler text) => text.Result;
}

[InterpolatedStringHandler]
public ref struct SpanFormatHandler
{
    private readonly Span<char> _buffer;
    private int _length;

    public SpanFormatHandler(int literalLength, int formattedCount, Span<char> destination)
    {
        _buffer = destination;
        _length = 0;
    }

    public readonly ReadOnlySpan<char> Result => _buffer[.._length];

    public void AppendLiteral(string s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted(ReadOnlySpan<char> s)
    {
        int n = Math.Min(s.Length, _buffer.Length - _length);
        s[..n].CopyTo(_buffer[_length..]);
        _length += n;
    }

    public void AppendFormatted(string? s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted<T>(T value, string? format = null) where T : ISpanFormattable
    {
        if (value.TryFormat(_buffer[_length..], out int written, format, CultureInfo.InvariantCulture)) _length += written;
        else _length = _buffer.Length;
    }
}
