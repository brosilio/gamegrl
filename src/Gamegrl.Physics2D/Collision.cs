using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>One contact point: where (relative to each body's origin, world-oriented), how deep (negative) or how far apart (positive, speculative).</summary>
public struct ManifoldPoint
{
    public Vector2 AnchorA, AnchorB;
    public float Separation;
    /// <summary>Identifies the features in contact, so a point keeps its impulse from step to step (warm starting).</summary>
    public ushort Id;
}

/// <summary>Up to two contact points between two shapes, with the normal pointing from A to B.</summary>
public struct Manifold
{
    public Vector2 Normal;
    public ManifoldPoint P0, P1;
    public int Count;
}

/// <summary>Contact generation. Points with separation below <c>margin</c> are kept, so fast bodies get speculative contacts before they touch.</summary>
internal static class Collision
{
    public static void Collide(in Shape a, Vector2 pa, Rot qa, in Shape b, Vector2 pb, Rot qb, float margin, out Manifold m)
    {
        m = default;
        if (a.Kind == ShapeKind.Circle && b.Kind == ShapeKind.Circle) Circles(a, pa, qa, b, pb, qb, margin, ref m);
        else if (a.Kind == ShapeKind.Polygon && b.Kind == ShapeKind.Circle) PolygonCircle(a, pa, qa, b, pb, qb, margin, ref m);
        else if (a.Kind == ShapeKind.Circle && b.Kind == ShapeKind.Polygon)
        {
            PolygonCircle(b, pb, qb, a, pa, qa, margin, ref m);
            Flip(ref m);
        }
        else Polygons(a, pa, qa, b, pb, qb, margin, ref m);
    }

    private static void Flip(ref Manifold m)
    {
        m.Normal = -m.Normal;
        (m.P0.AnchorA, m.P0.AnchorB) = (m.P0.AnchorB, m.P0.AnchorA);
        (m.P1.AnchorA, m.P1.AnchorB) = (m.P1.AnchorB, m.P1.AnchorA);
    }

    private static void Circles(in Shape a, Vector2 pa, Rot qa, in Shape b, Vector2 pb, Rot qb, float margin, ref Manifold m)
    {
        var ca = pa + qa.Rotate(a.Center); var cb = pb + qb.Rotate(b.Center);
        var d = cb - ca;
        float distance = SimMath.Length(d);
        float separation = distance - a.Radius - b.Radius;
        if (separation > margin) return;
        var n = distance > 1e-9f ? d / distance : new Vector2(0, 1);
        var point = 0.5f * ((ca + a.Radius * n) + (cb - b.Radius * n));
        m.Normal = n;
        m.Count = 1;
        m.P0 = new ManifoldPoint { AnchorA = point - pa, AnchorB = point - pb, Separation = separation };
    }

    /// <summary>Polygon A against circle B (Box2D's approach: deepest face, then the face or vertex region).</summary>
    private static void PolygonCircle(in Shape a, Vector2 pa, Rot qa, in Shape b, Vector2 pb, Rot qb, float margin, ref Manifold m)
    {
        // The circle's center in the polygon's frame.
        var c = qa.InvRotate(pb + qb.Rotate(b.Center) - pa);
        float radius = a.Radius + b.Radius;
        int face = 0;
        float faceSeparation = float.MinValue;
        for (int i = 0; i < a.Count; i++)
        {
            float s = V.Dot(a.Normal(i), c - a.Vertex(i));
            if (s > faceSeparation) { faceSeparation = s; face = i; }
        }
        if (faceSeparation - radius > margin) return;
        var v1 = a.Vertex(face); var v2 = a.Vertex(face + 1 < a.Count ? face + 1 : 0);
        float u1 = V.Dot(c - v1, v2 - v1), u2 = V.Dot(c - v2, v1 - v2);
        Vector2 normal, onPolygon;
        float separation;
        if (u1 < 0 && faceSeparation > 1e-6f)
        {
            normal = SimMath.Normalize(c - v1); separation = SimMath.Length(c - v1) - radius; onPolygon = v1;
        }
        else if (u2 < 0 && faceSeparation > 1e-6f)
        {
            normal = SimMath.Normalize(c - v2); separation = SimMath.Length(c - v2) - radius; onPolygon = v2;
        }
        else
        {
            normal = a.Normal(face); separation = faceSeparation - radius; onPolygon = c - normal * faceSeparation;
        }
        if (separation > margin) return;
        // Contact point half way between the surfaces, in world space.
        var surfaceA = onPolygon + normal * a.Radius;
        var surfaceB = c - normal * b.Radius;
        var point = pa + qa.Rotate(0.5f * (surfaceA + surfaceB));
        m.Normal = qa.Rotate(normal);
        m.Count = 1;
        m.P0 = new ManifoldPoint { AnchorA = point - pa, AnchorB = point - pb, Separation = separation, Id = (ushort)face };
    }

    /// <summary>The face of <paramref name="a"/> that separates the polygons most, and by how much (SAT). Both in one frame.</summary>
    private static float MaxSeparation(in Shape a, in Shape b, out int edge)
    {
        edge = 0;
        float best = float.MinValue;
        for (int i = 0; i < a.Count; i++)
        {
            var n = a.Normal(i); var v = a.Vertex(i);
            float s = float.MaxValue;
            for (int j = 0; j < b.Count; j++)
            {
                float d = V.Dot(n, b.Vertex(j) - v);
                if (d < s) s = d;
            }
            if (s > best) { best = s; edge = i; }
        }
        return best;
    }

    /// <summary>
    /// Polygon against polygon, worked in A's frame: B is moved into it once, so SAT, corner contacts and clipping all
    /// run on plain vertices (before, B's corners were turned again for every face tested).
    /// </summary>
    private static void Polygons(in Shape a, Vector2 pa, Rot qa, in Shape b, Vector2 pb, Rot qb, float margin, ref Manifold m)
    {
        var local = b.Transformed(qa.InvRotate(pb - pa), Rot.InvMul(qa, qb));
        float radius = a.Radius + b.Radius;
        float sa = MaxSeparation(a, local, out int edgeA);
        if (sa - radius > margin) return;
        float sb = MaxSeparation(local, a, out int edgeB);
        if (sb - radius > margin) return;
        // Prefer A's face unless B's is clearly better, so the choice doesn't flicker between steps.
        bool flip = sb > sa + 0.1f * 0.005f;
        float coreSeparation = Math.Max(sa, sb);
        bool done = false;
        if (coreSeparation > 0.1f * 0.005f)
        {
            // The cores are apart: if their closest features are two corners, contact is corner to corner, along the
            // line between them (a face normal there would catch on the corner). Rounded shapes need this most.
            done = flip ? CornerContact(local, edgeB, a, radius, margin, true, ref m) : CornerContact(a, edgeA, local, radius, margin, false, ref m);
        }
        if (!done)
        {
            if (flip) Clip(local, edgeB, a, radius, margin, true, ref m);
            else Clip(a, edgeA, local, radius, margin, false, ref m);
        }
        if (m.Count == 0) return;
        // Back to world space: the normal, and anchors relative to each body's origin.
        m.Normal = qa.Rotate(m.Normal);
        ToWorld(ref m.P0, pa, qa, pb);
        if (m.Count > 1) ToWorld(ref m.P1, pa, qa, pb);
    }

    private static void ToWorld(ref ManifoldPoint p, Vector2 pa, Rot qa, Vector2 pb)
    {
        var world = pa + qa.Rotate(p.AnchorA); // the point, in A's frame until now
        p.AnchorA = world - pa;
        p.AnchorB = world - pb;
    }

    private static int IncidentEdge(in Shape inc, Vector2 n)
    {
        int edge = 0;
        float minDot = float.MaxValue;
        for (int i = 0; i < inc.Count; i++)
        {
            float d = V.Dot(n, inc.Normal(i));
            if (d < minDot) { minDot = d; edge = i; }
        }
        return edge;
    }

    /// <summary>
    /// The closest points between the reference edge and the incident edge; when both sit at segment ends, a single
    /// corner-to-corner contact (true, done: with a point, or none when apart). Otherwise false: clipping handles it.
    /// Points are left in the shared frame in <see cref="ManifoldPoint.AnchorA"/>, and the normal points from A to B.
    /// </summary>
    private static bool CornerContact(in Shape refShape, int refEdge, in Shape inc, float radius, float margin, bool flip, ref Manifold m)
    {
        var n = refShape.Normal(refEdge);
        int incEdge = IncidentEdge(inc, n);
        int r1 = refEdge, r2 = refEdge + 1 < refShape.Count ? refEdge + 1 : 0;
        int i1 = incEdge, i2 = incEdge + 1 < inc.Count ? incEdge + 1 : 0;
        SegmentDistance(refShape.Vertex(r1), refShape.Vertex(r2), inc.Vertex(i1), inc.Vertex(i2), out float f1, out float f2, out var c1, out var c2);
        if ((f1 != 0 && f1 != 1) || (f2 != 0 && f2 != 1)) return false;
        float distance = SimMath.Distance(c1, c2);
        if (distance - radius > margin) return true; // apart: no contact, and clipping wouldn't add one
        if (distance < 1e-6f) return false;
        var normal = (c2 - c1) / distance; // reference to incident
        m.Normal = flip ? -normal : normal;
        ushort id = (ushort)((f1 == 0 ? r1 : r2) << 8 | (f2 == 0 ? i1 : i2) | 0x2000 | (flip ? 0x4000 : 0));
        m.P0 = new ManifoldPoint { AnchorA = 0.5f * ((c1 + normal * refShape.Radius) + (c2 - normal * inc.Radius)), Separation = distance - radius, Id = id };
        m.Count = 1;
        return true;
    }

    /// <summary>Clip the incident face against the reference face (both in the shared frame); points go in <see cref="ManifoldPoint.AnchorA"/>.</summary>
    private static void Clip(in Shape refShape, int refEdge, in Shape inc, float radius, float margin, bool flip, ref Manifold m)
    {
        var n = refShape.Normal(refEdge);
        var v1 = refShape.Vertex(refEdge); var v2 = refShape.Vertex(refEdge + 1 < refShape.Count ? refEdge + 1 : 0);
        int incEdge = IncidentEdge(inc, n);
        int i1 = incEdge, i2 = incEdge + 1 < inc.Count ? incEdge + 1 : 0;
        var w1 = inc.Vertex(i1); var w2 = inc.Vertex(i2);
        var tangent = SimMath.Normalize(v2 - v1);
        float lower = 0, upper = V.Dot(v2 - v1, tangent);
        float a1 = V.Dot(w1 - v1, tangent), a2 = V.Dot(w2 - v1, tangent);
        Vector2 c1 = w1, c2 = w2;
        if (a1 < lower && a2 > lower) c1 = w1 + (w2 - w1) * ((lower - a1) / (a2 - a1));
        else if (a2 < lower && a1 > lower) c2 = w2 + (w1 - w2) * ((lower - a2) / (a1 - a2));
        float b1 = V.Dot(c1 - v1, tangent), b2 = V.Dot(c2 - v1, tangent);
        if (b1 > upper && b2 < upper) c1 = c2 + (c1 - c2) * ((upper - b2) / (b1 - b2));
        else if (b2 > upper && b1 < upper) c2 = c1 + (c2 - c1) * ((upper - b1) / (b2 - b1));
        m.Normal = flip ? -n : n;
        AddPoint(ref m, c1, n, v1, refShape.Radius, inc.Radius, radius, margin, (ushort)(refEdge << 8 | i1 | (flip ? 0x4000 : 0)), lower, upper, tangent);
        AddPoint(ref m, c2, n, v1, refShape.Radius, inc.Radius, radius, margin, (ushort)(refEdge << 8 | i2 | 0x80 | (flip ? 0x4000 : 0)), lower, upper, tangent);
    }

    private static void AddPoint(ref Manifold m, Vector2 c, Vector2 n, Vector2 v1, float refRadius, float incRadius, float radius, float margin, ushort id, float lower, float upper, Vector2 tangent)
    {
        float along = V.Dot(c - v1, tangent);
        if (along < lower - 1e-4f || along > upper + 1e-4f) return; // the clip removed it
        float depth = V.Dot(c - v1, n);
        float separation = depth - radius;
        if (separation > margin) return;
        // Half way between the reference surface and the incident point's surface.
        var point = 0.5f * ((c - n * (depth - refRadius)) + (c - n * incRadius));
        var mp = new ManifoldPoint { AnchorA = point, Separation = separation, Id = id };
        if (m.Count == 0) m.P0 = mp; else m.P1 = mp;
        m.Count++;
    }

    /// <summary>Closest points between segments p1–q1 and p2–q2 (Ericson), with their parameters clamped to [0, 1].</summary>
    private static void SegmentDistance(Vector2 p1, Vector2 q1, Vector2 p2, Vector2 q2, out float s, out float t, out Vector2 c1, out Vector2 c2)
    {
        var d1 = q1 - p1; var d2 = q2 - p2; var r = p1 - p2;
        float a = V.Dot(d1, d1), e = V.Dot(d2, d2), f = V.Dot(d2, r);
        if (a <= 1e-12f && e <= 1e-12f) { s = t = 0; c1 = p1; c2 = p2; return; }
        if (a <= 1e-12f) { s = 0; t = Math.Clamp(f / e, 0, 1); }
        else
        {
            float c = V.Dot(d1, r);
            if (e <= 1e-12f) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
            else
            {
                float b = V.Dot(d1, d2), denom = a * e - b * b;
                s = denom != 0 ? Math.Clamp((b * f - c * e) / denom, 0, 1) : 0;
                t = (b * s + f) / e;
                if (t < 0) { t = 0; s = Math.Clamp(-c / a, 0, 1); }
                else if (t > 1) { t = 1; s = Math.Clamp((b - c) / a, 0, 1); }
            }
        }
        c1 = p1 + d1 * s; c2 = p2 + d2 * t;
    }

    /// <summary>Clip the incident face of <paramref name="inc"/> against the reference face of <paramref name="refShape"/>.</summary>
    private static void Clip(in Shape refShape, Vector2 pr, Rot qr, int refEdge, in Shape inc, Vector2 pi, Rot qi, float radius, float margin, bool flip, ref Manifold m)
    {
        // Work in the reference polygon's frame.
        var q = Rot.InvMul(qr, qi);
        var p = qr.InvRotate(pi - pr);
        var n = refShape.Normal(refEdge);
        var v1 = refShape.Vertex(refEdge); var v2 = refShape.Vertex(refEdge + 1 < refShape.Count ? refEdge + 1 : 0);
        // Incident face: the one most anti-parallel to the reference normal.
        int incEdge = 0;
        float minDot = float.MaxValue;
        for (int i = 0; i < inc.Count; i++)
        {
            float d = V.Dot(n, q.Rotate(inc.Normal(i)));
            if (d < minDot) { minDot = d; incEdge = i; }
        }
        int i1 = incEdge, i2 = incEdge + 1 < inc.Count ? incEdge + 1 : 0;
        var w1 = p + q.Rotate(inc.Vertex(i1)); var w2 = p + q.Rotate(inc.Vertex(i2));
        // Clip the segment w1–w2 to the reference face's side planes.
        var tangent = SimMath.Normalize(v2 - v1);
        float lower = 0, upper = V.Dot(v2 - v1, tangent);
        float a1 = V.Dot(w1 - v1, tangent), a2 = V.Dot(w2 - v1, tangent);
        Vector2 c1 = w1, c2 = w2;
        if (a1 < lower && a2 > lower) c1 = w1 + (w2 - w1) * ((lower - a1) / (a2 - a1));
        else if (a2 < lower && a1 > lower) c2 = w2 + (w1 - w2) * ((lower - a2) / (a1 - a2));
        float b1 = V.Dot(c1 - v1, tangent), b2 = V.Dot(c2 - v1, tangent);
        if (b1 > upper && b2 < upper) c1 = c2 + (c1 - c2) * ((upper - b2) / (b1 - b2));
        else if (b2 > upper && b1 < upper) c2 = c1 + (c2 - c1) * ((upper - b1) / (b2 - b1));
        var normal = qr.Rotate(n);
        m.Normal = flip ? -normal : normal;
        AddPoint(ref m, c1, n, v1, refShape.Radius, inc.Radius, radius, margin, pr, qr, pi, flip, (ushort)(refEdge << 8 | i1), lower, upper, tangent);
        AddPoint(ref m, c2, n, v1, refShape.Radius, inc.Radius, radius, margin, pr, qr, pi, flip, (ushort)(refEdge << 8 | i2 | 0x80), lower, upper, tangent);
    }

    private static void AddPoint(ref Manifold m, Vector2 c, Vector2 n, Vector2 v1, float refRadius, float incRadius, float radius, float margin,
        Vector2 pr, Rot qr, Vector2 pi, bool flip, ushort id, float lower, float upper, Vector2 tangent)
    {
        float along = V.Dot(c - v1, tangent);
        if (along < lower - 1e-4f || along > upper + 1e-4f) return; // the clip removed it
        float separation = V.Dot(c - v1, n) - radius;
        if (separation > margin) return;
        // Half way between the reference surface and the incident point's surface.
        var surfaceRef = c - n * (V.Dot(c - v1, n) - refRadius);
        var surfaceInc = c - n * incRadius;
        var point = pr + qr.Rotate(0.5f * (surfaceRef + surfaceInc));
        var mp = new ManifoldPoint { Separation = separation, Id = (ushort)(id | (flip ? 0x4000 : 0)) };
        if (flip) { mp.AnchorA = point - pi; mp.AnchorB = point - pr; }
        else { mp.AnchorA = point - pr; mp.AnchorB = point - pi; }
        if (m.Count == 0) m.P0 = mp; else m.P1 = mp;
        m.Count++;
    }
}
