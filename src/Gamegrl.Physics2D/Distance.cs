// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Gamegrl.Physics2D;

/// <summary>A rigid transform: rotate by <see cref="Q"/>, then translate by <see cref="P"/>.</summary>
public struct Transform2D
{
    public Vector2 P;
    public Rot Q;

    public Transform2D(Vector2 p, Rot q) { P = p; Q = q; }
    public static Transform2D Identity => new(Vector2.Zero, Rot.Identity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector2 Apply(Vector2 v) => P + Q.Rotate(v);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public readonly Vector2 InvApply(Vector2 v) => Q.InvRotate(v - P);

    /// <summary>a⁻¹·b: <paramref name="b"/> expressed in <paramref name="a"/>'s frame.</summary>
    public static Transform2D InvMul(Transform2D a, Transform2D b) => new(a.Q.InvRotate(b.P - a.P), Rot.InvMul(a.Q, b.Q));
    /// <summary>a·b: <paramref name="b"/>'s frame moved by <paramref name="a"/>.</summary>
    public static Transform2D Mul(Transform2D a, Transform2D b) => new(a.Apply(b.P), Rot.Mul(a.Q, b.Q));
}

/// <summary>A convex point cloud (1 to <see cref="Shape.MaxVertices"/> points) grown by a radius: what GJK, shape casts and time of impact work on.</summary>
public unsafe struct ShapeProxy
{
    public fixed float X[Shape.MaxVertices];
    public fixed float Y[Shape.MaxVertices];
    public int Count;
    public float Radius;

    public readonly Vector2 Point(int i) => new(X[i], Y[i]);
    public void Set(int i, Vector2 p) { X[i] = p.X; Y[i] = p.Y; }

    public static ShapeProxy Make(ReadOnlySpan<Vector2> points, float radius)
    {
        var p = new ShapeProxy { Count = Math.Min(points.Length, Shape.MaxVertices), Radius = radius };
        for (int i = 0; i < p.Count; i++) p.Set(i, points[i]);
        return p;
    }

    public static ShapeProxy Make(ReadOnlySpan<Vector2> points, float radius, Transform2D transform)
    {
        var p = new ShapeProxy { Count = Math.Min(points.Length, Shape.MaxVertices), Radius = radius };
        for (int i = 0; i < p.Count; i++) p.Set(i, transform.Apply(points[i]));
        return p;
    }

    /// <summary>A shape's proxy in its body's frame (a circle is a point, a capsule two, a polygon its corners).</summary>
    public static ShapeProxy Of(in Shape s)
    {
        if (s.Kind == ShapeKind.Circle) { var c = new ShapeProxy { Count = 1, Radius = s.Radius }; c.Set(0, s.Center); return c; }
        var p = new ShapeProxy { Count = s.Kind == ShapeKind.ChainSegment ? 2 : s.Count, Radius = s.Radius };
        for (int i = 0; i < p.Count; i++) p.Set(i, s.Vertex(i));
        return p;
    }

    internal readonly int Support(Vector2 d)
    {
        int best = 0;
        float bestValue = X[0] * d.X + Y[0] * d.Y;
        for (int i = 1; i < Count; i++)
        {
            float v = X[i] * d.X + Y[i] * d.Y;
            if (v > bestValue) { best = i; bestValue = v; }
        }
        return best;
    }
}

/// <summary>The GJK simplex of the last distance query between a pair: passing it back warm starts the next one.</summary>
public unsafe struct SimplexCache
{
    public int Count;
    public fixed byte IndexA[3];
    public fixed byte IndexB[3];
}

public struct DistanceOutput
{
    /// <summary>Closest points on each shape and the normal from A to B, in A's frame. The normal is invalid at distance 0.</summary>
    public Vector2 PointA, PointB, Normal;
    public float Distance;
    public int Iterations;
}

/// <summary>A shape cast's or ray cast's result: the hit point and normal, and the fraction of the translation travelled.</summary>
public struct CastOutput
{
    public Vector2 Normal, Point;
    public float Fraction;
    public int Iterations;
    public bool Hit;
}

/// <summary>A body's motion over a step, for time of impact: its center of mass from <see cref="C1"/> to <see cref="C2"/>, turning from <see cref="Q1"/> to <see cref="Q2"/>.</summary>
public struct Sweep
{
    public Vector2 LocalCenter, C1, C2;
    public Rot Q1, Q2;

    /// <summary>The body's transform (origin, rotation) at <paramref name="time"/> in [0, 1].</summary>
    public readonly Transform2D At(float time)
    {
        var p = (1 - time) * C1 + time * C2;
        float c = (1 - time) * Q1.C + time * Q2.C, s = (1 - time) * Q1.S + time * Q2.S;
        float l = SimMath.Sqrt(c * c + s * s);
        var q = l > 0 ? new Rot { C = c / l, S = s / l } : Rot.Identity;
        return new Transform2D(p - q.Rotate(LocalCenter), q);
    }
}

public enum ToiState : byte { Unknown, Failed, Overlapped, Hit, Separated }

public struct ToiOutput
{
    public ToiState State;
    public Vector2 Point, Normal;
    public float Fraction;
}

/// <summary>
/// Distance, shape casts and time of impact between convex proxies (GJK and conservative advancement, after Box2D v3's
/// distance.c by Erin Catto). Everything runs in shape A's frame.
/// </summary>
public static unsafe class Distance
{
    private struct Vertex
    {
        public Vector2 WA, WB, W; // support points on A and B, and wA − wB
        public float A;           // barycentric coordinate
        public int IndexA, IndexB;
    }

    private struct Simplex
    {
        public Vertex V1, V2, V3;
        public int Count;
    }

    /// <summary>Closest points between segments p1–q1 and p2–q2, with each one's fraction along its segment.</summary>
    public static (Vector2 Closest1, Vector2 Closest2, float Fraction1, float Fraction2, float DistanceSquared) Segments(Vector2 p1, Vector2 q1, Vector2 p2, Vector2 q2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        float dd1 = V.Dot(d1, d1), dd2 = V.Dot(d2, d2), rd1 = V.Dot(r, d1), rd2 = V.Dot(r, d2);
        const float epsSqr = F.Epsilon * F.Epsilon;
        float f1, f2;
        if (dd1 < epsSqr || dd2 < epsSqr)
        {
            if (dd1 >= epsSqr) { f1 = F.Clamp(-rd1 / dd1, 0, 1); f2 = 0; }
            else if (dd2 >= epsSqr) { f1 = 0; f2 = F.Clamp(rd2 / dd2, 0, 1); }
            else { f1 = 0; f2 = 0; }
        }
        else
        {
            float d12 = V.Dot(d1, d2), denominator = dd1 * dd2 - d12 * d12;
            f1 = denominator != 0 ? F.Clamp((d12 * rd2 - rd1 * dd2) / denominator, 0, 1) : 0;
            f2 = (d12 * f1 + rd2) / dd2;
            if (f2 < 0) { f2 = 0; f1 = F.Clamp(-rd1 / dd1, 0, 1); }
            else if (f2 > 1) { f2 = 1; f1 = F.Clamp((d12 - rd1) / dd1, 0, 1); }
        }
        var c1 = p1 + f1 * d1; var c2 = p2 + f2 * d2;
        return (c1, c2, f1, f2, SimMath.LengthSquared(c1 - c2));
    }

    /// <summary>
    /// The distance between proxy A and proxy B placed at <paramref name="transform"/> in A's frame (GJK, warm started
    /// from and written back to <paramref name="cache"/>). With <paramref name="useRadii"/> the radii are subtracted and
    /// the points moved onto the rounded surfaces.
    /// </summary>
    public static DistanceOutput Compute(in ShapeProxy proxyA, in ShapeProxy proxyB, Transform2D transform, bool useRadii, ref SimplexCache cache)
    {
        var output = new DistanceOutput();
        var localB = new ShapeProxy { Count = proxyB.Count, Radius = proxyB.Radius };
        for (int i = 0; i < localB.Count; i++) localB.Set(i, transform.Apply(proxyB.Point(i)));

        var simplex = FromCache(cache, proxyA, localB);
        var nonUnitNormal = Vector2.Zero;
        int* saveA = stackalloc int[3], saveB = stackalloc int[3];
        const int maxIterations = 20;
        int iteration = 0;
        while (iteration < maxIterations)
        {
            int saveCount = simplex.Count;
            for (int i = 0; i < saveCount; i++) { ref var v = ref At(ref simplex, i); saveA[i] = v.IndexA; saveB[i] = v.IndexB; }
            Vector2 d = simplex.Count switch { 1 => -simplex.V1.W, 2 => Solve2(ref simplex), _ => Solve3(ref simplex) };
            // A triangle holds the origin, or the origin is (nearly) on the simplex: overlapping.
            if (simplex.Count == 3 || V.Dot(d, d) < 1000 * F.MinNormal)
            {
                cache = ToCache(simplex);
                Witness(simplex, out output.PointA, out output.PointB);
                output.Distance = 0;
                output.Iterations = iteration;
                return output;
            }
            nonUnitNormal = d;
            ref var vertex = ref At(ref simplex, simplex.Count);
            vertex.IndexA = proxyA.Support(d);
            vertex.WA = proxyA.Point(vertex.IndexA);
            vertex.IndexB = localB.Support(-d);
            vertex.WB = localB.Point(vertex.IndexB);
            vertex.W = vertex.WA - vertex.WB;
            iteration++;
            // A repeated support point means no more progress.
            bool duplicate = false;
            for (int i = 0; i < saveCount; i++) if (vertex.IndexA == saveA[i] && vertex.IndexB == saveB[i]) { duplicate = true; break; }
            if (duplicate) break;
            simplex.Count++;
        }
        var normal = SimMath.Normalize(nonUnitNormal);
        Witness(simplex, out var pa, out var pb);
        output.Normal = normal;
        output.Distance = SimMath.Distance(pa, pb);
        output.PointA = pa; output.PointB = pb;
        output.Iterations = iteration;
        cache = ToCache(simplex);
        if (useRadii)
        {
            output.Distance = F.Max(0, output.Distance - proxyA.Radius - proxyB.Radius);
            output.PointA = pa + proxyA.Radius * normal;
            output.PointB = pb - proxyB.Radius * normal;
        }
        return output;
    }

    /// <summary>Distance with a fresh cache.</summary>
    public static DistanceOutput Compute(in ShapeProxy proxyA, in ShapeProxy proxyB, Transform2D transform, bool useRadii)
    {
        var cache = new SimplexCache();
        return Compute(proxyA, proxyB, transform, useRadii, ref cache);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ref Vertex At(ref Simplex s, int i)
    {
        if (i == 0) return ref s.V1;
        if (i == 1) return ref s.V2;
        return ref s.V3;
    }

    private static Simplex FromCache(in SimplexCache cache, in ShapeProxy a, in ShapeProxy b)
    {
        var s = new Simplex { Count = cache.Count };
        for (int i = 0; i < s.Count; i++)
        {
            ref var v = ref At(ref s, i);
            v.IndexA = cache.IndexA[i]; v.IndexB = cache.IndexB[i];
            v.WA = a.Point(v.IndexA); v.WB = b.Point(v.IndexB);
            v.W = v.WA - v.WB;
            v.A = -1;
        }
        if (s.Count == 0)
        {
            s.V1 = new Vertex { WA = a.Point(0), WB = b.Point(0), A = 1 };
            s.V1.W = s.V1.WA - s.V1.WB;
            s.Count = 1;
        }
        return s;
    }

    private static SimplexCache ToCache(in Simplex s)
    {
        var cache = new SimplexCache { Count = s.Count };
        var copy = s;
        for (int i = 0; i < s.Count; i++)
        {
            ref var v = ref At(ref copy, i);
            cache.IndexA[i] = (byte)v.IndexA; cache.IndexB[i] = (byte)v.IndexB;
        }
        return cache;
    }

    private static void Witness(in Simplex s, out Vector2 a, out Vector2 b)
    {
        switch (s.Count)
        {
            case 1: a = s.V1.WA; b = s.V1.WB; break;
            case 2:
                a = s.V1.A * s.V1.WA + s.V2.A * s.V2.WA;
                b = s.V1.A * s.V1.WB + s.V2.A * s.V2.WB;
                break;
            default:
                a = s.V1.A * s.V1.WA + s.V2.A * s.V2.WA + s.V3.A * s.V3.WA;
                b = a;
                break;
        }
    }

    // ω × v for a scalar s: (−s·v.y, s·v.x).
    private static Vector2 CrossSV(float s, Vector2 v) => new(-s * v.Y, s * v.X);

    private static Vector2 Solve2(ref Simplex s)
    {
        var w1 = s.V1.W; var w2 = s.V2.W;
        var e12 = w2 - w1;
        float d12_2 = -V.Dot(w1, e12);
        if (d12_2 <= 0) { s.V1.A = 1; s.Count = 1; return -w1; }
        float d12_1 = V.Dot(w2, e12);
        if (d12_1 <= 0) { s.V2.A = 1; s.Count = 1; s.V1 = s.V2; return -w2; }
        float inv = 1 / (d12_1 + d12_2);
        s.V1.A = d12_1 * inv; s.V2.A = d12_2 * inv; s.Count = 2;
        return CrossSV(V.Cross(w1 + w2, e12), e12);
    }

    private static Vector2 Solve3(ref Simplex s)
    {
        var w1 = s.V1.W; var w2 = s.V2.W; var w3 = s.V3.W;
        var e12 = w2 - w1;
        float d12_1 = V.Dot(w2, e12), d12_2 = -V.Dot(w1, e12);
        var e13 = w3 - w1;
        float d13_1 = V.Dot(w3, e13), d13_2 = -V.Dot(w1, e13);
        var e23 = w3 - w2;
        float d23_1 = V.Dot(w3, e23), d23_2 = -V.Dot(w2, e23);
        float n123 = V.Cross(e12, e13);
        float d123_1 = n123 * V.Cross(w2, w3), d123_2 = n123 * V.Cross(w3, w1), d123_3 = n123 * V.Cross(w1, w2);
        if (d12_2 <= 0 && d13_2 <= 0) { s.V1.A = 1; s.Count = 1; return -w1; }
        if (d12_1 > 0 && d12_2 > 0 && d123_3 <= 0)
        {
            float inv = 1 / (d12_1 + d12_2);
            s.V1.A = d12_1 * inv; s.V2.A = d12_2 * inv; s.Count = 2;
            return CrossSV(V.Cross(w1 + w2, e12), e12);
        }
        if (d13_1 > 0 && d13_2 > 0 && d123_2 <= 0)
        {
            float inv = 1 / (d13_1 + d13_2);
            s.V1.A = d13_1 * inv; s.V3.A = d13_2 * inv; s.Count = 2; s.V2 = s.V3;
            return CrossSV(V.Cross(w1 + w3, e13), e13);
        }
        if (d12_1 <= 0 && d23_2 <= 0) { s.V2.A = 1; s.Count = 1; s.V1 = s.V2; return -w2; }
        if (d13_1 <= 0 && d23_1 <= 0) { s.V3.A = 1; s.Count = 1; s.V1 = s.V3; return -w3; }
        if (d23_1 > 0 && d23_2 > 0 && d123_1 <= 0)
        {
            float inv = 1 / (d23_1 + d23_2);
            s.V2.A = d23_1 * inv; s.V3.A = d23_2 * inv; s.Count = 2; s.V1 = s.V3;
            return CrossSV(V.Cross(w2 + w3, e23), e23);
        }
        float inv123 = 1 / (d123_1 + d123_2 + d123_3);
        s.V1.A = d123_1 * inv123; s.V2.A = d123_2 * inv123; s.V3.A = d123_3 * inv123; s.Count = 3;
        return Vector2.Zero;
    }

    /// <summary>
    /// Shape B (at <paramref name="transform"/> in A's frame) moving by <paramref name="translationB"/> against A, fixed:
    /// the fraction of the translation at first contact, with the point and normal in A's frame. Initially touching
    /// shapes hit at fraction 0 (unless <paramref name="canEncroach"/> lets rounded shapes close the gap a little).
    /// </summary>
    public static CastOutput Cast(in ShapeProxy proxyA, in ShapeProxy proxyB, Transform2D transform, Vector2 translationB, float maxFraction, bool canEncroach = false)
    {
        const float slop = PhysicsWorld2D.LinearSlop;
        float totalRadius = proxyA.Radius + proxyB.Radius;
        float target = F.Max(slop, totalRadius - slop), tolerance = 0.25f * slop;
        var cache = new SimplexCache();
        float fraction = 0;
        var xf = transform;
        var output = new CastOutput();
        for (int iteration = 0; iteration < 20; iteration++)
        {
            output.Iterations++;
            var d = Compute(proxyA, proxyB, xf, false, ref cache);
            if (d.Distance < target + tolerance)
            {
                if (iteration == 0)
                {
                    if (canEncroach && d.Distance > 2 * slop) target = d.Distance - slop;
                    else
                    {
                        output.Hit = true;
                        var c1 = d.PointA + proxyA.Radius * d.Normal; var c2 = d.PointB - proxyB.Radius * d.Normal;
                        output.Point = 0.5f * (c1 + c2);
                        return output;
                    }
                }
                else
                {
                    output.Fraction = fraction;
                    output.Point = d.PointA + proxyA.Radius * d.Normal;
                    output.Normal = d.Normal;
                    output.Hit = true;
                    return output;
                }
            }
            float denominator = V.Dot(translationB, d.Normal);
            if (denominator >= 0) return output; // moving apart
            fraction += (target - d.Distance) / denominator;
            if (fraction >= maxFraction) return output;
            xf.P = transform.P + fraction * translationB;
        }
        return output;
    }

    private enum SeparationType : byte { Points, FaceA, FaceB }

    private struct SeparationFunction
    {
        public Sweep SweepA, SweepB;
        public Vector2 LocalPoint, Axis;
        public SeparationType Type;
    }

    private static SeparationFunction MakeSeparation(in SimplexCache cache, in ShapeProxy a, in Sweep sweepA, in ShapeProxy b, in Sweep sweepB, float t1)
    {
        var f = new SeparationFunction { SweepA = sweepA, SweepB = sweepB };
        var xfA = sweepA.At(t1); var xfB = sweepB.At(t1);
        if (cache.Count == 1)
        {
            f.Type = SeparationType.Points;
            f.Axis = SimMath.Normalize(xfB.Apply(b.Point(cache.IndexB[0])) - xfA.Apply(a.Point(cache.IndexA[0])));
            return f;
        }
        if (cache.IndexA[0] == cache.IndexA[1])
        {
            // Two points on B, one on A: B's face.
            f.Type = SeparationType.FaceB;
            var b1 = b.Point(cache.IndexB[0]); var b2 = b.Point(cache.IndexB[1]);
            f.Axis = SimMath.Normalize(V.RightPerp(b2 - b1));
            var normal = xfB.Q.Rotate(f.Axis);
            f.LocalPoint = 0.5f * (b1 + b2);
            if (V.Dot(xfA.Apply(a.Point(cache.IndexA[0])) - xfB.Apply(f.LocalPoint), normal) < 0) f.Axis = -f.Axis;
            return f;
        }
        f.Type = SeparationType.FaceA;
        var a1 = a.Point(cache.IndexA[0]); var a2 = a.Point(cache.IndexA[1]);
        f.Axis = SimMath.Normalize(V.RightPerp(a2 - a1));
        var na = xfA.Q.Rotate(f.Axis);
        f.LocalPoint = 0.5f * (a1 + a2);
        if (V.Dot(xfB.Apply(b.Point(cache.IndexB[0])) - xfA.Apply(f.LocalPoint), na) < 0) f.Axis = -f.Axis;
        return f;
    }

    private static float FindMinSeparation(in SeparationFunction f, in ShapeProxy a, in ShapeProxy b, out int indexA, out int indexB, float t)
    {
        var xfA = f.SweepA.At(t); var xfB = f.SweepB.At(t);
        switch (f.Type)
        {
            case SeparationType.Points:
            {
                indexA = a.Support(xfA.Q.InvRotate(f.Axis));
                indexB = b.Support(xfB.Q.InvRotate(-f.Axis));
                return V.Dot(xfB.Apply(b.Point(indexB)) - xfA.Apply(a.Point(indexA)), f.Axis);
            }
            case SeparationType.FaceA:
            {
                var normal = xfA.Q.Rotate(f.Axis);
                var pointA = xfA.Apply(f.LocalPoint);
                indexA = -1;
                indexB = b.Support(xfB.Q.InvRotate(-normal));
                return V.Dot(xfB.Apply(b.Point(indexB)) - pointA, normal);
            }
            default:
            {
                var normal = xfB.Q.Rotate(f.Axis);
                var pointB = xfB.Apply(f.LocalPoint);
                indexB = -1;
                indexA = a.Support(xfA.Q.InvRotate(-normal));
                return V.Dot(xfA.Apply(a.Point(indexA)) - pointB, normal);
            }
        }
    }

    private static float Evaluate(in SeparationFunction f, in ShapeProxy a, in ShapeProxy b, int indexA, int indexB, float t)
    {
        var xfA = f.SweepA.At(t); var xfB = f.SweepB.At(t);
        switch (f.Type)
        {
            case SeparationType.Points: return V.Dot(xfB.Apply(b.Point(indexB)) - xfA.Apply(a.Point(indexA)), f.Axis);
            case SeparationType.FaceA:
            {
                var normal = xfA.Q.Rotate(f.Axis);
                return V.Dot(xfB.Apply(b.Point(indexB)) - xfA.Apply(f.LocalPoint), normal);
            }
            default:
            {
                var normal = xfB.Q.Rotate(f.Axis);
                return V.Dot(xfA.Apply(a.Point(indexA)) - xfB.Apply(f.LocalPoint), normal);
            }
        }
    }

    /// <summary>
    /// When two moving shapes first come within the linear slop of touching, as a fraction of their sweeps (conservative
    /// advancement with separating axes from GJK, after Box2D's b2TimeOfImpact).
    /// </summary>
    public static ToiOutput TimeOfImpact(in ShapeProxy proxyA, in Sweep sweepA, in ShapeProxy proxyB, in Sweep sweepB, float maxFraction)
    {
        const float slop = PhysicsWorld2D.LinearSlop;
        var output = new ToiOutput { State = ToiState.Unknown, Fraction = maxFraction };
        float tMax = maxFraction;
        float totalRadius = proxyA.Radius + proxyB.Radius;
        float target = F.Max(slop, totalRadius - slop), tolerance = 0.25f * slop;
        float t1 = 0;
        int distanceIterations = 0;
        var cache = new SimplexCache();
        for (; ; )
        {
            var xfA = sweepA.At(t1); var xfB = sweepB.At(t1);
            var d = Compute(proxyA, proxyB, Transform2D.InvMul(xfA, xfB), false, ref cache);
            var worldNormal = xfA.Q.Rotate(d.Normal);
            var worldPointA = xfA.Apply(d.PointA); var worldPointB = xfA.Apply(d.PointB);
            distanceIterations++;
            if (d.Distance <= 0) { output.State = ToiState.Overlapped; output.Fraction = 0; break; }
            if (d.Distance <= target + tolerance)
            {
                output.State = ToiState.Hit;
                output.Point = 0.5f * ((worldPointA + proxyA.Radius * worldNormal) + (worldPointB - proxyB.Radius * worldNormal));
                output.Normal = worldNormal;
                output.Fraction = t1;
                break;
            }
            var fcn = MakeSeparation(cache, proxyA, sweepA, proxyB, sweepB, t1);
            bool done = false;
            float t2 = tMax;
            int pushBackIterations = 0;
            for (; ; )
            {
                float s2 = FindMinSeparation(fcn, proxyA, proxyB, out int indexA, out int indexB, t2);
                if (s2 > target + tolerance) { output.State = ToiState.Separated; output.Fraction = tMax; done = true; break; }
                if (s2 > target - tolerance) { t1 = t2; break; }
                float s1 = Evaluate(fcn, proxyA, proxyB, indexA, indexB, t1);
                if (s1 < target - tolerance) { output.State = ToiState.Failed; output.Fraction = t1; done = true; break; }
                if (s1 <= target + tolerance)
                {
                    output.State = ToiState.Hit;
                    output.Point = 0.5f * ((worldPointA + proxyA.Radius * worldNormal) + (worldPointB - proxyB.Radius * worldNormal));
                    output.Normal = worldNormal;
                    output.Fraction = t1;
                    done = true;
                    break;
                }
                // Root of s(t) = target on [t1, t2]: alternate bisection and the secant rule.
                int rootIterations = 0;
                float a1 = t1, a2 = t2;
                for (; ; )
                {
                    float t = (rootIterations & 1) != 0 ? a1 + (target - s1) * (a2 - a1) / (s2 - s1) : 0.5f * (a1 + a2);
                    rootIterations++;
                    float s = Evaluate(fcn, proxyA, proxyB, indexA, indexB, t);
                    if (MathF.Abs(s - target) < tolerance) { t2 = t; break; }
                    if (s > target) { a1 = t; s1 = s; } else { a2 = t; s2 = s; }
                    if (rootIterations == 50) break;
                }
                if (++pushBackIterations == Shape.MaxVertices) break;
            }
            if (done) break;
            if (distanceIterations == 20)
            {
                output.State = ToiState.Failed;
                output.Point = 0.5f * ((worldPointA + proxyA.Radius * worldNormal) + (worldPointB - proxyB.Radius * worldNormal));
                output.Normal = worldNormal;
                output.Fraction = t1;
                break;
            }
        }
        return output;
    }
}
