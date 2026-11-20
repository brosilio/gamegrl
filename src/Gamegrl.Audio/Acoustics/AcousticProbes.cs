using System.Buffers.Binary;
using System.Numerics;
using Gamegrl.Assets;

namespace Gamegrl.Audio.Acoustics;

/// <summary>
/// A region whose acoustics are baked: the unit cube from −0.5 to 0.5 through the entity's transform, probed every
/// <see cref="Spacing"/> metres. The editor's bake fills <see cref="Probes"/>; while the listener is inside, the reverb
/// comes from the probes instead of being traced live.
/// </summary>
public struct AcousticProbeVolume : IComponent
{
    [Range(0.25, 50)] public float Spacing;
    public AssetRef<AcousticProbesAsset> Probes;

    public static AcousticProbeVolume Default => new() { Spacing = 2 };
}

/// <summary>
/// Reverb estimates baked on a grid through a volume's transform: each probe holds a <see cref="ReverbParams"/>, or is
/// invalid (inside solid geometry). <see cref="Sample"/> interpolates trilinearly between the valid probes around a point.
/// Stored as <c>"GGAP" u32 version · 4×4 transform · u32 nx, ny, nz · per probe: u8 valid · 8 floats</c>.
/// </summary>
public sealed class AcousticProbeGrid
{
    private const uint Magic = 0x50414747; // "GGAP"
    private const int ProbeBytes = 1 + 8 * 4;

    public Matrix4x4 Transform { get; }
    public int CountX { get; }
    public int CountY { get; }
    public int CountZ { get; }
    private readonly ReverbParams[] _probes;
    private readonly bool[] _valid;
    private readonly Matrix4x4 _inverse;

    public int Count => _probes.Length;
    public int ValidCount { get { int n = 0; foreach (bool v in _valid) if (v) n++; return n; } }

    private AcousticProbeGrid(Matrix4x4 transform, int nx, int ny, int nz, ReverbParams[] probes, bool[] valid)
    {
        Transform = transform; CountX = nx; CountY = ny; CountZ = nz; _probes = probes; _valid = valid;
        Matrix4x4.Invert(transform, out _inverse);
    }

    /// <summary>The world position of probe (x, y, z).</summary>
    public Vector3 Position(int x, int y, int z) => Vector3.Transform(Local(x, y, z), Transform);

    private Vector3 Local(int x, int y, int z) => new(Coord(x, CountX), Coord(y, CountY), Coord(z, CountZ));
    private static float Coord(int i, int n) => n <= 1 ? 0 : (float)i / (n - 1) - 0.5f;

    public bool Contains(Vector3 world)
    {
        var p = Vector3.Transform(world, _inverse);
        return MathF.Abs(p.X) <= 0.5f && MathF.Abs(p.Y) <= 0.5f && MathF.Abs(p.Z) <= 0.5f;
    }

    /// <summary>The probe counts along each axis for a volume (the box's size divided by the spacing, at least two).</summary>
    public static (int X, int Y, int Z) Counts(Matrix4x4 volume, float spacing)
    {
        float Size(Vector3 axis) => axis.Length();
        int N(float size) => Math.Clamp((int)MathF.Ceiling(size / MathF.Max(spacing, 0.05f)) + 1, 2, 256);
        return (N(Size(new Vector3(volume.M11, volume.M12, volume.M13))), N(Size(new Vector3(volume.M21, volume.M22, volume.M23))), N(Size(new Vector3(volume.M31, volume.M32, volume.M33))));
    }

    /// <summary>
    /// Bakes a volume: a reverb estimate at every probe outside solid geometry, in parallel. Takes as long as
    /// probes × rays × bounces ray casts; run it off the UI thread. <paramref name="progress"/> gets the fraction done.
    /// </summary>
    public static AcousticProbeGrid Bake(AcousticScene scene, Matrix4x4 volume, float spacing, int rays = 2048, int bounces = 64,
        float temperatureC = 20, float humidityPercent = 50, CancellationToken stop = default, Action<float>? progress = null)
    {
        var (nx, ny, nz) = Counts(volume, spacing);
        var probes = new ReverbParams[nx * ny * nz];
        var valid = new bool[probes.Length];
        var grid = new AcousticProbeGrid(volume, nx, ny, nz, probes, valid);
        int done = 0;
        Parallel.For(0, probes.Length, new ParallelOptions { CancellationToken = stop }, i =>
        {
            int x = i % nx, y = i / nx % ny, z = i / (nx * ny);
            var at = grid.Position(x, y, z);
            if (!scene.InsideSolid(at))
            {
                probes[i] = ReverbEstimator.Estimate(scene, at, rays, bounces, temperatureC, humidityPercent);
                valid[i] = true;
            }
            int n = Interlocked.Increment(ref done);
            if (progress is not null && (n & 15) == 0) progress((float)n / probes.Length);
        });
        return grid;
    }

    /// <summary>
    /// The reverb at a point: trilinear interpolation over the eight probes around it, renormalised over the valid ones
    /// (probes inside walls are skipped). False outside the volume or when none of the eight is valid.
    /// </summary>
    public bool Sample(Vector3 world, out ReverbParams reverb)
    {
        reverb = ReverbParams.None;
        var p = Vector3.Transform(world, _inverse);
        if (MathF.Abs(p.X) > 0.5f || MathF.Abs(p.Y) > 0.5f || MathF.Abs(p.Z) > 0.5f) return false;
        float gx = (p.X + 0.5f) * (CountX - 1), gy = (p.Y + 0.5f) * (CountY - 1), gz = (p.Z + 0.5f) * (CountZ - 1);
        int x0 = Math.Clamp((int)gx, 0, CountX - 2), y0 = Math.Clamp((int)gy, 0, CountY - 2), z0 = Math.Clamp((int)gz, 0, CountZ - 2);
        float fx = gx - x0, fy = gy - y0, fz = gz - z0;
        float weight = 0;
        var sum = default(ReverbParams);
        for (int c = 0; c < 8; c++)
        {
            int x = x0 + (c & 1), y = y0 + ((c >> 1) & 1), z = z0 + ((c >> 2) & 1);
            int i = x + CountX * (y + CountY * z);
            if (!_valid[i]) continue;
            float w = ((c & 1) != 0 ? fx : 1 - fx) * (((c >> 1) & 1) != 0 ? fy : 1 - fy) * (((c >> 2) & 1) != 0 ? fz : 1 - fz);
            if (w <= 0) continue;
            ref readonly var r = ref _probes[i];
            sum.DecaySeconds = sum.DecaySeconds + r.DecaySeconds * w;
            sum.Level += r.Level * w; sum.PreDelaySeconds += r.PreDelaySeconds * w;
            sum.Volume += r.Volume * w; sum.Surface += r.Surface * w; sum.Enclosure += r.Enclosure * w;
            weight += w;
        }
        if (weight <= 1e-6f) return false;
        float s = 1 / weight;
        reverb = new ReverbParams
        {
            DecaySeconds = sum.DecaySeconds * s, Level = sum.Level * s, PreDelaySeconds = sum.PreDelaySeconds * s,
            Volume = sum.Volume * s, Surface = sum.Surface * s, Enclosure = sum.Enclosure * s,
        };
        return true;
    }

    public byte[] ToBytes()
    {
        var b = new byte[8 + 64 + 12 + _probes.Length * ProbeBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(b, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        var m = Transform;
        ReadOnlySpan<float> matrix = [m.M11, m.M12, m.M13, m.M14, m.M21, m.M22, m.M23, m.M24, m.M31, m.M32, m.M33, m.M34, m.M41, m.M42, m.M43, m.M44];
        for (int i = 0; i < 16; i++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(8 + 4 * i), matrix[i]);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(72), CountX);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(76), CountY);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(80), CountZ);
        int o = 84;
        for (int i = 0; i < _probes.Length; i++, o += ProbeBytes)
        {
            b[o] = _valid[i] ? (byte)1 : (byte)0;
            ref readonly var r = ref _probes[i];
            ReadOnlySpan<float> f = [r.DecaySeconds.Low, r.DecaySeconds.Mid, r.DecaySeconds.High, r.Level, r.PreDelaySeconds, r.Volume, r.Surface, r.Enclosure];
            for (int k = 0; k < 8; k++) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o + 1 + 4 * k), f[k]);
        }
        return b;
    }

    public static AcousticProbeGrid FromBytes(ReadOnlySpan<byte> b)
    {
        if (b.Length < 84 || BinaryPrimitives.ReadUInt32LittleEndian(b) != Magic) throw new InvalidDataException("Not an acoustic probe grid");
        Span<float> m = stackalloc float[16];
        for (int i = 0; i < 16; i++) m[i] = BinaryPrimitives.ReadSingleLittleEndian(b[(8 + 4 * i)..]);
        var transform = new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
        int nx = BinaryPrimitives.ReadInt32LittleEndian(b[72..]), ny = BinaryPrimitives.ReadInt32LittleEndian(b[76..]), nz = BinaryPrimitives.ReadInt32LittleEndian(b[80..]);
        if (nx < 1 || ny < 1 || nz < 1 || (long)nx * ny * nz * ProbeBytes + 84 != b.Length) throw new InvalidDataException("Acoustic probe grid has the wrong size");
        var probes = new ReverbParams[nx * ny * nz];
        var valid = new bool[probes.Length];
        int o = 84;
        Span<float> f = stackalloc float[8];
        for (int i = 0; i < probes.Length; i++, o += ProbeBytes)
        {
            valid[i] = b[o] != 0;
            for (int k = 0; k < 8; k++) f[k] = BinaryPrimitives.ReadSingleLittleEndian(b[(o + 1 + 4 * k)..]);
            probes[i] = new ReverbParams { DecaySeconds = new Bands3(f[0], f[1], f[2]), Level = f[3], PreDelaySeconds = f[4], Volume = f[5], Surface = f[6], Enclosure = f[7] };
        }
        return new AcousticProbeGrid(transform, nx, ny, nz, probes, valid);
    }
}
