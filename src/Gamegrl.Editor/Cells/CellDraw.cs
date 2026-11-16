using System.Numerics;
using AnsiDotNet;
using Gamegrl.Rhi;
using Gamegrl.UI;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Editor;

/// <summary>Cell painting plus viewport composition. Geometry never enters the editor's UI model.</summary>
public sealed class CellDraw
{
    private readonly Stack<AnsiDotNet.Rect> _clips = new();
    private bool[] _transparent = [];
    public bool Ascii { get; set; }
    /// <summary>Text and background every cell starts a frame with; set from the theme.</summary>
    public Style Base { get; set; } = new(AnsiDotNet.Color.FromHex(0xdedee4), AnsiDotNet.Color.FromHex(0x16161b));
    public Surface Surface { get; } = new(1, 1);
    public List<(Rect Area, GpuTexture Texture)> Images { get; } = new();
    public Rect Clip => From(Surface.Clip);
    /// <summary>The cells a rectangle touches: floor its near edges, ceil its far edges.</summary>
    public static AnsiDotNet.Rect To(Rect r)
    {
        int x = (int)MathF.Floor(r.X), y = (int)MathF.Floor(r.Y);
        return new(x, y, Math.Max(0, (int)MathF.Ceiling(r.Right) - x), Math.Max(0, (int)MathF.Ceiling(r.Bottom) - y));
    }
    public static Rect From(AnsiDotNet.Rect r) => new(r.X, r.Y, r.Width, r.Height);
    public static Color Color(Color32 c) => AnsiDotNet.Color.Rgb(c.R, c.G, c.B);
    public void Reset(int width, int height)
    {
        Surface.Resize(width, height); Surface.ResetClip(); Surface.Clear(Base);
        if (_transparent.Length != width * height) _transparent = new bool[width * height]; else Array.Clear(_transparent);
        _clips.Clear(); Images.Clear();
    }
    public void Composite(CellDraw overlay, Rect rectangle)
    {
        var area = To(rectangle).Intersect(Surface.Bounds);
        for (int y = area.Y; y < area.Bottom; y++)
        {
            overlay.Surface.GetRow(y).Slice(area.X, area.Width).CopyTo(Surface.GetRow(y).Slice(area.X, area.Width));
            for (int x = area.X; x < area.Right; x++) _transparent[y * Surface.Width + x] = false;
        }
    }
    public bool Transparent(int x, int y) => _transparent[y * Surface.Width + x];
    public void PushClip(Rect rect) { _clips.Push(Surface.Clip); Surface.Clip = Surface.Clip.Intersect(To(rect)); }
    public void PopClip() => Surface.Clip = _clips.Pop();
    public int ClipDepth => _clips.Count;
    public void PopClipsTo(int depth) { while (_clips.Count > depth) PopClip(); }
    public void Rect(Rect rect, Color32 color)
    {
        var area = Surface.Clip.Intersect(To(rect));
        Surface.Fill(area, ' ', Base.WithBackground(Color(color)));
        for (int y = area.Y; y < area.Bottom; y++) for (int x = area.X; x < area.Right; x++) _transparent[y * Surface.Width + x] = false;
    }
    /// <summary>A square-cornered single-line border on the rectangle's edge cells, keeping their backgrounds.</summary>
    public void Box(Rect rect, Color32 color)
    {
        var r = To(rect);
        if (r.Width < 2 || r.Height < 2) return;
        int right = r.Right - 1, bottom = r.Bottom - 1;
        Glyph(r.X, r.Y, '┌', color); Glyph(right, r.Y, '┐', color);
        Glyph(r.X, bottom, '└', color); Glyph(right, bottom, '┘', color);
        for (int x = r.X + 1; x < right; x++) { Glyph(x, r.Y, '─', color); Glyph(x, bottom, '─', color); }
        for (int y = r.Y + 1; y < bottom; y++) { Glyph(r.X, y, '│', color); Glyph(right, y, '│', color); }
    }

    /// <summary>Set one cell's character and foreground inside the clip, keeping its background.</summary>
    public void Glyph(float x, float y, char c, Color32 color)
    {
        int cx = (int)MathF.Floor(x), cy = (int)MathF.Floor(y);
        if (!Surface.Clip.Contains(cx, cy)) return;
        var rune = Ascii && c > 127 ? AsciiFallback(new System.Text.Rune(c)) : new System.Text.Rune(c);
        Surface.SetCell(cx, cy, rune, Surface[cx, cy].Style.WithForeground(Color(color)));
    }

    /// <summary>Change the foreground of every cell in the rectangle, keeping characters; used for hover highlights.</summary>
    public void Recolor(Rect rect, Color32 color)
    {
        var area = Surface.Clip.Intersect(To(rect));
        var foreground = Color(color);
        for (int y = area.Y; y < area.Bottom; y++)
            for (int x = area.X; x < area.Right; x++)
            {
                var cell = Surface[x, y];
                if (!cell.IsContinuation) Surface.SetCell(x, y, cell.Rune, cell.Style.WithForeground(foreground));
            }
    }
    /// <summary>Write one line of text; returns the column after it.</summary>
    public float Text(Vector2 pos, ReadOnlySpan<char> text, Color32 color)
    {
        int y = (int)pos.Y, x = (int)pos.X;
        var foreground = Color(color);
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r') break;
            var display = Ascii && rune.Value > 127 ? AsciiFallback(rune) : rune;
            if (Surface.Clip.Contains(x, y)) Surface.SetCell(x, y, display, Surface[x, y].Style.WithForeground(foreground));
            x += CellWidth.Of(rune);
        }
        return x;
    }

    /// <summary>Fill a rectangle's first row with one character, keeping each cell's background.</summary>
    public void Repeat(Rect rect, char c, Color32 color)
    {
        var area = Surface.Clip.Intersect(To(rect));
        if (area.Height <= 0) return;
        var rune = Ascii && c > 127 ? AsciiFallback(new System.Text.Rune(c)) : new System.Text.Rune(c);
        var foreground = Color(color);
        for (int x = area.X; x < area.Right; x++) Surface.SetCell(x, area.Y, rune, Surface[x, area.Y].Style.WithForeground(foreground));
    }

    private static System.Text.Rune AsciiFallback(System.Text.Rune rune) => new(rune.Value switch
    {
        0x2713 => 'x',
        0x25be or 0x25bc => 'v',
        0x25b8 or 0x25b6 => '>',
        0x25cf => '*',
        0x25cb => 'o',
        0x25a0 => '#',
        0x2016 or 0x2502 or 0x2503 or 0x2595 => '|',
        0x2590 or 0x259d or 0x2597 => '#',
        0x00b7 => '.',
        0x00ab => '<',
        0x00bb or 0x203a => '>',
        0x00d7 => 'x',
        0x2026 => '~',
        0x250c or 0x2510 or 0x2514 or 0x2518 or 0x252c or 0x2534 or 0x253c or 0x251c or 0x2524 => '+',
        >= 0x2500 and <= 0x257f => '-',
        _ => '.',
    });
    public void Line(Vector2 a, Vector2 b, Color32 color, float thickness = 1)
    {
        int steps = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(a, b) * 2));
        for (int i = 0; i <= steps; i++) Text(Vector2.Lerp(a, b, i / (float)steps), "•", color);
    }
    public void Image(Rect area, GpuTexture texture)
    {
        Images.Add((area, texture));
        var rect = To(area).Intersect(Surface.Clip);
        for (int y = rect.Y; y < rect.Bottom; y++) for (int x = rect.X; x < rect.Right; x++) _transparent[y * Surface.Width + x] = true;
    }
}

public sealed class CellFont
{
    public float LineHeight => 1;
    public float Measure(ReadOnlySpan<char> text) => AnsiText.VisibleWidth(text);
    public int HitTest(string text, float column)
    {
        int width = 0, offset = 0;
        foreach (var rune in text.EnumerateRunes()) { if (width >= column) break; width += CellWidth.Of(rune); offset += rune.Utf16SequenceLength; }
        return offset;
    }
}
