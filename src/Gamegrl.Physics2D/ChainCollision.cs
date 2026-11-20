using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>
/// Chain segments against circles, capsules and polygons: one-sided, smoothed across the joins by the ghost vertices
/// (after Box2D v3's manifold.c by Erin Catto; see https://box2d.org/posts/2020/06/ghost-collisions/). Worked in the
/// segment's frame, then moved to world space like the other manifolds.
/// </summary>
internal static partial class Collision
{
    private struct LocalPoint
    {
        public Vector2 Point;
        public float Separation;
        public ushort Id;
    }

    private struct LocalManifold
    {
        public Vector2 Normal;
        public LocalPoint P0, P1;
        public int Count;
    }

    private static ushort MakeId(int a, int b) => (ushort)((byte)a << 8 | (byte)b);

    /// <summary>A manifold in A's frame to world space, anchors relative to each body's origin.</summary>
    private static void FromLocal(in LocalManifold local, Vector2 pa, Rot qa, Vector2 pb, ref Manifold m)
    {
        m.Count = local.Count;
        if (local.Count == 0) return;
        m.Normal = qa.Rotate(local.Normal);
        m.P0 = World(local.P0, pa, qa, pb);
        if (local.Count > 1) m.P1 = World(local.P1, pa, qa, pb);
    }

    private static ManifoldPoint World(in LocalPoint p, Vector2 pa, Rot qa, Vector2 pb)
    {
        var anchorA = qa.Rotate(p.Point);
        return new ManifoldPoint { AnchorA = anchorA, AnchorB = anchorA + pa - pb, Separation = p.Separation, Id = p.Id };
    }

    private static void ChainCircle(in Shape seg, Vector2 pa, Rot qa, in Shape circle, Vector2 pb, Rot qb, float margin, ref Manifold m)
    {
        var xf = Transform2D.InvMul(new Transform2D(pa, qa), new Transform2D(pb, qb));
        var pB = xf.Apply(circle.Center);
        var p1 = seg.Vertex(0); var p2 = seg.Vertex(1);
        var e = p2 - p1;
        if (V.Dot(V.RightPerp(e), pB - p1) < 0) return; // one-sided
        float u = V.Dot(e, p2 - pB), v = V.Dot(e, pB - p1);
        Vector2 pA;
        if (v <= 0)
        {
            // Behind p1: the previous segment owns it unless it's in this one's vertex region.
            if (V.Dot(p1 - seg.Ghost1, pB - p1) <= 0) return;
            pA = p1;
        }
        else if (u <= 0)
        {
            if (V.Dot(seg.Ghost2 - p2, pB - p2) > 0) return;
            pA = p2;
        }
        else
        {
            float ee = V.Dot(e, e);
            pA = ee > 0 ? (u * p1 + v * p2) * (1 / ee) : p1;
        }
        var d = pB - pA;
        float distance = SimMath.Length(d);
        var normal = distance < F.Epsilon ? Vector2.Zero : d * (1 / distance);
        float separation = distance - circle.Radius;
        if (separation > margin) return;
        var local = new LocalManifold { Normal = normal, Count = 1 };
        local.P0 = new LocalPoint { Point = 0.5f * (pA + (pB - circle.Radius * normal)), Separation = separation };
        FromLocal(local, pa, qa, pb, ref m);
    }

    private static LocalManifold ClipSegments(Vector2 a1, Vector2 a2, Vector2 b1, Vector2 b2, Vector2 normal, float ra, float rb, ushort id1, ushort id2)
    {
        var manifold = new LocalManifold();
        var tangent = new Vector2(-normal.Y, normal.X); // left perpendicular
        float lower1 = 0, upper1 = V.Dot(a2 - a1, tangent);
        // The incident edge runs the other way (counter-clockwise winding).
        float upper2 = V.Dot(b1 - a1, tangent), lower2 = V.Dot(b2 - a1, tangent);
        if (upper2 < lower1 || upper1 < lower2) return manifold;
        var vLower = lower2 < lower1 && upper2 - lower2 > F.Epsilon ? SimMath.Lerp(b2, b1, (lower1 - lower2) / (upper2 - lower2)) : b2;
        var vUpper = upper2 > upper1 && upper2 - lower2 > F.Epsilon ? SimMath.Lerp(b2, b1, (upper1 - lower2) / (upper2 - lower2)) : b1;
        float separationLower = V.Dot(vLower - a1, normal), separationUpper = V.Dot(vUpper - a1, normal);
        // Points half way between the surfaces.
        vLower += (0.5f * (ra - rb - separationLower)) * normal;
        vUpper += (0.5f * (ra - rb - separationUpper)) * normal;
        float radius = ra + rb;
        manifold.Normal = normal;
        manifold.P0 = new LocalPoint { Point = vLower, Separation = separationLower - radius, Id = id1 };
        manifold.P1 = new LocalPoint { Point = vUpper, Separation = separationUpper - radius, Id = id2 };
        manifold.Count = 2;
        return manifold;
    }

    private enum NormalType : byte { Skip, Admit, Snap }

    private struct SmoothParams
    {
        public Vector2 Edge1, Normal0, Normal2;
        public bool Convex1, Convex2;
    }

    /// <summary>Where a normal falls on the chain's Gauss map: skip it (a neighbour owns it), admit it, or snap it to the segment normal (concave join).</summary>
    private static NormalType Classify(in SmoothParams p, Vector2 normal)
    {
        const float sinTol = 0.01f;
        if (V.Dot(normal, p.Edge1) <= 0)
        {
            if (!p.Convex1) return NormalType.Snap;
            return V.Cross(normal, p.Normal0) > sinTol ? NormalType.Skip : NormalType.Admit;
        }
        if (!p.Convex2) return NormalType.Snap;
        return V.Cross(p.Normal2, normal) > sinTol ? NormalType.Skip : NormalType.Admit;
    }

    private static unsafe void ChainPolygon(in Shape seg, Vector2 pa, Rot qa, in Shape poly, Vector2 pb, Rot qb, float margin, ref Manifold m)
    {
        var xf = Transform2D.InvMul(new Transform2D(pa, qa), new Transform2D(pb, qb));
        var local = ChainPolygonLocal(seg, xf, poly, margin);
        FromLocal(local, pa, qa, pb, ref m);
    }

    private static unsafe LocalManifold ChainPolygonLocal(in Shape seg, Transform2D xf, in Shape poly, float margin)
    {
        var manifold = new LocalManifold();
        var centroidB = xf.Apply(poly.Center);
        float radiusB = poly.Radius;
        var p1 = seg.Vertex(0); var p2 = seg.Vertex(1);
        var edge1 = SimMath.Normalize(p2 - p1);
        const float convexTol = 0.01f;
        var sp = new SmoothParams { Edge1 = edge1 };
        var edge0 = SimMath.Normalize(p1 - seg.Ghost1);
        sp.Normal0 = V.RightPerp(edge0);
        sp.Convex1 = V.Cross(edge0, edge1) >= convexTol;
        var edge2 = SimMath.Normalize(seg.Ghost2 - p2);
        sp.Normal2 = V.RightPerp(edge2);
        sp.Convex2 = V.Cross(edge1, edge2) >= convexTol;

        var normal1 = V.RightPerp(edge1);
        bool behind1 = V.Dot(normal1, centroidB - p1) < 0, behind0 = true, behind2 = true;
        if (sp.Convex1) behind0 = V.Dot(sp.Normal0, centroidB - p1) < 0;
        if (sp.Convex2) behind2 = V.Dot(sp.Normal2, centroidB - p2) < 0;
        if (behind1 && behind0 && behind2) return manifold; // one-sided

        int count = poly.Count;
        var vertices = stackalloc Vector2[Shape.MaxVertices];
        var normals = stackalloc Vector2[Shape.MaxVertices];
        for (int i = 0; i < count; i++) { vertices[i] = xf.Apply(poly.Vertex(i)); normals[i] = xf.Q.Rotate(poly.Normal(i)); }

        // Distance on the polygon's core (GJK doesn't suit partial polygons).
        var proxyA = new ShapeProxy { Count = 2 };
        proxyA.Set(0, p1); proxyA.Set(1, p2);
        var proxyB = new ShapeProxy { Count = count };
        for (int i = 0; i < count; i++) proxyB.Set(i, vertices[i]);
        var cache = new SimplexCache();
        var output = Distance.Compute(proxyA, proxyB, Transform2D.Identity, false, ref cache);
        if (output.Distance > radiusB + margin) return manifold;

        // Concave neighbours snap to this segment's normal.
        var n0 = sp.Convex1 ? sp.Normal0 : normal1;
        var n2 = sp.Convex2 ? sp.Normal2 : normal1;
        int incidentIndex = -1, incidentNormal = -1;

        if (!behind1 && output.Distance > 0.1f * PhysicsWorld2D.LinearSlop)
        {
            // The closest features may be two vertices, or a vertex and an edge, even when there should be face contact.
            if (cache.Count == 1)
            {
                var pA = output.PointA; var pB = output.PointB;
                var normal = SimMath.Normalize(pB - pA);
                var type = Classify(sp, normal);
                if (type == NormalType.Skip) return manifold;
                if (type == NormalType.Admit)
                {
                    manifold.Normal = normal;
                    manifold.P0 = new LocalPoint { Point = pA, Separation = output.Distance - radiusB, Id = MakeId(cache.IndexA[0], cache.IndexB[0]) };
                    manifold.Count = 1;
                    return manifold;
                }
                incidentIndex = cache.IndexB[0];
            }
            else
            {
                int ia1 = cache.IndexA[0], ia2 = cache.IndexA[1], ib1 = cache.IndexB[0], ib2 = cache.IndexB[1];
                if (ia1 == ia2)
                {
                    // One point on A, two on B: the polygon normal best aligned with the gap.
                    var normalB = output.PointA - output.PointB;
                    float dot1 = V.Dot(normalB, normals[ib1]), dot2 = V.Dot(normalB, normals[ib2]);
                    int ib = dot1 > dot2 ? ib1 : ib2;
                    normalB = normals[ib];
                    var type = Classify(sp, -normalB);
                    if (type == NormalType.Skip) return manifold;
                    if (type == NormalType.Admit)
                    {
                        ib1 = ib;
                        ib2 = ib < count - 1 ? ib + 1 : 0;
                        var b1 = vertices[ib1]; var b2 = vertices[ib2];
                        dot1 = V.Dot(normalB, p1 - b1);
                        dot2 = V.Dot(normalB, p2 - b1);
                        if (dot1 < dot2) { if (V.Dot(n0, normalB) < V.Dot(normal1, normalB)) return manifold; } // the neighbour is incident
                        else if (V.Dot(n2, normalB) < V.Dot(normal1, normalB)) return manifold;
                        manifold = ClipSegments(b1, b2, p1, p2, normalB, radiusB, 0, MakeId(ib1, 1), MakeId(ib2, 0));
                        if (manifold.Count == 2) manifold.Normal = -normalB;
                        return manifold;
                    }
                    incidentNormal = ib;
                }
                else
                {
                    float dot1 = V.Dot(normal1, vertices[ib1] - p1), dot2 = V.Dot(normal1, vertices[ib2] - p2);
                    incidentIndex = dot1 < dot2 ? ib1 : ib2;
                }
            }
        }
        else
        {
            // SAT: the segment normal and its convex neighbours'...
            float edgeSeparation = float.MaxValue;
            for (int i = 0; i < count; i++)
            {
                float s = V.Dot(normal1, vertices[i] - p1);
                if (s < edgeSeparation) { edgeSeparation = s; incidentIndex = i; }
            }
            if (sp.Convex1)
            {
                float s0 = float.MaxValue;
                for (int i = 0; i < count; i++) s0 = F.Min(s0, V.Dot(sp.Normal0, vertices[i] - p1));
                if (s0 > edgeSeparation) { edgeSeparation = s0; incidentIndex = -1; }
            }
            if (sp.Convex2)
            {
                float s2 = float.MaxValue;
                for (int i = 0; i < count; i++) s2 = F.Min(s2, V.Dot(sp.Normal2, vertices[i] - p2));
                if (s2 > edgeSeparation) { edgeSeparation = s2; incidentIndex = -1; }
            }
            // ...then the polygon's admissible normals.
            float polygonSeparation = float.MinValue;
            int referenceIndex = -1;
            for (int i = 0; i < count; i++)
            {
                var n = normals[i];
                if (Classify(sp, -n) != NormalType.Admit) continue;
                var p = vertices[i];
                float s = F.Min(V.Dot(n, p2 - p), V.Dot(n, p1 - p));
                if (s > polygonSeparation) { polygonSeparation = s; referenceIndex = i; }
            }
            if (polygonSeparation > edgeSeparation)
            {
                int ia1 = referenceIndex, ia2 = ia1 < count - 1 ? ia1 + 1 : 0;
                var a1 = vertices[ia1]; var a2 = vertices[ia2];
                var n = normals[ia1];
                float dot1 = V.Dot(n, p1 - a1), dot2 = V.Dot(n, p2 - a1);
                if (dot1 < dot2) { if (V.Dot(n0, n) < V.Dot(normal1, n)) return manifold; }
                else if (V.Dot(n2, n) < V.Dot(normal1, n)) return manifold;
                manifold = ClipSegments(a1, a2, p1, p2, normals[ia1], radiusB, 0, MakeId(ia1, 1), MakeId(ia2, 0));
                if (manifold.Count == 2) manifold.Normal = -normals[ia1];
                return manifold;
            }
            if (incidentIndex == -1) return manifold; // a neighbouring segment is the separating axis
        }

        // The segment normal: the incident polygon edge is the one beside the deepest vertex most against it.
        int e1, e2;
        if (incidentNormal != -1)
        {
            e1 = incidentNormal;
            e2 = e1 < count - 1 ? e1 + 1 : 0;
        }
        else
        {
            int i2 = incidentIndex, i1 = i2 > 0 ? i2 - 1 : count - 1;
            if (V.Dot(normal1, normals[i1]) < V.Dot(normal1, normals[i2])) { e1 = i1; e2 = i2; }
            else { e1 = i2; e2 = i2 < count - 1 ? i2 + 1 : 0; }
        }
        return ClipSegments(p1, p2, vertices[e1], vertices[e2], normal1, 0, radiusB, MakeId(0, e2), MakeId(1, e1));
    }
}
