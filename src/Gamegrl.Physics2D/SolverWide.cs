using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace Gamegrl.Physics2D;

using F4 = Vector128<float>;

/// <summary>
/// The contact solver four constraints at a time. Each colour's constraints are packed in batches of four, field by
/// field, so one SIMD instruction does the same step of four independent constraints: the long dependent chains of the
/// scalar solver overlap instead of stalling. Constraints in a colour share no dynamic body, so solving four together
/// equals solving them one by one. Every lane does the same IEEE operations whatever the vector hardware (no fused
/// multiply-add, explicit compare-and-select for min and max), so results are the same on every platform.
/// </summary>
public sealed partial class PhysicsWorld2D
{
    private struct WidePoint
    {
        public F4 AnchorAX, AnchorAY, AnchorBX, AnchorBY, AdjustedSeparation, NormalImpulse, TangentImpulse, MaxNormalImpulse, NormalMass, TangentMass, RelativeVelocity;
    }

    private struct WideConstraint
    {
        public int A0, A1, A2, A3, B0, B1, B2, B3;
        public int C0, C1, C2, C3; // indices into _constraints, -1 for padding
        public F4 NormalX, NormalY, Friction, Restitution, MassA, MassB, InertiaA, InertiaB, BiasRate, MassScale, ImpulseScale;
        public WidePoint P0, P1;
    }

    private WideConstraint[] _wide = new WideConstraint[16];
    private readonly int[] _colorBatchStart = new int[Colors + 1];
    private int _wideCount;

    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static F4 Max(F4 a, F4 b) => Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] private static F4 Min(F4 a, F4 b) => Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);

    /// <summary>Where each colour's batches of four start (constraints are prepared and packed later, in parallel).</summary>
    private void SizeBatches()
    {
        int w = 0;
        for (int color = 0; color < Colors; color++)
        {
            _colorBatchStart[color] = w;
            w += (_colorStart[color + 1] - _colorStart[color] + 3) / 4;
        }
        _colorBatchStart[Colors] = w;
        _wideCount = w;
        if (_wide.Length < w) _wide = new WideConstraint[w * 2];
        // Padding lanes read some body (never written: they have no mass).
        _paddingBody = _constraintCount > 0 ? _constraints[0].Contact >= 0 ? Contacts[_constraints[0].Contact].BodyA : 0 : 0;
    }

    private int _paddingBody;

    /// <summary>Pack batch <paramref name="w"/> from its four prepared constraints (padding lanes get no mass, so they do nothing).</summary>
    private void PackBatch(int w)
    {
        int color = 0;
        while (_colorBatchStart[color + 1] <= w) color++;
        int i = _colorStart[color] + (w - _colorBatchStart[color]) * 4;
        int n = Math.Min(4, _colorStart[color + 1] - i);
        ref var wc = ref _wide[w];
        var pad = new ContactConstraint { A = _paddingBody, B = _paddingBody };
        ref var c0 = ref n > 0 ? ref _constraints[i] : ref pad;
        ref var c1 = ref n > 1 ? ref _constraints[i + 1] : ref pad;
        ref var c2 = ref n > 2 ? ref _constraints[i + 2] : ref pad;
        ref var c3 = ref n > 3 ? ref _constraints[i + 3] : ref pad;
        wc.C0 = n > 0 ? i : -1; wc.C1 = n > 1 ? i + 1 : -1; wc.C2 = n > 2 ? i + 2 : -1; wc.C3 = n > 3 ? i + 3 : -1;
        wc.A0 = c0.A; wc.A1 = c1.A; wc.A2 = c2.A; wc.A3 = c3.A;
        wc.B0 = c0.B; wc.B1 = c1.B; wc.B2 = c2.B; wc.B3 = c3.B;
        wc.NormalX = V4(c0.Normal.X, c1.Normal.X, c2.Normal.X, c3.Normal.X);
        wc.NormalY = V4(c0.Normal.Y, c1.Normal.Y, c2.Normal.Y, c3.Normal.Y);
        wc.Friction = V4(c0.Friction, c1.Friction, c2.Friction, c3.Friction);
        wc.Restitution = V4(c0.Restitution, c1.Restitution, c2.Restitution, c3.Restitution);
        wc.MassA = V4(c0.MassA, c1.MassA, c2.MassA, c3.MassA);
        wc.MassB = V4(c0.MassB, c1.MassB, c2.MassB, c3.MassB);
        wc.InertiaA = V4(c0.InertiaA, c1.InertiaA, c2.InertiaA, c3.InertiaA);
        wc.InertiaB = V4(c0.InertiaB, c1.InertiaB, c2.InertiaB, c3.InertiaB);
        wc.BiasRate = V4(c0.Soft.BiasRate, c1.Soft.BiasRate, c2.Soft.BiasRate, c3.Soft.BiasRate);
        wc.MassScale = V4(c0.Soft.MassScale, c1.Soft.MassScale, c2.Soft.MassScale, c3.Soft.MassScale);
        wc.ImpulseScale = V4(c0.Soft.ImpulseScale, c1.Soft.ImpulseScale, c2.Soft.ImpulseScale, c3.Soft.ImpulseScale);
        PackPoint(ref wc.P0, c0.P0, c1.P0, c2.P0, c3.P0, 1, c0.Count, c1.Count, c2.Count, c3.Count);
        PackPoint(ref wc.P1, c0.P1, c1.P1, c2.P1, c3.P1, 2, c0.Count, c1.Count, c2.Count, c3.Count);
    }

    private static F4 V4(float a, float b, float c, float d) => Vector128.Create(a, b, c, d);

    private static void PackPoint(ref WidePoint w, in ConstraintPoint p0, in ConstraintPoint p1, in ConstraintPoint p2, in ConstraintPoint p3, int need, int n0, int n1, int n2, int n3)
    {
        // A manifold with one point leaves its second lane empty: zero masses there make every impulse zero.
        static float M(float v, int count, int need) => count >= need ? v : 0;
        w.AnchorAX = V4(p0.AnchorA.X, p1.AnchorA.X, p2.AnchorA.X, p3.AnchorA.X);
        w.AnchorAY = V4(p0.AnchorA.Y, p1.AnchorA.Y, p2.AnchorA.Y, p3.AnchorA.Y);
        w.AnchorBX = V4(p0.AnchorB.X, p1.AnchorB.X, p2.AnchorB.X, p3.AnchorB.X);
        w.AnchorBY = V4(p0.AnchorB.Y, p1.AnchorB.Y, p2.AnchorB.Y, p3.AnchorB.Y);
        w.AdjustedSeparation = V4(p0.AdjustedSeparation, p1.AdjustedSeparation, p2.AdjustedSeparation, p3.AdjustedSeparation);
        w.NormalImpulse = V4(M(p0.NormalImpulse, n0, need), M(p1.NormalImpulse, n1, need), M(p2.NormalImpulse, n2, need), M(p3.NormalImpulse, n3, need));
        w.TangentImpulse = V4(M(p0.TangentImpulse, n0, need), M(p1.TangentImpulse, n1, need), M(p2.TangentImpulse, n2, need), M(p3.TangentImpulse, n3, need));
        w.MaxNormalImpulse = F4.Zero;
        w.NormalMass = V4(M(p0.NormalMass, n0, need), M(p1.NormalMass, n1, need), M(p2.NormalMass, n2, need), M(p3.NormalMass, n3, need));
        w.TangentMass = V4(M(p0.TangentMass, n0, need), M(p1.TangentMass, n1, need), M(p2.TangentMass, n2, need), M(p3.TangentMass, n3, need));
        w.RelativeVelocity = V4(p0.RelativeVelocity, p1.RelativeVelocity, p2.RelativeVelocity, p3.RelativeVelocity);
    }

    /// <summary>Four bodies' solver state in lanes.</summary>
    private struct WideBody
    {
        public F4 VX, VY, W, DPX, DPY, QC, QS;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private WideBody Gather(int i0, int i1, int i2, int i3)
    {
        ref readonly var s0 = ref States[i0]; ref readonly var s1 = ref States[i1]; ref readonly var s2 = ref States[i2]; ref readonly var s3 = ref States[i3];
        return new WideBody
        {
            VX = V4(s0.V.X, s1.V.X, s2.V.X, s3.V.X), VY = V4(s0.V.Y, s1.V.Y, s2.V.Y, s3.V.Y), W = V4(s0.W, s1.W, s2.W, s3.W),
            DPX = V4(s0.DP.X, s1.DP.X, s2.DP.X, s3.DP.X), DPY = V4(s0.DP.Y, s1.DP.Y, s2.DP.Y, s3.DP.Y),
            QC = V4(s0.DQ.C, s1.DQ.C, s2.DQ.C, s3.DQ.C), QS = V4(s0.DQ.S, s1.DQ.S, s2.DQ.S, s3.DQ.S),
        };
    }

    /// <summary>Write back velocities of the lanes whose body has mass (static and kinematic bodies are only read).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Scatter(in WideBody b, F4 mass, F4 inertia, int i0, int i1, int i2, int i3)
    {
        var moves = Vector128.ExtractMostSignificantBits(Vector128.GreaterThan(mass, F4.Zero) | Vector128.GreaterThan(inertia, F4.Zero));
        if ((moves & 1) != 0) { ref var s = ref States[i0]; s.V = new Vector2(b.VX.GetElement(0), b.VY.GetElement(0)); s.W = b.W.GetElement(0); }
        if ((moves & 2) != 0) { ref var s = ref States[i1]; s.V = new Vector2(b.VX.GetElement(1), b.VY.GetElement(1)); s.W = b.W.GetElement(1); }
        if ((moves & 4) != 0) { ref var s = ref States[i2]; s.V = new Vector2(b.VX.GetElement(2), b.VY.GetElement(2)); s.W = b.W.GetElement(2); }
        if ((moves & 8) != 0) { ref var s = ref States[i3]; s.V = new Vector2(b.VX.GetElement(3), b.VY.GetElement(3)); s.W = b.W.GetElement(3); }
    }

    private void WarmStartWide(ref WideConstraint c)
    {
        var a = Gather(c.A0, c.A1, c.A2, c.A3); var b = Gather(c.B0, c.B1, c.B2, c.B3);
        var tx = c.NormalY; var ty = -c.NormalX; // right perpendicular
        ApplyWide(ref a, ref b, c, c.P0, c.P0.NormalImpulse * c.NormalX + c.P0.TangentImpulse * tx, c.P0.NormalImpulse * c.NormalY + c.P0.TangentImpulse * ty);
        ApplyWide(ref a, ref b, c, c.P1, c.P1.NormalImpulse * c.NormalX + c.P1.TangentImpulse * tx, c.P1.NormalImpulse * c.NormalY + c.P1.TangentImpulse * ty);
        Scatter(a, c.MassA, c.InertiaA, c.A0, c.A1, c.A2, c.A3);
        Scatter(b, c.MassB, c.InertiaB, c.B0, c.B1, c.B2, c.B3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ApplyWide(ref WideBody a, ref WideBody b, in WideConstraint c, in WidePoint p, F4 px, F4 py)
    {
        a.VX -= c.MassA * px; a.VY -= c.MassA * py; a.W -= c.InertiaA * (p.AnchorAX * py - p.AnchorAY * px);
        b.VX += c.MassB * px; b.VY += c.MassB * py; b.W += c.InertiaB * (p.AnchorBX * py - p.AnchorBY * px);
    }

    /// <summary>Relative velocity at a point: vB + ωB × rB − vA − ωA × rA.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (F4 X, F4 Y) RelativeVelocity(in WideBody a, in WideBody b, in WidePoint p) =>
        ((b.VX - b.W * p.AnchorBY) - (a.VX - a.W * p.AnchorAY), (b.VY + b.W * p.AnchorBX) - (a.VY + a.W * p.AnchorAX));

    private void SolveWide(ref WideConstraint c, float invH, bool useBias, float maxPushout)
    {
        var a = Gather(c.A0, c.A1, c.A2, c.A3); var b = Gather(c.B0, c.B1, c.B2, c.B3);
        var dpx = b.DPX - a.DPX; var dpy = b.DPY - a.DPY;
        var inv = Vector128.Create(invH);
        var pushout = Vector128.Create(-maxPushout);
        SolveNormal(ref a, ref b, ref c, ref c.P0, dpx, dpy, inv, useBias, pushout);
        SolveNormal(ref a, ref b, ref c, ref c.P1, dpx, dpy, inv, useBias, pushout);
        SolveFriction(ref a, ref b, ref c, ref c.P0);
        SolveFriction(ref a, ref b, ref c, ref c.P1);
        Scatter(a, c.MassA, c.InertiaA, c.A0, c.A1, c.A2, c.A3);
        Scatter(b, c.MassB, c.InertiaB, c.B0, c.B1, c.B2, c.B3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SolveNormal(ref WideBody a, ref WideBody b, ref WideConstraint c, ref WidePoint p, F4 dpx, F4 dpy, F4 invH, bool useBias, F4 pushout)
    {
        // Separation now, from the anchors turned by each body's rotation this step.
        var rax = a.QC * p.AnchorAX - a.QS * p.AnchorAY; var ray = a.QS * p.AnchorAX + a.QC * p.AnchorAY;
        var rbx = b.QC * p.AnchorBX - b.QS * p.AnchorBY; var rby = b.QS * p.AnchorBX + b.QC * p.AnchorBY;
        var separation = ((dpx + (rbx - rax)) * c.NormalX + (dpy + (rby - ray)) * c.NormalY) + p.AdjustedSeparation;
        var speculative = Vector128.GreaterThan(separation, F4.Zero);
        F4 bias, massScale, impulseScale;
        if (useBias)
        {
            var soft = Max(c.BiasRate * separation, pushout);
            bias = Vector128.ConditionalSelect(speculative, separation * invH, soft);
            massScale = Vector128.ConditionalSelect(speculative, Vector128<float>.One, c.MassScale);
            impulseScale = Vector128.ConditionalSelect(speculative, F4.Zero, c.ImpulseScale);
        }
        else
        {
            bias = Vector128.ConditionalSelect(speculative, separation * invH, F4.Zero);
            massScale = Vector128<float>.One;
            impulseScale = F4.Zero;
        }
        var (dvx, dvy) = RelativeVelocity(a, b, p);
        var vn = dvx * c.NormalX + dvy * c.NormalY;
        var impulse = -(p.NormalMass * massScale * (vn + bias)) - impulseScale * p.NormalImpulse;
        var total = Max(p.NormalImpulse + impulse, F4.Zero);
        impulse = total - p.NormalImpulse;
        p.NormalImpulse = total;
        p.MaxNormalImpulse = Max(p.MaxNormalImpulse, impulse);
        ApplyWide(ref a, ref b, c, p, impulse * c.NormalX, impulse * c.NormalY);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SolveFriction(ref WideBody a, ref WideBody b, ref WideConstraint c, ref WidePoint p)
    {
        var tx = c.NormalY; var ty = -c.NormalX;
        var (dvx, dvy) = RelativeVelocity(a, b, p);
        var vt = dvx * tx + dvy * ty;
        var impulse = -(p.TangentMass * vt);
        var limit = c.Friction * p.NormalImpulse;
        var total = Min(Max(p.TangentImpulse + impulse, -limit), limit);
        impulse = total - p.TangentImpulse;
        p.TangentImpulse = total;
        ApplyWide(ref a, ref b, c, p, impulse * tx, impulse * ty);
    }

    private void RestitutionWide(ref WideConstraint c, float threshold)
    {
        var a = Gather(c.A0, c.A1, c.A2, c.A3); var b = Gather(c.B0, c.B1, c.B2, c.B3);
        var limit = Vector128.Create(-threshold);
        RestitutionPoint(ref a, ref b, ref c, ref c.P0, limit);
        RestitutionPoint(ref a, ref b, ref c, ref c.P1, limit);
        Scatter(a, c.MassA, c.InertiaA, c.A0, c.A1, c.A2, c.A3);
        Scatter(b, c.MassB, c.InertiaB, c.B0, c.B1, c.B2, c.B3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RestitutionPoint(ref WideBody a, ref WideBody b, ref WideConstraint c, ref WidePoint p, F4 threshold)
    {
        // Only lanes that approached fast enough and pushed this step bounce.
        var active = Vector128.LessThan(p.RelativeVelocity, threshold) & Vector128.GreaterThan(p.MaxNormalImpulse, F4.Zero) & Vector128.GreaterThan(c.Restitution, F4.Zero);
        if (active == F4.Zero) return;
        var (dvx, dvy) = RelativeVelocity(a, b, p);
        var vn = dvx * c.NormalX + dvy * c.NormalY;
        var impulse = -(p.NormalMass * (vn + c.Restitution * p.RelativeVelocity));
        var total = Max(p.NormalImpulse + impulse, F4.Zero);
        impulse = Vector128.ConditionalSelect(active, total - p.NormalImpulse, F4.Zero);
        p.NormalImpulse += impulse;
        ApplyWide(ref a, ref b, c, p, impulse * c.NormalX, impulse * c.NormalY);
    }

    /// <summary>Lanes' impulses back into the persistent contacts, for warm starting next step.</summary>
    private void StoreWide(ref WideConstraint c)
    {
        Store(c.C0, 0, c); Store(c.C1, 1, c); Store(c.C2, 2, c); Store(c.C3, 3, c);
        void Store(int i, int lane, in WideConstraint w)
        {
            if (i < 0) return;
            ref var contact = ref Contacts[_constraints[i].Contact];
            contact.P0.NormalImpulse = w.P0.NormalImpulse.GetElement(lane); contact.P0.TangentImpulse = w.P0.TangentImpulse.GetElement(lane);
            contact.P1.NormalImpulse = w.P1.NormalImpulse.GetElement(lane); contact.P1.TangentImpulse = w.P1.TangentImpulse.GetElement(lane);
        }
    }
}
