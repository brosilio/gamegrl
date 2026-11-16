using System.Buffers.Binary;
using System.IO.Compression;
using Gamegrl.Assets;

namespace Gamegrl.AssetPipeline.Importers;

/// <summary>
/// A PNG decoder (ISO/IEC 15948): every color type, bit depths 1–16, Adam7 interlacing, palettes and tRNS
/// transparency. Output is RGBA8; 16-bit samples keep their high byte. CRCs are verified.
/// </summary>
public static class Png
{
    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static TextureData Decode(ReadOnlySpan<byte> file, bool srgb = true)
    {
        if (file.Length < 8 || !file[..8].SequenceEqual(Signature)) throw new FormatException("Not a PNG file.");

        int width = 0, height = 0, bitDepth = 0, colorType = 0, interlace = 0;
        byte[]? palette = null, transparency = null;
        using var idat = new MemoryStream();
        int p = 8;
        bool seenHeader = false, ended = false;
        while (p + 12 <= file.Length && !ended)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file[p..]);
            if (length < 0 || p + 12 + length > file.Length) throw new FormatException("Truncated PNG chunk.");
            var type = file.Slice(p + 4, 4);
            var data = file.Slice(p + 8, length);
            uint crc = BinaryPrimitives.ReadUInt32BigEndian(file[(p + 8 + length)..]);
            if (Crc32(file.Slice(p + 4, 4 + length)) != crc) throw new FormatException($"PNG chunk {System.Text.Encoding.ASCII.GetString(type)} has a bad CRC.");
            switch (System.Text.Encoding.ASCII.GetString(type))
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                    bitDepth = data[8];
                    colorType = data[9];
                    if (data[10] != 0 || data[11] != 0) throw new FormatException("Unsupported PNG compression or filter method.");
                    interlace = data[12];
                    seenHeader = true;
                    break;
                case "PLTE": palette = data.ToArray(); break;
                case "tRNS": transparency = data.ToArray(); break;
                case "IDAT": idat.Write(data); break;
                case "IEND": ended = true; break;
                default:
                    if ((type[0] & 0x20) == 0) throw new FormatException($"Unknown critical PNG chunk {System.Text.Encoding.ASCII.GetString(type)}.");
                    break; // ancillary: ignore
            }
            p += 12 + length;
        }
        if (!seenHeader || width <= 0 || height <= 0) throw new FormatException("PNG has no valid IHDR.");
        if ((long)width * height > 1 << 28) throw new FormatException("PNG is too large.");

        int channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new FormatException($"Bad PNG color type {colorType}.") };
        bool validDepth = colorType switch
        {
            0 => bitDepth is 1 or 2 or 4 or 8 or 16,
            3 => bitDepth is 1 or 2 or 4 or 8,
            _ => bitDepth is 8 or 16,
        };
        if (!validDepth) throw new FormatException($"Bad PNG bit depth {bitDepth} for color type {colorType}.");
        if (colorType == 3 && palette is null) throw new FormatException("Palette PNG without PLTE.");

        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        z.CopyTo(raw);
        var inflated = raw.GetBuffer().AsSpan(0, (int)raw.Length);

        var rgba = new byte[width * height * 4];
        int bitsPerPixel = channels * bitDepth;
        int bpp = Math.Max(1, bitsPerPixel / 8); // filter byte distance
        if (interlace == 0)
        {
            Unfilter(inflated, width, height, bitsPerPixel, bpp, out int consumed);
            Expand(inflated, consumed, width, height, 0, 0, 1, 1, width, bitDepth, colorType, channels, palette, transparency, rgba);
        }
        else
        {
            // Adam7: seven reduced images, each filtered separately.
            ReadOnlySpan<int> x0 = [0, 4, 0, 2, 0, 1, 0], y0 = [0, 0, 4, 0, 2, 0, 1], dx = [8, 8, 4, 4, 2, 2, 1], dy = [8, 8, 8, 4, 4, 2, 2];
            int offset = 0;
            for (int pass = 0; pass < 7; pass++)
            {
                int pw = (width - x0[pass] + dx[pass] - 1) / dx[pass];
                int ph = (height - y0[pass] + dy[pass] - 1) / dy[pass];
                if (pw <= 0 || ph <= 0) continue;
                var sub = inflated[offset..];
                Unfilter(sub, pw, ph, bitsPerPixel, bpp, out int consumed);
                Expand(sub, consumed, pw, ph, x0[pass], y0[pass], dx[pass], dy[pass], width, bitDepth, colorType, channels, palette, transparency, rgba);
                offset += consumed;
            }
        }
        return new TextureData(width, height, rgba, srgb);
    }

    /// <summary>Undo the per-row filters in place; rows keep their leading filter byte.</summary>
    private static void Unfilter(Span<byte> data, int width, int height, int bitsPerPixel, int bpp, out int consumed)
    {
        int stride = (width * bitsPerPixel + 7) / 8;
        consumed = (stride + 1) * height;
        if (data.Length < consumed) throw new FormatException("PNG image data is truncated.");
        for (int y = 0; y < height; y++)
        {
            var row = data.Slice(y * (stride + 1) + 1, stride);
            var prev = y > 0 ? data.Slice((y - 1) * (stride + 1) + 1, stride) : Span<byte>.Empty;
            byte filter = data[y * (stride + 1)];
            switch (filter)
            {
                case 0: break;
                case 1:
                    for (int i = bpp; i < stride; i++) row[i] += row[i - bpp];
                    break;
                case 2:
                    if (y > 0) for (int i = 0; i < stride; i++) row[i] += prev[i];
                    break;
                case 3:
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= bpp ? row[i - bpp] : 0, up = y > 0 ? prev[i] : 0;
                        row[i] += (byte)((left + up) >> 1);
                    }
                    break;
                case 4:
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= bpp ? row[i - bpp] : 0, b = y > 0 ? prev[i] : 0, c = i >= bpp && y > 0 ? prev[i - bpp] : 0;
                        row[i] += Paeth(a, b, c);
                    }
                    break;
                default: throw new FormatException($"Bad PNG filter {filter}.");
            }
        }
    }

    private static byte Paeth(int a, int b, int c)
    {
        int p = a + b - c, pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
        return (byte)(pa <= pb && pa <= pc ? a : pb <= pc ? b : c);
    }

    /// <summary>Convert unfiltered rows to RGBA8 and place them (with the interlace pattern) in the output.</summary>
    private static void Expand(ReadOnlySpan<byte> data, int consumed, int w, int h, int ox, int oy, int sx, int sy, int fullWidth,
        int depth, int colorType, int channels, byte[]? palette, byte[]? trns, byte[] rgba)
    {
        int stride = (w * channels * depth + 7) / 8;
        int maxValue = (1 << Math.Min(depth, 8)) - 1;
        for (int y = 0; y < h; y++)
        {
            var row = data.Slice(y * (stride + 1) + 1, stride);
            for (int x = 0; x < w; x++)
            {
                int o = (((oy + y * sy) * fullWidth) + ox + x * sx) * 4;
                byte r, g, b, a = 255;
                switch (colorType)
                {
                    case 0:
                    {
                        int raw = Sample(row, x, 0, 1, depth);
                        byte v = Scale(raw, depth, maxValue);
                        r = g = b = v;
                        if (trns is { Length: >= 2 } && raw == BinaryPrimitives.ReadUInt16BigEndian(trns)) a = 0;
                        break;
                    }
                    case 2:
                    {
                        int rr = Sample(row, x, 0, 3, depth), gg = Sample(row, x, 1, 3, depth), bb = Sample(row, x, 2, 3, depth);
                        r = Scale(rr, depth, maxValue); g = Scale(gg, depth, maxValue); b = Scale(bb, depth, maxValue);
                        if (trns is { Length: >= 6 } && rr == BinaryPrimitives.ReadUInt16BigEndian(trns)
                            && gg == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(2)) && bb == BinaryPrimitives.ReadUInt16BigEndian(trns.AsSpan(4))) a = 0;
                        break;
                    }
                    case 3:
                    {
                        int i = Sample(row, x, 0, 1, depth);
                        if (i * 3 + 2 >= palette!.Length) throw new FormatException("PNG palette index out of range.");
                        r = palette[i * 3]; g = palette[i * 3 + 1]; b = palette[i * 3 + 2];
                        if (trns is not null && i < trns.Length) a = trns[i];
                        break;
                    }
                    case 4:
                        r = g = b = Scale(Sample(row, x, 0, 2, depth), depth, maxValue);
                        a = Scale(Sample(row, x, 1, 2, depth), depth, maxValue);
                        break;
                    default: // 6
                        r = Scale(Sample(row, x, 0, 4, depth), depth, maxValue);
                        g = Scale(Sample(row, x, 1, 4, depth), depth, maxValue);
                        b = Scale(Sample(row, x, 2, 4, depth), depth, maxValue);
                        a = Scale(Sample(row, x, 3, 4, depth), depth, maxValue);
                        break;
                }
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }
        _ = consumed;
    }

    /// <summary>The raw value of channel <paramref name="c"/> of pixel <paramref name="x"/> (16-bit samples whole).</summary>
    private static int Sample(ReadOnlySpan<byte> row, int x, int c, int channels, int depth)
    {
        if (depth == 8) return row[x * channels + c];
        if (depth == 16) return BinaryPrimitives.ReadUInt16BigEndian(row[((x * channels + c) * 2)..]);
        int bit = (x * channels + c) * depth;
        return (row[bit >> 3] >> (8 - depth - (bit & 7))) & ((1 << depth) - 1);
    }

    private static byte Scale(int v, int depth, int max) => depth switch
    {
        8 => (byte)v,
        16 => (byte)(v >> 8),
        _ => (byte)(v * 255 / max),
    };

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }

    public static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFFu;
        foreach (byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }
}
