using System.Buffers.Binary;
using System.Numerics;

namespace Gamegrl.UI.Fonts;

/// <summary>
/// A TrueType (glyf-outline) font reader: cmap formats 4 and 12, simple and composite glyphs, horizontal metrics
/// and the legacy kern table. CFF (.otf) outlines aren't supported.
/// </summary>
public sealed class TrueTypeFont
{
    private readonly byte[] _data;
    private readonly int _glyf, _loca, _hmtx, _kern;
    private readonly bool _longLoca;
    private readonly int _numHMetrics;
    private readonly Dictionary<int, int> _cmap = new();
    private readonly Dictionary<uint, short> _kerning = new();

    public TrueTypeFont(byte[] data)
    {
        _data = data;
        uint version = U32(0);
        if (version != 0x00010000 && version != 0x74727565) throw new FormatException("Not a TrueType font (CFF/OpenType outlines aren't supported).");
        int tables = U16(4);
        var offsets = new Dictionary<string, int>();
        for (int i = 0; i < tables; i++)
        {
            int rec = 12 + i * 16;
            string tag = System.Text.Encoding.ASCII.GetString(data, rec, 4);
            offsets[tag] = (int)U32(rec + 8);
        }
        foreach (var required in new[] { "head", "hhea", "maxp", "cmap", "loca", "glyf", "hmtx" })
            if (!offsets.ContainsKey(required)) throw new FormatException($"Font has no '{required}' table.");

        int head = offsets["head"], hhea = offsets["hhea"];
        UnitsPerEm = U16(head + 18);
        _longLoca = S16(head + 50) != 0;
        Ascent = S16(hhea + 4);
        Descent = S16(hhea + 6);
        LineGap = S16(hhea + 8);
        _numHMetrics = U16(hhea + 34);
        GlyphCount = U16(offsets["maxp"] + 4);
        _glyf = offsets["glyf"];
        _loca = offsets["loca"];
        _hmtx = offsets["hmtx"];
        _kern = offsets.GetValueOrDefault("kern", 0);
        ReadCmap(offsets["cmap"]);
        if (_kern != 0) ReadKern();
    }

    public int UnitsPerEm { get; }
    public int Ascent { get; }
    public int Descent { get; }
    public int LineGap { get; }
    public int GlyphCount { get; }

    public int GlyphIndex(int codepoint) => _cmap.GetValueOrDefault(codepoint, 0);

    public int AdvanceWidth(int glyph) => U16(_hmtx + 4 * Math.Min(glyph, _numHMetrics - 1));

    public int Kerning(int left, int right) => _kerning.GetValueOrDefault(((uint)left << 16) | (uint)right, (short)0);

    /// <summary>The glyph's outline as contours of points (font units, y up), with on-curve flags.</summary>
    public List<(Vector2 Point, bool OnCurve)[]> Outline(int glyph) => Outline(glyph, Matrix3x2.Identity, 0);

    private List<(Vector2, bool)[]> Outline(int glyph, Matrix3x2 transform, int depth)
    {
        var contours = new List<(Vector2, bool)[]>();
        if (glyph < 0 || glyph >= GlyphCount || depth > 8) return contours;
        int start = GlyphOffset(glyph), end = GlyphOffset(glyph + 1);
        if (end <= start) return contours; // empty glyph (space)
        int g = _glyf + start;
        short numContours = S16(g);
        if (numContours >= 0) SimpleGlyph(g, numContours, transform, contours);
        else CompositeGlyph(g, transform, depth, contours);
        return contours;
    }

    private void SimpleGlyph(int g, int numContours, Matrix3x2 transform, List<(Vector2, bool)[]> contours)
    {
        var endPts = new int[numContours];
        for (int i = 0; i < numContours; i++) endPts[i] = U16(g + 10 + i * 2);
        int pointCount = numContours == 0 ? 0 : endPts[^1] + 1;
        int p = g + 10 + numContours * 2;
        p += 2 + U16(p); // skip instructions

        var flags = new byte[pointCount];
        for (int i = 0; i < pointCount;)
        {
            byte f = _data[p++];
            flags[i++] = f;
            if ((f & 8) != 0)
            {
                int repeat = _data[p++];
                while (repeat-- > 0 && i < pointCount) flags[i++] = f;
            }
        }
        var xs = new int[pointCount];
        var ys = new int[pointCount];
        int v = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte f = flags[i];
            if ((f & 2) != 0) { int d = _data[p++]; v += (f & 16) != 0 ? d : -d; }
            else if ((f & 16) == 0) { v += S16(p); p += 2; }
            xs[i] = v;
        }
        v = 0;
        for (int i = 0; i < pointCount; i++)
        {
            byte f = flags[i];
            if ((f & 4) != 0) { int d = _data[p++]; v += (f & 32) != 0 ? d : -d; }
            else if ((f & 32) == 0) { v += S16(p); p += 2; }
            ys[i] = v;
        }
        int first = 0;
        for (int c = 0; c < numContours; c++)
        {
            int last = endPts[c];
            var contour = new (Vector2, bool)[last - first + 1];
            for (int i = first; i <= last; i++) contour[i - first] = (Vector2.Transform(new Vector2(xs[i], ys[i]), transform), (flags[i] & 1) != 0);
            contours.Add(contour);
            first = last + 1;
        }
    }

    private void CompositeGlyph(int g, Matrix3x2 transform, int depth, List<(Vector2, bool)[]> contours)
    {
        int p = g + 10;
        while (true)
        {
            int flags = U16(p);
            int glyph = U16(p + 2);
            p += 4;
            float dx, dy;
            if ((flags & 1) != 0) { dx = S16(p); dy = S16(p + 2); p += 4; }
            else { dx = (sbyte)_data[p]; dy = (sbyte)_data[p + 1]; p += 2; }
            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & 8) != 0) { a = d = F2Dot14(p); p += 2; }
            else if ((flags & 0x40) != 0) { a = F2Dot14(p); d = F2Dot14(p + 2); p += 4; }
            else if ((flags & 0x80) != 0) { a = F2Dot14(p); b = F2Dot14(p + 2); c = F2Dot14(p + 4); d = F2Dot14(p + 6); p += 8; }
            if ((flags & 2) == 0) { dx = 0; dy = 0; } // point-matching offsets aren't supported; place at origin
            var local = new Matrix3x2(a, b, c, d, dx, dy);
            contours.AddRange(Outline(glyph, local * transform, depth + 1));
            if ((flags & 0x20) == 0) break; // MORE_COMPONENTS
        }
    }

    private int GlyphOffset(int glyph) => _longLoca ? (int)U32(_loca + glyph * 4) : U16(_loca + glyph * 2) * 2;

    private void ReadCmap(int cmap)
    {
        int count = U16(cmap + 2);
        int best = -1, bestScore = -1;
        for (int i = 0; i < count; i++)
        {
            int platform = U16(cmap + 4 + i * 8), encoding = U16(cmap + 6 + i * 8);
            int offset = cmap + (int)U32(cmap + 8 + i * 8);
            int format = U16(offset);
            int score = (platform, encoding, format) switch
            {
                (3, 10, 12) or (0, 4, 12) or (0, 6, 12) => 3,
                (3, 1, 4) or (0, 3, 4) => 2,
                (0, _, 4) => 1,
                _ => -1,
            };
            if (score > bestScore) { bestScore = score; best = offset; }
        }
        if (best < 0) throw new FormatException("Font has no Unicode cmap.");
        if (U16(best) == 4)
        {
            int segX2 = U16(best + 6);
            int ends = best + 14, starts = ends + segX2 + 2, deltas = starts + segX2, ranges = deltas + segX2;
            for (int s = 0; s < segX2 / 2; s++)
            {
                int end = U16(ends + s * 2), start = U16(starts + s * 2), delta = S16(deltas + s * 2), range = U16(ranges + s * 2);
                for (int c = start; c <= end && c != 0xFFFF; c++)
                {
                    int glyph = range == 0 ? (c + delta) & 0xFFFF : U16(ranges + s * 2 + range + (c - start) * 2);
                    if (range != 0 && glyph != 0) glyph = (glyph + delta) & 0xFFFF;
                    if (glyph != 0) _cmap[c] = glyph;
                }
            }
        }
        else
        {
            int groups = (int)U32(best + 12);
            for (int i = 0; i < groups; i++)
            {
                int rec = best + 16 + i * 12;
                uint start = U32(rec), end = U32(rec + 4), glyph = U32(rec + 8);
                for (uint c = start; c <= end && c < 0x110000 && c - start < 0x10000; c++) _cmap[(int)c] = (int)(glyph + c - start);
            }
        }
    }

    private void ReadKern()
    {
        int tables = U16(_kern + 2);
        int p = _kern + 4;
        for (int t = 0; t < tables; t++)
        {
            int length = U16(p + 2), coverage = U16(p + 4);
            if ((coverage >> 8) == 0 && (coverage & 1) != 0) // format 0, horizontal
            {
                int pairs = U16(p + 6);
                for (int i = 0; i < pairs; i++)
                {
                    int rec = p + 14 + i * 6;
                    _kerning[((uint)U16(rec) << 16) | U16(rec + 2)] = S16(rec + 4);
                }
            }
            p += length;
        }
    }

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_data.AsSpan(at));
    private short S16(int at) => BinaryPrimitives.ReadInt16BigEndian(_data.AsSpan(at));
    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(at));
    private float F2Dot14(int at) => S16(at) / 16384f;
}

/// <summary>
/// Antialiased glyph rasterization by exact area coverage: each line segment adds signed area and cover to an
/// accumulation buffer, and a running sum turns that into coverage (the approach of font-rs and stb_truetype v2).
/// </summary>
public static class GlyphRasterizer
{
    /// <summary>Rasterize contours (already in pixel space, y down) into a width × height alpha bitmap.</summary>
    public static byte[] Rasterize(List<(Vector2 Point, bool OnCurve)[]> contours, int width, int height)
    {
        var acc = new float[width * height + width + 2];
        foreach (var contour in contours)
        {
            if (contour.Length < 2) continue;
            foreach (var (a, b) in Flatten(contour)) Line(acc, width, height, a, b);
        }
        var alpha = new byte[width * height];
        float sum = 0;
        for (int i = 0; i < alpha.Length; i++)
        {
            sum += acc[i];
            alpha[i] = (byte)(Math.Min(1f, Math.Abs(sum)) * 255f + 0.5f);
        }
        return alpha;
    }

    /// <summary>Quadratic contours to line segments; consecutive off-curve points imply an on-curve midpoint.</summary>
    private static IEnumerable<(Vector2, Vector2)> Flatten((Vector2 Point, bool OnCurve)[] c)
    {
        int n = c.Length;
        int startIndex = Array.FindIndex(c, p => p.OnCurve);
        Vector2 start = startIndex >= 0 ? c[startIndex].Point : (c[0].Point + c[1].Point) * 0.5f;
        int offset = startIndex >= 0 ? startIndex : 0;
        Vector2 current = start;
        Vector2? control = null;
        for (int k = 1; k <= n; k++)
        {
            var (p, on) = c[(offset + k) % n];
            if (k == n && startIndex < 0) { p = start; on = true; }
            if (on)
            {
                if (control is { } ctl) foreach (var seg in Quad(current, ctl, p)) yield return seg;
                else yield return (current, p);
                current = p;
                control = null;
            }
            else
            {
                if (control is { } ctl)
                {
                    var mid = (ctl + p) * 0.5f;
                    foreach (var seg in Quad(current, ctl, mid)) yield return seg;
                    current = mid;
                }
                control = p;
            }
        }
        if (control is { } last) foreach (var seg in Quad(current, last, start)) yield return seg;
        else if (current != start) yield return (current, start);
    }

    private static IEnumerable<(Vector2, Vector2)> Quad(Vector2 p0, Vector2 p1, Vector2 p2)
    {
        float dd = (p0 - 2 * p1 + p2).Length();
        int steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(dd * 2)), 1, 32);
        var prev = p0;
        for (int i = 1; i <= steps; i++)
        {
            float t = i / (float)steps, u = 1 - t;
            var q = u * u * p0 + 2 * u * t * p1 + t * t * p2;
            yield return (prev, q);
            prev = q;
        }
    }

    private static void Line(float[] a, int w, int h, Vector2 p0, Vector2 p1)
    {
        if (MathF.Abs(p0.Y - p1.Y) <= float.Epsilon) return;
        float dir = 1;
        if (p0.Y > p1.Y) { (p0, p1) = (p1, p0); dir = -1; }
        float dxdy = (p1.X - p0.X) / (p1.Y - p0.Y);
        float x = p0.X;
        int y0 = Math.Max(0, (int)p0.Y);
        if (p0.Y < 0) x -= p0.Y * dxdy;
        int yEnd = Math.Min(h, (int)MathF.Ceiling(p1.Y));
        for (int y = y0; y < yEnd; y++)
        {
            int row = y * w;
            float dy = MathF.Min(y + 1, p1.Y) - MathF.Max(y, p0.Y);
            float xnext = x + dxdy * dy;
            float d = dy * dir;
            float x0 = MathF.Min(x, xnext), x1 = MathF.Max(x, xnext);
            float x0floor = MathF.Floor(x0);
            int x0i = (int)x0floor;
            float x1ceil = MathF.Ceiling(x1);
            int x1i = (int)x1ceil;
            if (x0i < 0 || x1i > w) { x = xnext; continue; } // outside the padded box: skip (glyph boxes include padding)
            if (x1i <= x0i + 1)
            {
                float xmf = 0.5f * (x + xnext) - x0floor;
                a[row + x0i] += d - d * xmf;
                a[row + x0i + 1] += d * xmf;
            }
            else
            {
                float s = 1 / (x1 - x0);
                float x0f = x0 - x0floor;
                float a0 = 0.5f * s * (1 - x0f) * (1 - x0f);
                float x1f = x1 - x1ceil + 1;
                float am = 0.5f * s * x1f * x1f;
                a[row + x0i] += d * a0;
                if (x1i == x0i + 2) a[row + x0i + 1] += d * (1 - a0 - am);
                else
                {
                    float a1 = s * (1.5f - x0f);
                    a[row + x0i + 1] += d * (a1 - a0);
                    for (int xi = x0i + 2; xi < x1i - 1; xi++) a[row + xi] += d * s;
                    float a2 = a1 + (x1i - x0i - 3) * s;
                    a[row + x1i - 1] += d * (1 - a2 - am);
                }
                a[row + x1i] += d * am;
            }
            x = xnext;
        }
    }
}
