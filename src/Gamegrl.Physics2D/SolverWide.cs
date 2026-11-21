// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
using Gamegrl.Core.Memory;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

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
        public F4 AdjustedSeparation, NormalImpulse, TangentImpulse, MaxNormalImpulse, NormalMass, TangentMass;
        // The anchors crossed with the normal and the tangent, fixed for the step. They stand in for the anchors
        // themselves (rn = r·t, rt = −r·n), which keeps 64 bytes per point out of the stream the passes read.
        public F4 RnA, RnB, RtA, RtB;
    }

    private struct WideConstraint
    {
        public int A0, A1, A2, A3, B0, B1, B2, B3;
        public F4 NormalX, NormalY, Friction, MassA, MassB, InertiaA, InertiaB;
        // How each row's impulse changes the later rows' velocities (Box2D's k21…k43), fixed for the step: worked out
        // once here instead of in every one of the twelve passes.
        public F4 K21, K31, K32, K41, K42, K43;
        public WidePoint P0, P1;
    }

    /// <summary>
    /// What only restitution and storing read, kept out of <see cref="WideConstraint"/>: the passes run twelve times a
    /// step stream that record, and every byte left out of it is bandwidth saved.
    /// </summary>
    private struct WideCold
    {
        public int C0, C1, C2, C3; // contacts, -1 for padding
        public F4 Restitution, RelativeVelocity0, RelativeVelocity1;
    }

    private AlignedArray<WideConstraint> _wide = new(16);
    private AlignedArray<WideCold> _wideCold = new(16);
    private int[] _colorBatchStart = new int[Colors + 1];
    private int _wideCount;

    // a > b ? a : b, per lane. SSE's maxps/minps are defined as exactly that (NaN and ±0 included: equal or unordered
    // lanes take b), so they are bit-identical to the select used elsewhere, in one instruction instead of three or four.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static F4 Max(F4 a, F4 b) => Sse.IsSupported ? Sse.Max(a, b) : Vector128.ConditionalSelect(Vector128.GreaterThan(a, b), a, b);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static F4 Min(F4 a, F4 b) => Sse.IsSupported ? Sse.Min(a, b) : Vector128.ConditionalSelect(Vector128.LessThan(a, b), a, b);

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
        if (_wide.Length < w) { _wide = new AlignedArray<WideConstraint>(w * 2); _wideCold = new AlignedArray<WideCold>(w * 2); }
        // Padding lanes read some body (never written: they have no mass).
        _paddingBody = _constraintCount > 0 ? Contacts[_order[0]].BodyA : 0;
        _padContact = new Contact { BodyA = _paddingBody, BodyB = _paddingBody };
    }

    private int _paddingBody;
    private Contact _padContact; // what empty lanes read: no points, on the padding body

    /// <summary>Pack batch <paramref name="w"/> from its four prepared constraints (padding lanes get no mass, so they do nothing).</summary>
    private unsafe void PackBatch(int w)
    {
        int color = 0;
        while (_colorBatchStart[color + 1] <= w) color++;
        int i = _colorStart[color] + (w - _colorBatchStart[color]) * 4;
        int n = Math.Min(4, _colorStart[color + 1] - i);
        ref var wc = ref _wide[w];
        ref var cold = ref _wideCold[w];
        // The next batch's contacts are scattered through the contact array: start fetching them now.
        if (Sse.IsSupported)
        {
            int ahead = Math.Min(i + 8, _constraintCount);
            for (int k = i + 4; k < ahead; k++)
            {
                byte* contact = (byte*)Unsafe.AsPointer(ref Contacts[_order[k]]); // a hint only: a stale address is harmless
                Sse.Prefetch0(contact); Sse.Prefetch0(contact + 64);
            }
        }
        // The four contacts; empty lanes read a zeroed contact on the padding body and get zero masses below.
        int k0 = _order[i], k1 = n > 1 ? _order[i + 1] : -1, k2 = n > 2 ? _order[i + 2] : -1, k3 = n > 3 ? _order[i + 3] : -1;
        ref readonly var c0 = ref Contacts[k0];
        ref readonly var c1 = ref k1 >= 0 ? ref Contacts[k1] : ref _padContact;
        ref readonly var c2 = ref k2 >= 0 ? ref Contacts[k2] : ref _padContact;
        ref readonly var c3 = ref k3 >= 0 ? ref Contacts[k3] : ref _padContact;
        cold.C0 = k0; cold.C1 = k1; cold.C2 = k2; cold.C3 = k3;
        wc.A0 = c0.BodyA; wc.A1 = c1.BodyA; wc.A2 = c2.BodyA; wc.A3 = c3.BodyA;
        wc.B0 = c0.BodyB; wc.B1 = c1.BodyB; wc.B2 = c2.BodyB; wc.B3 = c3.BodyB;

        // Fields come in as 16-byte rows per contact and are transposed into lanes (like the body gathers), so every
        // lane value is built in registers and stored once: scalar lane stores read back as vectors would stall on
        // store forwarding.
        Transpose(Row(c0.Normal.X), Row(c1.Normal.X), Row(c2.Normal.X), Row(c3.Normal.X), out var nx, out var ny, out var friction, out var restitution);
        var valid = Vector128.LessThan(Vector128.Create(0, 1, 2, 3), Vector128.Create(n)).AsSingle();
        var masses = (double*)_stepMass.Pointer; // (inverse mass, inverse inertia) per body
        Transpose(Vector128.Create(masses[wc.A0], masses[wc.B0]).AsSingle(), Vector128.Create(masses[wc.A1], masses[wc.B1]).AsSingle(),
            Vector128.Create(masses[wc.A2], masses[wc.B2]).AsSingle(), Vector128.Create(masses[wc.A3], masses[wc.B3]).AsSingle(),
            out var mA, out var iA, out var mB, out var iB);
        mA &= valid; iA &= valid; mB &= valid; iB &= valid;
        wc.NormalX = nx; wc.NormalY = ny; wc.Friction = friction; cold.Restitution = restitution;
        wc.MassA = mA; wc.MassB = mB; wc.InertiaA = iA; wc.InertiaB = iB;

        var states = States.Pointer;
        GatherRow(states, wc.A0, wc.A1, wc.A2, wc.A3, 0, out var vxA, out var vyA, out var wA, out _);
        GatherRow(states, wc.B0, wc.B1, wc.B2, wc.B3, 0, out var vxB, out var vyB, out var wB, out _);
        var tx = ny; var ty = -nx; // right perpendicular
        var invMass = mA + mB;
        // A missing second point keeps zero masses, so every impulse on it is zero.
        int second = (c0.Count > 1 ? 1 : 0) | (c1.Count > 1 ? 2 : 0) | (c2.Count > 1 ? 4 : 0) | (c3.Count > 1 ? 8 : 0);
        var hasSecond = Vector128.Equals(Vector128.Create(second) & Vector128.Create(1, 2, 4, 8), Vector128.Create(1, 2, 4, 8)).AsSingle();
        cold.RelativeVelocity0 = PackPoint(ref wc.P0, Row(c0.P0.AnchorA.X), Row(c1.P0.AnchorA.X), Row(c2.P0.AnchorA.X), Row(c3.P0.AnchorA.X),
            Row(c0.P0.Separation), Row(c1.P0.Separation), Row(c2.P0.Separation), Row(c3.P0.Separation),
            nx, ny, tx, ty, invMass, iA, iB, vxA, vyA, wA, vxB, vyB, wB, valid);
        cold.RelativeVelocity1 = PackPoint(ref wc.P1, Row(c0.P1.AnchorA.X), Row(c1.P1.AnchorA.X), Row(c2.P1.AnchorA.X), Row(c3.P1.AnchorA.X),
            Row(c0.P1.Separation), Row(c1.P1.Separation), Row(c2.P1.Separation), Row(c3.P1.Separation),
            nx, ny, tx, ty, invMass, iA, iB, vxA, vyA, wA, vxB, vyB, wB, hasSecond & valid);

        // How each row's impulse changes the later rows' velocities.
        ref var p1 = ref wc.P0; ref var p2 = ref wc.P1;
        var wnA1 = iA * p1.RnA; var wnB1 = iB * p1.RnB;
        var wnA2 = iA * p2.RnA; var wnB2 = iB * p2.RnB;
        var wtA1 = iA * p1.RtA; var wtB1 = iB * p1.RtB;
        wc.K21 = invMass + (wnA1 * p2.RnA + wnB1 * p2.RnB);
        wc.K31 = wnA1 * p1.RtA + wnB1 * p1.RtB;
        wc.K32 = wnA2 * p1.RtA + wnB2 * p1.RtB;
        wc.K41 = wnA1 * p2.RtA + wnB1 * p2.RtB;
        wc.K42 = wnA2 * p2.RtA + wnB2 * p2.RtB;
        wc.K43 = invMass + (wtA1 * p2.RtA + wtB1 * p2.RtB);
    }

    /// <summary>Sixteen bytes of a contact starting at <paramref name="first"/> (fields laid out in order).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static F4 Row(in float first) => Vector128.LoadUnsafe(ref Unsafe.AsRef(in first));

    /// <summary>
    /// One contact point in lanes, from per-contact rows (anchors: rAx rAy rBx rBy; then separation, base separation,
    /// normal and tangent impulse), with its step constants.
    /// </summary>
    /// <returns>The approach speed along the normal (for restitution).</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static F4 PackPoint(ref WidePoint p, F4 a0, F4 a1, F4 a2, F4 a3, F4 s0, F4 s1, F4 s2, F4 s3,
        F4 nx, F4 ny, F4 tx, F4 ty, F4 invMass, F4 iA, F4 iB, F4 vxA, F4 vyA, F4 wA, F4 vxB, F4 vyB, F4 wB, F4 exists)
    {
        Transpose(a0, a1, a2, a3, out var rax, out var ray, out var rbx, out var rby);
        Transpose(s0, s1, s2, s3, out var separation, out _, out var normalImpulse, out var tangentImpulse);
        p.NormalImpulse = normalImpulse; p.TangentImpulse = tangentImpulse; p.MaxNormalImpulse = F4.Zero;
        var rnA = rax * ny - ray * nx; var rnB = rbx * ny - rby * nx;
        var rtA = rax * ty - ray * tx; var rtB = rbx * ty - rby * tx;
        p.RnA = rnA; p.RnB = rnB; p.RtA = rtA; p.RtB = rtB;
        var kn = invMass + iA * rnA * rnA + iB * rnB * rnB;
        var kt = invMass + iA * rtA * rtA + iB * rtB * rtB;
        p.NormalMass = Vector128.ConditionalSelect(Vector128.GreaterThan(kn, F4.Zero) & exists, Vector128<float>.One / kn, F4.Zero);
        p.TangentMass = Vector128.ConditionalSelect(Vector128.GreaterThan(kt, F4.Zero) & exists, Vector128<float>.One / kt, F4.Zero);
        // Separation later = Δ(cB + rB) − Δ(cA + rA) along n, plus this constant.
        p.AdjustedSeparation = separation - ((rbx - rax) * nx + (rby - ray) * ny);
        var vx = (vxB - wB * rby) - (vxA - wA * ray);
        var vy = (vyB + wB * rbx) - (vyA + wA * rax);
        return vx * nx + vy * ny;
    }



    /// <summary>Four bodies' solver state in lanes.</summary>
    private struct WideBody
    {
        public F4 VX, VY, W, DPX, DPY, QC, QS;
    }

    /// <summary>
    /// Four bodies' states in lanes: two 16-byte rows per body, transposed with unpacks (Box2D's approach), instead of
    /// building every field from four scattered scalar loads.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe WideBody Gather(int i0, int i1, int i2, int i3)
    {
        var states = States.Pointer;
        float* p0 = (float*)(states + i0), p1 = (float*)(states + i1), p2 = (float*)(states + i2), p3 = (float*)(states + i3);
        Transpose(Vector128.LoadAligned(p0), Vector128.LoadAligned(p1), Vector128.LoadAligned(p2), Vector128.LoadAligned(p3), out var vx, out var vy, out var w, out _);
        Transpose(Vector128.LoadAligned(p0 + 4), Vector128.LoadAligned(p1 + 4), Vector128.LoadAligned(p2 + 4), Vector128.LoadAligned(p3 + 4), out var dpx, out var dpy, out var qc, out var qs);
        return new WideBody { VX = vx, VY = vy, W = w, DPX = dpx, DPY = dpy, QC = qc, QS = qs };
    }

    /// <summary>Rows to columns (or back): four vectors of four floats, transposed.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Transpose(F4 r0, F4 r1, F4 r2, F4 r3, out F4 c0, out F4 c1, out F4 c2, out F4 c3)
    {
        if (System.Runtime.Intrinsics.X86.Sse.IsSupported)
        {
            var t0 = System.Runtime.Intrinsics.X86.Sse.UnpackLow(r0, r1);  // r0.0 r1.0 r0.1 r1.1
            var t1 = System.Runtime.Intrinsics.X86.Sse.UnpackLow(r2, r3);  // r2.0 r3.0 r2.1 r3.1
            var t2 = System.Runtime.Intrinsics.X86.Sse.UnpackHigh(r0, r1); // r0.2 r1.2 r0.3 r1.3
            var t3 = System.Runtime.Intrinsics.X86.Sse.UnpackHigh(r2, r3); // r2.2 r3.2 r2.3 r3.3
            c0 = System.Runtime.Intrinsics.X86.Sse.MoveLowToHigh(t0, t1);
            c1 = System.Runtime.Intrinsics.X86.Sse.MoveHighToLow(t1, t0);
            c2 = System.Runtime.Intrinsics.X86.Sse.MoveLowToHigh(t2, t3);
            c3 = System.Runtime.Intrinsics.X86.Sse.MoveHighToLow(t3, t2);
        }
        else if (System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.IsSupported)
        {
            var t0 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(r0, r1);
            var t1 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(r2, r3);
            var t2 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(r0, r1);
            var t3 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(r2, r3);
            c0 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(t0.AsDouble(), t1.AsDouble()).AsSingle();
            c1 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(t0.AsDouble(), t1.AsDouble()).AsSingle();
            c2 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipLow(t2.AsDouble(), t3.AsDouble()).AsSingle();
            c3 = System.Runtime.Intrinsics.Arm.AdvSimd.Arm64.ZipHigh(t2.AsDouble(), t3.AsDouble()).AsSingle();
        }
        else
        {
            c0 = Vector128.Create(r0[0], r1[0], r2[0], r3[0]);
            c1 = Vector128.Create(r0[1], r1[1], r2[1], r3[1]);
            c2 = Vector128.Create(r0[2], r1[2], r2[2], r3[2]);
            c3 = Vector128.Create(r0[3], r1[3], r2[3], r3[3]);
        }
    }

    private unsafe void WarmStartRange(int start, int end)
    {
        var wide = _wide.Pointer; var states = States.Pointer;
        for (int w = start; w < end; w++) WarmStartWide(ref wide[w], states);
    }

    /// <summary>Last step's impulses, applied again: velocities only, with the cross products prepared in Pack.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void WarmStartWide(ref WideConstraint c, BodyState* states)
    {
        GatherRow(states, c.A0, c.A1, c.A2, c.A3, 0, out var vxA, out var vyA, out var wA, out _);
        GatherRow(states, c.B0, c.B1, c.B2, c.B3, 0, out var vxB, out var vyB, out var wB, out _);
        var nx = c.NormalX; var ny = c.NormalY;
        ref readonly var p1 = ref c.P0; ref readonly var p2 = ref c.P1;
        // P = Nn + Tt for each point (t = (ny, −nx)); r × P = N (r × n) + T (r × t).
        var normal = p1.NormalImpulse + p2.NormalImpulse; var tangent = p1.TangentImpulse + p2.TangentImpulse;
        var px = normal * nx + tangent * ny; var py = normal * ny - tangent * nx;
        var angularA = (p1.NormalImpulse * p1.RnA + p2.NormalImpulse * p2.RnA) + (p1.TangentImpulse * p1.RtA + p2.TangentImpulse * p2.RtA);
        var angularB = (p1.NormalImpulse * p1.RnB + p2.NormalImpulse * p2.RnB) + (p1.TangentImpulse * p1.RtB + p2.TangentImpulse * p2.RtB);
        var mA = c.MassA; var mB = c.MassB; var iA = c.InertiaA; var iB = c.InertiaB;
        Scatter(states, vxA - mA * px, vyA - mA * py, wA - iA * angularA, mA, iA, c.A0, c.A1, c.A2, c.A3);
        Scatter(states, vxB + mB * px, vyB + mB * py, wB + iB * angularB, mB, iB, c.B0, c.B1, c.B2, c.B3);
    }


    /// <summary>
    /// The biased pass of a substep (Box2D's "push"): only the two normal points, with the soft overlap bias. Friction is
    /// solved once per substep, in the relax pass, as Box2D does; the second point sees the first's impulse through the
    /// coupling k21, and the bodies' velocities change once.
    /// </summary>
    /// <summary>Per-pass constants, made once per range rather than once per batch.</summary>
    private struct PushConstants
    {
        public F4 InvH, Pushout, BiasRate, MassScale, ImpulseScale, StaticBiasRate, StaticMassScale, StaticImpulseScale;
    }

    // The passes loop inside one function each: the per-batch work inlines into the loop, and the prologue, the
    // constants and the array pointers are set up once per range instead of once per batch.

    private unsafe void PushRange(int start, int end)
    {
        var k = new PushConstants
        {
            InvH = Vector128.Create(_invH), Pushout = Vector128.Create(-Settings.MaxPushout),
            BiasRate = Vector128.Create(_contactSoft.BiasRate), MassScale = Vector128.Create(_contactSoft.MassScale), ImpulseScale = Vector128.Create(_contactSoft.ImpulseScale),
            StaticBiasRate = Vector128.Create(_staticSoft.BiasRate), StaticMassScale = Vector128.Create(_staticSoft.MassScale), StaticImpulseScale = Vector128.Create(_staticSoft.ImpulseScale),
        };
        var wide = _wide.Pointer; var states = States.Pointer;
        PushBatches(wide, start, end, states, k);
    }

    private unsafe void RelaxRange(int start, int end)
    {
        var inv = Vector128.Create(_invH);
        var wide = _wide.Pointer; var states = States.Pointer;
        RelaxBatches(wide, start, end, states, inv);
    }

    private static unsafe void PushBatches(WideConstraint* wide, int start, int end, BodyState* states, in PushConstants k)
    {
        for (int w = start; w < end; w++)
        {
            ref var c = ref wide[w];
            // Plain vector locals rather than a body struct, gathered as needed: the JIT keeps them in registers.
            GatherRow(states, c.A0, c.A1, c.A2, c.A3, 1, out var dpxA, out var dpyA, out var qcA, out var qsA);
            GatherRow(states, c.B0, c.B1, c.B2, c.B3, 1, out var dpxB, out var dpyB, out var qcB, out var qsB);
            var nx = c.NormalX; var ny = c.NormalY;
            var dpx = dpxB - dpxA; var dpy = dpyB - dpyA;
            ref var p1 = ref c.P0; ref var p2 = ref c.P1;
            var s1 = Separation(qcA, qsA, qcB, qsB, nx, ny, p1, dpx, dpy);
            var s2 = Separation(qcA, qsA, qcB, qsB, nx, ny, p2, dpx, dpy);
            // Contacts with an immovable body are stiffer (Box2D's static softness); chosen per lane from the masses.
            var anchored = (Vector128.Equals(c.MassA, F4.Zero) & Vector128.Equals(c.InertiaA, F4.Zero)) | (Vector128.Equals(c.MassB, F4.Zero) & Vector128.Equals(c.InertiaB, F4.Zero));
            var biasRate = Vector128.ConditionalSelect(anchored, k.StaticBiasRate, k.BiasRate);
            var softMass = Vector128.ConditionalSelect(anchored, k.StaticMassScale, k.MassScale);
            var softImpulse = Vector128.ConditionalSelect(anchored, k.StaticImpulseScale, k.ImpulseScale);
            SoftBias(biasRate, softMass, softImpulse, s1, k.InvH, k.Pushout, out var bias1, out var massScale1, out var impulseScale1);
            SoftBias(biasRate, softMass, softImpulse, s2, k.InvH, k.Pushout, out var bias2, out var massScale2, out var impulseScale2);

            GatherRow(states, c.A0, c.A1, c.A2, c.A3, 0, out var vxA, out var vyA, out var wA, out _);
            GatherRow(states, c.B0, c.B1, c.B2, c.B3, 0, out var vxB, out var vyB, out var wB, out _);
            var rnA1 = p1.RnA; var rnB1 = p1.RnB; var rnA2 = p2.RnA; var rnB2 = p2.RnB;
            var mA = c.MassA; var mB = c.MassB; var iA = c.InertiaA; var iB = c.InertiaB;
            var k21 = c.K21;
            var dn = (vxB - vxA) * nx + (vyB - vyA) * ny;
            var vn1 = dn + (wB * rnB1 - wA * rnA1);
            var vn2 = dn + (wB * rnB2 - wA * rnA2);
            var impulse1 = Max(-(p1.NormalMass * massScale1 * (vn1 + bias1)) - impulseScale1 * p1.NormalImpulse, -p1.NormalImpulse);
            p1.NormalImpulse += impulse1;
            p1.MaxNormalImpulse = Max(p1.MaxNormalImpulse, impulse1);
            vn2 += k21 * impulse1;
            var impulse2 = Max(-(p2.NormalMass * massScale2 * (vn2 + bias2)) - impulseScale2 * p2.NormalImpulse, -p2.NormalImpulse);
            p2.NormalImpulse += impulse2;
            p2.MaxNormalImpulse = Max(p2.MaxNormalImpulse, impulse2);
            var impulse = impulse1 + impulse2;
            var px = impulse * nx; var py = impulse * ny;
            var angularA = impulse1 * rnA1 + impulse2 * rnA2;
            var angularB = impulse1 * rnB1 + impulse2 * rnB2;
            Scatter(states, vxA - mA * px, vyA - mA * py, wA - iA * angularA, mA, iA, c.A0, c.A1, c.A2, c.A3);
            Scatter(states, vxB + mB * px, vyB + mB * py, wB + iB * angularB, mB, iB, c.B0, c.B1, c.B2, c.B3);
        }
    }

    /// <summary>
    /// The relax pass of a substep: both normal points with only the speculative bias, then both friction rows, as
    /// sequential impulses whose effects on later rows come from precomputed couplings (k21…k43, as in Box2D's wide
    /// solver), so the bodies' velocities change once, at the end.
    /// </summary>
    private static unsafe void RelaxBatches(WideConstraint* wide, int start, int end, BodyState* states, F4 inv)
    {
        for (int w = start; w < end; w++)
        {
            ref var c = ref wide[w];
            GatherRow(states, c.A0, c.A1, c.A2, c.A3, 1, out var dpxA, out var dpyA, out var qcA, out var qsA);
            GatherRow(states, c.B0, c.B1, c.B2, c.B3, 1, out var dpxB, out var dpyB, out var qcB, out var qsB);
            var nx = c.NormalX; var ny = c.NormalY;
            var dpx = dpxB - dpxA; var dpy = dpyB - dpyA;
            ref var p1 = ref c.P0; ref var p2 = ref c.P1;
            // Speculative bias only: close a gap this substep, never push.
            var bias1 = Max(Separation(qcA, qsA, qcB, qsB, nx, ny, p1, dpx, dpy) * inv, F4.Zero);
            var bias2 = Max(Separation(qcA, qsA, qcB, qsB, nx, ny, p2, dpx, dpy) * inv, F4.Zero);

            GatherRow(states, c.A0, c.A1, c.A2, c.A3, 0, out var vxA, out var vyA, out var wA, out _);
            GatherRow(states, c.B0, c.B1, c.B2, c.B3, 0, out var vxB, out var vyB, out var wB, out _);
            var tx = ny; var ty = -nx; // right perpendicular
            var rnA1 = p1.RnA; var rnB1 = p1.RnB; var rnA2 = p2.RnA; var rnB2 = p2.RnB;
            var rtA1 = p1.RtA; var rtB1 = p1.RtB; var rtA2 = p2.RtA; var rtB2 = p2.RtB;
            var iA = c.InertiaA; var iB = c.InertiaB;

            var dvx = vxB - vxA; var dvy = vyB - vyA;
            var dn = dvx * nx + dvy * ny;
            var dt = dvx * tx + dvy * ty;
            var vn1 = dn + (wB * rnB1 - wA * rnA1);
            var vn2 = dn + (wB * rnB2 - wA * rnA2);
            var vt1 = dt + (wB * rtB1 - wA * rtA1);
            var vt2 = dt + (wB * rtB2 - wA * rtA2);

            var impulse1 = Max(-(p1.NormalMass * (vn1 + bias1)), -p1.NormalImpulse);
            p1.NormalImpulse += impulse1;
            p1.MaxNormalImpulse = Max(p1.MaxNormalImpulse, impulse1);
            vn2 += c.K21 * impulse1;
            var impulse2 = Max(-(p2.NormalMass * (vn2 + bias2)), -p2.NormalImpulse);
            p2.NormalImpulse += impulse2;
            p2.MaxNormalImpulse = Max(p2.MaxNormalImpulse, impulse2);

            vt1 += c.K31 * impulse1 + c.K32 * impulse2;
            vt2 += c.K41 * impulse1 + c.K42 * impulse2;

            var friction = c.Friction;
            var limit1 = friction * p1.NormalImpulse;
            var tangent1 = Min(Max(p1.TangentImpulse - p1.TangentMass * vt1, -limit1), limit1) - p1.TangentImpulse;
            p1.TangentImpulse += tangent1;
            vt2 += c.K43 * tangent1;
            var limit2 = friction * p2.NormalImpulse;
            var tangent2 = Min(Max(p2.TangentImpulse - p2.TangentMass * vt2, -limit2), limit2) - p2.TangentImpulse;
            p2.TangentImpulse += tangent2;

            var normal = impulse1 + impulse2; var tangent = tangent1 + tangent2;
            var px = normal * nx + tangent * tx; var py = normal * ny + tangent * ty;
            var angularA = (impulse1 * rnA1 + impulse2 * rnA2) + (tangent1 * rtA1 + tangent2 * rtA2);
            var angularB = (impulse1 * rnB1 + impulse2 * rnB2) + (tangent1 * rtB1 + tangent2 * rtB2);
            var mA = c.MassA; var mB = c.MassB;
            Scatter(states, vxA - mA * px, vyA - mA * py, wA - iA * angularA, mA, iA, c.A0, c.A1, c.A2, c.A3);
            Scatter(states, vxB + mB * px, vyB + mB * py, wB + iB * angularB, mB, iB, c.B0, c.B1, c.B2, c.B3);
        }
    }

    /// <summary>
    /// A point's separation now: (Δp + dqB·rB − dqA·rA)·n plus the step constant. A turn (c, s) moves an anchor's
    /// projection on n to c(r·n) + s(r×n) = s·rn − c·rt, so the anchors themselves aren't needed.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static F4 Separation(F4 qcA, F4 qsA, F4 qcB, F4 qsB, F4 nx, F4 ny, in WidePoint p, F4 dpx, F4 dpy) =>
        ((dpx * nx + dpy * ny) + ((qsB * p.RnB - qcB * p.RtB) - (qsA * p.RnA - qcA * p.RtA))) + p.AdjustedSeparation;

    /// <summary>Speculative bias when separated; otherwise the soft constraint's bias and scales.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void SoftBias(F4 biasRate, F4 softMass, F4 softImpulse, F4 separation, F4 invH, F4 pushout, out F4 bias, out F4 massScale, out F4 impulseScale)
    {
        var speculative = Vector128.GreaterThan(separation, F4.Zero);
        var soft = Max(biasRate * separation, pushout);
        bias = Vector128.ConditionalSelect(speculative, separation * invH, soft);
        massScale = Vector128.ConditionalSelect(speculative, Vector128<float>.One, softMass);
        impulseScale = Vector128.ConditionalSelect(speculative, F4.Zero, softImpulse);
    }

    /// <summary>One 16-byte row (0: velocity, 1: position delta) of four bodies' states, as columns.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void GatherRow(int i0, int i1, int i2, int i3, int row, out F4 c0, out F4 c1, out F4 c2, out F4 c3) =>
        GatherRow(States.Pointer, i0, i1, i2, i3, row, out c0, out c1, out c2, out c3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void GatherRow(BodyState* states, int i0, int i1, int i2, int i3, int row, out F4 c0, out F4 c1, out F4 c2, out F4 c3)
    {
        var rows = (float*)states + row * 4;
        Transpose(Vector128.LoadAligned(rows + i0 * 8), Vector128.LoadAligned(rows + i1 * 8), Vector128.LoadAligned(rows + i2 * 8), Vector128.LoadAligned(rows + i3 * 8),
            out c0, out c1, out c2, out c3);
    }

    /// <summary>Write back velocities of the lanes whose body has mass (static and kinematic bodies are only read).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private unsafe void Scatter(F4 vx, F4 vy, F4 w, F4 mass, F4 inertia, int i0, int i1, int i2, int i3) =>
        Scatter(States.Pointer, vx, vy, w, mass, inertia, i0, i1, i2, i3);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void Scatter(BodyState* states, F4 vx, F4 vy, F4 w, F4 mass, F4 inertia, int i0, int i1, int i2, int i3)
    {
        var moves = Vector128.ExtractMostSignificantBits(Vector128.GreaterThan(mass, F4.Zero) | Vector128.GreaterThan(inertia, F4.Zero));
        if (moves == 0) return;
        Transpose(vx, vy, w, F4.Zero, out var r0, out var r1, out var r2, out var r3);
        if ((moves & 1) != 0) r0.StoreAligned((float*)(states + i0));
        if ((moves & 2) != 0) r1.StoreAligned((float*)(states + i1));
        if ((moves & 4) != 0) r2.StoreAligned((float*)(states + i2));
        if ((moves & 8) != 0) r3.StoreAligned((float*)(states + i3));
    }

    private void RestitutionWide(ref WideConstraint c, ref WideCold cold, float threshold)
    {
        if (cold.Restitution == F4.Zero) return; // nothing in this batch bounces
        var a = Gather(c.A0, c.A1, c.A2, c.A3); var b = Gather(c.B0, c.B1, c.B2, c.B3);
        var limit = Vector128.Create(-threshold);
        RestitutionPoint(ref a, ref b, ref c, ref c.P0, cold.Restitution, cold.RelativeVelocity0, limit);
        RestitutionPoint(ref a, ref b, ref c, ref c.P1, cold.Restitution, cold.RelativeVelocity1, limit);
        Scatter(a.VX, a.VY, a.W, c.MassA, c.InertiaA, c.A0, c.A1, c.A2, c.A3);
        Scatter(b.VX, b.VY, b.W, c.MassB, c.InertiaB, c.B0, c.B1, c.B2, c.B3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RestitutionPoint(ref WideBody a, ref WideBody b, ref WideConstraint c, ref WidePoint p, F4 restitution, F4 relativeVelocity, F4 threshold)
    {
        // Only lanes that approached fast enough and pushed this step bounce.
        var active = Vector128.LessThan(relativeVelocity, threshold) & Vector128.GreaterThan(p.MaxNormalImpulse, F4.Zero) & Vector128.GreaterThan(restitution, F4.Zero);
        if (active == F4.Zero) return;
        var vn = ((b.VX - a.VX) * c.NormalX + (b.VY - a.VY) * c.NormalY) + (b.W * p.RnB - a.W * p.RnA);
        var impulse = -(p.NormalMass * (vn + restitution * relativeVelocity));
        var total = Max(p.NormalImpulse + impulse, F4.Zero);
        impulse = Vector128.ConditionalSelect(active, total - p.NormalImpulse, F4.Zero);
        p.NormalImpulse += impulse;
        var px = impulse * c.NormalX; var py = impulse * c.NormalY;
        a.VX -= c.MassA * px; a.VY -= c.MassA * py; a.W -= c.InertiaA * (impulse * p.RnA);
        b.VX += c.MassB * px; b.VY += c.MassB * py; b.W += c.InertiaB * (impulse * p.RnB);
    }

    /// <summary>Lanes' impulses back into the persistent contacts, for warm starting next step.</summary>
    private void StoreWide(ref WideConstraint c, in WideCold cold)
    {
        // Transpose the four impulse columns to per-constraint rows, then one pass of stores.
        Transpose(c.P0.NormalImpulse, c.P0.TangentImpulse, c.P1.NormalImpulse, c.P1.TangentImpulse, out var r0, out var r1, out var r2, out var r3);
        StoreLane(cold.C0, r0); StoreLane(cold.C1, r1); StoreLane(cold.C2, r2); StoreLane(cold.C3, r3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void StoreLane(int i, F4 impulses)
    {
        if (i < 0) return;
        ref var contact = ref Contacts[i];
        contact.P0.NormalImpulse = impulses.GetElement(0); contact.P0.TangentImpulse = impulses.GetElement(1);
        contact.P1.NormalImpulse = impulses.GetElement(2); contact.P1.TangentImpulse = impulses.GetElement(3);
    }
}
