using System.Buffers.Binary;

namespace Gamegrl.Audio;

public enum WavSampleFormat : byte { Pcm16, Pcm24, Float32 }

/// <summary>
/// RIFF WAVE reading and writing: PCM 8, 16, 24 and 32-bit, float 32 and 64-bit, and WAVE_FORMAT_EXTENSIBLE with its
/// channel mask (any layout up to 18 speakers).
/// </summary>
public static class Wav
{
    private const ushort FormatPcm = 1, FormatFloat = 3, FormatExtensible = 0xFFFE;

    public static AudioClip Read(string path) => Read(File.ReadAllBytes(path), Path.GetFileNameWithoutExtension(path));

    public static AudioClip Read(ReadOnlySpan<byte> file, string name = "wav")
    {
        if (file.Length < 12 || !file[..4].SequenceEqual("RIFF"u8) || !file.Slice(8, 4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a RIFF WAVE file");
        int pos = 12;
        ushort format = 0, channels = 0, bits = 0;
        int rate = 0;
        uint mask = 0;
        ReadOnlySpan<byte> data = default;
        bool haveFormat = false, haveData = false;
        while (pos + 8 <= file.Length)
        {
            var id = file.Slice(pos, 4);
            int size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(file.Slice(pos + 4, 4)), (uint)(file.Length - pos - 8));
            var body = file.Slice(pos + 8, size);
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException("Short fmt chunk");
                format = BinaryPrimitives.ReadUInt16LittleEndian(body);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(body[4..]);
                bits = BinaryPrimitives.ReadUInt16LittleEndian(body[14..]);
                if (format == FormatExtensible)
                {
                    if (size < 40) throw new InvalidDataException("Short WAVE_FORMAT_EXTENSIBLE chunk");
                    mask = BinaryPrimitives.ReadUInt32LittleEndian(body[20..]);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(body[24..]); // the sub-format GUID starts with the format tag
                }
                haveFormat = true;
            }
            else if (id.SequenceEqual("data"u8)) { data = body; haveData = true; }
            pos += 8 + size + (size & 1);
        }
        if (!haveFormat || !haveData) throw new InvalidDataException("Missing fmt or data chunk");
        if (channels == 0 || rate <= 0) throw new InvalidDataException("Bad channel count or rate");
        var layout = mask != 0 && System.Numerics.BitOperations.PopCount(mask) == channels ? ChannelLayout.FromMask(mask) : ChannelLayout.ForChannels(channels);

        int bytes = bits / 8;
        int count = data.Length / bytes / channels * channels;
        var samples = new float[count];
        switch (format, bits)
        {
            case (FormatPcm, 8): for (int i = 0; i < count; i++) samples[i] = (data[i] - 128) / 128f; break;
            case (FormatPcm, 16): for (int i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(2 * i)..]) / 32768f; break;
            case (FormatPcm, 24):
                for (int i = 0; i < count; i++)
                {
                    int v = data[3 * i] | (data[3 * i + 1] << 8) | ((sbyte)data[3 * i + 2] << 16);
                    samples[i] = v / 8388608f;
                }
                break;
            case (FormatPcm, 32): for (int i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(4 * i)..]) / 2147483648f; break;
            case (FormatFloat, 32): for (int i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(4 * i)..]); break;
            case (FormatFloat, 64): for (int i = 0; i < count; i++) samples[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(data[(8 * i)..]); break;
            default: throw new InvalidDataException($"Unsupported WAV format {format} with {bits} bits");
        }
        return new AudioClip(name, rate, layout, samples);
    }

    public static void Write(string path, AudioClip clip, WavSampleFormat format = WavSampleFormat.Float32)
    {
        using var f = File.Create(path);
        Write(f, clip.Samples, clip.SampleRate, clip.Layout, format);
    }

    /// <summary>
    /// Writes interleaved samples. Layouts other than mono and stereo use WAVE_FORMAT_EXTENSIBLE with the channel mask,
    /// so players route 5.1 and 7.1 correctly. PCM output is clipped to [−1, 1].
    /// </summary>
    public static void Write(Stream stream, ReadOnlySpan<float> interleaved, int sampleRate, ChannelLayout layout, WavSampleFormat format = WavSampleFormat.Float32)
    {
        int channels = layout.Count;
        int bytes = format switch { WavSampleFormat.Pcm16 => 2, WavSampleFormat.Pcm24 => 3, _ => 4 };
        bool extensible = channels > 2;
        int fmtSize = extensible ? 40 : 16;
        int dataSize = interleaved.Length * bytes;
        ushort tag = format == WavSampleFormat.Float32 ? FormatFloat : FormatPcm;

        Span<byte> header = stackalloc byte[12 + 8 + 40 + 8];
        int p = 0;
        void Tag(Span<byte> h, ReadOnlySpan<byte> t) { t.CopyTo(h[p..]); p += 4; }
        void U32(Span<byte> h, uint v) { BinaryPrimitives.WriteUInt32LittleEndian(h[p..], v); p += 4; }
        void U16(Span<byte> h, ushort v) { BinaryPrimitives.WriteUInt16LittleEndian(h[p..], v); p += 2; }
        Tag(header, "RIFF"u8); U32(header, (uint)(4 + 8 + fmtSize + 8 + dataSize + (dataSize & 1))); Tag(header, "WAVE"u8);
        Tag(header, "fmt "u8); U32(header, (uint)fmtSize);
        U16(header, extensible ? FormatExtensible : tag); U16(header, (ushort)channels);
        U32(header, (uint)sampleRate); U32(header, (uint)(sampleRate * channels * bytes));
        U16(header, (ushort)(channels * bytes)); U16(header, (ushort)(bytes * 8));
        if (extensible)
        {
            U16(header, 22); U16(header, (ushort)(bytes * 8)); U32(header, layout.Mask);
            // KSDATAFORMAT_SUBTYPE_PCM / IEEE_FLOAT: {0000000x-0000-0010-8000-00AA00389B71}
            U16(header, tag); U16(header, 0);
            ReadOnlySpan<byte> guidTail = [0x00, 0x00, 0x10, 0x00, 0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71];
            guidTail.CopyTo(header[p..]); p += 12;
        }
        Tag(header, "data"u8); U32(header, (uint)dataSize);
        stream.Write(header[..p]);

        var buffer = new byte[Math.Min(dataSize, 1 << 16) / (bytes * channels) * (bytes * channels) + bytes * channels];
        int written = 0;
        while (written < interleaved.Length)
        {
            int n = Math.Min(interleaved.Length - written, buffer.Length / bytes);
            var src = interleaved.Slice(written, n);
            for (int i = 0; i < n; i++)
            {
                float x = src[i];
                switch (format)
                {
                    case WavSampleFormat.Float32: BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(4 * i), x); break;
                    case WavSampleFormat.Pcm16: BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(2 * i), (short)Math.Clamp(MathF.Round(x * 32767f), -32768, 32767)); break;
                    default:
                    {
                        int v = (int)Math.Clamp(MathF.Round(x * 8388607f), -8388608, 8388607);
                        buffer[3 * i] = (byte)v; buffer[3 * i + 1] = (byte)(v >> 8); buffer[3 * i + 2] = (byte)(v >> 16);
                        break;
                    }
                }
            }
            stream.Write(buffer, 0, n * bytes);
            written += n;
        }
        if ((dataSize & 1) != 0) stream.WriteByte(0);
    }
}
