using System.Numerics;

namespace Gamegrl.UI.Fonts;

/// <summary>One baked glyph: where it is in the atlas and how to place it relative to the pen (baseline, y down).</summary>
public readonly record struct Glyph(Vector2 Uv0, Vector2 Uv1, Vector2 Offset, Vector2 Size, float Advance, int Index);

/// <summary>
/// Glyphs of one font at one pixel size, packed into an RGBA atlas (white, coverage in alpha) with a solid-white
/// block, so text and filled shapes share one texture and one draw call.
/// TODO: SDF glyphs for in-game text that scales; HiDPI rebake on scale change.
/// </summary>
public sealed class FontAtlas
{
    private readonly Dictionary<int, Glyph> _glyphs = new();
    private readonly TrueTypeFont _font;
    private readonly float _scale;

    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }
    public float Size { get; }
    public float Ascent { get; }
    public float LineHeight { get; }
    public Vector2 WhiteUv { get; private set; }
    public Glyph Fallback { get; private set; }

    private FontAtlas(TrueTypeFont font, float size, int width, int height)
    {
        _font = font;
        Size = size;
        _scale = size / font.UnitsPerEm;
        Width = width;
        Height = height;
        Rgba = new byte[width * height * 4];
        Ascent = font.Ascent * _scale;
        LineHeight = MathF.Ceiling((font.Ascent - font.Descent + font.LineGap) * _scale);
    }

    /// <summary>Printable ASCII, Latin-1 and a few symbols the editor uses.</summary>
    public static IEnumerable<int> DefaultCharacters()
    {
        for (int c = 32; c < 127; c++) yield return c;
        for (int c = 160; c < 256; c++) yield return c;
        foreach (int c in new[] { 0x2026, 0x2022, 0x2013, 0x2014, 0x2190, 0x2191, 0x2192, 0x2193, 0x25B6, 0x25BC, 0x2713, 0x00D7 }) yield return c;
    }

    public static FontAtlas Bake(TrueTypeFont font, float pixelSize, IEnumerable<int>? codepoints = null, int atlasSize = 512)
    {
        var atlas = new FontAtlas(font, pixelSize, atlasSize, atlasSize);
        int x = 4, y = 0, shelf = 0;
        // Solid white 4×4 at the origin; sample its center for fills.
        for (int py = 0; py < 4; py++)
            for (int px = 0; px < 4; px++)
                atlas.Put(px, py, 255);
        atlas.WhiteUv = new Vector2(2f / atlasSize, 2f / atlasSize);

        foreach (int cp in (codepoints ?? DefaultCharacters()).Distinct())
        {
            int index = font.GlyphIndex(cp);
            if (index == 0 && cp != 0xFFFD) continue;
            var (bitmap, w, h, ox, oy) = atlas.RasterizeGlyph(index);
            if (x + w + 1 > atlasSize) { x = 0; y += shelf + 1; shelf = 0; }
            if (y + h > atlasSize) throw new InvalidOperationException($"Font atlas {atlasSize}² is full at {pixelSize}px; use a bigger atlas.");
            for (int py = 0; py < h; py++)
                for (int px = 0; px < w; px++)
                    atlas.Put(x + px, y + py, bitmap[py * w + px]);
            atlas._glyphs[cp] = new Glyph(new Vector2((float)x / atlasSize, (float)y / atlasSize),
                new Vector2((float)(x + w) / atlasSize, (float)(y + h) / atlasSize),
                new Vector2(ox, oy), new Vector2(w, h), font.AdvanceWidth(index) * atlas._scale, index);
            x += w + 1;
            shelf = Math.Max(shelf, h);
        }
        atlas.Fallback = atlas._glyphs.TryGetValue('?', out var q) ? q : default;
        return atlas;
    }

    private void Put(int x, int y, byte alpha)
    {
        int o = (y * Width + x) * 4;
        Rgba[o] = Rgba[o + 1] = Rgba[o + 2] = 255;
        Rgba[o + 3] = alpha;
    }

    private (byte[] Bitmap, int W, int H, int OffsetX, int OffsetY) RasterizeGlyph(int index)
    {
        var contours = _font.Outline(index);
        if (contours.Count == 0) return ([], 0, 0, 0, 0);
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        foreach (var c in contours)
            foreach (var (p, _) in c)
            {
                minX = MathF.Min(minX, p.X); maxX = MathF.Max(maxX, p.X);
                minY = MathF.Min(minY, p.Y); maxY = MathF.Max(maxY, p.Y);
            }
        int x0 = (int)MathF.Floor(minX * _scale) - 1, x1 = (int)MathF.Ceiling(maxX * _scale) + 1;
        int y0 = (int)MathF.Floor(-maxY * _scale) - 1, y1 = (int)MathF.Ceiling(-minY * _scale) + 1;
        int w = x1 - x0, h = y1 - y0;
        var pixel = new List<(Vector2, bool)[]>(contours.Count);
        foreach (var c in contours)
        {
            var t = new (Vector2, bool)[c.Length];
            for (int i = 0; i < c.Length; i++) t[i] = (new Vector2(c[i].Point.X * _scale - x0, -c[i].Point.Y * _scale - y0), c[i].OnCurve);
            pixel.Add(t);
        }
        return (GlyphRasterizer.Rasterize(pixel, w, h), w, h, x0, y0);
    }

    public Glyph GetGlyph(int codepoint) => _glyphs.TryGetValue(codepoint, out var g) ? g : Fallback;

    public float Kerning(Glyph left, Glyph right) => _font.Kerning(left.Index, right.Index) * _scale;

    /// <summary>The width of a single line of text, with kerning.</summary>
    public float Measure(ReadOnlySpan<char> text)
    {
        float w = 0;
        Glyph prev = default;
        bool first = true;
        foreach (var rune in text.EnumerateRunes())
        {
            var g = GetGlyph(rune.Value);
            if (!first) w += Kerning(prev, g);
            w += g.Advance;
            prev = g;
            first = false;
        }
        return w;
    }

    /// <summary>The character index in <paramref name="text"/> closest to <paramref name="x"/> pixels from its start.</summary>
    public int HitTest(ReadOnlySpan<char> text, float x)
    {
        float pen = 0;
        for (int i = 0; i < text.Length; i++)
        {
            float adv = GetGlyph(text[i]).Advance;
            if (x < pen + adv * 0.5f) return i;
            pen += adv;
        }
        return text.Length;
    }

    /// <summary>Find a TrueType font: an explicit path, GAMEGRL_FONT, then common system locations.</summary>
    public static string? FindSystemFont(string? preferred = null)
    {
        var candidates = new List<string?>
        {
            preferred,
            Environment.GetEnvironmentVariable("GAMEGRL_FONT"),
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/TTF/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/noto/NotoSans-Regular.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
            "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            "/System/Library/Fonts/Supplemental/Arial.ttf",
        };
        return candidates.FirstOrDefault(c => c is not null && File.Exists(c));
    }
}
