// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>
/// Convex hulls for polygon shapes (quickhull, after Box2D's hull.c): points closer than a quarter of the linear slop
/// weld, and nearly collinear points drop out, so the result is a polygon the collision code can trust.
/// </summary>
public static class Hull
{
    /// <summary>
    /// The convex hull of 3 to <see cref="Shape.MaxVertices"/> points, counter-clockwise, written to
    /// <paramref name="hull"/>; returns how many points it has (0 when the points are too few, too many or collinear).
    /// </summary>
    public static int Compute(ReadOnlySpan<Vector2> points, Span<Vector2> hull)
    {
        if (points.Length is < 3 or > Shape.MaxVertices || hull.Length < Shape.MaxVertices) return 0;
        const float slop = PhysicsWorld2D.LinearSlop, tolSqr = 0.25f * slop * slop;
        Span<Vector2> ps = stackalloc Vector2[Shape.MaxVertices];
        int n = 0;
        var min = new Vector2(float.MaxValue); var max = new Vector2(float.MinValue);
        foreach (var v in points)
        {
            min = SimMath.Min(min, v); max = SimMath.Max(max, v);
            bool unique = true;
            for (int j = 0; j < n; j++) if (SimMath.LengthSquared(v - ps[j]) < tolSqr) { unique = false; break; }
            if (unique) ps[n++] = v;
        }
        if (n < 3) return 0;

        // The two extreme points: farthest from the box centre, then farthest from that.
        var c = 0.5f * (min + max);
        int f1 = Farthest(ps[..n], c);
        var p1 = ps[f1]; ps[f1] = ps[n - 1]; n--;
        int f2 = Farthest(ps[..n], p1);
        var p2 = ps[f2]; ps[f2] = ps[n - 1]; n--;

        Span<Vector2> right = stackalloc Vector2[Shape.MaxVertices], left = stackalloc Vector2[Shape.MaxVertices];
        int rightCount = 0, leftCount = 0;
        var e = SimMath.Normalize(p2 - p1);
        for (int i = 0; i < n; i++)
        {
            float d = V.Cross(ps[i] - p1, e);
            if (d >= 2 * slop) right[rightCount++] = ps[i];
            else if (d <= -2 * slop) left[leftCount++] = ps[i];
        }
        Span<Vector2> h1 = stackalloc Vector2[Shape.MaxVertices], h2 = stackalloc Vector2[Shape.MaxVertices];
        int c1 = Recurse(p1, p2, right[..rightCount], h1), c2 = Recurse(p2, p1, left[..leftCount], h2);
        if (c1 == 0 && c2 == 0) return 0;

        int count = 0;
        hull[count++] = p1;
        for (int i = 0; i < c1; i++) hull[count++] = h1[i];
        hull[count++] = p2;
        for (int i = 0; i < c2; i++) hull[count++] = h2[i];

        // Drop points that sit (nearly) on the line through their neighbours.
        bool searching = true;
        while (searching && count > 2)
        {
            searching = false;
            for (int i = 0; i < count; i++)
            {
                var s1 = hull[i]; var s2 = hull[(i + 1) % count]; var s3 = hull[(i + 2) % count];
                if (V.Cross(s2 - s1, SimMath.Normalize(s3 - s1)) <= 2 * slop)
                {
                    for (int j = (i + 1) % count; j < count - 1; j++) hull[j] = hull[j + 1];
                    count--;
                    searching = true;
                    break;
                }
            }
        }
        return count < 3 ? 0 : count;
    }

    private static int Farthest(ReadOnlySpan<Vector2> ps, Vector2 from)
    {
        int best = 0;
        float bestSq = SimMath.LengthSquared(from - ps[0]);
        for (int i = 1; i < ps.Length; i++)
        {
            float d = SimMath.LengthSquared(from - ps[i]);
            if (d > bestSq) { best = i; bestSq = d; }
        }
        return best;
    }

    /// <summary>Points of the hull strictly right of p1→p2, in order, between them.</summary>
    private static int Recurse(Vector2 p1, Vector2 p2, ReadOnlySpan<Vector2> ps, Span<Vector2> hull)
    {
        if (ps.Length == 0) return 0;
        var e = SimMath.Normalize(p2 - p1);
        Span<Vector2> right = stackalloc Vector2[Shape.MaxVertices];
        int rightCount = 0, best = 0;
        float bestDistance = V.Cross(ps[0] - p1, e);
        if (bestDistance > 0) right[rightCount++] = ps[0];
        for (int i = 1; i < ps.Length; i++)
        {
            float d = V.Cross(ps[i] - p1, e);
            if (d > bestDistance) { best = i; bestDistance = d; }
            if (d > 0) right[rightCount++] = ps[i];
        }
        if (bestDistance < 2 * PhysicsWorld2D.LinearSlop) return 0;
        var bestPoint = ps[best];
        Span<Vector2> h1 = stackalloc Vector2[Shape.MaxVertices], h2 = stackalloc Vector2[Shape.MaxVertices];
        int c1 = Recurse(p1, bestPoint, right[..rightCount], h1), c2 = Recurse(bestPoint, p2, right[..rightCount], h2);
        int count = 0;
        for (int i = 0; i < c1; i++) hull[count++] = h1[i];
        hull[count++] = bestPoint;
        for (int i = 0; i < c2; i++) hull[count++] = h2[i];
        return count;
    }

    /// <summary>Whether the points form a convex, counter-clockwise polygon with no nearly collinear corners.</summary>
    public static bool Validate(ReadOnlySpan<Vector2> hull)
    {
        int n = hull.Length;
        if (n is < 3 or > Shape.MaxVertices) return false;
        for (int i = 0; i < n; i++)
        {
            int i2 = i < n - 1 ? i + 1 : 0;
            var p = hull[i]; var e = SimMath.Normalize(hull[i2] - p);
            for (int j = 0; j < n; j++)
                if (j != i && j != i2 && V.Cross(hull[j] - p, e) >= 0) return false;
        }
        for (int i = 0; i < n; i++)
        {
            var p1 = hull[i]; var p2 = hull[(i + 1) % n]; var p3 = hull[(i + 2) % n];
            if (V.Cross(p2 - p1, SimMath.Normalize(p3 - p1)) <= PhysicsWorld2D.LinearSlop) return false;
        }
        return true;
    }
}
