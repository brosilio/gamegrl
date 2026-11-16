using System.Numerics;
using System.Text;
using AnsiDotNet;
using Gamegrl.Rhi;
using Gamegrl.UI;
using Gamegrl.UI.Fonts;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Editor;

/// <summary>Desktop presentation of the shared cell surface, with full-resolution viewport textures.</summary>
internal sealed class CellRenderer : IDisposable
{
    private readonly UiRenderer _renderer;
    private readonly FontAtlas _font;
    private readonly DrawList _draw = new();
    public int CellWidth { get; }
    public int CellHeight { get; }
    public CellRenderer(GpuDevice device, TextureFormat format, string fontPath)
    {
        var characters = FontAtlas.DefaultCharacters().Concat(Enumerable.Range(0x2500, 256)).Concat(Enumerable.Range(0x2010, 0x30)).Concat(Enumerable.Range(0x2800, 256));
        _font = FontAtlas.Bake(new TrueTypeFont(File.ReadAllBytes(fontPath)), 14, characters, 1024);
        CellWidth = (int)MathF.Ceiling(_font.Measure("M")); CellHeight = (int)MathF.Ceiling(_font.LineHeight);
        _renderer = new UiRenderer(device, format, _font);
    }
    public void Forget(GpuTexture texture) => _renderer.Forget(texture);
    public void Render(CommandList commands, int frame, CellDraw cells, int width, int height)
    {
        _draw.Reset(_font, _renderer.FontTexture, new Rect(0, 0, width, height));
        foreach (var (area, texture) in cells.Images) _draw.Image(Pixels(area), texture, Color32.White, Vector2.Zero, Vector2.One);
        for (int y = 0; y < cells.Surface.Height; y++)
        for (int x = 0; x < cells.Surface.Width; x++)
        {
            var (_, bg) = Colors(cells.Surface[x, y].Style, cells.Base);
            if (!cells.Transparent(x, y)) _draw.Rect(new Rect(x * CellWidth, y * CellHeight, CellWidth, CellHeight), bg);
        }
        Span<char> glyph = stackalloc char[2];
        for (int y = 0; y < cells.Surface.Height; y++)
        for (int x = 0; x < cells.Surface.Width; x++)
        {
            var cell = cells.Surface[x, y]; var style = cell.Style;
            var (fg, _) = Colors(style, cells.Base);
            if (style.Has(TextAttributes.Dim)) fg = new Color32((byte)(fg.R / 2), (byte)(fg.G / 2), (byte)(fg.B / 2));
            var rect = new Rect(x * CellWidth, y * CellHeight, CellWidth, CellHeight);
            if (cell.IsContinuation || cell.Rune.Value is 0 or 32 || style.Has(TextAttributes.Hidden)) continue;
            int count = cell.Rune.EncodeToUtf16(glyph);
            _draw.PushClip(rect with { W = CellWidth * CellWidthOf(cell.Rune) });
            _draw.Text(rect.Min, glyph[..count], fg);
            if (style.Has(TextAttributes.Bold)) _draw.Text(rect.Min + Vector2.UnitX, glyph[..count], fg);
            if (style.Has(TextAttributes.Underline) || style.Has(TextAttributes.DoubleUnderline)) _draw.Rect(new Rect(rect.X, rect.Bottom - 2, rect.W, 1), fg);
            if (style.Has(TextAttributes.DoubleUnderline)) _draw.Rect(new Rect(rect.X, rect.Bottom - 4, rect.W, 1), fg);
            if (style.Has(TextAttributes.Strikethrough)) _draw.Rect(new Rect(rect.X, rect.Y + rect.H / 2, rect.W, 1), fg);
            if (style.Has(TextAttributes.Overline)) _draw.Rect(new Rect(rect.X, rect.Y, rect.W, 1), fg);
            _draw.PopClip();
        }
        _renderer.Render(commands, frame, _draw, width, height);
    }
    private static int CellWidthOf(Rune rune) => Math.Max(1, AnsiDotNet.CellWidth.Of(rune));
    public Rect Pixels(Rect rect) => new(rect.X * CellWidth, rect.Y * CellHeight, rect.W * CellWidth, rect.H * CellHeight);
    /// <summary>Resolve default colors on their own channel before inverse swaps them.</summary>
    private static (Color32 Foreground, Color32 Background) Colors(Style style, Style defaults)
    {
        var fg = Resolve(style.Foreground, defaults.Foreground); var bg = Resolve(style.Background, defaults.Background);
        return style.Has(TextAttributes.Inverse) ? (bg, fg) : (fg, bg);
    }
    private static Color32 Resolve(Color color, Color fallback)
    { var (r, g, b) = (color.Kind == ColorKind.Default ? fallback : color).ToRgb(); return new(r, g, b); }
    public void Dispose() => _renderer.Dispose();
}
