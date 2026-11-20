namespace Gamegrl.Audio.Acoustics;

/// <summary>Three values, one per band: below 800 Hz, 800 Hz to 8 kHz, above 8 kHz (see <see cref="Spatial.Bands"/>).</summary>
public struct Bands3
{
    public float Low, Mid, High;

    public Bands3(float low, float mid, float high) { Low = low; Mid = mid; High = high; }
    public static Bands3 All(float v) => new(v, v, v);
    public static Bands3 operator *(Bands3 a, Bands3 b) => new(a.Low * b.Low, a.Mid * b.Mid, a.High * b.High);
    public static Bands3 operator *(Bands3 a, float s) => new(a.Low * s, a.Mid * s, a.High * s);
    public static Bands3 operator +(Bands3 a, Bands3 b) => new(a.Low + b.Low, a.Mid + b.Mid, a.High + b.High);
    public readonly float this[int band] => band switch { 0 => Low, 1 => Mid, _ => High };
    public override readonly string ToString() => $"({Low:0.###}, {Mid:0.###}, {High:0.###})";
}

/// <summary>
/// How a surface treats sound, per band: the fraction of energy it absorbs on reflection, the fraction of reflected
/// energy scattered diffusely rather than mirrored, and the fraction of sound pressure that passes through it (per
/// crossing: a surface is a thin wall). Presets follow typical published absorption tables, averaged into the three
/// bands; <see cref="FromHardness"/> makes one from a single "how hard is it" number.
/// </summary>
public struct AcousticMaterial
{
    public Bands3 Absorption;
    [Range(0, 1)] public float Scattering;
    public Bands3 Transmission;

    public AcousticMaterial(Bands3 absorption, float scattering, Bands3 transmission)
    {
        Absorption = absorption; Scattering = scattering; Transmission = transmission;
    }

    public readonly bool Same(in AcousticMaterial o) =>
        Absorption.Low == o.Absorption.Low && Absorption.Mid == o.Absorption.Mid && Absorption.High == o.Absorption.High && Scattering == o.Scattering
        && Transmission.Low == o.Transmission.Low && Transmission.Mid == o.Transmission.Mid && Transmission.High == o.Transmission.High;

    public static AcousticMaterial Generic => new(new(0.10f, 0.20f, 0.30f), 0.05f, new(0.100f, 0.050f, 0.030f));
    public static AcousticMaterial Brick => new(new(0.03f, 0.04f, 0.07f), 0.05f, new(0.015f, 0.015f, 0.015f));
    public static AcousticMaterial Concrete => new(new(0.05f, 0.07f, 0.08f), 0.05f, new(0.015f, 0.002f, 0.001f));
    public static AcousticMaterial Ceramic => new(new(0.01f, 0.02f, 0.02f), 0.05f, new(0.060f, 0.044f, 0.011f));
    public static AcousticMaterial Gravel => new(new(0.60f, 0.70f, 0.80f), 0.05f, new(0.031f, 0.012f, 0.008f));
    public static AcousticMaterial Carpet => new(new(0.24f, 0.69f, 0.73f), 0.05f, new(0.020f, 0.005f, 0.003f));
    public static AcousticMaterial Glass => new(new(0.06f, 0.03f, 0.02f), 0.05f, new(0.060f, 0.044f, 0.011f));
    public static AcousticMaterial Plaster => new(new(0.12f, 0.06f, 0.04f), 0.05f, new(0.056f, 0.056f, 0.004f));
    public static AcousticMaterial Wood => new(new(0.11f, 0.07f, 0.06f), 0.05f, new(0.070f, 0.014f, 0.005f));
    public static AcousticMaterial Metal => new(new(0.20f, 0.07f, 0.06f), 0.05f, new(0.200f, 0.025f, 0.010f));
    public static AcousticMaterial Rock => new(new(0.13f, 0.20f, 0.24f), 0.20f, new(0.015f, 0.002f, 0.001f));
    /// <summary>Still water: almost a perfect mirror.</summary>
    public static AcousticMaterial Water => new(new(0.01f, 0.01f, 0.02f), 0.02f, new(0.001f, 0.001f, 0.001f));
    /// <summary>Hedges and leaves: absorb highs, scatter a lot, let most sound through.</summary>
    public static AcousticMaterial Foliage => new(new(0.03f, 0.10f, 0.25f), 0.70f, new(0.80f, 0.60f, 0.40f));
    /// <summary>Heavy curtains and soft furnishings.</summary>
    public static AcousticMaterial Fabric => new(new(0.14f, 0.55f, 0.65f), 0.10f, new(0.30f, 0.15f, 0.08f));

    /// <summary>
    /// A material from hardness alone: 0 is soft and absorbent (heavy fabric, absorbing highs most), 1 is hard and
    /// reflective (glazed tile). Softer surfaces also scatter more and let more through.
    /// </summary>
    public static AcousticMaterial FromHardness(float hardness)
    {
        float h = Math.Clamp(hardness, 0, 1), soft = 1 - h;
        static float Mix(float a, float b, float t) => a + (b - a) * t;
        return new AcousticMaterial(
            new(Mix(0.01f, 0.25f, soft), Mix(0.02f, 0.65f, soft), Mix(0.02f, 0.75f, soft)),
            Mix(0.05f, 0.30f, soft),
            new(Mix(0.01f, 0.30f, soft), Mix(0.002f, 0.15f, soft), Mix(0.001f, 0.08f, soft)));
    }
}
