using System.Numerics;

namespace Gamegrl.Audio.Spatial;

/// <summary>
/// First-order ambisonics in AmbiX (ACN channel order W, Y, Z, X; SN3D normalisation). Ambisonic axes are X forward,
/// Y left, Z up. Scene beds (rain, wind, crowds) are recorded or authored this way and decoded into the speaker bed,
/// rotated against the listener's head so the world stays put when the camera turns.
/// </summary>
public static class Ambisonics
{
    /// <summary>A listener-space direction (+X right, +Y up, −Z forward) in ambisonic axes.</summary>
    public static Vector3 ToAmbi(Vector3 listener) => new(-listener.Z, -listener.X, listener.Y);

    /// <summary>The four AmbiX gains for a plane wave from a listener-space direction.</summary>
    public static Vector4 Encode(Vector3 direction)
    {
        var a = ToAmbi(Vector3.Normalize(direction));
        return new Vector4(1, a.Y, a.Z, a.X); // W, Y, Z, X
    }

    /// <summary>The decoding matrix for a bed with no rotation (row per bed channel, <c>m[o * 4 + i]</c>); see <see cref="AmbisonicDecoder"/>.</summary>
    public static float[] DecodeMatrix(ChannelLayout bed, Quaternion rotation)
    {
        var m = new float[bed.Count * 4];
        new AmbisonicDecoder(bed).Fill(rotation, m);
        return m;
    }
}

/// <summary>
/// Decodes AmbiX into a speaker bed: a projection decoder with max-rE weighting (first order: 0.577 in 3D, 0.707 for a
/// flat ring), scaled so a plane wave keeps unit power on average. The rotation turns the sound field before decoding
/// (pass the inverse of the listener's orientation so the field stays fixed in the world). LFE gets nothing.
/// <see cref="Fill"/> doesn't allocate, so the audio thread calls it.
/// </summary>
public sealed class AmbisonicDecoder
{
    private readonly ChannelLayout _bed;
    private readonly Vector3[] _speakers; // listener-space directions; zero for LFE
    private readonly bool _threeD;
    private readonly float _order1, _scale;

    public AmbisonicDecoder(ChannelLayout bed)
    {
        _bed = bed;
        _speakers = new Vector3[bed.Count];
        for (int c = 0; c < bed.Count; c++)
        {
            if (bed.Speakers[c] == Speaker.Lfe) continue;
            _speakers[c] = Directions.SpeakerVector(bed, bed.Speakers[c]);
            _threeD |= Directions.Speaker(bed, bed.Speakers[c]).Elevation > 1;
        }
        _order1 = _threeD ? 3 * 0.57735f : 2 * 0.70711f;
        _scale = 1;
        // Average plane-wave power over directions, for normalisation.
        Span<float> m = stackalloc float[bed.Count * 4];
        Fill(Quaternion.Identity, m);
        double power = 0;
        int samples = 0;
        for (int i = 0; i < 16; i++)
        for (int j = -3; j <= 3; j++)
        {
            if (!_threeD && j != 0) continue;
            var e = Ambisonics.Encode(Directions.FromAngles(i * 22.5f, j * 25f));
            for (int c = 0; c < bed.Count; c++)
            {
                float g = m[c * 4] * e.X + m[c * 4 + 1] * e.Y + m[c * 4 + 2] * e.Z + m[c * 4 + 3] * e.W;
                power += g * g;
            }
            samples++;
        }
        _scale = power > 0 ? (float)(1 / Math.Sqrt(power / samples)) : 1;
    }

    /// <summary>Writes the bed.Count × 4 matrix for a field rotated by <paramref name="rotation"/>.</summary>
    public void Fill(Quaternion rotation, Span<float> m)
    {
        // Rotating the field by R is decoding with the speakers rotated by R⁻¹.
        var inverse = Quaternion.Conjugate(rotation);
        for (int c = 0; c < _bed.Count; c++)
        {
            if (_speakers[c] == Vector3.Zero) { m.Slice(c * 4, 4).Clear(); continue; }
            var u = Ambisonics.ToAmbi(Vector3.Transform(_speakers[c], inverse));
            if (!_threeD) u.Z = 0;
            m[c * 4 + 0] = _scale;
            m[c * 4 + 1] = _scale * _order1 * u.Y;
            m[c * 4 + 2] = _scale * _order1 * u.Z;
            m[c * 4 + 3] = _scale * _order1 * u.X;
        }
    }
}
