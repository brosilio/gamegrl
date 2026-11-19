using System.Numerics;
using System.Runtime.CompilerServices;

namespace Gamegrl;

/// <summary>
/// Math for the fixed-tick simulation. Every function picks its implementation from <see cref="Determinism"/>:
/// <see cref="Strict"/> under <c>CrossPlatform</c> (bit-identical everywhere), <see cref="Fast"/> under <c>Local</c>.
/// Simulation code calls these instead of <see cref="MathF"/> and the <c>System.Numerics</c> members that may fuse or
/// reduce in a platform-specific order (<c>Dot</c>, <c>Length</c>, <c>Normalize</c>, <c>Lerp</c>, <c>Transform</c>,
/// quaternion products). Element-wise vector operators (+, −, ×, ÷ by a scalar) are exact everywhere and fine to use.
/// </summary>
public static class SimMath
{
    public const float Pi = MathF.PI, Tau = MathF.PI * 2;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Sqrt(float x) => MathF.Sqrt(x); // correctly rounded on every target
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float MulAdd(float a, float b, float c) => Determinism.CrossPlatform ? a * b + c : MathF.FusedMultiplyAdd(a, b, c);

    public static float Sin(float x) => Determinism.CrossPlatform ? Strict.Sin(x) : MathF.Sin(x);
    public static float Cos(float x) => Determinism.CrossPlatform ? Strict.Cos(x) : MathF.Cos(x);
    public static (float Sin, float Cos) SinCos(float x) => Determinism.CrossPlatform ? Strict.SinCos(x) : MathF.SinCos(x);
    public static float Atan(float x) => Determinism.CrossPlatform ? Strict.Atan(x) : MathF.Atan(x);
    public static float Atan2(float y, float x) => Determinism.CrossPlatform ? Strict.Atan2(y, x) : MathF.Atan2(y, x);
    public static float Asin(float x) => Determinism.CrossPlatform ? Strict.Asin(x) : MathF.Asin(x);
    public static float Acos(float x) => Determinism.CrossPlatform ? Strict.Acos(x) : MathF.Acos(x);
    public static float Exp(float x) => Determinism.CrossPlatform ? Strict.Exp(x) : MathF.Exp(x);
    public static float Log(float x) => Determinism.CrossPlatform ? Strict.Log(x) : MathF.Log(x);
    public static float Pow(float x, float y) => Determinism.CrossPlatform ? Strict.Pow(x, y) : MathF.Pow(x, y);

    // Scalar in both modes: a fixed order, and faster than Vector2/3.Dot, which compiles to dpps (about 13 cycles of
    // latency on common x64 cores) against two or three multiplies and adds.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Dot(Vector3 a, Vector3 b) => (a.X * b.X + a.Y * b.Y) + a.Z * b.Z;
    /// <summary>The z of the 3D cross product of (a, 0) and (b, 0).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector3 Cross(Vector3 a, Vector3 b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    public static float LengthSquared(Vector2 v) => Dot(v, v);
    public static float LengthSquared(Vector3 v) => Dot(v, v);
    public static float Length(Vector2 v) => Sqrt(Dot(v, v));
    public static float Length(Vector3 v) => Sqrt(Dot(v, v));
    public static float Distance(Vector2 a, Vector2 b) => Length(a - b);
    public static float Distance(Vector3 a, Vector3 b) => Length(a - b);
    /// <summary>The unit vector, or zero for a zero vector.</summary>
    public static Vector2 Normalize(Vector2 v) { float l = Length(v); return l > 0 ? v / l : default; }
    public static Vector3 Normalize(Vector3 v) { float l = Length(v); return l > 0 ? v / l : default; }
    public static float Lerp(float a, float b, float t) => a + (b - a) * t;
    public static Vector2 Lerp(Vector2 a, Vector2 b, float t) => a + (b - a) * t;
    public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * t;

    /// <summary>Hamilton product: rotate by <paramref name="b"/>, then by <paramref name="a"/> (as <c>a * b</c>).</summary>
    public static Quaternion Mul(Quaternion a, Quaternion b) => new(
        a.W * b.X + a.X * b.W + (a.Y * b.Z - a.Z * b.Y),
        a.W * b.Y + a.Y * b.W + (a.Z * b.X - a.X * b.Z),
        a.W * b.Z + a.Z * b.W + (a.X * b.Y - a.Y * b.X),
        a.W * b.W - (a.X * b.X + a.Y * b.Y + a.Z * b.Z));

    public static Quaternion Normalize(Quaternion q)
    {
        float l = Sqrt((q.X * q.X + q.Y * q.Y) + (q.Z * q.Z + q.W * q.W));
        return l > 0 ? new Quaternion(q.X / l, q.Y / l, q.Z / l, q.W / l) : Quaternion.Identity;
    }

    /// <summary>A rotation of <paramref name="angle"/> radians around a unit <paramref name="axis"/>.</summary>
    public static Quaternion AxisAngle(Vector3 axis, float angle)
    {
        var (s, c) = SinCos(angle * 0.5f);
        return new Quaternion(axis.X * s, axis.Y * s, axis.Z * s, c);
    }

    /// <summary>Rotate <paramref name="v"/> by the unit quaternion <paramref name="q"/>.</summary>
    public static Vector3 Rotate(Quaternion q, Vector3 v)
    {
        var u = new Vector3(q.X, q.Y, q.Z);
        var t = Cross(u, v) * 2;
        return v + t * q.W + Cross(u, t);
    }

    /// <summary>
    /// The <c>CrossPlatform</c> implementations, callable directly. Transcendentals evaluate in double with only
    /// correctly rounded operations (no fused multiply-add, no platform library), then round to float once, so they
    /// return the same bits on every IEEE-754 target and stay within about one ulp of the true value.
    /// </summary>
    public static class Strict
    {
        // π/2 split for Cody–Waite reduction: Pio2Hi has 33 significant bits, so k·Pio2Hi is exact for |k| < 2^20.
        private const double Pio2Hi = 1.57079632673412561417e+00, Pio2Lo = 6.07710050650619224932e-11, TwoOverPi = 6.36619772367581382433e-01;
        private const double Ln2Hi = 6.93147180369123816490e-01, Ln2Lo = 1.90821492927058770002e-10;

        public static float Sin(float x) => (float)SinCosCore(x, out _);
        public static float Cos(float x) { SinCosCore(x, out double c); return (float)c; }
        public static (float Sin, float Cos) SinCos(float x) { double s = SinCosCore(x, out double c); return ((float)s, (float)c); }

        private static double SinCosCore(double x, out double cos)
        {
            if (!double.IsFinite(x)) { cos = double.NaN; return double.NaN; }
            double k = Math.Round(x * TwoOverPi);
            double r = (x - k * Pio2Hi) - k * Pio2Lo;
            double s = SinKernel(r), c = CosKernel(r);
            switch ((long)k & 3)
            {
                case 0: cos = c; return s;
                case 1: cos = -s; return c;
                case 2: cos = -c; return -s;
                default: cos = s; return -c;
            }
        }

        // fdlibm's minimax polynomials on [-π/4, π/4], evaluated without fusing.
        private static double SinKernel(double x)
        {
            double z = x * x;
            double p = 1.58969099521155010221e-10;
            p = p * z + -2.50507602534068634195e-08;
            p = p * z + 2.75573137070700676789e-06;
            p = p * z + -1.98412698298579493134e-04;
            p = p * z + 8.33333333332248946124e-03;
            p = p * z + -1.66666666666666324348e-01;
            return x + x * z * p;
        }

        private static double CosKernel(double x)
        {
            double z = x * x;
            double p = -1.13596475577881948265e-11;
            p = p * z + 2.08757232129817482790e-09;
            p = p * z + -2.75573143513906633035e-07;
            p = p * z + 2.48015872894767294178e-05;
            p = p * z + -1.38888888888741095749e-03;
            p = p * z + 4.16666666666666019037e-02;
            return 1.0 - 0.5 * z + z * z * p;
        }

        public static float Atan(float x) => (float)AtanCore(x);
        public static float Atan2(float y, float x) => (float)Atan2Core(y, x);
        public static float Asin(float x) => x is >= -1 and <= 1 ? (float)Atan2Core(x, Math.Sqrt((1.0 - x) * (1.0 + x))) : float.NaN;
        public static float Acos(float x) => x is >= -1 and <= 1 ? (float)Atan2Core(Math.Sqrt((1.0 - x) * (1.0 + x)), x) : float.NaN;

        private static double AtanCore(double x)
        {
            if (double.IsNaN(x)) return x;
            bool negative = x < 0;
            double t = Math.Abs(x), offset = 0;
            bool inverted = t > 1;
            if (inverted) t = 1 / t;
            // tan(π/12) ≈ 0.2679: shift by π/6 so the series argument stays small.
            if (t > 0.2679491924311227)
            {
                const double Sqrt3 = 1.7320508075688772;
                t = (t * Sqrt3 - 1) / (t + Sqrt3);
                offset = 0.52359877559829887308; // π/6
            }
            // atan(t) = t − t³/3 + t⁵/5 − …, |t| ≤ 0.268: 15 terms reach double precision.
            double z = t * t, p = 0;
            for (int n = 29; n >= 3; n -= 2) p = (((n >> 1) & 1) == 0 ? 1.0 : -1.0) / n + z * p;
            double a = offset + (t + t * z * p);
            if (inverted) a = 1.57079632679489655800 - a;
            return negative ? -a : a;
        }

        private static double Atan2Core(double y, double x)
        {
            if (double.IsNaN(x) || double.IsNaN(y)) return double.NaN;
            const double Pi = 3.14159265358979311600, HalfPi = 1.57079632679489655800;
            if (x == 0)
            {
                if (y == 0) return double.IsNegative(x) ? (double.IsNegative(y) ? -Pi : Pi) : y; // keeps signed zeros
                return y > 0 ? HalfPi : -HalfPi;
            }
            if (double.IsInfinity(x) || double.IsInfinity(y))
            {
                if (double.IsInfinity(x) && double.IsInfinity(y)) return (x > 0 ? Pi / 4 : 3 * Pi / 4) * Math.Sign(y);
                if (double.IsInfinity(y)) return y > 0 ? HalfPi : -HalfPi;
                return x > 0 ? (double.IsNegative(y) ? -0.0 : 0.0) : (double.IsNegative(y) ? -Pi : Pi);
            }
            double a = AtanCore(y / x);
            if (x > 0) return a;
            return double.IsNegative(y) ? a - Pi : a + Pi;
        }

        public static float Exp(float x) => (float)ExpCore(x);
        public static float Log(float x) => (float)LogCore(x);
        public static float Pow(float x, float y)
        {
            if (y == 0) return 1;
            if (x == 1) return 1;
            if (x < 0)
            {
                // Only integer exponents are defined for negative bases.
                if (MathF.Round(y) != y) return float.NaN;
                double r = ExpCore(y * LogCore(-x));
                return (float)(((long)y & 1) != 0 ? -r : r);
            }
            if (x == 0) return y > 0 ? 0 : float.PositiveInfinity;
            return (float)ExpCore(y * LogCore(x));
        }

        private static double ExpCore(double x)
        {
            if (double.IsNaN(x)) return x;
            if (x > 709.78) return double.PositiveInfinity;
            if (x < -745.2) return 0;
            double k = Math.Round(x * 1.44269504088896338700); // 1/ln 2
            double r = (x - k * Ln2Hi) - k * Ln2Lo; // |r| ≤ ln2/2
            // e^r by Horner on the Taylor series to r^13: |r|^14/14! < 2e-17 for |r| ≤ 0.347.
            double p = 1.0;
            for (int n = 13; n >= 1; n--) p = 1.0 + p * r / n;
            return Math.ScaleB(p, (int)k);
        }

        private static double LogCore(double x)
        {
            if (double.IsNaN(x) || x < 0) return double.NaN;
            if (x == 0) return double.NegativeInfinity;
            if (double.IsPositiveInfinity(x)) return x;
            int k = Math.ILogB(x);
            double m = Math.ScaleB(x, -k); // [1, 2)
            if (m > 1.41421356237309514547) { m *= 0.5; k++; }
            // log(m) = 2·atanh(s), s = (m−1)/(m+1), |s| ≤ 0.1716: 11 terms reach double precision.
            double s = (m - 1) / (m + 1), z = s * s, p = 0;
            for (int n = 23; n >= 3; n -= 2) p = 1.0 / n + z * p;
            double log = 2 * (s + s * z * p);
            return k * Ln2Hi + (k * Ln2Lo + log);
        }
    }

    /// <summary>The <c>Local</c> implementations, callable directly: the platform's own (fast, not portable).</summary>
    public static class Fast
    {
        public static float Sin(float x) => MathF.Sin(x);
        public static float Cos(float x) => MathF.Cos(x);
        public static float Atan2(float y, float x) => MathF.Atan2(y, x);
        public static float Exp(float x) => MathF.Exp(x);
        public static float Log(float x) => MathF.Log(x);
        public static float MulAdd(float a, float b, float c) => MathF.FusedMultiplyAdd(a, b, c);
    }
}
