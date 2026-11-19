using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Gamegrl.Physics2D;

public enum BodyType : byte { Static, Kinematic, Dynamic }

/// <summary>World-wide settings (a resource read each step).</summary>
public struct PhysicsSettings2D
{
    public Vector2 Gravity;
    /// <summary>Solver substeps per fixed step: more is stiffer and more accurate (4 is a good default).</summary>
    public int Substeps;
    /// <summary>Contact stiffness, capped at a quarter of the substep rate.</summary>
    public float ContactHertz;
    public float ContactDampingRatio;
    /// <summary>Joint stiffness for rigid joints, and its damping.</summary>
    public float JointHertz, JointDampingRatio;
    /// <summary>The fastest overlapping bodies are pushed apart, in m/s.</summary>
    public float MaxPushout;
    /// <summary>Approach speed below which collisions don't bounce, in m/s.</summary>
    public float RestitutionThreshold;
    /// <summary>Let bodies at rest sleep (cost nothing until something touches them).</summary>
    public bool EnableSleep;
    /// <summary>How long an island must stay below the sleep speeds before it sleeps, in seconds.</summary>
    public float TimeToSleep;
    public float LinearSleepSpeed, AngularSleepSpeed;

    public static PhysicsSettings2D Default => new()
    {
        Gravity = new Vector2(0, -10), Substeps = 4, ContactHertz = 30, ContactDampingRatio = 10, JointHertz = 60, JointDampingRatio = 2,
        MaxPushout = 3, RestitutionThreshold = 1, EnableSleep = true, TimeToSleep = 0.5f, LinearSleepSpeed = 0.05f, AngularSleepSpeed = 2 * SimMath.Pi / 180,
    };
}

/// <summary>A ray hit: the collider's entity, the point, the surface normal and the fraction of the ray travelled.</summary>
public struct RayHit2D
{
    public Entity Entity;
    public Vector2 Point, Normal;
    public float Fraction;
}

/// <summary>Two colliders started or stopped touching (or overlapping, for a sensor and a visitor).</summary>
public struct ContactEvent2D
{
    /// <summary>The colliders' entities; for sensor events, A is the sensor.</summary>
    public Entity A, B;
    /// <summary>For begin-touch events: a contact point, the normal from A to B, and the approach speed along it.</summary>
    public Vector2 Point, Normal;
    public float Speed;
}

/// <summary>A filter for what collides: two colliders collide when each one's category is in the other's mask.</summary>
public struct Filter2D
{
    public uint Category, Mask;
    public static Filter2D Default => new() { Category = 1, Mask = uint.MaxValue };
    public readonly bool Accepts(in Filter2D other) => (Category & other.Mask) != 0 && (other.Category & Mask) != 0;
}

/// <summary>
/// The 2D physics state. Bodies and shapes in struct-of-arrays storage keyed by dense indices; a body has any number
/// of shapes. Shapes live in two dynamic AABB trees (static, and moving) with fat boxes; contacts persist while fat
/// boxes overlap and keep their impulses between steps (warm starting). Each step: update the broadphase, collide every
/// awake contact (in parallel), wake and sleep islands, colour the contact graph and solve it with a soft-step solver
/// (substeps with soft contacts, relaxation, restitution; after Box2D v3), each colour in parallel. Deterministic: the
/// same steps give the same bits whatever the thread count, and on every platform under <c>CrossPlatform</c>.
/// Allocation-free once grown. <see cref="Clone"/> copies it for snapshots and rollback.
/// </summary>
public sealed partial class PhysicsWorld2D
{
    internal const float LinearSlop = 0.005f, Speculative = 4 * LinearSlop, AabbMargin = 0.1f, AabbPrediction = 4;

    // ---------------------------------------------------------------- bodies (struct of arrays)
    private int _bodyCapacity, _bodyHigh;
    private int[] _freeBodies = new int[16];
    private int _freeBodyCount;
    internal Entity[] BodyEntity = [];
    internal BodyType[] Types = [];
    internal bool[] BodyAlive = [], Awake = [], AllowSleep = [], FixedRotationOf = [], Implicit = [];
    internal float[] SleepTime = [];
    internal Vector2[] Center = [], LocalCenter = [], Velocity = [];
    internal Rot[] Rotation = [];
    internal float[] AngularVelocityOf = [], InvMass = [], InvInertia = [], Mass = [], GravityScale = [], LinearDamping = [], AngularDamping = [];
    internal int[] FirstShape = [];
    internal Vector2[] Written = [];
    internal Rot[] WrittenRotation = [];
    internal bool[] MassDirty = [];
    internal bool[] _justSlept = [];
    internal float[] MaxExtent = []; // farthest any shape reaches from the center of mass
    internal int[] TransformVersion = []; // bumped by teleports, which invalidate cached manifolds

    // ---------------------------------------------------------------- shapes
    private int _shapeCapacity, _shapeHigh;
    private int[] _freeShapes = new int[16];
    private int _freeShapeCount;
    internal int[] ShapeBody = [], NextShape = [], Proxy = [];
    internal Shape[] Geometry = [];
    internal float[] Density = [], Friction = [], Restitution = [];
    internal bool[] Sensor = [], ShapeAlive = [], InStaticTree = [], Moved = [];
    internal Filter2D[] Filters = [];
    internal Entity[] ShapeEntity = [];
    internal Aabb[] Fat = [];
    // The body pose (centre of mass, rotation) each fat box was fitted at; a NaN rotation means "refit to know".
    internal FatPose[] FatPoses = [];
    internal ShapeSource[] Sources = [];
    internal float[] Bound = []; // radius of a circle about the shape's center that holds it

    private DynamicTree _staticTree = new(), _movingTree = new();
    private int[] _moveBuffer = new int[64];
    private int _moveCount;
    private ulong[] _newPairs = new ulong[64];
    private int _newPairCount;

    // ---------------------------------------------------------------- contacts
    internal Contact[] Contacts = new Contact[64];
    internal int ContactCount;
    private Dictionary<ulong, int> _pairs = new(PairComparer.Instance);
    private HashSet<ulong> _noCollide = new(PairComparer.Instance);

    // ---------------------------------------------------------------- events (valid until the next step)
    private ContactEvent2D[] _begins = new ContactEvent2D[16], _ends = new ContactEvent2D[16], _sensorBegins = new ContactEvent2D[16], _sensorEnds = new ContactEvent2D[16];
    private int _beginCount, _endCount, _sensorBeginCount, _sensorEndCount;
    public ReadOnlySpan<ContactEvent2D> ContactBegins => _begins.AsSpan(0, _beginCount);
    public ReadOnlySpan<ContactEvent2D> ContactEnds => _ends.AsSpan(0, _endCount);
    public ReadOnlySpan<ContactEvent2D> SensorBegins => _sensorBegins.AsSpan(0, _sensorBeginCount);
    public ReadOnlySpan<ContactEvent2D> SensorEnds => _sensorEnds.AsSpan(0, _sensorEndCount);

    public PhysicsSettings2D Settings = PhysicsSettings2D.Default;
    /// <summary>Worker threads for the narrowphase and solver; null runs single-threaded (same results either way).</summary>
    public JobScheduler? Jobs;
    /// <summary>The most threads the solver uses (0: as many as the work warrants, up to every worker).</summary>
    public int MaxThreads;
    /// <summary>Colours the last step's contact graph used, and constraints that fit none (solved serially).</summary>
    public int ColorsUsed { get; private set; }
    public int OverflowConstraints { get; private set; }

    public int BodyCount => _bodyHigh - _freeBodyCount;
    public int ShapeCount => _shapeHigh - _freeShapeCount;
    public int ContactTotal => ContactCount;
    public int BodyCapacity => _bodyHigh;
    public int ShapeCapacity => _shapeHigh;
    /// <summary>Shapes whose fat boxes moved in the last step (each queried the trees for new pairs).</summary>
    public int MovedProxies { get; private set; }
    public int PairCandidates { get; private set; }
    public int AwakeBodyCount { get { int n = 0; for (int i = 0; i < _bodyHigh; i++) if (BodyAlive[i] && Awake[i] && Types[i] != BodyType.Static) n++; return n; } }
    /// <summary>Contacts that are touching and solved this step.</summary>
    public int TouchingCount { get; private set; }

    [Flags]
    internal enum ContactFlags : byte { Touching = 1, Sensor = 2, Destroy = 4, Began = 8, Ended = 16, Cached = 32, GraphDirty = 64 }

    internal struct FatPose
    {
        public Vector2 Center;
        public Rot Rotation;
        public float Slack; // the fat box's clearance around the shape's tight box at this pose
    }

    // Sequential layout matters: the solver loads AnchorA..AnchorB, Separation..TangentImpulse and Normal..Restitution
    // as 16-byte rows (SolverWide.PackBatch).
    [StructLayout(LayoutKind.Sequential)]
    internal struct ContactPoint
    {
        public Vector2 AnchorA, AnchorB; // relative to each body's center of mass
        public float Separation, BaseSeparation, NormalImpulse, TangentImpulse;
        public ushort Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Contact
    {
        public int ShapeA, ShapeB, BodyA, BodyB;
        public ContactFlags Flags;
        public Vector2 Normal;
        public float Friction, Restitution;
        public int Count;
        public ContactPoint P0, P1;
        // The relative pose the manifold was made at, for recycling.
        public Rot CachedQA, CachedQB, CachedTurn;
        public Vector2 CachedPose;
        public int VersionA, VersionB;
        public int GraphColor, GraphSlot; // colour + 1 (0: not in the graph) and place in its list
    }

    // ---------------------------------------------------------------- bodies

    public int CreateBody(Entity entity, BodyType type, Vector2 position, Rot rotation, bool isImplicit = false)
    {
        int b;
        if (_freeBodyCount > 0) b = _freeBodies[--_freeBodyCount];
        else
        {
            if (_bodyHigh == _bodyCapacity) GrowBodies(Math.Max(64, _bodyCapacity * 2));
            b = _bodyHigh++;
        }
        BodyEntity[b] = entity; Types[b] = type; BodyAlive[b] = true; Awake[b] = true; AllowSleep[b] = true; FixedRotationOf[b] = false; Implicit[b] = isImplicit;
        SleepTime[b] = 0; Center[b] = position; LocalCenter[b] = default; Rotation[b] = rotation; Velocity[b] = default; AngularVelocityOf[b] = 0;
        GravityScale[b] = 1; LinearDamping[b] = 0; AngularDamping[b] = 0; FirstShape[b] = -1; MassDirty[b] = true;
        Written[b] = position; WrittenRotation[b] = rotation;
        UpdateMass(b);
        return b;
    }

    public void DestroyBody(int b)
    {
        if (!IsAlive(b)) return;
        WakeTouching(b);
        while (FirstShape[b] >= 0) DestroyShape(FirstShape[b]);
        RemoveJointsOf(b);
        BodyAlive[b] = false;
        _recolor = true;
        BodyEntity[b] = default;
        if (_freeBodyCount == _freeBodies.Length) Array.Resize(ref _freeBodies, _freeBodies.Length * 2);
        _freeBodies[_freeBodyCount++] = b;
    }

    public bool IsAlive(int b) => (uint)b < (uint)_bodyHigh && BodyAlive[b];
    public Entity EntityOf(int b) => BodyEntity[b];
    public BodyType TypeOf(int b) => Types[b];
    public bool IsImplicit(int b) => Implicit[b];
    public bool IsAwake(int b) => Awake[b];
    /// <summary>Asleep since before the last step (its pose and velocity haven't changed since they were last written).</summary>
    public bool SleptBefore(int b) => !Awake[b] && !_justSlept[b];

    /// <summary>The body's origin (where its shapes are defined), not its center of mass.</summary>
    public Vector2 GetPosition(int b) => Center[b] - Rotation[b].Rotate(LocalCenter[b]);
    public Rot GetRotation(int b) => Rotation[b];
    public Vector2 GetCenterOfMass(int b) => Center[b];

    /// <summary>Move a body (a teleport): wakes it, and moves its shapes' boxes.</summary>
    public void SetTransform(int b, Vector2 origin, Rot rotation)
    {
        TransformVersion[b]++;
        Rotation[b] = rotation;
        Center[b] = origin + rotation.Rotate(LocalCenter[b]);
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) RefreshProxy(s, 0, force: true);
        Wake(b);
    }

    public Vector2 GetVelocity(int b) => Velocity[b];
    public float GetAngularVelocity(int b) => AngularVelocityOf[b];
    public void SetVelocity(int b, Vector2 v, float w)
    {
        if (Types[b] == BodyType.Static) return;
        if (FixedRotationOf[b]) w = 0;
        if (v != Velocity[b] || w != AngularVelocityOf[b])
        {
            Velocity[b] = v; AngularVelocityOf[b] = w;
            if (v != default || w != 0) Wake(b);
        }
    }

    public void SetType(int b, BodyType type)
    {
        if (Types[b] == type) return;
        Types[b] = type;
        _recolor = true;
        if (type == BodyType.Static) { Velocity[b] = default; AngularVelocityOf[b] = 0; }
        // Shapes change trees (static bodies' shapes live in the static tree).
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) { DestroyProxy(s); CreateProxy(s); }
        MassDirty[b] = true;
        Wake(b);
    }

    public void SetDamping(int b, float linear, float angular, float gravityScale) { LinearDamping[b] = linear; AngularDamping[b] = angular; GravityScale[b] = gravityScale; }
    public void SetFixedRotation(int b, bool fixedRotation) { if (FixedRotationOf[b] != fixedRotation) { FixedRotationOf[b] = fixedRotation; MassDirty[b] = true; } }
    public void SetAllowSleep(int b, bool allow) { AllowSleep[b] = allow; if (!allow) Wake(b); }

    /// <summary>Wake a body and everything touching it.</summary>
    public void Wake(int b)
    {
        if (!IsAlive(b) || Types[b] == BodyType.Static) return;
        SleepTime[b] = 0;
        if (!Awake[b]) _recolor = true;
        Awake[b] = true;
    }

    private void WakeTouching(int b)
    {
        for (int c = 0; c < ContactCount; c++)
        {
            ref var contact = ref Contacts[c];
            if ((contact.Flags & ContactFlags.Touching) == 0) continue;
            if (contact.BodyA == b) Wake(contact.BodyB);
            else if (contact.BodyB == b) Wake(contact.BodyA);
        }
    }

    /// <summary>Recompute mass, center of mass and inertia from the body's shapes (keeps the origin in place).</summary>
    internal void UpdateMass(int b)
    {
        MassDirty[b] = false;
        var origin = GetPosition(b);
        var oldCenter = LocalCenter[b];
        if (Types[b] != BodyType.Dynamic)
        {
            InvMass[b] = 0; InvInertia[b] = 0; Mass[b] = 0; LocalCenter[b] = default;
            Center[b] = origin;
            UpdateExtent(b, oldCenter);
            return;
        }
        float mass = 0, inertia = 0;
        var center = Vector2.Zero;
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
        {
            if (Sensor[s] || Density[s] <= 0) continue;
            var (m, i, c) = Geometry[s].MassData(Density[s]);
            mass += m; inertia += i; center += m * c;
        }
        if (mass > 0)
        {
            center /= mass;
            inertia -= mass * SimMath.Dot(center, center); // about the center of mass
        }
        else { mass = 1; inertia = 0; }
        Mass[b] = mass;
        InvMass[b] = 1 / mass;
        InvInertia[b] = inertia > 0 && !FixedRotationOf[b] ? 1 / inertia : 0;
        LocalCenter[b] = center;
        Center[b] = origin + Rotation[b].Rotate(center);
        UpdateExtent(b, oldCenter);
    }

    private void UpdateExtent(int b, Vector2 oldCenter)
    {
        var center = LocalCenter[b];
        float extent = 0;
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
        {
            ref readonly var g = ref Geometry[s];
            if (g.Kind == ShapeKind.Circle) extent = Math.Max(extent, SimMath.Length(g.Center - center) + g.Radius);
            else for (int i = 0; i < g.Count; i++) extent = Math.Max(extent, SimMath.Length(g.Vertex(i) - center) + g.Radius);
        }
        MaxExtent[b] = extent;
        TransformVersion[b]++; // shapes or mass changed: cached manifolds are stale
        // A moved centre of mass changes what Center means: the poses the fat boxes were fitted at no longer compare.
        if (LocalCenter[b] != oldCenter) for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) FatPoses[s].Rotation.C = float.NaN;
    }

    private void GrowBodies(int capacity)
    {
        Array.Resize(ref BodyEntity, capacity); Array.Resize(ref Types, capacity); Array.Resize(ref BodyAlive, capacity); Array.Resize(ref Awake, capacity);
        Array.Resize(ref AllowSleep, capacity); Array.Resize(ref FixedRotationOf, capacity); Array.Resize(ref Implicit, capacity); Array.Resize(ref SleepTime, capacity);
        Array.Resize(ref Center, capacity); Array.Resize(ref LocalCenter, capacity); Array.Resize(ref Velocity, capacity); Array.Resize(ref Rotation, capacity);
        Array.Resize(ref AngularVelocityOf, capacity); Array.Resize(ref InvMass, capacity); Array.Resize(ref InvInertia, capacity); Array.Resize(ref Mass, capacity);
        Array.Resize(ref GravityScale, capacity); Array.Resize(ref LinearDamping, capacity); Array.Resize(ref AngularDamping, capacity); Array.Resize(ref FirstShape, capacity);
        Array.Resize(ref Written, capacity); Array.Resize(ref WrittenRotation, capacity); Array.Resize(ref MassDirty, capacity); Array.Resize(ref _justSlept, capacity);
        Array.Resize(ref MaxExtent, capacity); Array.Resize(ref TransformVersion, capacity);
        GrowSolver(capacity);
        _bodyCapacity = capacity;
    }

    // ---------------------------------------------------------------- shapes

    public int CreateShape(int body, Entity entity, in Shape shape, float density, float friction, float restitution, bool sensor = false, Filter2D? filter = null)
    {
        int s;
        if (_freeShapeCount > 0) s = _freeShapes[--_freeShapeCount];
        else
        {
            if (_shapeHigh == _shapeCapacity) GrowShapes(Math.Max(64, _shapeCapacity * 2));
            s = _shapeHigh++;
        }
        ShapeBody[s] = body; ShapeEntity[s] = entity; Geometry[s] = shape; Density[s] = density; Friction[s] = friction; Restitution[s] = restitution;
        Sensor[s] = sensor; Filters[s] = filter ?? Filter2D.Default; ShapeAlive[s] = true; Moved[s] = false;
        Bound[s] = BoundOf(shape);
        NextShape[s] = FirstShape[body];
        FirstShape[body] = s;
        CreateProxy(s);
        MassDirty[body] = true;
        Wake(body);
        return s;
    }

    public void DestroyShape(int s)
    {
        if (!ShapeAlive[s]) return;
        int b = ShapeBody[s];
        // Contacts of this shape end now.
        for (int c = 0; c < ContactCount; c++)
            if (Contacts[c].ShapeA == s || Contacts[c].ShapeB == s) Contacts[c].Flags |= ContactFlags.Destroy;
        RemoveDestroyedContacts(emitEvents: true);
        DestroyProxy(s);
        if (FirstShape[b] == s) FirstShape[b] = NextShape[s];
        else
            for (int k = FirstShape[b]; k >= 0; k = NextShape[k])
                if (NextShape[k] == s) { NextShape[k] = NextShape[s]; break; }
        ShapeAlive[s] = false;
        MassDirty[b] = true;
        if (_freeShapeCount == _freeShapes.Length) Array.Resize(ref _freeShapes, _freeShapes.Length * 2);
        _freeShapes[_freeShapeCount++] = s;
    }

    public bool IsShapeAlive(int s) => (uint)s < (uint)_shapeHigh && ShapeAlive[s];
    public int BodyOfShape(int s) => ShapeBody[s];
    public Entity EntityOfShape(int s) => ShapeEntity[s];

    /// <summary>Change a shape's geometry and material (the body's mass is recomputed before the next step).</summary>
    public void UpdateShape(int s, in Shape shape, float density, float friction, float restitution, bool sensor, Filter2D filter)
    {
        Geometry[s] = shape; Density[s] = density; Friction[s] = friction; Restitution[s] = restitution; Filters[s] = filter;
        Bound[s] = BoundOf(shape);
        FatPoses[s].Rotation.C = float.NaN; // new geometry: test its box exactly
        if (Sensor[s] != sensor)
        {
            Sensor[s] = sensor;
            for (int c = 0; c < ContactCount; c++) if (Contacts[c].ShapeA == s || Contacts[c].ShapeB == s) Contacts[c].Flags |= ContactFlags.Destroy;
            RemoveDestroyedContacts(emitEvents: true);
        }
        MassDirty[ShapeBody[s]] = true;
        RefreshProxy(s, 0, force: true);
        Wake(ShapeBody[s]);
    }

    private static float BoundOf(in Shape shape)
    {
        float r = 0;
        for (int i = 0; i < shape.Count; i++) r = Math.Max(r, SimMath.Length(shape.Vertex(i) - shape.Center));
        return r + shape.Radius;
    }

    private void GrowShapes(int capacity)
    {
        Array.Resize(ref ShapeBody, capacity); Array.Resize(ref NextShape, capacity); Array.Resize(ref Proxy, capacity); Array.Resize(ref Geometry, capacity);
        Array.Resize(ref Density, capacity); Array.Resize(ref Friction, capacity); Array.Resize(ref Restitution, capacity); Array.Resize(ref Sensor, capacity);
        Array.Resize(ref ShapeAlive, capacity); Array.Resize(ref InStaticTree, capacity); Array.Resize(ref Moved, capacity); Array.Resize(ref Filters, capacity);
        Array.Resize(ref ShapeEntity, capacity); Array.Resize(ref Fat, capacity); Array.Resize(ref FatPoses, capacity); Array.Resize(ref Sources, capacity); Array.Resize(ref Bound, capacity);
        _shapeCapacity = capacity;
    }

    private Aabb ShapeBox(int s)
    {
        int b = ShapeBody[s];
        return Geometry[s].Bounds(GetPosition(b), Rotation[b]);
    }

    private void CreateProxy(int s)
    {
        int b = ShapeBody[s];
        var box = ShapeBox(s);
        box.Min -= new Vector2(AabbMargin); box.Max += new Vector2(AabbMargin);
        Fat[s] = box;
        FatPoses[s] = new FatPose { Center = Center[b], Rotation = Rotation[b], Slack = AabbMargin };
        InStaticTree[s] = Types[b] == BodyType.Static;
        Proxy[s] = (InStaticTree[s] ? _staticTree : _movingTree).Create(box, s);
        QueueMove(s);
    }

    private void DestroyProxy(int s) => (InStaticTree[s] ? _staticTree : _movingTree).Destroy(Proxy[s]);

    private void QueueMove(int s)
    {
        if (Moved[s]) return;
        Moved[s] = true;
        if (_moveCount == _moveBuffer.Length) Array.Resize(ref _moveBuffer, _moveBuffer.Length * 2);
        _moveBuffer[_moveCount++] = s;
    }

    private bool[] _needsMove = [];
    private ulong[] _moveBits = new ulong[4]; // bodies with a shape that left its fat box this step
    private Aabb[] _swept = [];

    /// <summary>The swept box of a shape, and whether it left its fat box (no tree changes: safe in parallel).</summary>
    private bool Sweep(int s, float dt)
    {
        // Every point of the shape has moved at most |Δc| + extent·|Δq| since the reference pose (|Δq| being the chord
        // between the two rotations), and the swept box adds this step's motion: while all that stays well inside the
        // fat box's clearance at that pose, the box is certainly still contained and the shape's geometry is never
        // touched. Conservative (L1 norms, a quarter of the clearance to spare), so the outcome is the exact test's.
        int b = ShapeBody[s];
        ref var pose = ref FatPoses[s];
        var dc = Center[b] - pose.Center; var q = Rotation[b]; var v = Velocity[b] * dt;
        float moved = MathF.Abs(dc.X) + MathF.Abs(dc.Y) + MaxExtent[b] * (MathF.Abs(q.C - pose.Rotation.C) + MathF.Abs(q.S - pose.Rotation.S))
            + MathF.Abs(v.X) + MathF.Abs(v.Y);
        if (moved < 0.75f * pose.Slack) { _needsMove[s] = false; return false; }
        var tight = ShapeBox(s);
        var box = tight;
        box.Min = SimMath.Min(box.Min, box.Min + v);
        box.Max = SimMath.Max(box.Max, box.Max + v);
        ref readonly var fat = ref Fat[s];
        _swept[s] = box;
        // An invalidated pose (NaN) refits once, so the shortcut above works again.
        bool leaves = !(fat.Min.X <= box.Min.X && fat.Min.Y <= box.Min.Y && fat.Max.X >= box.Max.X && fat.Max.Y >= box.Max.Y) || float.IsNaN(pose.Rotation.C);
        if (!leaves)
        {
            // Still inside: measure from here, so a body that drifts slowly within its box keeps taking the shortcut.
            // (Less a few ulps of the coordinates, for the rounding in the exact boxes.)
            float slack = F.Min(F.Min(tight.Min.X - fat.Min.X, tight.Min.Y - fat.Min.Y), F.Min(fat.Max.X - tight.Max.X, fat.Max.Y - tight.Max.Y));
            slack -= 1e-6f * (MathF.Abs(tight.Max.X) + MathF.Abs(tight.Max.Y) + 1);
            pose = new FatPose { Center = Center[b], Rotation = q, Slack = slack };
        }
        return _needsMove[s] = leaves;
    }

    private struct SweepJob : IJobFor
    {
        public PhysicsWorld2D World;
        public float Dt;
        public readonly void Execute(int start, int end)
        {
            var w = World;
            ulong word = 0;
            for (int b = start; b < end; b++)
            {
                if (w.BodyAlive[b] && w.Awake[b] && w.Types[b] != BodyType.Static)
                {
                    bool any = false;
                    for (int s = w.FirstShape[b]; s >= 0; s = w.NextShape[s]) any |= w.Sweep(s, Dt);
                    if (any) word |= 1UL << b;
                }
                // Batches are multiples of 64 bodies, so each owns its words.
                if ((b & 63) == 63 || b == end - 1) { if (word != 0) w._moveBits[b >> 6] |= word; word = 0; }
            }
        }
    }

    /// <summary>Re-fit a shape's fat box if it left it (or always, when forced), grown along the body's motion this step.</summary>
    private void RefreshProxy(int s, float dt, bool force)
    {
        // The box swept through this step's motion: a fast body's pairs must exist before it gets there.
        var box = ShapeBox(s);
        var reach = Velocity[ShapeBody[s]] * dt;
        box.Min = SimMath.Min(box.Min, box.Min + reach);
        box.Max = SimMath.Max(box.Max, box.Max + reach);
        ref var fat = ref Fat[s];
        if (!force && fat.Min.X <= box.Min.X && fat.Min.Y <= box.Min.Y && fat.Max.X >= box.Max.X && fat.Max.Y >= box.Max.Y) return;
        box.Min -= new Vector2(AabbMargin);
        box.Max += new Vector2(AabbMargin);
        fat = box;
        FatPoses[s] = new FatPose { Center = Center[ShapeBody[s]], Rotation = Rotation[ShapeBody[s]], Slack = AabbMargin };
        (InStaticTree[s] ? _staticTree : _movingTree).Move(Proxy[s], box);
        QueueMove(s);
    }

    // ---------------------------------------------------------------- step

    private static readonly ProfileMarker PairsMarker = ProfileMarker.Get("Physics2D.Broadphase", ProfileCategory.Engine);
    private static readonly ProfileMarker CollideMarker = ProfileMarker.Get("Physics2D.Narrowphase", ProfileCategory.Engine);
    private static readonly ProfileMarker IslandMarker = ProfileMarker.Get("Physics2D.Islands", ProfileCategory.Engine);
    private static readonly ProfileMarker SolveMarker = ProfileMarker.Get("Physics2D.Solve", ProfileCategory.Engine);

    /// <summary>Advance by <paramref name="dt"/> seconds.</summary>
    public void Step(float dt)
    {
        if (dt <= 0) return;
        _beginCount = _endCount = _sensorBeginCount = _sensorEndCount = 0;
        for (int b = 0; b < _bodyHigh; b++) if (BodyAlive[b] && MassDirty[b]) UpdateMass(b);
        using (PairsMarker.Scope()) UpdatePairs(dt);
        using (CollideMarker.Scope()) Collide(dt);
        using (IslandMarker.Scope()) BuildIslands();
        using (SolveMarker.Scope()) Solve(dt);
        using (IslandMarker.Scope()) UpdateSleep(dt);
    }

    private struct PairQuery : ITreeQuery
    {
        public PhysicsWorld2D World;
        public int Shape, Thread;
        public bool Hit(int proxy, int other) { World.ConsiderPair(Shape, other, Thread); return true; }
    }

    // Candidate pairs per query thread, merged and sorted after.
    private ulong[][] _threadPairs = [new ulong[64]];
    private int[] _threadPairCount = new int[1];

    private struct QueryJob : IJobThreads
    {
        public PhysicsWorld2D World;
        public readonly void Execute(int thread, int threadCount)
        {
            var w = World;
            int start = (int)((long)w._moveCount * thread / threadCount), end = (int)((long)w._moveCount * (thread + 1) / threadCount);
            var query = new PairQuery { World = w, Thread = thread };
            w._threadPairCount[thread] = 0;
            for (int k = start; k < end; k++)
            {
                int s = w._moveBuffer[k];
                if (!w.ShapeAlive[s]) continue;
                query.Shape = s;
                if (!w.InStaticTree[s]) w._staticTree.Query(w.Fat[s], ref query);
                w._movingTree.Query(w.Fat[s], ref query);
            }
        }
    }

    /// <summary>Fat boxes of moving shapes, then new pairs for every moved shape, created in sorted order.</summary>
    private void UpdatePairs(float dt)
    {
        // Boxes in parallel; the few that left their fat boxes move in the trees serially, in shape order.
        if (_needsMove.Length < _shapeCapacity) { _needsMove = new bool[_shapeCapacity]; _swept = new Aabb[_shapeCapacity]; }
        var sweep = new SweepJob { World = this, Dt = dt };
        int bodyWords = (_bodyHigh + 63) >> 6;
        if (_moveBits.Length < bodyWords) _moveBits = new ulong[bodyWords * 2];
        if (Jobs is { } jobs) jobs.ParallelFor(_bodyHigh, 256, ref sweep); else sweep.Execute(0, _bodyHigh);
        // Only bodies the sweep flagged, in body order.
        for (int bw = 0; bw < bodyWords; bw++)
        {
            ulong bits = _moveBits[bw];
            _moveBits[bw] = 0;
            while (bits != 0)
            {
                int b = (bw << 6) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
                {
                    if (!_needsMove[s]) continue;
                    // Predict a few steps of motion ahead (Box2D v2's multiplier), so a moving body leaves its fat box
                    // every few steps instead of every step: each exit costs a tree move and a query.
                    var box = _swept[s];
                    var ahead = Velocity[b] * (dt * (AabbPrediction - 1));
                    box.Min = SimMath.Min(box.Min, box.Min + ahead) - new Vector2(AabbMargin);
                    box.Max = SimMath.Max(box.Max, box.Max + ahead) + new Vector2(AabbMargin);
                    Fat[s] = box;
                    FatPoses[s] = new FatPose { Center = Center[b], Rotation = Rotation[b], Slack = AabbMargin };
                    (InStaticTree[s] ? _staticTree : _movingTree).Move(Proxy[s], box);
                    QueueMove(s);
                }
            }
        }
        MovedProxies = _moveCount;
        // Queries in shape order, so pairs are found and created the same way every run.
        Sort.Heap(_moveBuffer.AsSpan(0, _moveCount));
        // Tree queries in parallel (the trees and pair table are only read), each thread into its own list.
        int threads = Jobs is { } jobs2 ? Math.Clamp(_moveCount / 64, 1, jobs2.WorkerCount + 1) : 1;
        if (_threadPairs.Length < threads)
        {
            var grown = new ulong[threads][];
            for (int t = 0; t < threads; t++) grown[t] = t < _threadPairs.Length ? _threadPairs[t] : new ulong[64];
            _threadPairs = grown;
            _threadPairCount = new int[threads];
        }
        var queries = new QueryJob { World = this };
        if (threads > 1) Jobs!.RunOnThreads(threads, ref queries); else queries.Execute(0, 1);
        _newPairCount = 0;
        for (int t = 0; t < threads; t++)
        {
            int n = _threadPairCount[t];
            if (_newPairCount + n > _newPairs.Length) Array.Resize(ref _newPairs, Math.Max(_newPairs.Length * 2, _newPairCount + n));
            Array.Copy(_threadPairs[t], 0, _newPairs, _newPairCount, n);
            _newPairCount += n;
        }
        for (int k = 0; k < _moveCount; k++) Moved[_moveBuffer[k]] = false;
        _moveCount = 0;
        PairCandidates = _newPairCount;
        var pairs = _newPairs.AsSpan(0, _newPairCount);
        Sort.Heap(pairs);
        ulong last = ulong.MaxValue;
        foreach (ulong key in pairs)
        {
            if (key == last) continue;
            last = key;
            AddContact((int)(key >> 32), (int)(uint)key);
        }
    }

    private void ConsiderPair(int s, int other, int thread)
    {
        if (other == s) return;
        // Both moved: the lower shape's query reports the pair.
        if (Moved[other] && other < s) return;
        int a = ShapeBody[s], b = ShapeBody[other];
        if (a == b) return;
        if (Types[a] != BodyType.Dynamic && Types[b] != BodyType.Dynamic) return;
        if (!Filters[s].Accepts(Filters[other])) return;
        if (_noCollide.Count > 0 && _noCollide.Contains(BodyPairKey(a, b))) return;
        ulong key = s < other ? (ulong)(uint)s << 32 | (uint)other : (ulong)(uint)other << 32 | (uint)s;
        if (_pairs.ContainsKey(key)) return; // already a contact
        ref var list = ref _threadPairs[thread];
        ref int count = ref _threadPairCount[thread];
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = key;
    }

    internal static ulong BodyPairKey(int a, int b) => a < b ? (ulong)(uint)a << 32 | (uint)b : (ulong)(uint)b << 32 | (uint)a;

    private void AddContact(int shapeA, int shapeB)
    {
        if (ContactCount == Contacts.Length) Array.Resize(ref Contacts, Contacts.Length * 2);
        int c = ContactCount++;
        Contacts[c] = new Contact
        {
            ShapeA = shapeA, ShapeB = shapeB, BodyA = ShapeBody[shapeA], BodyB = ShapeBody[shapeB],
            Flags = Sensor[shapeA] || Sensor[shapeB] ? ContactFlags.Sensor : 0,
            Friction = SimMath.Sqrt(Friction[shapeA] * Friction[shapeB]),
            Restitution = Math.Max(Restitution[shapeA], Restitution[shapeB]),
        };
        _pairs[(ulong)(uint)shapeA << 32 | (uint)shapeB] = c;
    }

    private struct CollideJob : IJobFor
    {
        public PhysicsWorld2D World;
        public float Dt;
        public readonly void Execute(int start, int end)
        {
            var w = World;
            var contacts = w.Contacts; var types = w.Types; var awake = w.Awake;
            ulong word = 0;
            for (int c = start; c < end; c++)
            {
                ref var contact = ref contacts[c];
                int a = contact.BodyA, b = contact.BodyB;
                var typeA = types[a]; var typeB = types[b]; bool awakeA = awake[a], awakeB = awake[b];
                if ((awakeA && typeA != BodyType.Static) || (awakeB && typeB != BodyType.Static)) w.UpdateContact(ref contact, a, b, Dt);
                // Graph membership changes (see Solvable), from the bodies' state already in hand.
                bool solvable = (contact.Flags & (ContactFlags.Touching | ContactFlags.Sensor | ContactFlags.Destroy)) == ContactFlags.Touching
                    && ((typeA == BodyType.Dynamic && awakeA) || (typeB == BodyType.Dynamic && awakeB));
                if (solvable != (contact.GraphColor != 0)) contact.Flags |= ContactFlags.GraphDirty;
                if ((contact.Flags & Changes) != 0) word |= 1UL << c;
                if ((c & 63) == 63 || c == end - 1) { if (word != 0) w._contactBits[c >> 6] |= word; word = 0; }
            }
        }
    }

    /// <summary>Narrowphase for every contact (in parallel: each writes only itself), then events and removals in contact order.</summary>
    private void Collide(float dt)
    {
        int words = (ContactCount + 63) >> 6;
        if (_contactBits.Length < words) _contactBits = new ulong[words * 2];
        var job = new CollideJob { World = this, Dt = dt };
        // Batches of 64: each batch owns one word of _contactBits, so the flags need no synchronisation.
        if (Jobs is { } jobs) jobs.ParallelFor(ContactCount, 64, ref job);
        else job.Execute(0, ContactCount);
        // Only flagged contacts (began, ended, destroyed, or changing colour) need the serial pass; in contact order.
        _destroyedCount = 0;
        for (int w = 0; w < words; w++)
        {
            ulong bits = _contactBits[w];
            _contactBits[w] = 0;
            while (bits != 0)
            {
                int c = (w << 6) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ContactChanged(c);
            }
        }
        // Destroyed contacts go highest first, so each swap brings in a contact that stays.
        for (int k = _destroyedCount - 1; k >= 0; k--) RemoveContact(_destroyed[k]);
    }

    private const ContactFlags Changes = ContactFlags.Began | ContactFlags.Ended | ContactFlags.Destroy | ContactFlags.GraphDirty;
    private ulong[] _contactBits = new ulong[4];
    private int[] _destroyed = new int[16];
    private int _destroyedCount;

    private void ContactChanged(int c)
    {
        ref var contact = ref Contacts[c];
        if ((contact.Flags & ContactFlags.GraphDirty) != 0)
        {
            contact.Flags &= ~ContactFlags.GraphDirty;
            if (!_recolor) { if (contact.GraphColor != 0) RemoveFromGraph(c); else AddToGraph(c); }
        }
        if ((contact.Flags & ContactFlags.Began) != 0)
        {
            contact.Flags &= ~ContactFlags.Began;
            if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorBegins, ref _sensorBeginCount, SensorEvent(contact));
            else
            {
                Push(ref _begins, ref _beginCount, BeginEvent(contact));
                // Something awake touched something asleep: wake it.
                if (!Awake[contact.BodyA]) Wake(contact.BodyA);
                if (!Awake[contact.BodyB]) Wake(contact.BodyB);
            }
        }
        if ((contact.Flags & ContactFlags.Ended) != 0)
        {
            contact.Flags &= ~ContactFlags.Ended;
            if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorEnds, ref _sensorEndCount, SensorEvent(contact));
            else Push(ref _ends, ref _endCount, new ContactEvent2D { A = ShapeEntity[contact.ShapeA], B = ShapeEntity[contact.ShapeB] });
        }
        if ((contact.Flags & ContactFlags.Destroy) != 0)
        {
            if (_destroyedCount == _destroyed.Length) Array.Resize(ref _destroyed, _destroyedCount * 2);
            _destroyed[_destroyedCount++] = c;
        }
    }

    private ContactEvent2D SensorEvent(in Contact c) => Sensor[c.ShapeA]
        ? new ContactEvent2D { A = ShapeEntity[c.ShapeA], B = ShapeEntity[c.ShapeB] }
        : new ContactEvent2D { A = ShapeEntity[c.ShapeB], B = ShapeEntity[c.ShapeA] };

    private ContactEvent2D BeginEvent(in Contact c)
    {
        var p = Center[c.BodyA] + c.P0.AnchorA;
        var dv = Velocity[c.BodyB] + V.Cross(AngularVelocityOf[c.BodyB], c.P0.AnchorB) - Velocity[c.BodyA] - V.Cross(AngularVelocityOf[c.BodyA], c.P0.AnchorA);
        return new ContactEvent2D { A = ShapeEntity[c.ShapeA], B = ShapeEntity[c.ShapeB], Point = p, Normal = c.Normal, Speed = -V.Dot(dv, c.Normal) };
    }

    private static void Push(ref ContactEvent2D[] list, ref int count, in ContactEvent2D e)
    {
        if (count == list.Length) Array.Resize(ref list, list.Length * 2);
        list[count++] = e;
    }

    /// <summary>One contact's narrowphase: overlap test for sensors, a manifold (keeping matching impulses) otherwise.</summary>
    /// <summary>Called only when at least one of the bodies can have moved (otherwise the contact stays as it is).</summary>
    internal void UpdateContact(ref Contact contact, int a, int b, float dt)
    {
        if (!Fat[contact.ShapeA].Overlaps(Fat[contact.ShapeB]) || (_noCollide.Count > 0 && _noCollide.Contains(BodyPairKey(a, b))))
        {
            if ((contact.Flags & ContactFlags.Touching) != 0) contact.Flags |= ContactFlags.Ended;
            contact.Flags = (contact.Flags & ~ContactFlags.Touching) | ContactFlags.Destroy;
            return;
        }
        bool sensor = (contact.Flags & ContactFlags.Sensor) != 0;
        bool was = (contact.Flags & ContactFlags.Touching) != 0;
        if (!sensor && TryRecycle(ref contact, a, b, was)) return;
        UpdateManifold(ref contact, a, b, dt, sensor, was);
    }

    /// <summary>
    /// The slow path, kept out of <see cref="UpdateContact"/> so the common recycled case doesn't pay for its frame
    /// (a manifold on the stack, zeroed on every call).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void UpdateManifold(ref Contact contact, int a, int b, float dt, bool sensor, bool was)
    {
        var pa = GetPosition(a); var pb = GetPosition(b);
        float margin = sensor ? 0 : Speculative + SimMath.Length(Velocity[a] - Velocity[b]) * dt;
        // Bounding circles first: about a third of contacts are fat boxes overlapping with the shapes apart.
        Manifold m = default;
        ref readonly var ga = ref Geometry[contact.ShapeA]; ref readonly var gb = ref Geometry[contact.ShapeB];
        var gap = (pb + Rotation[b].Rotate(gb.Center)) - (pa + Rotation[a].Rotate(ga.Center));
        float reach = Bound[contact.ShapeA] + Bound[contact.ShapeB] + margin;
        if (V.Dot(gap, gap) <= reach * reach)
            Collision.Collide(ga, pa, Rotation[a], gb, pb, Rotation[b], margin, out m);
        bool touching;
        if (sensor)
        {
            touching = m.Count > 0 && (m.P0.Separation < 0 || (m.Count > 1 && m.P1.Separation < 0));
        }
        else
        {
            touching = m.Count > 0;
            // Anchors relative to the centers of mass; keep impulses of points whose features persist.
            var ca = Center[a] - pa; var cb = Center[b] - pb;
            var old0 = contact.P0; var old1 = contact.P1; int oldCount = contact.Count;
            contact.Normal = m.Normal;
            contact.Count = m.Count;
            contact.P0 = Point(m.P0, ca, cb, old0, old1, oldCount);
            contact.P1 = m.Count > 1 ? Point(m.P1, ca, cb, old0, old1, oldCount) : default;
            contact.P0.BaseSeparation = contact.P0.Separation;
            contact.P1.BaseSeparation = contact.P1.Separation;
            // Remember the relative pose this manifold was made at (contact recycling).
            contact.CachedQA = Rotation[a]; contact.CachedQB = Rotation[b];
            contact.CachedPose = Rotation[a].InvRotate(Center[b] - Center[a]); // between centres of mass (LocalCenter changes bump the versions)
            contact.CachedTurn = Rot.InvMul(Rotation[a], Rotation[b]);
            contact.VersionA = TransformVersion[a]; contact.VersionB = TransformVersion[b];
            contact.Flags |= ContactFlags.Cached;
        }
        if (touching && !was) contact.Flags |= ContactFlags.Touching | ContactFlags.Began;
        else if (!touching && was) contact.Flags = (contact.Flags & ~ContactFlags.Touching) | ContactFlags.Ended;
    }

    private const float RecycleDistance = 10 * LinearSlop, RecycleCos = 0.98f;

    /// <summary>
    /// Contact recycling, from Box2D v3 (Erin Catto, physics_world.c: "Contact recycling optimization. Please cite this
    /// code if you use this optimization."): when the bodies have barely moved relative to each other since the manifold
    /// was made — translation plus each body's largest shape extent times the turn, under a tolerance — keep the manifold
    /// (anchors, normal, features) and only update each point's separation from the motion, as the substep solver does.
    /// In a resting pile nearly every contact recycles, which skips the polygon clipping.
    /// </summary>
    private bool TryRecycle(ref Contact contact, int a, int b, bool wasTouching)
    {
        if ((contact.Flags & ContactFlags.Cached) == 0 || contact.VersionA != TransformVersion[a] || contact.VersionB != TransformVersion[b]) return false;
        var qa = Rotation[a]; var qb = Rotation[b];
        // Each body's turn since then, as a cosine.
        float cosA = qa.C * contact.CachedQA.C + qa.S * contact.CachedQA.S;
        float cosB = qb.C * contact.CachedQB.C + qb.S * contact.CachedQB.S;
        if (F.Min(cosA, cosB) <= RecycleCos) return false;
        var dc = Center[b] - Center[a];
        var pose = qa.InvRotate(dc);
        var turn = Rot.InvMul(qa, qb);
        var dTurn = Rot.InvMul(turn, contact.CachedTurn);
        float extent = F.Max(Types[a] == BodyType.Static ? 0 : MaxExtent[a], Types[b] == BodyType.Static ? 0 : MaxExtent[b]);
        float tolerance = wasTouching ? RecycleDistance : F.Min(RecycleDistance, Speculative);
        if (SimMath.Distance(pose, contact.CachedPose) + extent * MathF.Abs(dTurn.S) >= tolerance) return false;
        var dqA = Rot.InvMul(contact.CachedQA, qa); var dqB = Rot.InvMul(contact.CachedQB, qb);
        // Anchors are relative to each center: (cB − cA) + rB − rA was zero when the manifold was made.
        for (int k = 0; k < contact.Count; k++)
        {
            ref var p = ref k == 0 ? ref contact.P0 : ref contact.P1;
            var d = dc + (dqB.Rotate(p.AnchorB) - dqA.Rotate(p.AnchorA));
            p.Separation = p.BaseSeparation + V.Dot(d, contact.Normal);
        }
        return true;
    }

    private static ContactPoint Point(in ManifoldPoint mp, Vector2 ca, Vector2 cb, in ContactPoint old0, in ContactPoint old1, int oldCount)
    {
        var p = new ContactPoint { AnchorA = mp.AnchorA - ca, AnchorB = mp.AnchorB - cb, Separation = mp.Separation, Id = mp.Id };
        if (oldCount > 0 && old0.Id == mp.Id) { p.NormalImpulse = old0.NormalImpulse; p.TangentImpulse = old0.TangentImpulse; }
        else if (oldCount > 1 && old1.Id == mp.Id) { p.NormalImpulse = old1.NormalImpulse; p.TangentImpulse = old1.TangentImpulse; }
        return p;
    }

    /// <summary>
    /// Drop destroyed contacts by moving the last contact into each hole, walking backwards (so whatever moves in has
    /// already been looked at). Each removal touches one dictionary entry, not every contact after it.
    /// </summary>
    private void RemoveDestroyedContacts(bool emitEvents)
    {
        if (emitEvents) _recolor = true; // outside the step: graph contacts may go without passing through the narrowphase
        for (int c = ContactCount - 1; c >= 0; c--)
        {
            ref var contact = ref Contacts[c];
            if ((contact.Flags & ContactFlags.Destroy) == 0) continue;
            if (emitEvents && (contact.Flags & ContactFlags.Touching) != 0)
            {
                var e = (contact.Flags & ContactFlags.Sensor) != 0 ? SensorEvent(contact) : new ContactEvent2D { A = ShapeEntity[contact.ShapeA], B = ShapeEntity[contact.ShapeB] };
                if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorEnds, ref _sensorEndCount, e);
                else Push(ref _ends, ref _endCount, e);
            }
            RemoveContact(c);
        }
    }

    /// <summary>Drop contact <paramref name="c"/>, moving the last contact into its place.</summary>
    private void RemoveContact(int c)
    {
        ref readonly var contact = ref Contacts[c];
        _pairs.Remove((ulong)(uint)contact.ShapeA << 32 | (uint)contact.ShapeB);
        int last = --ContactCount;
        if (c != last)
        {
            Contacts[c] = Contacts[last];
            MovedInGraph(last, c);
            _pairs[(ulong)(uint)Contacts[c].ShapeA << 32 | (uint)Contacts[c].ShapeB] = c;
        }
    }

    // ---------------------------------------------------------------- queries

    private struct RayQuery : ITreeQuery
    {
        public PhysicsWorld2D World;
        public Vector2 Origin, Translation;
        public float Fraction;
        public int Shape;
        public Vector2 Normal;
        public uint Mask;
        public bool Hit(int proxy, int s)
        {
            if (World.Sensor[s] || (World.Filters[s].Category & Mask) == 0) return true;
            int b = World.ShapeBody[s];
            var q = World.Rotation[b]; var p = World.GetPosition(b);
            if (ShapeRay.Cast(World.Geometry[s], q.InvRotate(Origin - p), q.InvRotate(Translation), Fraction, out float f, out var n) && f < Fraction)
            {
                Fraction = f; Shape = s; Normal = q.Rotate(n);
            }
            return true;
        }
    }

    /// <summary>The closest collider hit by the segment from <paramref name="origin"/> to <paramref name="origin"/> + <paramref name="translation"/>.</summary>
    public bool RayCast(Vector2 origin, Vector2 translation, out RayHit2D hit, uint mask = uint.MaxValue)
    {
        var q = new RayQuery { World = this, Origin = origin, Translation = translation, Fraction = 1, Shape = -1, Mask = mask };
        _staticTree.RayQuery(origin, translation, 1, ref q);
        _movingTree.RayQuery(origin, translation, q.Fraction, ref q);
        hit = default;
        if (q.Shape < 0) return false;
        hit = new RayHit2D { Entity = ShapeEntity[q.Shape], Fraction = q.Fraction, Point = origin + translation * q.Fraction, Normal = q.Normal };
        return true;
    }

    private struct BoxQuery : ITreeQuery
    {
        public PhysicsWorld2D World;
        public Aabb Box;
        public Entity[] Results;
        public int Count;
        public bool Hit(int proxy, int s)
        {
            if (!World.ShapeBox(s).Overlaps(Box)) return true;
            if (Count < Results.Length) Results[Count] = World.ShapeEntity[s];
            Count++;
            return true;
        }
    }

    private Entity[] _boxResults = new Entity[64];

    /// <summary>Colliders whose bounds overlap the box; writes up to <c>results.Length</c> and returns how many matched.</summary>
    public int OverlapBox(Vector2 min, Vector2 max, Span<Entity> results)
    {
        if (_boxResults.Length < results.Length) _boxResults = new Entity[results.Length];
        var q = new BoxQuery { World = this, Box = new Aabb { Min = min, Max = max }, Results = _boxResults };
        _staticTree.Query(q.Box, ref q);
        _movingTree.Query(q.Box, ref q);
        _boxResults.AsSpan(0, Math.Min(q.Count, results.Length)).CopyTo(results);
        return q.Count;
    }

    // ---------------------------------------------------------------- snapshots

    /// <summary>A deep copy (play-mode checkpoints, rollback).</summary>
    public PhysicsWorld2D Clone()
    {
        var w = (PhysicsWorld2D)MemberwiseClone();
        static T[] C<T>(T[] a) => (T[])a.Clone();
        w._freeBodies = C(_freeBodies); w.BodyEntity = C(BodyEntity); w.Types = C(Types); w.BodyAlive = C(BodyAlive); w.Awake = C(Awake);
        w.AllowSleep = C(AllowSleep); w.FixedRotationOf = C(FixedRotationOf); w.Implicit = C(Implicit); w.SleepTime = C(SleepTime);
        w.Center = C(Center); w.LocalCenter = C(LocalCenter); w.Velocity = C(Velocity); w.Rotation = C(Rotation); w.AngularVelocityOf = C(AngularVelocityOf);
        w.InvMass = C(InvMass); w.InvInertia = C(InvInertia); w.Mass = C(Mass); w.GravityScale = C(GravityScale); w.LinearDamping = C(LinearDamping);
        w.AngularDamping = C(AngularDamping); w.FirstShape = C(FirstShape); w.Written = C(Written); w.WrittenRotation = C(WrittenRotation); w.MassDirty = C(MassDirty); w._justSlept = C(_justSlept); w.MaxExtent = C(MaxExtent); w.TransformVersion = C(TransformVersion);
        w._freeShapes = C(_freeShapes); w.ShapeBody = C(ShapeBody); w.NextShape = C(NextShape); w.Proxy = C(Proxy); w.Geometry = C(Geometry);
        w.Density = C(Density); w.Friction = C(Friction); w.Restitution = C(Restitution); w.Sensor = C(Sensor); w.ShapeAlive = C(ShapeAlive);
        w.InStaticTree = C(InStaticTree); w.Moved = C(Moved); w.Filters = C(Filters); w.ShapeEntity = C(ShapeEntity); w.Fat = C(Fat); w.FatPoses = C(FatPoses); w.Sources = C(Sources); w.Bound = C(Bound); w._needsMove = new bool[_needsMove.Length]; w._swept = new Aabb[_swept.Length];
        w._staticTree = _staticTree.Clone(); w._movingTree = _movingTree.Clone(); w._moveBuffer = C(_moveBuffer); w._newPairs = C(_newPairs);
        w._threadPairs = new ulong[_threadPairs.Length][]; for (int t = 0; t < _threadPairs.Length; t++) w._threadPairs[t] = new ulong[_threadPairs[t].Length];
        w._threadPairCount = new int[_threadPairCount.Length];
        w.Contacts = C(Contacts); w._contactBits = new ulong[_contactBits.Length]; w._destroyed = new int[_destroyed.Length]; w._moveBits = new ulong[_moveBits.Length]; w._pairs = new Dictionary<ulong, int>(_pairs, PairComparer.Instance); w._noCollide = new HashSet<ulong>(_noCollide, PairComparer.Instance);
        w._begins = C(_begins); w._ends = C(_ends); w._sensorBegins = C(_sensorBegins); w._sensorEnds = C(_sensorEnds); w._boxResults = new Entity[_boxResults.Length];
        CloneSolver(w);
        CloneJoints(w);
        return w;
    }
}

/// <summary>
/// Hashes pair keys (two indices packed in a ulong). ulong's own hash XORs the halves, so neighbouring pairs, which have
/// near-equal indices, collide; a multiplicative mix spreads them.
/// </summary>
internal sealed class PairComparer : IEqualityComparer<ulong>
{
    public static readonly PairComparer Instance = new();
    public bool Equals(ulong a, ulong b) => a == b;
    public int GetHashCode(ulong key) => (int)((key * 0x9E3779B97F4A7C15UL) >> 32);
}
