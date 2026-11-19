using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.CompilerServices;

namespace Gamegrl.Physics2D;

/// <summary>A 2D rotation as its cosine and sine. Integrates without trigonometry, so it's deterministic in both modes.</summary>
public struct Rot
{
    public float C, S;
    public static Rot Identity => new() { C = 1, S = 0 };

    public static Rot FromAngle(float radians) { var (s, c) = SimMath.SinCos(radians); return new Rot { C = c, S = s }; }
    public readonly float Angle => SimMath.Atan2(S, C);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector2 Rotate(Vector2 v) => new(C * v.X - S * v.Y, S * v.X + C * v.Y);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector2 InvRotate(Vector2 v) => new(C * v.X + S * v.Y, -S * v.X + C * v.Y);

    /// <summary>q·r: rotate by r, then by q.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rot Mul(Rot q, Rot r) => new() { C = q.C * r.C - q.S * r.S, S = q.S * r.C + q.C * r.S };
    /// <summary>q⁻¹·r: the rotation from q to r.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rot InvMul(Rot q, Rot r) => new() { C = q.C * r.C + q.S * r.S, S = q.C * r.S - q.S * r.C };

    /// <summary>Advance by a small angle: q + Δθ·perp(q), renormalized (Box2D v3's integration; no trig).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Rot Integrate(Rot q, float deltaAngle)
    {
        float c = q.C - deltaAngle * q.S, s = q.S + deltaAngle * q.C;
        float l = SimMath.Sqrt(c * c + s * s);
        return l > 0 ? new Rot { C = c / l, S = s / l } : Identity;
    }

    /// <summary>The rotation about z held in a quaternion (the 2D part of a <see cref="LocalTransform"/>), without trig.</summary>
    public static Rot FromQuaternion(Quaternion q)
    {
        float c = q.W * q.W - q.Z * q.Z, s = 2 * q.W * q.Z;
        float l = SimMath.Sqrt(c * c + s * s);
        return l > 0 ? new Rot { C = c / l, S = s / l } : Identity;
    }

    /// <summary>The quaternion for this rotation about z, by half-angle identities (no trig).</summary>
    public readonly Quaternion ToQuaternion()
    {
        // cos(θ/2) = √((1+c)/2); sin(θ/2) = s / (2 cos(θ/2)), with the θ ≈ π case handled through the sine.
        if (C > -0.9f)
        {
            float w = SimMath.Sqrt((1 + C) * 0.5f);
            return new Quaternion(0, 0, S / (2 * w), w);
        }
        float z = SimMath.Sqrt((1 - C) * 0.5f);
        if (S < 0) z = -z;
        return new Quaternion(0, 0, z, S / (2 * z));
    }
}

/// <summary>An axis-aligned box.</summary>
public struct Aabb
{
    public Vector2 Min, Max;
    public readonly bool Overlaps(in Aabb o) => Min.X <= o.Max.X && o.Min.X <= Max.X && Min.Y <= o.Max.Y && o.Min.Y <= Max.Y;
}

/// <summary>
/// Plain-comparison min, max and clamp for the hot loops. <see cref="Math.Max(float, float)"/> implements IEEE 754-2019
/// (NaN and signed-zero cases), which costs branches per call; these are identical for the values physics produces.
/// </summary>
internal static class F
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Max(float a, float b) => a > b ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Min(float a, float b) => a < b ? a : b;
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
}

internal static class V
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    /// <summary>ω × r for a scalar angular velocity.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 Cross(float w, Vector2 r) => new(-w * r.Y, w * r.X);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static Vector2 RightPerp(Vector2 v) => new(v.Y, -v.X);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static float Dot(Vector2 a, Vector2 b) => a.X * b.X + a.Y * b.Y;
}

/// <summary>A convex collision shape in body space: a circle or a convex polygon of up to <see cref="MaxVertices"/> corners, with an optional rounding radius.</summary>
public unsafe struct Shape
{
    public const int MaxVertices = 8;
    public ShapeKind Kind;
    public float Radius;
    public Vector2 Center; // circle center, polygon centroid
    public int Count;
    public fixed float Vx[MaxVertices];
    public fixed float Vy[MaxVertices];
    public fixed float Nx[MaxVertices];
    public fixed float Ny[MaxVertices];

    public readonly Vector2 Vertex(int i) => new(Vx[i], Vy[i]);
    public readonly Vector2 Normal(int i) => new(Nx[i], Ny[i]);

    public static Shape Circle(float radius, Vector2 center) => new() { Kind = ShapeKind.Circle, Radius = radius, Center = center };

    /// <summary>A capsule: the segment <paramref name="a"/>–<paramref name="b"/> grown by <paramref name="radius"/> (a two-vertex rounded polygon).</summary>
    public static Shape Capsule(Vector2 a, Vector2 b, float radius)
    {
        var axis = b - a;
        if (SimMath.LengthSquared(axis) < 1e-8f) return Circle(radius, a);
        var n = SimMath.Normalize(V.RightPerp(axis));
        var s = new Shape { Kind = ShapeKind.Polygon, Count = 2, Radius = radius, Center = 0.5f * (a + b) };
        s.Vx[0] = a.X; s.Vy[0] = a.Y; s.Vx[1] = b.X; s.Vy[1] = b.Y;
        s.Nx[0] = n.X; s.Ny[0] = n.Y; s.Nx[1] = -n.X; s.Ny[1] = -n.Y;
        return s;
    }

    /// <summary>This shape moved into a parent body's frame by <paramref name="offset"/> and <paramref name="rotation"/> (compound bodies).</summary>
    public readonly Shape Transformed(Vector2 offset, Rot rotation)
    {
        var s = this;
        s.Center = offset + rotation.Rotate(Center);
        for (int i = 0; i < Count; i++)
        {
            var v = offset + rotation.Rotate(Vertex(i)); var n = rotation.Rotate(Normal(i));
            s.Vx[i] = v.X; s.Vy[i] = v.Y; s.Nx[i] = n.X; s.Ny[i] = n.Y;
        }
        return s;
    }

    public static Shape Box(Vector2 halfExtents, Vector2 center, Rot rotation)
    {
        Span<Vector2> v = [new(-halfExtents.X, -halfExtents.Y), new(halfExtents.X, -halfExtents.Y), new(halfExtents.X, halfExtents.Y), new(-halfExtents.X, halfExtents.Y)];
        for (int i = 0; i < 4; i++) v[i] = center + rotation.Rotate(v[i]);
        return Polygon(v);
    }

    /// <summary>A convex polygon from counter-clockwise points (3 to 8).</summary>
    public static Shape Polygon(ReadOnlySpan<Vector2> points)
    {
        if (points.Length is < 3 or > MaxVertices) throw new ArgumentException($"A polygon needs 3 to {MaxVertices} points.");
        var s = new Shape { Kind = ShapeKind.Polygon, Count = points.Length };
        var centroid = Vector2.Zero;
        float area = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            s.Vx[i] = a.X; s.Vy[i] = a.Y;
            var n = SimMath.Normalize(V.RightPerp(b - a));
            s.Nx[i] = n.X; s.Ny[i] = n.Y;
            float cross = V.Cross(a, b);
            area += cross;
            centroid += (a + b) * cross;
        }
        if (area <= 0) throw new ArgumentException("Polygon points must be counter-clockwise and not degenerate.");
        s.Center = centroid / (3 * area);
        return s;
    }

    /// <summary>Mass, rotational inertia about the body origin, and center of mass for a density.</summary>
    public readonly (float Mass, float Inertia, Vector2 Center) MassData(float density)
    {
        if (Kind == ShapeKind.Circle)
        {
            float m = density * SimMath.Pi * Radius * Radius;
            return (m, m * (0.5f * Radius * Radius + SimMath.Dot(Center, Center)), Center);
        }
        if (Count == 2)
        {
            // A capsule: a box plus two half discs (Box2D's formula, parallel-axis shifts included).
            var p1 = Vertex(0); var p2 = Vertex(1);
            float rr = Radius * Radius, length = SimMath.Distance(p1, p2), ll = length * length;
            float circleMass = density * SimMath.Pi * rr, boxMass = density * 2 * Radius * length;
            float lc = 4 * Radius / (3 * SimMath.Pi), h = 0.5f * length;
            var mid = 0.5f * (p1 + p2);
            float inertiaAtCenter = circleMass * (0.5f * rr + h * h + 2 * h * lc) + boxMass * (4 * rr + ll) / 12;
            float m = circleMass + boxMass;
            return (m, inertiaAtCenter + m * SimMath.Dot(mid, mid), mid);
        }
        // Triangle fan about the first vertex; inertia about the origin.
        float area = 0, inertia = 0;
        var c = Vector2.Zero;
        var origin = Vertex(0);
        for (int i = 1; i < Count - 1; i++)
        {
            var e1 = Vertex(i) - origin; var e2 = Vertex(i + 1) - origin;
            float d = V.Cross(e1, e2), a = 0.5f * d;
            area += a;
            c += (e1 + e2) * (a / 3);
            float intx2 = e1.X * e1.X + e2.X * e1.X + e2.X * e2.X, inty2 = e1.Y * e1.Y + e2.Y * e1.Y + e2.Y * e2.Y;
            inertia += 0.25f / 3 * d * (intx2 + inty2);
        }
        float mass = density * area;
        c = c / area;
        var center = origin + c;
        // Inertia about the centroid, then moved to the body origin.
        float iCentroid = density * inertia - mass * SimMath.Dot(c, c);
        return (mass, iCentroid + mass * SimMath.Dot(center, center), center);
    }

    public readonly Aabb Bounds(Vector2 position, Rot rotation)
    {
        if (Kind == ShapeKind.Circle)
        {
            var p = position + rotation.Rotate(Center);
            return new Aabb { Min = p - new Vector2(Radius), Max = p + new Vector2(Radius) };
        }
        if (Count <= 4) return Bounds4(position, rotation);
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        for (int i = 0; i < Count; i++)
        {
            var p = position + rotation.Rotate(Vertex(i));
            min = SimMath.Min(min, p); max = SimMath.Max(max, p);
        }
        return new Aabb { Min = min - new Vector2(Radius), Max = max + new Vector2(Radius) };
    }

    /// <summary>
    /// Up to four vertices in one pass: the same rotate-then-translate per vertex as the loop, in lanes (unused lanes
    /// repeat vertex 0), then a horizontal min and max: the same values as the loop (a zero may differ in sign only).
    /// </summary>
    private readonly Aabb Bounds4(Vector2 position, Rot rotation)
    {
        fixed (float* vx = Vx, vy = Vy)
        {
            var x = Vector128.Load(vx); var y = Vector128.Load(vy);
            if (Count < 4)
            {
                var used = Vector128.LessThan(Vector128.Create(0, 1, 2, 3), Vector128.Create(Count)).AsSingle();
                x = Vector128.ConditionalSelect(used, x, Vector128.Create(vx[0]));
                y = Vector128.ConditionalSelect(used, y, Vector128.Create(vy[0]));
            }
            var c = Vector128.Create(rotation.C); var sn = Vector128.Create(rotation.S);
            var px = (c * x - sn * y) + Vector128.Create(position.X);
            var py = (sn * x + c * y) + Vector128.Create(position.Y);
            float r = Radius;
            return new Aabb
            {
                Min = new Vector2(HMin(px) - r, HMin(py) - r),
                Max = new Vector2(HMax(px) + r, HMax(py) + r),
            };
        }
    }

    // Order-independent reductions (min and max of exact values), so the shuffle order cannot change the result.
    private static float HMin(Vector128<float> v)
    {
        var m = Min4(v, Vector128.Shuffle(v, Vector128.Create(2, 3, 0, 1)));
        return Min4(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))).ToScalar();
    }

    private static float HMax(Vector128<float> v)
    {
        var m = Max4(v, Vector128.Shuffle(v, Vector128.Create(2, 3, 0, 1)));
        return Max4(m, Vector128.Shuffle(m, Vector128.Create(1, 0, 3, 2))).ToScalar();
    }

    private static Vector128<float> Min4(Vector128<float> a, Vector128<float> b) =>
        System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.Min(a, b) : Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);

    private static Vector128<float> Max4(Vector128<float> a, Vector128<float> b) =>
        System.Runtime.Intrinsics.X86.Sse.IsSupported ? System.Runtime.Intrinsics.X86.Sse.Max(a, b) : Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);
}

public enum ShapeKind : byte { Circle, Polygon }

internal static class ShapeRay
{
    /// <summary>
    /// Where the segment from <paramref name="origin"/> along <paramref name="d"/> first enters the shape (all in the
    /// shape's frame), up to <paramref name="maxFraction"/>. Rounded polygons and capsules test their corner circles and
    /// their faces pushed out by the radius.
    /// </summary>
    public static bool Cast(in Shape s, Vector2 origin, Vector2 d, float maxFraction, out float fraction, out Vector2 normal)
    {
        fraction = maxFraction; normal = default;
        if (s.Kind == ShapeKind.Circle) return Circle(s.Center, s.Radius, origin, d, ref fraction, ref normal);
        if (s.Radius > 0)
        {
            bool hit = false;
            for (int i = 0; i < s.Count; i++)
            {
                hit |= Circle(s.Vertex(i), s.Radius, origin, d, ref fraction, ref normal);
                var n = s.Normal(i);
                var a = s.Vertex(i) + n * s.Radius; var b = s.Vertex(i + 1 < s.Count ? i + 1 : 0) + n * s.Radius;
                hit |= Segment(a, b, n, origin, d, ref fraction, ref normal);
            }
            return hit;
        }
        float lower = 0, upper = maxFraction;
        int index = -1;
        for (int i = 0; i < s.Count; i++)
        {
            var n = s.Normal(i);
            float numerator = V.Dot(n, s.Vertex(i) - origin), denominator = V.Dot(n, d);
            if (denominator == 0) { if (numerator < 0) return false; continue; }
            float t = numerator / denominator;
            if (denominator < 0 && t > lower) { lower = t; index = i; }
            else if (denominator > 0 && t < upper) upper = t;
            if (upper < lower) return false;
        }
        if (index < 0) return false;
        fraction = lower;
        normal = s.Normal(index);
        return true;
    }

    private static bool Circle(Vector2 center, float radius, Vector2 origin, Vector2 d, ref float fraction, ref Vector2 normal)
    {
        var m = origin - center;
        float a = V.Dot(d, d), b = V.Dot(m, d), c = V.Dot(m, m) - radius * radius;
        if (a == 0) return false;
        float disc = b * b - a * c;
        if (disc < 0) return false;
        float t = (-b - SimMath.Sqrt(disc)) / a;
        if (t < 0 || t > fraction) return false;
        fraction = t;
        normal = SimMath.Normalize(m + d * t);
        return true;
    }

    private static bool Segment(Vector2 a, Vector2 b, Vector2 n, Vector2 origin, Vector2 d, ref float fraction, ref Vector2 normal)
    {
        float denominator = V.Dot(n, d);
        if (denominator >= 0) return false; // only from the outside
        float t = V.Dot(n, a - origin) / denominator;
        if (t < 0 || t > fraction) return false;
        var p = origin + d * t;
        var e = b - a;
        float u = V.Dot(p - a, e);
        if (u < 0 || u > V.Dot(e, e)) return false;
        fraction = t;
        normal = n;
        return true;
    }
}
