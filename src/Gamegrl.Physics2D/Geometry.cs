// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
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
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly bool Overlaps(in Aabb o) => Min.X <= o.Max.X && o.Min.X <= Max.X && Min.Y <= o.Max.Y && o.Min.Y <= Max.Y;
}

/// <summary>
/// Plain-comparison min, max and clamp for the hot loops. <see cref="Math.Max(float, float)"/> implements IEEE 754-2019
/// (NaN and signed-zero cases), which costs branches per call; these are identical for the values physics produces.
/// </summary>
internal static class F
{
    /// <summary>C's FLT_EPSILON (the gap above 1), not <see cref="float.Epsilon"/> (the smallest subnormal).</summary>
    public const float Epsilon = 1.1920929e-7f;
    /// <summary>C's FLT_MIN: the smallest normal float.</summary>
    public const float MinNormal = 1.17549435e-38f;
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

/// <summary>
/// A convex collision shape in body space: a circle, a convex polygon of up to <see cref="MaxVertices"/> corners with
/// an optional rounding radius (two corners make a segment, or a capsule when rounded), or one segment of a chain.
/// </summary>
/// <remarks>A chain segment keeps its ends in vertices 0 and 1 and its neighbours' far ends (the ghost vertices that
/// make collision smooth across the joins) in 2 and 3; it collides only on its right side, the outside of a
/// counter-clockwise loop.</remarks>
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
    /// <summary>A chain segment's ghost vertices: the previous segment's start and the next segment's end.</summary>
    public readonly Vector2 Ghost1 => new(Vx[2], Vy[2]);
    public readonly Vector2 Ghost2 => new(Vx[3], Vy[3]);
    /// <summary>A segment: two corners and no radius (a capsule has a radius).</summary>
    public readonly bool IsSegment => Kind == ShapeKind.Polygon && Count == 2 && Radius == 0;
    public readonly bool IsCapsule => Kind == ShapeKind.Polygon && Count == 2 && Radius > 0;

    private void SetVertex(int i, Vector2 v) { Vx[i] = v.X; Vy[i] = v.Y; }
    private void SetNormal(int i, Vector2 n) { Nx[i] = n.X; Ny[i] = n.Y; }

    public static Shape Circle(float radius, Vector2 center) => new() { Kind = ShapeKind.Circle, Radius = radius, Center = center };

    /// <summary>A capsule: the segment <paramref name="a"/>–<paramref name="b"/> grown by <paramref name="radius"/> (a two-vertex rounded polygon).</summary>
    public static Shape Capsule(Vector2 a, Vector2 b, float radius)
    {
        var axis = b - a;
        if (SimMath.LengthSquared(axis) < 1e-8f) return Circle(radius, a);
        var n = SimMath.Normalize(V.RightPerp(axis));
        var s = new Shape { Kind = ShapeKind.Polygon, Count = 2, Radius = radius, Center = 0.5f * (a + b) };
        s.SetVertex(0, a); s.SetVertex(1, b); s.SetNormal(0, n); s.SetNormal(1, -n);
        return s;
    }

    /// <summary>A two-sided line segment (no mass: for static and kinematic bodies, like Box2D's segments).</summary>
    public static Shape Segment(Vector2 a, Vector2 b)
    {
        var s = Capsule(a, b, 0);
        if (s.Kind != ShapeKind.Polygon) throw new ArgumentException("A segment needs two distinct points.");
        return s;
    }

    /// <summary>
    /// One segment of a chain, from <paramref name="a"/> to <paramref name="b"/>, colliding on its right side only, with
    /// the neighbouring segments' far ends as ghost vertices so shapes slide across the joins without catching.
    /// </summary>
    public static Shape ChainSegment(Vector2 ghost1, Vector2 a, Vector2 b, Vector2 ghost2)
    {
        var s = new Shape { Kind = ShapeKind.ChainSegment, Count = 2, Center = 0.5f * (a + b) };
        var n = SimMath.Normalize(V.RightPerp(b - a));
        s.SetVertex(0, a); s.SetVertex(1, b); s.SetVertex(2, ghost1); s.SetVertex(3, ghost2);
        s.SetNormal(0, n); s.SetNormal(1, -n);
        return s;
    }

    /// <summary>This shape moved into a parent body's frame by <paramref name="offset"/> and <paramref name="rotation"/> (compound bodies).</summary>
    public readonly Shape Transformed(Vector2 offset, Rot rotation)
    {
        var s = this;
        s.Center = offset + rotation.Rotate(Center);
        for (int i = 0; i < Count; i++)
        {
            s.SetVertex(i, offset + rotation.Rotate(Vertex(i)));
            s.SetNormal(i, rotation.Rotate(Normal(i)));
        }
        if (Kind == ShapeKind.ChainSegment) { s.SetVertex(2, offset + rotation.Rotate(Ghost1)); s.SetVertex(3, offset + rotation.Rotate(Ghost2)); }
        return s;
    }

    /// <summary>A box of half extents, centred and turned within the body, its corners optionally rounded (it then reaches <paramref name="radius"/> further).</summary>
    public static Shape Box(Vector2 halfExtents, Vector2 center, Rot rotation, float radius = 0)
    {
        Span<Vector2> v = [new(-halfExtents.X, -halfExtents.Y), new(halfExtents.X, -halfExtents.Y), new(halfExtents.X, halfExtents.Y), new(-halfExtents.X, halfExtents.Y)];
        for (int i = 0; i < 4; i++) v[i] = center + rotation.Rotate(v[i]);
        return Polygon(v, radius);
    }

    /// <summary>A box about the body origin.</summary>
    public static Shape Box(float halfWidth, float halfHeight, float radius = 0) => Box(new Vector2(halfWidth, halfHeight), Vector2.Zero, Rot.Identity, radius);

    /// <summary>A convex polygon from counter-clockwise points (3 to 8) with an optional rounding radius.</summary>
    public static Shape Polygon(ReadOnlySpan<Vector2> points, float radius = 0)
    {
        if (points.Length is < 3 or > MaxVertices) throw new ArgumentException($"A polygon needs 3 to {MaxVertices} points.");
        var s = new Shape { Kind = ShapeKind.Polygon, Count = points.Length, Radius = radius };
        // Area-weighted centroid of a fan about the first vertex (Box2D's way: less round-off than about the origin).
        var origin = points[0];
        var center = Vector2.Zero;
        float area = 0;
        for (int i = 0; i < points.Length; i++)
        {
            var a = points[i]; var b = points[(i + 1) % points.Length];
            s.SetVertex(i, a);
            s.SetNormal(i, SimMath.Normalize(V.RightPerp(b - a)));
            if (i >= 1 && i < points.Length - 1)
            {
                var e1 = a - origin; var e2 = b - origin;
                float t = 0.5f * V.Cross(e1, e2);
                center += (t * (1f / 3)) * (e1 + e2);
                area += t;
            }
        }
        if (area <= F.Epsilon) throw new ArgumentException("Polygon points must be counter-clockwise and not degenerate.");
        s.Center = origin + center * (1 / area);
        return s;
    }

    /// <summary>The convex hull of 3 to 8 points in any order, as a polygon (see <see cref="Hull.Compute"/>).</summary>
    public static Shape ConvexHull(ReadOnlySpan<Vector2> points, float radius = 0)
    {
        Span<Vector2> hull = stackalloc Vector2[MaxVertices];
        int n = Hull.Compute(points, hull);
        if (n == 0) throw new ArgumentException("The points have no usable convex hull (too few, too many, or collinear).");
        return Polygon(hull[..n], radius);
    }

    /// <summary>Mass, rotational inertia about the body origin, and center of mass for a density.</summary>
    public readonly (float Mass, float Inertia, Vector2 Center) MassData(float density)
    {
        if (Kind == ShapeKind.ChainSegment) return (0, 0, Center);
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

public enum ShapeKind : byte { Circle, Polygon, ChainSegment }

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
        if (s.Kind == ShapeKind.ChainSegment)
        {
            // One-sided: a ray starting on the left (inside) passes through.
            if (V.Cross(origin - s.Vertex(0), s.Vertex(1) - s.Vertex(0)) < 0) return false;
            return TwoSided(s.Vertex(0), s.Vertex(1), origin, d, ref fraction, ref normal);
        }
        if (s.IsSegment) return TwoSided(s.Vertex(0), s.Vertex(1), origin, d, ref fraction, ref normal);
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

    /// <summary>A line segment hit from either side (the normal faces the ray).</summary>
    private static bool TwoSided(Vector2 v1, Vector2 v2, Vector2 origin, Vector2 d, ref float fraction, ref Vector2 normal)
    {
        var e = v2 - v1;
        float length = SimMath.Length(e);
        if (length == 0) return false;
        var unit = e / length;
        var n = V.RightPerp(unit);
        float numerator = V.Dot(n, v1 - origin), denominator = V.Dot(n, d);
        if (denominator == 0) return false;
        float t = numerator / denominator;
        if (t < 0 || t > fraction) return false;
        float along = V.Dot(origin + t * d - v1, unit);
        if (along < 0 || along > length) return false;
        fraction = t;
        normal = numerator > 0 ? -n : n;
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
