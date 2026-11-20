using Gamegrl.Core.Memory;
using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>A body's solver state during a step: velocities, and how far it has moved and turned since the step began.</summary>
/// <remarks>Laid out as two 16-byte rows, [vx vy w pad] [dpx dpy qc qs], so the wide solver loads four bodies with eight
/// vector loads and a transpose.</remarks>
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
internal struct BodyState
{
    public Vector2 V;
    public float W;
    public float Pad;
    public Vector2 DP;
    public Rot DQ;
}

public sealed partial class PhysicsWorld2D
{
    // Box2D's graph: 23 colours and an overflow; constraints between two dynamic bodies take the first 20, constraints
    // with one dynamic body take colours from the end (solved last, so they get the final say).
    private const int Colors = 23, Overflow = Colors, DynamicColors = 20;
    // Colours with fewer constraints than this run inline: dispatching to workers costs more than it saves.
    private const int ParallelBatch = 128;

    internal AlignedArray<BodyState> States = new(0);
    private AlignedArray<Vector2> _stepMass = new(0); // inverse mass and inertia, zero unless awake (set each step)
    private int[] _awake = [];
    private int[] _justSleptList = [];
    private int _justSleptCount;
    private int _awakeCount;
    private int[] _order = new int[64]; // solvable contacts sorted by colour: constraint slot -> contact
    private ContactConstraint[] _constraints = new ContactConstraint[64];
    private int[] _colorStart = new int[Colors + 2];
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
        States = States.Resized(capacity); _stepMass = _stepMass.Resized(capacity);
        Array.Resize(ref _awake, capacity);
        Array.Resize(ref _justSleptList, capacity);
        Array.Resize(ref _bodyColors, capacity);
        Array.Resize(ref _sleepVelocity, capacity);
    }

    private void CloneSolver(PhysicsWorld2D w)
    {
        w.States = States.Clone(); w._stepMass = _stepMass.Clone(); w._awake = (int[])_awake.Clone(); w._justSleptList = (int[])_justSleptList.Clone(); w._bodyColors = (uint[])_bodyColors.Clone(); w._sleepVelocity = (float[])_sleepVelocity.Clone(); w._bullets = new int[_bullets.Length];
        w._colorLists = new int[Colors + 1][]; for (int k = 0; k <= Colors; k++) w._colorLists[k] = (int[])_colorLists[k].Clone();
        w._colorCounts = (int[])_colorCounts.Clone(); w._colorJointCounts = (int[])_colorJointCounts.Clone(); w._jointColorStart = (int[])_jointColorStart.Clone(); w._jointOrder = (int[])_jointOrder.Clone();
        w._colorJoints = new int[Colors + 1][]; for (int k = 0; k <= Colors; k++) w._colorJoints[k] = (int[])_colorJoints[k].Clone(); w._colorStart = (int[])_colorStart.Clone(); w._colorBatchStart = (int[])_colorBatchStart.Clone(); w._order = (int[])_order.Clone(); w._constraints = (ContactConstraint[])_constraints.Clone();
        w._wide = _wide.Clone(); w._wideCold = _wideCold.Clone();
        w._segments = (Segment[])_segments.Clone(); w._claims = new int[_claims.Length]; w._barrier = new SpinBarrier();
    }

    // ---------------------------------------------------------------- solve

    private enum Stage : byte { PrepareJoints, BeginBodies, Finalize, Prepare, IntegrateVelocities, WarmStart, Solve, IntegratePositions, Relax, Restitution, Store, Serial, Pack, WideWarmStart, WideSolve, WideRelax, WideRestitution, WideStore }
    private enum SerialWork : byte { PrepareJoints, WarmStart, Solve, Relax, Restitution, Store, Bullets }

    /// <summary>One step of the solver's schedule: a stage over a range (a colour's constraints, or the awake bodies), or serial work.</summary>
    private struct Segment
    {
        public Stage Stage;
        public SerialWork Serial;
        public int Start, Count, Block;
        public int JointStart, JointCount; // a colour's joints, solved alongside its contact batches
    }

    private Segment[] _segments = new Segment[256];
    private int _segmentCount;
    private int[] _claims = new int[256];
    private SpinBarrier _barrier = new();
    private float _h, _invH, _invDt, _dt;
    private float[] _sleepVelocity = [];

    private void AddSegment(Stage stage, int start, int count, int block, SerialWork serial = default, int jointStart = 0, int jointCount = 0)
    {
        if (stage != Stage.Serial && count == 0 && jointCount == 0) return;
        if (_segmentCount == _segments.Length) { Array.Resize(ref _segments, _segments.Length * 2); Array.Resize(ref _claims, _segments.Length); }
        _segments[_segmentCount++] = new Segment { Stage = stage, Start = start, Count = count, Block = block, Serial = serial, JointStart = jointStart, JointCount = jointCount };
    }

    private void AddColors(Stage stage, SerialWork serial, bool joints = true)
    {
        // Colours run as batches of four contacts (SolverWide) beside the colour's joints.
        for (int color = 0; color < Colors; color++)
            AddSegment(stage, _colorBatchStart[color], _colorBatchStart[color + 1] - _colorBatchStart[color], 8, default,
                _jointColorStart[color], joints ? _jointColorStart[color + 1] - _jointColorStart[color] : 0);
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
        bool timed = StageTicks is not null && thread == 0;
        for (int i = 0; i < _segmentCount; i++)
        {
            ref readonly var seg = ref _segments[i];
            long t0 = timed ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            if (seg.Stage == Stage.Serial) { if (thread == 0) RunSerial(seg.Serial); }
            else
            {
                // Joints first, then contact batches, split as one range.
                int total = seg.Count + seg.JointCount, joints = seg.JointCount;
                int start = (int)((long)total * thread / threads), end = (int)((long)total * (thread + 1) / threads);
                if (start < joints) RunJoints(seg.Stage, seg.JointStart + start, seg.JointStart + Math.Min(end, joints));
                int bs = Math.Max(start, joints) - joints, be = end - joints;
                if (be > bs) RunRange(seg.Stage, seg.Start + bs, seg.Start + be);
            }
            _barrier.Wait();
            if (timed) StageTicks![(int)seg.Stage] += System.Diagnostics.Stopwatch.GetTimestamp() - t0;
        }
    }

    /// <summary>
    /// Benchmarking aid: when set (one slot per solver stage, at least 32), thread 0 adds each stage's time, barrier
    /// included, in <see cref="System.Diagnostics.Stopwatch"/> ticks. Null, the default, costs one branch per stage.
    /// </summary>
    public static long[]? StageTicks;

    /// <summary>Names for <see cref="StageTicks"/> slots.</summary>
    public static string StageName(int slot) => ((Stage)slot).ToString();

    private int _threads = 1;

    private unsafe void RunRange(Stage stage, int start, int end)
    {
        var wide = _wide.Pointer; // ranges come from the schedule, within _wideCount
        float h = _h, invH = _invH, pushout = Settings.MaxPushout;
        switch (stage)
        {
            case Stage.IntegrateVelocities: for (int k = start; k < end; k++) IntegrateVelocity(_awake[k], h); break;
            case Stage.IntegratePositions: for (int k = start; k < end; k++) IntegratePosition(_awake[k], h); break;
            // The scalar stages run the overflow constraints, held from the overflow's first slot.
            case Stage.Prepare: for (int i = start; i < end; i++) { ref var cc = ref _constraints[i - _colorStart[Overflow]]; cc.Contact = _order[i]; Prepare(ref cc); } break;
            case Stage.WarmStart: for (int i = start; i < end; i++) WarmStart(ref _constraints[i - _colorStart[Overflow]]); break;
            case Stage.Solve: for (int i = start; i < end; i++) SolveContact(ref _constraints[i - _colorStart[Overflow]], invH, true, pushout); break;
            case Stage.Relax: for (int i = start; i < end; i++) SolveContact(ref _constraints[i - _colorStart[Overflow]], invH, false, pushout); break;
            case Stage.Restitution: for (int i = start; i < end; i++) ApplyRestitution(ref _constraints[i - _colorStart[Overflow]], Settings.RestitutionThreshold); break;
            case Stage.Store: for (int i = start; i < end; i++) StoreImpulses(ref _constraints[i - _colorStart[Overflow]]); break;
            case Stage.Pack: for (int w = start; w < end; w++) PackBatch(w); break;
            case Stage.BeginBodies: for (int b = start; b < end; b++) BeginBody(b); break;
            case Stage.Finalize: for (int k = start; k < end; k++) FinalizeBody(_awake[k]); break;
            case Stage.WideWarmStart: WarmStartRange(start, end); break;
            case Stage.WideSolve: PushRange(start, end); break;
            case Stage.WideRelax: RelaxRange(start, end); break;
            case Stage.WideRestitution: for (int w = start; w < end; w++) RestitutionWide(ref wide[w], ref _wideCold.Pointer[w], Settings.RestitutionThreshold); break;
            case Stage.WideStore: for (int w = start; w < end; w++) StoreWide(ref wide[w], _wideCold.Pointer[w]); break;
        }
    }

    /// <summary>A range of the colour-ordered joints for a stage (prepare is over all of them).</summary>
    private void RunJoints(Stage stage, int start, int end)
    {
        switch (stage)
        {
            case Stage.PrepareJoints: for (int k = start; k < end; k++) PrepareJoint(_jointOrder[k]); break;
            case Stage.WideWarmStart: for (int k = start; k < end; k++) WarmStartJoint(_jointOrder[k]); break;
            case Stage.WideSolve: for (int k = start; k < end; k++) SolveJoint(_jointOrder[k], true); break;
            case Stage.WideRelax: for (int k = start; k < end; k++) SolveJoint(_jointOrder[k], false); break;
        }
    }

    private void RunSerial(SerialWork work)
    {
        int start = _colorStart[Overflow], end = _colorStart[Overflow + 1];
        int js = _jointColorStart[Overflow], je = _jointColorStart[Overflow + 1];
        switch (work)
        {
            case SerialWork.PrepareJoints: break;
            case SerialWork.WarmStart: RunJoints(Stage.WideWarmStart, js, je); RunRange(Stage.WarmStart, start, end); break;
            case SerialWork.Solve: RunJoints(Stage.WideSolve, js, je); RunRange(Stage.Solve, start, end); break;
            case SerialWork.Relax: RunJoints(Stage.WideRelax, js, je); RunRange(Stage.Relax, start, end); break;
            case SerialWork.Restitution: RunRange(Stage.Restitution, start, end); break;
            case SerialWork.Store: RunRange(Stage.Store, start, end); break;
            case SerialWork.Bullets: SolveBullets(); break;
        }
    }

    private void Solve(float dt)
    {
        var s = Settings;
        int substeps = Math.Max(1, s.Substeps);
        float h = dt / substeps, invH = 1 / h;
        _h = h; _invH = invH; _invDt = 1 / dt; _dt = dt;
        if (_bullets.Length < _awakeCount) _bullets = new int[_awakeCount * 2];
        _bulletCount = 0;
        float contactHertz = Math.Min(s.ContactHertz, 0.25f * invH);
        _contactSoft = Softness.Make(contactHertz, s.ContactDampingRatio, h);
        _staticSoft = Softness.Make(2 * contactHertz, s.ContactDampingRatio, h);

        using (ColorMarker.Scope()) { ColorContacts(); SizeBatches(); }

        _segmentCount = 0;
        AddSegment(Stage.BeginBodies, 0, _bodyHigh, 256);
        // Coloured constraints are prepared straight into their batches (Pack); only the overflow uses the scalar form.
        AddSegment(Stage.Prepare, _colorStart[Overflow], _colorStart[Overflow + 1] - _colorStart[Overflow], 64);
        AddSegment(Stage.Pack, 0, _wideCount, 16);
        AddSegment(Stage.PrepareJoints, 0, 0, 16, default, 0, _jointOrderCount);
        for (int sub = 0; sub < substeps; sub++)
        {
            AddSegment(Stage.IntegrateVelocities, 0, _awakeCount, 128);
            AddColors(Stage.WideWarmStart, SerialWork.WarmStart);
            AddColors(Stage.WideSolve, SerialWork.Solve);
            AddSegment(Stage.IntegratePositions, 0, _awakeCount, 128);
            AddColors(Stage.WideRelax, SerialWork.Relax);
        }
        AddColors(Stage.WideRestitution, SerialWork.Restitution, joints: false);
        AddSegment(Stage.WideStore, 0, _wideCount, 16);
        AddSegment(Stage.Serial, 0, 0, 0, SerialWork.Store);
        AddSegment(Stage.Finalize, 0, _awakeCount, 256);
        AddSegment(Stage.Serial, 0, 0, 0, SerialWork.Bullets);
        Array.Clear(_claims, 0, _segmentCount);

        // Threads for the work there is: a few hundred constraints per thread, or barriers cost more than they save.
        int threads = Jobs is { } jobs ? Math.Clamp(Math.Max(_constraintCount + _jointOrderCount, _awakeCount) / 384, 1, jobs.WorkerCount + 1) : 1;
        if (MaxThreads > 0) threads = Math.Min(threads, MaxThreads);
        _barrier.Reset(threads);
        _threads = threads;
        var job = new SolverJob { World = this };
        using (ScheduleMarker.Scope())
        {
            if (threads > 1) Jobs!.RunOnThreads(threads, ref job);
            else { _threads = 1; RunSchedule(0); }
        }
        CollectJointEvents();

    }

    /// <summary>A body's solver state for the step: its velocity if it moves, zero motion otherwise.</summary>
    private void BeginBody(int b)
    {
        bool moves = BodyAlive[b] && Awake[b] && Types[b] != BodyType.Static;
        States[b] = new BodyState { V = moves ? Velocity[b] : default, W = moves ? AngularVelocityOf[b] : 0, DQ = Rot.Identity };
        // What the constraints see: a sleeping body (touched by an awake one) is as immovable as a static one.
        bool awake = BodyAlive[b] && Awake[b];
        _stepMass[b] = new Vector2(awake ? InvMass[b] : 0, awake ? InvInertia[b] : 0);
    }

    /// <summary>Apply the step's accumulated motion once (substeps only track deltas, as Box2D does).</summary>
    private void FinalizeBody(int b)
    {
        ref readonly var st = ref States[b];
        // The fastest any point of the body moves, and the farthest any point moved this step.
        float extent = MaxExtent[b];
        float maxVelocity = SimMath.Length(st.V) + MathF.Abs(st.W) * extent;
        float maxDelta = SimMath.Length(st.DP) + MathF.Abs(st.DQ.S) * extent;
        // Box2D's sleep speed: that, or half the speed its correction moved it at (a body pushed out of an overlap each
        // step isn't resting even when its velocity is small).
        float sleepVelocity = F.Max(maxVelocity, 0.5f * _invDt * maxDelta);
        _sleepVelocity[b] = sleepVelocity;
        Center0[b] = Center[b]; Rotation0[b] = Rotation[b];
        Center[b] += st.DP;
        var q = Rot.Mul(st.DQ, Rotation[b]);
        float l = SimMath.Sqrt(q.C * q.C + q.S * q.S);
        Rotation[b] = l > 0 ? new Rot { C = q.C / l, S = q.S / l } : Rot.Identity;
        Velocity[b] = st.V;
        AngularVelocityOf[b] = st.W;
        // A resting body can't have tunnelled (Box2D skips the sleepy).
        if (!Settings.EnableSleep || !AllowSleep[b] || sleepVelocity > SleepThreshold[b]) Continuous(b, maxDelta, maxVelocity, _dt);
    }

    private Softness _contactSoft, _staticSoft;
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
        st.DP += h * st.V;
        st.DQ = Rot.Integrate(st.DQ, h * st.W);
    }

    // ---------------------------------------------------------------- the constraint graph

    // Persistent colouring (Box2D's constraint graph): a contact takes the first colour neither of its awake dynamic
    // bodies has when it becomes solvable, and keeps it until it stops being solvable. Contacts in one colour share no
    // dynamic body, so they solve in parallel without changing the result. The narrowphase flags contacts whose state
    // changed and the event pass moves only those; anything that changes which bodies are awake or dynamic (waking,
    // sleeping, type changes, destruction) recolours everything at the next solve instead.

    private uint[] _bodyColors = []; // per body: bit k set when one of its graph contacts has colour k
    private int[][] _colorLists = NewColorLists();
    private int[] _colorCounts = new int[Colors + 1];
    private int[][] _colorJoints = NewColorLists();
    private int[] _colorJointCounts = new int[Colors + 1];
    private int[] _jointColorStart = new int[Colors + 2];
    private int[] _jointOrder = new int[16];
    private int _jointOrderCount;
    private bool _recolor = true;

    private static int[][] NewColorLists()
    {
        var lists = new int[Colors + 1][];
        for (int k = 0; k <= Colors; k++) lists[k] = new int[16];
        return lists;
    }

    /// <summary>Whether a contact belongs in the graph: touching, solid, and with an awake dynamic body.</summary>
    private bool Solvable(in Contact contact)
    {
        if ((contact.Flags & (ContactFlags.Touching | ContactFlags.Sensor | ContactFlags.Destroy)) != ContactFlags.Touching) return false;
        int a = contact.BodyA, b = contact.BodyB;
        return (Types[a] == BodyType.Dynamic && Awake[a]) || (Types[b] == BodyType.Dynamic && Awake[b]);
    }

    /// <summary>A colour for a constraint between two bodies (Box2D's policy), marking it on their dynamic, awake bodies; Overflow when none is free.</summary>
    private int AssignColor(int a, int b)
    {
        bool dynA = a >= 0 && Types[a] == BodyType.Dynamic && Awake[a], dynB = b >= 0 && Types[b] == BodyType.Dynamic && Awake[b];
        int color = Overflow;
        if (dynA && dynB)
        {
            int c = BitOperations.TrailingZeroCount(~(_bodyColors[a] | _bodyColors[b]));
            if (c < DynamicColors) color = c;
        }
        else if (dynA || dynB)
        {
            // From the last colour down to 1.
            uint free = ~_bodyColors[dynA ? a : b] & (((1u << Colors) - 1) & ~1u);
            if (free != 0) color = 31 - BitOperations.LeadingZeroCount(free);
        }
        if (color < Colors)
        {
            if (dynA) _bodyColors[a] |= 1u << color;
            if (dynB) _bodyColors[b] |= 1u << color;
        }
        return color;
    }

    /// <summary>Clear a constraint's colour from its bodies (they're as they were when it was added: any change since would have scheduled a recolour).</summary>
    private void ReleaseColor(int color, int a, int b)
    {
        if (color >= Colors) return;
        if (a >= 0 && Types[a] == BodyType.Dynamic && Awake[a]) _bodyColors[a] &= ~(1u << color);
        if (b >= 0 && Types[b] == BodyType.Dynamic && Awake[b]) _bodyColors[b] &= ~(1u << color);
    }

    private void AddToGraph(int c)
    {
        ref var contact = ref Contacts[c];
        int color = AssignColor(contact.BodyA, contact.BodyB);
        ref var list = ref _colorLists[color];
        ref int count = ref _colorCounts[color];
        if (count == list.Length) Array.Resize(ref list, count * 2);
        contact.GraphColor = color + 1;
        contact.GraphSlot = count;
        list[count++] = c;
    }

    private void RemoveFromGraph(int c)
    {
        ref var contact = ref Contacts[c];
        int color = contact.GraphColor - 1;
        ReleaseColor(color, contact.BodyA, contact.BodyB);
        var list = _colorLists[color];
        int last = list[--_colorCounts[color]];
        list[contact.GraphSlot] = last;
        Contacts[last].GraphSlot = contact.GraphSlot;
        contact.GraphColor = 0;
    }

    private void Recolor()
    {
        Array.Clear(_bodyColors, 0, _bodyHigh);
        Array.Clear(_colorCounts);
        // Every contact leaves the graph (sleeping ones may still hold a stale colour); the solvable are awake.
        Array.Clear(_colorJointCounts);
        for (int c = 0; c < _contactHigh; c++) Contacts[c].GraphColor = 0;
        for (int j = 0; j < _jointHigh; j++) _joints[j].GraphColor = 0;
        for (int j = 0; j < _jointHigh; j++) if (JointSolvable(j)) AddJointToGraph(j);
        for (int k = 0; k < _awakeContactCount; k++)
        {
            int c = _awakeContacts[k];
            if (Solvable(Contacts[c])) AddToGraph(c);
        }
        _recolor = false;
    }

    /// <summary>The solve order: each colour's contacts in turn.</summary>
    private void ColorContacts()
    {
        if (_recolor) Recolor();
        int n = 0, used = 0;
        for (int k = 0; k <= Overflow; k++)
        {
            _colorStart[k] = n;
            n += _colorCounts[k];
            if (k < Colors && _colorCounts[k] > 0) used = k + 1;
        }
        _colorStart[Overflow + 1] = n;
        TouchingCount = n;
        ColorsUsed = used;
        OverflowConstraints = _colorCounts[Overflow];
        if (_order.Length < n) _order = new int[n * 2];
        if (_constraints.Length < _colorCounts[Overflow]) _constraints = new ContactConstraint[_colorCounts[Overflow] * 2];
        for (int k = 0; k <= Overflow; k++) Array.Copy(_colorLists[k], 0, _order, _colorStart[k], _colorCounts[k]);
        _constraintCount = n;
        // Joints, colour by colour, the same way.
        int jn = 0;
        for (int k = 0; k <= Overflow; k++) { _jointColorStart[k] = jn; jn += _colorJointCounts[k]; }
        _jointColorStart[Overflow + 1] = jn;
        if (_jointOrder.Length < jn) _jointOrder = new int[jn * 2];
        for (int k = 0; k <= Overflow; k++) Array.Copy(_colorJoints[k], 0, _jointOrder, _jointColorStart[k], _colorJointCounts[k]);
        _jointOrderCount = jn;
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

        // Friction, bounded by each point's normal impulse: once per substep, in the relax pass (as the wide path).
        if (useBias) { if (mA != 0 || iA != 0) { sa.V = vA; sa.W = wA; } if (mB != 0 || iB != 0) { sb.V = vB; sb.W = wB; } return; }
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
