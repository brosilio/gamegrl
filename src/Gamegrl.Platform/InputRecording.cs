using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace Gamegrl.Platform;

/// <summary>
/// A session's input as the app saw it: per frame, the time step the host ticked with and every input event in
/// order. Replaying it (same deltas, same events, same build) reproduces the session bit for bit; under
/// <c>CrossPlatform</c> determinism, on any platform. Set it as <see cref="Input.Recording"/> to capture, call
/// <see cref="BeginFrame"/> with each frame's delta, then <see cref="Save"/>. Binary and compact: an hour at 60 Hz with
/// no input is about 700 KB.
/// </summary>
public sealed class InputRecording
{
    private enum Op : byte { Key = 1, MouseButton = 2, MouseMove = 3, Wheel = 4, Text = 5, ReleaseAll = 6 }

    private static ReadOnlySpan<byte> Magic => "GGIN"u8;
    private const int Version = 1;
    private readonly List<double> _deltas = new();
    private readonly List<int> _frameStart = new();
    private readonly List<byte> _events = new();

    public int FrameCount => _deltas.Count;
    public double Delta(int frame) => _deltas[frame];

    /// <summary>Start a frame that the host will tick with <paramref name="deltaSeconds"/>; events until the next call belong to it.</summary>
    public void BeginFrame(double deltaSeconds)
    {
        _deltas.Add(deltaSeconds);
        _frameStart.Add(_events.Count);
    }

    /// <summary>Drop the last frame and its events (a frame that started but never ticked).</summary>
    public void DiscardLastFrame()
    {
        if (_deltas.Count == 0) return;
        _events.RemoveRange(_frameStart[^1], _events.Count - _frameStart[^1]);
        _deltas.RemoveAt(_deltas.Count - 1);
        _frameStart.RemoveAt(_frameStart.Count - 1);
    }

    internal void Key(int hid, bool down, bool repeat) { if (!Open) return; Put(Op.Key); PutU16((ushort)hid); _events.Add((byte)((down ? 1 : 0) | (repeat ? 2 : 0))); }
    internal void MouseButton(int button, bool down) { if (!Open) return; Put(Op.MouseButton); _events.Add((byte)button); _events.Add(down ? (byte)1 : (byte)0); }
    internal void MouseMove(Vector2 position, Vector2 delta) { if (!Open) return; Put(Op.MouseMove); PutF(position.X); PutF(position.Y); PutF(delta.X); PutF(delta.Y); }
    internal void Wheel(Vector2 delta) { if (!Open) return; Put(Op.Wheel); PutF(delta.X); PutF(delta.Y); }
    internal void ReleaseAll() { if (Open) Put(Op.ReleaseAll); }
    internal void Text(string text)
    {
        if (!Open) return;
        Put(Op.Text);
        var bytes = Encoding.UTF8.GetBytes(text);
        PutU16((ushort)bytes.Length);
        _events.AddRange(bytes);
    }

    // Events before the first frame have nowhere to go.
    private bool Open => _deltas.Count > 0;
    private void Put(Op op) => _events.Add((byte)op);
    private void PutU16(ushort v) { _events.Add((byte)v); _events.Add((byte)(v >> 8)); }
    private void PutF(float v) { uint bits = BitConverter.SingleToUInt32Bits(v); for (int i = 0; i < 4; i++) _events.Add((byte)(bits >> (8 * i))); }

    /// <summary>Feed frame <paramref name="frame"/>'s events into <paramref name="input"/> (after its <see cref="Input.BeginFrame"/>).</summary>
    public void Apply(int frame, Input input)
    {
        var recording = input.Recording;
        input.Recording = null; // don't re-record a replay
        try
        {
            int end = frame + 1 < _frameStart.Count ? _frameStart[frame + 1] : _events.Count;
            var events = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_events)[_frameStart[frame]..end];
            int i = 0;
            while (i < events.Length)
            {
                switch ((Op)events[i++])
                {
                    case Op.Key:
                        int hid = events[i] | events[i + 1] << 8;
                        byte flags = events[i + 2];
                        i += 3;
                        input.OnKey(hid, (flags & 1) != 0, (flags & 2) != 0);
                        break;
                    case Op.MouseButton: input.OnMouseButton(events[i], events[i + 1] != 0); i += 2; break;
                    case Op.MouseMove: input.OnMouseMove(new Vector2(F(events, i), F(events, i + 4)), new Vector2(F(events, i + 8), F(events, i + 12))); i += 16; break;
                    case Op.Wheel: input.OnWheel(new Vector2(F(events, i), F(events, i + 4))); i += 8; break;
                    case Op.ReleaseAll: input.ReleaseAll(); break;
                    case Op.Text:
                        int length = events[i] | events[i + 1] << 8;
                        input.OnText(Encoding.UTF8.GetString(events.Slice(i + 2, length)));
                        i += 2 + length;
                        break;
                    default: throw new InvalidDataException("Corrupt input recording.");
                }
            }
        }
        finally { input.Recording = recording; }
    }

    private static float F(ReadOnlySpan<byte> bytes, int at) => BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]));

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic); w.Write(Version);
        w.Write(_deltas.Count);
        for (int f = 0; f < _deltas.Count; f++) { w.Write(_deltas[f]); w.Write(_frameStart[f]); }
        w.Write(_events.Count);
        w.Write(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_events));
    }

    public static InputRecording Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != Version) throw new InvalidDataException("Not a gamegrl input recording (or a different version).");
        var rec = new InputRecording();
        int frames = r.ReadInt32();
        for (int f = 0; f < frames; f++) { rec._deltas.Add(r.ReadDouble()); rec._frameStart.Add(r.ReadInt32()); }
        int bytes = r.ReadInt32();
        rec._events.AddRange(r.ReadBytes(bytes));
        return rec;
    }
}
