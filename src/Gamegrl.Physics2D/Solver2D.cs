using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>A body's solver state during a step: velocities, and how far it has moved and turned since the step began.</summary>
internal struct BodyState
{
    public Vector2 V;
    public float W;
    public Vector2 DP;
    public Rot DQ;
}

public sealed partial class PhysicsWorld2D
{
    private const int Colors = 24, Overflow = Colors;
    // Colours with fewer constraints than this run inline: dispatching to workers costs more than it saves.
    private const int ParallelBatch = 128;

    internal BodyState[] States = [];
    private int[] _parent = [];
    private bool[] _islandAwake = [];
    private float[] _islandSleep = [];
    private int[] _awake = [];
    private int _awakeCount;
    private ulong[] _colorBits = [];
    private int _colorWords;
    private int[] _contactColor = new int[64], _solvable = new int[64];
    private ContactConstraint[] _constraints = new ContactConstraint[64];
    private readonly int[] _colorStart = new int[Colors + 2];
    private int _constraintCount;

    private struct ConstraintPoint
    {
        public Vector2 AnchorA, AnchorB;
        public float AdjustedSeparation, NormalImpulse, TangentImpulse, MaxNormalImpulse, NormalMass, TangentMass, RelativeVelocity;
    }

    private struct ContactConstraint
    {
        public int Contact, A, B, Count;
        public Vector2 Normal;
        public float Friction, Restitution, MassA, MassB, InertiaA, InertiaB;
        public Softness Soft;
        public ConstraintPoint P0, P1;
    }

    /// <summary>A soft constraint's coefficients for one substep (a spring of the given frequency and damping ratio).</summary>
    internal struct Softness
    {
        public float BiasRate, MassScale, ImpulseScale;
        public static Softness Make(float hertz, float zeta, float h)
        {
            if (hertz == 0) return new Softness { BiasRate = 0, MassScale = 1, ImpulseScale = 0 };
            float omega = 2 * SimMath.Pi * hertz;
            float a1 = 2 * zeta + h * omega, a2 = h * omega * a1, a3 = 1 / (1 + a2);
            return new Softness { BiasRate = omega / a1, MassScale = a2 * a3, ImpulseScale = a3 };
        }
    }

    private void GrowSolver(int capacity)
    {
        Array.Resize(ref States, capacity);
        Array.Resize(ref _parent, capacity);
        Array.Resize(ref _islandAwake, capacity);
        Array.Resize(ref _islandSleep, capacity);
        Array.Resize(ref _awake, capacity);
        _colorWords = (capacity + 63) / 64;
        _colorBits = new ulong[Colors * _colorWords];
    }

    private void CloneSolver(PhysicsWorld2D w)
    {
        w.States = (BodyState[])States.Clone(); w._parent = (int[])_parent.Clone(); w._islandAwake = (bool[])_islandAwake.Clone();
        w._islandSleep = (float[])_islandSleep.Clone(); w._awake = (int[])_awake.Clone(); w._colorBits = (ulong[])_colorBits.Clone();
        w._contactColor = (int[])_contactColor.Clone(); w._solvable = (int[])_solvable.Clone(); w._constraints = (ContactConstraint[])_constraints.Clone();
        w._wide = (WideConstraint[])_wide.Clone();
        w._segments = (Segment[])_segments.Clone(); w._claims = new int[_claims.Length]; w._barrier = new SpinBarrier();
    }

    // ---------------------------------------------------------------- islands and sleep

    private int Find(int b)
    {
        while (_parent[b] != b) { _parent[b] = _parent[_parent[b]]; b = _parent[b]; }
        return b;
    }

    private void Union(int a, int b)
    {
        int ra = Find(a), rb = Find(b);
        if (ra == rb) return;
        // The lower index wins, so roots don't depend on the order of unions.
        if (ra < rb) _parent[rb] = ra; else _parent[ra] = rb;
    }

    /// <summary>Islands of dynamic bodies joined by touching contacts and joints; anything touching the awake wakes.</summary>
    private void BuildIslands()
    {
        for (int b = 0; b < _bodyHigh; b++) _parent[b] = b;
        for (int c = 0; c < ContactCount; c++)
        {
            ref readonly var contact = ref Contacts[c];
            if ((contact.Flags & (ContactFlags.Touching | ContactFlags.Sensor)) != ContactFlags.Touching) continue;
            int a = contact.BodyA, b = contact.BodyB;
            if (Types[a] == BodyType.Dynamic && Types[b] == BodyType.Dynamic) Union(a, b);
            // A moving kinematic body wakes what it touches.
            else if (Types[a] == BodyType.Kinematic && Awake[a] && Types[b] == BodyType.Dynamic) Wake(b);
            else if (Types[b] == BodyType.Kinematic && Awake[b] && Types[a] == BodyType.Dynamic) Wake(a);
        }
        UnionJoints();
        for (int b = 0; b < _bodyHigh; b++) _islandAwake[b] = false;
        for (int b = 0; b < _bodyHigh; b++)
            if (BodyAlive[b] && Types[b] == BodyType.Dynamic && Awake[b]) _islandAwake[Find(b)] = true;
        _awakeCount = 0;
        for (int b = 0; b < _bodyHigh; b++)
        {
            if (!BodyAlive[b] || Types[b] == BodyType.Static) continue;
            if (Types[b] == BodyType.Dynamic && !Awake[b] && _islandAwake[Find(b)]) Wake(b);
            if (Awake[b]) _awake[_awakeCount++] = b;
        }
    }

    private void UpdateSleep(float dt)
    {
        var s = Settings;
        if (!s.EnableSleep) return;
        float linear2 = s.LinearSleepSpeed * s.LinearSleepSpeed;
        for (int b = 0; b < _bodyHigh; b++) _islandSleep[b] = float.MaxValue;
        for (int k = 0; k < _awakeCount; k++)
        {
            int b = _awake[k];
            bool still = AllowSleep[b] && SimMath.LengthSquared(Velocity[b]) <= linear2 && MathF.Abs(AngularVelocityOf[b]) <= s.AngularSleepSpeed;
            SleepTime[b] = still ? SleepTime[b] + dt : 0;
            if (Types[b] == BodyType.Dynamic) { int r = Find(b); _islandSleep[r] = Math.Min(_islandSleep[r], SleepTime[b]); }
        }
        for (int k = 0; k < _awakeCount; k++)
        {
            int b = _awake[k];
            float island = Types[b] == BodyType.Dynamic ? _islandSleep[Find(b)] : SleepTime[b];
            if (island < s.TimeToSleep) continue;
            Awake[b] = false;
            Velocity[b] = default;
            AngularVelocityOf[b] = 0;
        }
    }

    // ---------------------------------------------------------------- solve

    private enum Stage : byte { Prepare, IntegrateVelocities, WarmStart, Solve, IntegratePositions, Relax, Restitution, Store, Serial, Pack, WideWarmStart, WideSolve, WideRelax, WideRestitution, WideStore }
    private enum SerialWork : byte { PrepareJoints, WarmStart, Solve, Relax, Restitution, Store }

    /// <summary>One step of the solver's schedule: a stage over a range (a colour's constraints, or the awake bodies), or serial work.</summary>
    private struct Segment
    {
        public Stage Stage;
        public SerialWork Serial;
        public int Start, Count, Block;
    }

    private Segment[] _segments = new Segment[256];
    private int _segmentCount;
    private int[] _claims = new int[256];
    private SpinBarrier _barrier = new();
    private float _h, _invH;

    private void AddSegment(Stage stage, int start, int count, int block, SerialWork serial = default)
    {
        if (stage != Stage.Serial && count == 0) return;
        if (_segmentCount == _segments.Length) { Array.Resize(ref _segments, _segments.Length * 2); Array.Resize(ref _claims, _segments.Length); }
        _segments[_segmentCount++] = new Segment { Stage = stage, Start = start, Count = count, Block = block, Serial = serial };
    }

    private void AddColors(Stage stage, SerialWork serial)
    {
        // Colours run as batches of four constraints (SolverWide).
        for (int color = 0; color < Colors; color++)
            AddSegment(stage, _colorBatchStart[color], _colorBatchStart[color + 1] - _colorBatchStart[color], 8);
        // Joints and the constraints no colour could take share bodies: one thread does them, in order.
        AddSegment(Stage.Serial, 0, 0, 0, serial);
    }

    private struct SolverJob : IJobThreads
    {
        public PhysicsWorld2D World;
        public readonly void Execute(int thread, int threadCount) => World.RunSchedule(thread);
    }

    /// <summary>
    /// Every thread walks the schedule: each parallel segment is cut into one contiguous slice per thread, the same slice
    /// every stage, so a core keeps reworking the constraints and bodies already in its cache (with dynamic claiming,
    /// data moved between cores every stage and two threads ran slower than one). Serial segments run on thread 0. A
    /// barrier ends each segment. Which thread does what never changes the result.
    /// </summary>
    private void RunSchedule(int thread)
    {
        int threads = _threads;
        for (int i = 0; i < _segmentCount; i++)
        {
            ref readonly var seg = ref _segments[i];
            if (seg.Stage == Stage.Serial) { if (thread == 0) RunSerial(seg.Serial); }
            else
            {
                int start = (int)((long)seg.Count * thread / threads), end = (int)((long)seg.Count * (thread + 1) / threads);
                if (end > start) RunRange(seg.Stage, seg.Start + start, seg.Start + end);
            }
            _barrier.Wait();
        }
    }

    private int _threads = 1;

    private void RunRange(Stage stage, int start, int end)
    {
        float h = _h, invH = _invH, pushout = Settings.MaxPushout;
        switch (stage)
        {
            case Stage.IntegrateVelocities: for (int k = start; k < end; k++) IntegrateVelocity(_awake[k], h); break;
            case Stage.IntegratePositions: for (int k = start; k < end; k++) IntegratePosition(_awake[k], h); break;
            case Stage.Prepare: for (int i = start; i < end; i++) Prepare(ref _constraints[i]); break;
            case Stage.WarmStart: for (int i = start; i < end; i++) WarmStart(ref _constraints[i]); break;
            case Stage.Solve: for (int i = start; i < end; i++) SolveContact(ref _constraints[i], invH, true, pushout); break;
            case Stage.Relax: for (int i = start; i < end; i++) SolveContact(ref _constraints[i], invH, false, pushout); break;
            case Stage.Restitution: for (int i = start; i < end; i++) ApplyRestitution(ref _constraints[i], Settings.RestitutionThreshold); break;
            case Stage.Store: for (int i = start; i < end; i++) StoreImpulses(ref _constraints[i]); break;
            case Stage.Pack: for (int w = start; w < end; w++) PackBatch(w); break;
            case Stage.WideWarmStart: for (int w = start; w < end; w++) WarmStartWide(ref _wide[w]); break;
            case Stage.WideSolve: for (int w = start; w < end; w++) SolveWide(ref _wide[w], invH, true, pushout); break;
            case Stage.WideRelax: for (int w = start; w < end; w++) SolveWide(ref _wide[w], invH, false, pushout); break;
            case Stage.WideRestitution: for (int w = start; w < end; w++) RestitutionWide(ref _wide[w], Settings.RestitutionThreshold); break;
            case Stage.WideStore: for (int w = start; w < end; w++) StoreWide(ref _wide[w]); break;
        }
    }

    private void RunSerial(SerialWork work)
    {
        int start = _colorStart[Overflow], end = _colorStart[Overflow + 1];
        switch (work)
        {
            case SerialWork.PrepareJoints: PrepareJoints(_h); break;
            case SerialWork.WarmStart: WarmStartJoints(); RunRange(Stage.WarmStart, start, end); break;
            case SerialWork.Solve: SolveJoints(_h, _invH, true); RunRange(Stage.Solve, start, end); break;
            case SerialWork.Relax: SolveJoints(_h, _invH, false); RunRange(Stage.Relax, start, end); break;
            case SerialWork.Restitution: RunRange(Stage.Restitution, start, end); break;
            case SerialWork.Store: RunRange(Stage.Store, start, end); break;
        }
    }

    private void Solve(float dt)
    {
        var s = Settings;
        int substeps = Math.Max(1, s.Substeps);
        float h = dt / substeps, invH = 1 / h;
        _h = h; _invH = invH;
        float contactHertz = Math.Min(s.ContactHertz, 0.25f * invH);
        _contactSoft = Softness.Make(contactHertz, s.ContactDampingRatio, h);
        _staticSoft = Softness.Make(2 * contactHertz, s.ContactDampingRatio, h);
        _jointSoft = Softness.Make(2 * contactHertz, s.JointDampingRatio, h);

        for (int b = 0; b < _bodyHigh; b++) States[b] = new BodyState { DQ = Rot.Identity };
        for (int k = 0; k < _awakeCount; k++)
        {
            int b = _awake[k];
            States[b].V = Velocity[b];
            States[b].W = AngularVelocityOf[b];
        }
        using (ColorMarker.Scope()) { ColorContacts(); SizeBatches(); }

        _segmentCount = 0;
        AddSegment(Stage.Prepare, 0, _constraintCount, 64);
        AddSegment(Stage.Pack, 0, _wideCount, 16);
        AddSegment(Stage.Serial, 0, 0, 0, SerialWork.PrepareJoints);
        for (int sub = 0; sub < substeps; sub++)
        {
            AddSegment(Stage.IntegrateVelocities, 0, _awakeCount, 128);
            AddColors(Stage.WideWarmStart, SerialWork.WarmStart);
            AddColors(Stage.WideSolve, SerialWork.Solve);
            AddSegment(Stage.IntegratePositions, 0, _awakeCount, 128);
            AddColors(Stage.WideRelax, SerialWork.Relax);
        }
        AddColors(Stage.WideRestitution, SerialWork.Restitution);
        AddSegment(Stage.WideStore, 0, _wideCount, 16);
        AddSegment(Stage.Serial, 0, 0, 0, SerialWork.Store);
        Array.Clear(_claims, 0, _segmentCount);

        // Threads for the work there is: a few hundred constraints per thread, or barriers cost more than they save.
        int threads = Jobs is { } jobs ? Math.Clamp(Math.Max(_constraintCount, _awakeCount) / 384, 1, jobs.WorkerCount + 1) : 1;
        if (MaxThreads > 0) threads = Math.Min(threads, MaxThreads);
        _barrier.Reset(threads);
        _threads = threads;
        var job = new SolverJob { World = this };
        using (ScheduleMarker.Scope())
        {
            if (threads > 1) Jobs!.RunOnThreads(threads, ref job);
            else { _threads = 1; RunSchedule(0); }
        }

        for (int k = 0; k < _awakeCount; k++)
        {
            int b = _awake[k];
            Velocity[b] = States[b].V;
            AngularVelocityOf[b] = States[b].W;
        }
    }

    private Softness _contactSoft, _staticSoft, _jointSoft;
    private static readonly ProfileMarker ColorMarker = ProfileMarker.Get("Physics2D.Color", ProfileCategory.Engine);
    private static readonly ProfileMarker ScheduleMarker = ProfileMarker.Get("Physics2D.Constraints", ProfileCategory.Engine);

    private void IntegrateVelocity(int b, float h)
    {
        if (Types[b] != BodyType.Dynamic) return;
        ref var st = ref States[b];
        var v = st.V + h * GravityScale[b] * Settings.Gravity;
        st.V = v / (1 + h * LinearDamping[b]);
        st.W /= 1 + h * AngularDamping[b];
    }

    private void IntegratePosition(int b, float h)
    {
        ref var st = ref States[b];
        var dp = h * st.V;
        Center[b] += dp;
        st.DP += dp;
        float da = h * st.W;
        Rotation[b] = Rot.Integrate(Rotation[b], da);
        st.DQ = Rot.Integrate(st.DQ, da);
    }

    /// <summary>
    /// Greedy colouring: each solvable contact takes the first colour neither of its dynamic bodies has yet. Contacts in
    /// one colour share no dynamic body, so they solve in parallel without changing the result.
    /// </summary>
    private void ColorContacts()
    {
        Array.Clear(_colorBits, 0, Colors * _colorWords);
        if (_solvable.Length < ContactCount) { _solvable = new int[ContactCount * 2]; _contactColor = new int[ContactCount * 2]; }
        int n = 0;
        Span<int> counts = stackalloc int[Colors + 1];
        for (int c = 0; c < ContactCount; c++)
        {
            ref readonly var contact = ref Contacts[c];
            if ((contact.Flags & (ContactFlags.Touching | ContactFlags.Sensor)) != ContactFlags.Touching) continue;
            int a = contact.BodyA, b = contact.BodyB;
            bool dynA = Types[a] == BodyType.Dynamic && Awake[a], dynB = Types[b] == BodyType.Dynamic && Awake[b];
            if (!dynA && !dynB) continue;
            int color = Overflow;
            for (int k = 0; k < Colors; k++)
            {
                if (dynA && (_colorBits[k * _colorWords + (a >> 6)] & (1UL << a)) != 0) continue;
                if (dynB && (_colorBits[k * _colorWords + (b >> 6)] & (1UL << b)) != 0) continue;
                color = k;
                if (dynA) _colorBits[k * _colorWords + (a >> 6)] |= 1UL << a;
                if (dynB) _colorBits[k * _colorWords + (b >> 6)] |= 1UL << b;
                break;
            }
            _solvable[n] = c;
            _contactColor[n++] = color;
            counts[color]++;
        }
        TouchingCount = n;
        _colorStart[0] = 0;
        for (int k = 0; k <= Overflow; k++) _colorStart[k + 1] = _colorStart[k] + counts[k];
        int used = 0;
        for (int k = 0; k < Colors; k++) if (counts[k] > 0) used = k + 1;
        ColorsUsed = used;
        OverflowConstraints = counts[Overflow];
        if (_constraints.Length < n) _constraints = new ContactConstraint[n * 2];
        Span<int> next = stackalloc int[Colors + 1];
        for (int k = 0; k <= Overflow; k++) next[k] = _colorStart[k];
        for (int i = 0; i < n; i++) _constraints[next[_contactColor[i]]++].Contact = _solvable[i];
        _constraintCount = n;
    }

    private void Prepare(ref ContactConstraint cc)
    {
        ref readonly var c = ref Contacts[cc.Contact];
        int a = c.BodyA, b = c.BodyB;
        cc.A = a; cc.B = b; cc.Normal = c.Normal; cc.Friction = c.Friction; cc.Restitution = c.Restitution; cc.Count = c.Count;
        float mA = Awake[a] ? InvMass[a] : 0, mB = Awake[b] ? InvMass[b] : 0, iA = Awake[a] ? InvInertia[a] : 0, iB = Awake[b] ? InvInertia[b] : 0;
        cc.MassA = mA; cc.MassB = mB; cc.InertiaA = iA; cc.InertiaB = iB;
        cc.Soft = (mA == 0 && iA == 0) || (mB == 0 && iB == 0) ? _staticSoft : _contactSoft;
        PreparePoint(ref cc, ref cc.P0, c.P0);
        if (c.Count > 1) PreparePoint(ref cc, ref cc.P1, c.P1);
    }

    private void PreparePoint(ref ContactConstraint cc, ref ConstraintPoint p, in ContactPoint src)
    {
        var n = cc.Normal; var t = V.RightPerp(n);
        p.AnchorA = src.AnchorA; p.AnchorB = src.AnchorB;
        p.NormalImpulse = src.NormalImpulse; p.TangentImpulse = src.TangentImpulse; p.MaxNormalImpulse = 0;
        float rnA = V.Cross(p.AnchorA, n), rnB = V.Cross(p.AnchorB, n);
        float k = cc.MassA + cc.MassB + cc.InertiaA * rnA * rnA + cc.InertiaB * rnB * rnB;
        p.NormalMass = k > 0 ? 1 / k : 0;
        float rtA = V.Cross(p.AnchorA, t), rtB = V.Cross(p.AnchorB, t);
        k = cc.MassA + cc.MassB + cc.InertiaA * rtA * rtA + cc.InertiaB * rtB * rtB;
        p.TangentMass = k > 0 ? 1 / k : 0;
        // Separation later = Δ(cB + rB) − Δ(cA + rA) along n, plus this constant.
        p.AdjustedSeparation = src.Separation - V.Dot(p.AnchorB - p.AnchorA, n);
        ref readonly var sa = ref States[cc.A]; ref readonly var sb = ref States[cc.B];
        p.RelativeVelocity = V.Dot(n, sb.V + V.Cross(sb.W, p.AnchorB) - sa.V - V.Cross(sa.W, p.AnchorA));
    }

    // The hot loops keep both bodies' velocities in locals and write them back once (static and kinematic bodies,
    // with no mass, are only read: several threads may share them).

    private void WarmStart(ref ContactConstraint cc)
    {
        ref var sa = ref States[cc.A]; ref var sb = ref States[cc.B];
        var vA = sa.V; float wA = sa.W; var vB = sb.V; float wB = sb.W;
        float mA = cc.MassA, iA = cc.InertiaA, mB = cc.MassB, iB = cc.InertiaB;
        var n = cc.Normal; var t = V.RightPerp(n);
        var p = cc.P0.NormalImpulse * n + cc.P0.TangentImpulse * t;
        vA -= mA * p; wA -= iA * V.Cross(cc.P0.AnchorA, p); vB += mB * p; wB += iB * V.Cross(cc.P0.AnchorB, p);
        if (cc.Count > 1)
        {
            p = cc.P1.NormalImpulse * n + cc.P1.TangentImpulse * t;
            vA -= mA * p; wA -= iA * V.Cross(cc.P1.AnchorA, p); vB += mB * p; wB += iB * V.Cross(cc.P1.AnchorB, p);
        }
        if (mA != 0 || iA != 0) { sa.V = vA; sa.W = wA; }
        if (mB != 0 || iB != 0) { sb.V = vB; sb.W = wB; }
    }

    private void SolveContact(ref ContactConstraint cc, float invH, bool useBias, float maxPushout)
    {
        ref var sa = ref States[cc.A]; ref var sb = ref States[cc.B];
        var vA = sa.V; float wA = sa.W; var vB = sb.V; float wB = sb.W;
        float mA = cc.MassA, iA = cc.InertiaA, mB = cc.MassB, iB = cc.InertiaB;
        var dp = sb.DP - sa.DP;
        var qA = sa.DQ; var qB = sb.DQ;
        var n = cc.Normal;
        float biasRate = cc.Soft.BiasRate, softMass = cc.Soft.MassScale, softImpulse = cc.Soft.ImpulseScale;
        int count = cc.Count;

        // Non-penetration, point by point.
        for (int k = 0; k < count; k++)
        {
            ref var p = ref k == 0 ? ref cc.P0 : ref cc.P1;
            var rA = p.AnchorA; var rB = p.AnchorB;
            var d = dp + qB.Rotate(rB) - qA.Rotate(rA);
            float separation = V.Dot(d, n) + p.AdjustedSeparation;
            float bias = 0, massScale = 1, impulseScale = 0;
            if (separation > 0) bias = separation * invH; // speculative: close the gap this substep, no further
            else if (useBias) { bias = F.Max(biasRate * separation, -maxPushout); massScale = softMass; impulseScale = softImpulse; }
            float vn = V.Dot(vB + V.Cross(wB, rB) - vA - V.Cross(wA, rA), n);
            float impulse = -p.NormalMass * massScale * (vn + bias) - impulseScale * p.NormalImpulse;
            float total = F.Max(p.NormalImpulse + impulse, 0);
            impulse = total - p.NormalImpulse;
            p.NormalImpulse = total;
            p.MaxNormalImpulse = F.Max(p.MaxNormalImpulse, impulse);
            var P = impulse * n;
            vA -= mA * P; wA -= iA * V.Cross(rA, P); vB += mB * P; wB += iB * V.Cross(rB, P);
        }

        // Friction, bounded by each point's normal impulse.
        var t = V.RightPerp(n);
        float friction = cc.Friction;
        for (int k = 0; k < count; k++)
        {
            ref var p = ref k == 0 ? ref cc.P0 : ref cc.P1;
            var rA = p.AnchorA; var rB = p.AnchorB;
            float vt = V.Dot(vB + V.Cross(wB, rB) - vA - V.Cross(wA, rA), t);
            float impulse = -p.TangentMass * vt;
            float limit = friction * p.NormalImpulse;
            float total = F.Clamp(p.TangentImpulse + impulse, -limit, limit);
            impulse = total - p.TangentImpulse;
            p.TangentImpulse = total;
            var P = impulse * t;
            vA -= mA * P; wA -= iA * V.Cross(rA, P); vB += mB * P; wB += iB * V.Cross(rB, P);
        }

        if (mA != 0 || iA != 0) { sa.V = vA; sa.W = wA; }
        if (mB != 0 || iB != 0) { sb.V = vB; sb.W = wB; }
    }

    private void ApplyRestitution(ref ContactConstraint cc, float threshold)
    {
        if (cc.Restitution == 0) return;
        ref var sa = ref States[cc.A]; ref var sb = ref States[cc.B];
        var vA = sa.V; float wA = sa.W; var vB = sb.V; float wB = sb.W;
        float mA = cc.MassA, iA = cc.InertiaA, mB = cc.MassB, iB = cc.InertiaB;
        var n = cc.Normal;
        for (int k = 0; k < cc.Count; k++)
        {
            ref var p = ref k == 0 ? ref cc.P0 : ref cc.P1;
            if (p.RelativeVelocity > -threshold || p.MaxNormalImpulse == 0) continue;
            var rA = p.AnchorA; var rB = p.AnchorB;
            float vn = V.Dot(vB + V.Cross(wB, rB) - vA - V.Cross(wA, rA), n);
            float impulse = -p.NormalMass * (vn + cc.Restitution * p.RelativeVelocity);
            float total = F.Max(p.NormalImpulse + impulse, 0);
            impulse = total - p.NormalImpulse;
            p.NormalImpulse = total;
            var P = impulse * n;
            vA -= mA * P; wA -= iA * V.Cross(rA, P); vB += mB * P; wB += iB * V.Cross(rB, P);
        }
        if (mA != 0 || iA != 0) { sa.V = vA; sa.W = wA; }
        if (mB != 0 || iB != 0) { sb.V = vB; sb.W = wB; }
    }

    private void StoreImpulses(ref ContactConstraint cc)
    {
        ref var c = ref Contacts[cc.Contact];
        c.P0.NormalImpulse = cc.P0.NormalImpulse; c.P0.TangentImpulse = cc.P0.TangentImpulse;
        c.P1.NormalImpulse = cc.P1.NormalImpulse; c.P1.TangentImpulse = cc.P1.TangentImpulse;
    }
}
