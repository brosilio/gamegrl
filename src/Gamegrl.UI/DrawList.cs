using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Rhi;
using Gamegrl.UI.Fonts;

namespace Gamegrl.UI;

/// <summary>An axis-aligned rectangle in pixels (origin top-left).</summary>
public readonly record struct Rect(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public Vector2 Min => new(X, Y);
    public Vector2 Max => new(X + W, Y + H);
    public Vector2 Size => new(W, H);
    public Vector2 Center => new(X + W * 0.5f, Y + H * 0.5f);

    public bool Contains(Vector2 p) => p.X >= X && p.Y >= Y && p.X < X + W && p.Y < Y + H;
    public Rect Shrink(float d) => new(X + d, Y + d, MathF.Max(0, W - 2 * d), MathF.Max(0, H - 2 * d));
    public Rect Shrink(float dx, float dy) => new(X + dx, Y + dy, MathF.Max(0, W - 2 * dx), MathF.Max(0, H - 2 * dy));

    public Rect Intersect(Rect o)
    {
        float x0 = MathF.Max(X, o.X), y0 = MathF.Max(Y, o.Y), x1 = MathF.Min(Right, o.Right), y1 = MathF.Min(Bottom, o.Bottom);
        return new Rect(x0, y0, MathF.Max(0, x1 - x0), MathF.Max(0, y1 - y0));
    }

    public (Rect Left, Rect Right) SplitX(float leftWidth) => (this with { W = leftWidth }, new Rect(X + leftWidth, Y, W - leftWidth, H));
    public (Rect Top, Rect Bottom) SplitY(float topHeight) => (this with { H = topHeight }, new Rect(X, Y + topHeight, W, H - topHeight));
}

/// <summary>A color in sRGB, 8 bits per channel, packed as RGBA in memory.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct Color32(byte R, byte G, byte B, byte A = 255)
{
    public static Color32 Hex(uint rgb, byte a = 255) => new((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb, a);
    public Color32 WithAlpha(byte a) => this with { A = a };
    public Color32 Lighter(int d) => new((byte)Math.Min(255, R + d), (byte)Math.Min(255, G + d), (byte)Math.Min(255, B + d), A);
    public static readonly Color32 White = new(255, 255, 255);
    public static readonly Color32 Transparent = new(0, 0, 0, 0);
}

[StructLayout(LayoutKind.Sequential)]
public struct UiVertex(Vector2 pos, Vector2 uv, Color32 color)
{
    public Vector2 Pos = pos;
    public Vector2 Uv = uv;
    public Color32 Color = color;
}

/// <summary>A run of indices drawn with one texture and one clip rectangle.</summary>
public readonly record struct DrawCommand(GpuTexture? Texture, Rect Clip, int IndexStart, int IndexCount);

/// <summary>
/// Accumulates UI geometry for a frame: filled and outlined shapes, text, images, all clipped. Consecutive
/// primitives with the same texture and clip merge into one draw command (the font atlas holds a white block,
/// so text and shapes batch together).
/// </summary>
public sealed class DrawList
{
    private UiVertex[] _vertices = new UiVertex[4096];
    private uint[] _indices = new uint[8192];
    private int _vertexCount, _indexCount;
    private readonly List<DrawCommand> _commands = new();
    private readonly Stack<Rect> _clips = new();
    private GpuTexture? _texture;
    private FontAtlas _font = null!;
    private GpuTexture? _fontTexture;

    public ReadOnlySpan<UiVertex> Vertices => _vertices.AsSpan(0, _vertexCount);
    public ReadOnlySpan<uint> Indices => _indices.AsSpan(0, _indexCount);
    public IReadOnlyList<DrawCommand> Commands => _commands;
    public Rect Clip => _clips.Count > 0 ? _clips.Peek() : new Rect(0, 0, 1e9f, 1e9f);

    public void Reset(FontAtlas font, GpuTexture? fontTexture, Rect screen)
    {
        _vertexCount = _indexCount = 0;
        _commands.Clear();
        _clips.Clear();
        _clips.Push(screen);
        _font = font;
        _fontTexture = fontTexture;
        _texture = fontTexture;
    }

    public void PushClip(Rect r) => _clips.Push(r.Intersect(Clip));
    public void PopClip() => _clips.Pop();

    // ------------------------------------------------------------------ shapes

    public void Rect(Rect r, Color32 color)
    {
        if (color.A == 0 || r.W <= 0 || r.H <= 0) return;
        var uv = _font.WhiteUv;
        Quad(r.Min, new Vector2(r.Right, r.Y), r.Max, new Vector2(r.X, r.Bottom), uv, uv, uv, uv, color, _fontTexture);
    }

    public void RectOutline(Rect r, Color32 color, float thickness = 1)
    {
        Rect(r with { H = thickness }, color);
        Rect(new Rect(r.X, r.Bottom - thickness, r.W, thickness), color);
        Rect(new Rect(r.X, r.Y + thickness, thickness, r.H - 2 * thickness), color);
        Rect(new Rect(r.Right - thickness, r.Y + thickness, thickness, r.H - 2 * thickness), color);
    }

    public void Triangle(Vector2 a, Vector2 b, Vector2 c, Color32 color)
    {
        Use(_fontTexture);
        uint i = Reserve(3, 3);
        var uv = _font.WhiteUv;
        _vertices[i] = new UiVertex(a, uv, color);
        _vertices[i + 1] = new UiVertex(b, uv, color);
        _vertices[i + 2] = new UiVertex(c, uv, color);
        _indices[_indexCount++] = i;
        _indices[_indexCount++] = i + 1;
        _indices[_indexCount++] = i + 2;
        Extend(3);
    }

    /// <summary>A line as a thin quad.</summary>
    public void Line(Vector2 a, Vector2 b, Color32 color, float thickness = 1)
    {
        var d = b - a;
        float len = d.Length();
        if (len < 1e-4f) return;
        var n = new Vector2(-d.Y, d.X) / len * (thickness * 0.5f);
        var uv = _font.WhiteUv;
        Quad(a - n, b - n, b + n, a + n, uv, uv, uv, uv, color, _fontTexture);
    }

    public void Image(Rect r, GpuTexture texture, Color32 tint, Vector2 uv0, Vector2 uv1) =>
        Quad(r.Min, new Vector2(r.Right, r.Y), r.Max, new Vector2(r.X, r.Bottom), uv0, new Vector2(uv1.X, uv0.Y), uv1, new Vector2(uv0.X, uv1.Y), tint, texture);

    // ------------------------------------------------------------------ text

    /// <summary>Draw one line of text with its top-left at <paramref name="pos"/>; returns the width drawn.</summary>
    public float Text(Vector2 pos, ReadOnlySpan<char> text, Color32 color)
    {
        float pen = MathF.Round(pos.X);
        float baseline = MathF.Round(pos.Y + _font.Ascent);
        Glyph prev = default;
        bool first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var g = _font.GetGlyph(rune.Value);
            if (!first) pen += _font.Kerning(prev, g);
            if (g.Size.X > 0)
            {
                var p0 = new Vector2(pen + g.Offset.X, baseline + g.Offset.Y);
                var p1 = p0 + g.Size;
                Quad(p0, new Vector2(p1.X, p0.Y), p1, new Vector2(p0.X, p1.Y), g.Uv0, new Vector2(g.Uv1.X, g.Uv0.Y), g.Uv1, new Vector2(g.Uv0.X, g.Uv1.Y), color, _fontTexture);
            }
            pen += g.Advance;
            prev = g;
            first = false;
        }
        return pen - pos.X;
    }

    // ------------------------------------------------------------------ internals

    private void Quad(Vector2 a, Vector2 b, Vector2 c, Vector2 d, Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Color32 color, GpuTexture? texture)
    {
        // Cull quads entirely outside the clip rectangle.
        var clip = Clip;
        float minX = MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), maxX = MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X));
        float minY = MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)), maxY = MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y));
        if (maxX < clip.X || minX > clip.Right || maxY < clip.Y || minY > clip.Bottom) return;

        Use(texture);
        uint i = Reserve(4, 6);
        _vertices[i] = new UiVertex(a, ua, color);
        _vertices[i + 1] = new UiVertex(b, ub, color);
        _vertices[i + 2] = new UiVertex(c, uc, color);
        _vertices[i + 3] = new UiVertex(d, ud, color);
        _indices[_indexCount++] = i;
        _indices[_indexCount++] = i + 1;
        _indices[_indexCount++] = i + 2;
        _indices[_indexCount++] = i;
        _indices[_indexCount++] = i + 2;
        _indices[_indexCount++] = i + 3;
        Extend(6);
    }

    private uint Reserve(int vertices, int indices)
    {
        if (_vertexCount + vertices > _vertices.Length) Array.Resize(ref _vertices, _vertices.Length * 2);
        if (_indexCount + indices > _indices.Length) Array.Resize(ref _indices, _indices.Length * 2);
        uint start = (uint)_vertexCount;
        _vertexCount += vertices;
        return start;
    }

    private void Use(GpuTexture? texture) => _texture = texture;

    /// <summary>Attribute the indices just added to a draw command, merging with the previous one when possible.</summary>
    private void Extend(int indexCount)
    {
        var clip = Clip;
        int start = _indexCount - indexCount;
        if (_commands.Count > 0)
        {
            var last = _commands[^1];
            if (ReferenceEquals(last.Texture, _texture) && last.Clip == clip && last.IndexStart + last.IndexCount == start)
            {
                _commands[^1] = last with { IndexCount = last.IndexCount + indexCount };
                return;
            }
        }
        _commands.Add(new DrawCommand(_texture, clip, start, indexCount));
    }

    /// <summary>Append another list's geometry (an overlay layer drawn on top).</summary>
    public void Append(DrawList other)
    {
        uint baseVertex = (uint)_vertexCount;
        int baseIndex = _indexCount;
        Reserve(other._vertexCount, other._indexCount);
        Array.Copy(other._vertices, 0, _vertices, baseVertex, other._vertexCount);
        for (int i = 0; i < other._indexCount; i++) _indices[_indexCount++] = other._indices[i] + baseVertex;
        foreach (var c in other._commands) _commands.Add(c with { IndexStart = c.IndexStart + baseIndex });
    }
}
