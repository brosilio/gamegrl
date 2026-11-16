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
    public Surface Surface { get; } = new(1, 1);
    public List<(Rect Area, GpuTexture Texture)> Images { get; } = new();
    public Rect Clip => From(Surface.Clip);
    public static AnsiDotNet.Rect To(Rect r) => new((int)MathF.Floor(r.X), (int)MathF.Floor(r.Y), Math.Max(0, (int)MathF.Ceiling(r.Right) - (int)MathF.Floor(r.X)), Math.Max(0, (int)MathF.Ceiling(r.Bottom) - (int)MathF.Floor(r.Y)));
    public static Rect From(AnsiDotNet.Rect r) => new(r.X, r.Y, r.Width, r.Height);
    public static Color Color(Color32 c) => AnsiDotNet.Color.Rgb(c.R, c.G, c.B);
    public void Reset(int width, int height)
    {
        Surface.Resize(width, height); Surface.ResetClip(); Surface.Clear(new Style(AnsiDotNet.Color.FromHex(0xdcdde0), AnsiDotNet.Color.FromHex(0x1e1f22)));
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
    public void Rect(Rect rect, Color32 color)
    {
        var area = Surface.Clip.Intersect(To(rect));
        Surface.Fill(area, ' ', new Style(AnsiDotNet.Color.FromHex(0xdcdde0), Color(color)));
        for (int y = area.Y; y < area.Bottom; y++) for (int x = area.X; x < area.Right; x++) _transparent[y * Surface.Width + x] = false;
    }
    public void RectOutline(Rect rect, Color32 color) => Surface.DrawBox(To(rect), Color(color), Ascii ? BoxStyle.Ascii : BoxStyle.Rounded);
    public void Text(Vector2 pos, string text, Color32 color)
    {
        int y = (int)pos.Y, x = (int)pos.X;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\r') break;
            var display = Ascii && rune.Value > 127 ? new System.Text.Rune(rune.Value switch { 0x2713 => 'x', 0x25be or 0x25bc => 'v', 0x25b8 or 0x25b6 => '>', >= 0x2500 and <= 0x257f => '-', _ => '.' }) : rune;
            if (Surface.Clip.Contains(x, y)) Surface.SetCell(x, y, display, Surface[x, y].Style.WithForeground(Color(color)));
            x += CellWidth.Of(rune);
        }
    }
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
    public float Measure(string text) => AnsiText.VisibleWidth(text);
    public int HitTest(string text, float column)
    {
        int width = 0, offset = 0;
        foreach (var rune in text.EnumerateRunes()) { if (width >= column) break; width += CellWidth.Of(rune); offset += rune.Utf16SequenceLength; }
        return offset;
    }
}
