// Ported from Box2D (https://github.com/erincatto/box2d), Copyright (c) 2022 Erin Catto, under the MIT License;
// see THIRD_PARTY_NOTICES.md.
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
    /// <summary>Stop fast bodies tunnelling through static geometry (bullets: through everything) by time of impact.</summary>
    public bool EnableContinuous;
    /// <summary>How long every body of an island must stay below its sleep threshold before the island sleeps, in seconds.</summary>
    public float TimeToSleep;

    public static PhysicsSettings2D Default => new()
    {
        Gravity = new Vector2(0, -10), Substeps = 4, ContactHertz = 30, ContactDampingRatio = 10, JointHertz = 60, JointDampingRatio = 2,
        MaxPushout = 3, RestitutionThreshold = 1, EnableSleep = true, EnableContinuous = true, TimeToSleep = 0.5f,
    };
}

/// <summary>Everything a new body starts with (Box2D's body definition).</summary>
public struct BodyDef2D
{
    public BodyType Type;
    /// <summary>The body's origin (where its shapes are defined) and rotation.</summary>
    public Vector2 Position;
    public Rot Rotation;
    public Vector2 LinearVelocity;
    public float AngularVelocity;
    public float LinearDamping, AngularDamping, GravityScale;
    /// <summary>Speed (m/s, including turning at the body's extent) below which the body counts as resting.</summary>
    public float SleepThreshold;
    public bool EnableSleep;
    /// <summary>Start asleep (a settled level loads at rest, and costs nothing until something touches it).</summary>
    public bool IsAwake;
    public bool FixedRotation;
    /// <summary>A fast body that mustn't tunnel through other moving bodies either (costs a time-of-impact pass against them).</summary>
    public bool IsBullet;
    /// <summary>A collider with no body of its own gets an implicit static body (ECS scenery).</summary>
    public bool IsImplicit;

    public static BodyDef2D Default => new()
    {
        Type = BodyType.Static, Rotation = Rot.Identity, GravityScale = 1, SleepThreshold = 0.05f, EnableSleep = true, IsAwake = true,
    };
    public static BodyDef2D Dynamic(Vector2 position) => Default with { Type = BodyType.Dynamic, Position = position };
    public static BodyDef2D Static(Vector2 position) => Default with { Position = position };
}

/// <summary>A new shape's material, filter and flags (Box2D's shape definition).</summary>
public struct ShapeDef2D
{
    public float Density, Friction, Restitution;
    public Filter2D Filter;
    /// <summary>Detects overlaps (sensor events) without colliding.</summary>
    public bool IsSensor;
    /// <summary>A static shape finds contacts with what's already resting on it (off for streamed level geometry nothing rests on yet).</summary>
    public bool InvokeContactCreation;

    public static ShapeDef2D Default => new() { Density = 1, Friction = 0.6f, Filter = Filter2D.Default, InvokeContactCreation = true };
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

/// <summary>
/// What collides (Box2D's filter): two colliders collide when each one's category is in the other's mask, unless they
/// share a non-zero group, which decides alone: a positive group always collides, a negative group never does.
/// </summary>
public struct Filter2D
{
    public ulong Category, Mask;
    public int Group;
    public static Filter2D Default => new() { Category = 1, Mask = ulong.MaxValue };
    public readonly bool Accepts(in Filter2D other)
    {
        if (Group == other.Group && Group != 0) return Group > 0;
        return (Category & other.Mask) != 0 && (other.Category & Mask) != 0;
    }
}

/// <summary>Which colliders a query sees: those whose category is in <see cref="Mask"/> and whose mask has a bit of <see cref="Category"/>.</summary>
public struct QueryFilter2D
{
    public ulong Category, Mask;
    public static QueryFilter2D Default => new() { Category = 1, Mask = ulong.MaxValue };
    public readonly bool Accepts(in Filter2D shape) => (shape.Category & Mask) != 0 && (shape.Mask & Category) != 0;
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
    internal const float LinearSlop = 0.005f, Speculative = 4 * LinearSlop, MaxAabbMargin = 0.05f, AabbMarginFraction = 0.125f;

    // ---------------------------------------------------------------- bodies (struct of arrays)
    private int _bodyCapacity, _bodyHigh;
    private int[] _freeBodies = new int[16];
    private int _freeBodyCount;
    internal Entity[] BodyEntity = [];
    internal BodyType[] Types = [];
    internal bool[] BodyAlive = [], Awake = [], AllowSleep = [], FixedRotationOf = [], Implicit = [];
    internal float[] SleepTime = [], SleepThreshold = [];
    internal Vector2[] Center = [], LocalCenter = [], Velocity = [];
    internal Rot[] Rotation = [];
    internal float[] AngularVelocityOf = [], InvMass = [], InvInertia = [], Mass = [], GravityScale = [], LinearDamping = [], AngularDamping = [];
    internal int[] FirstShape = [];
    internal Vector2[] Written = [];
    internal Rot[] WrittenRotation = [];
    internal bool[] MassDirty = [];
    internal bool[] _justSlept = [];
    internal float[] MaxExtent = [], MinExtent = []; // farthest any shape reaches from the center of mass; thinnest shape's half-thickness
    internal Vector2[] Center0 = []; // pose at the start of the last step (continuous collision)
    internal Rot[] Rotation0 = [];
    internal bool[] Bullet = [];
    internal int[] TransformVersion = []; // bumped by teleports, which invalidate cached manifolds

    // ---------------------------------------------------------------- shapes
    private int _shapeCapacity, _shapeHigh;
    private int[] _freeShapes = new int[16];
    private int _freeShapeCount;
    internal int[] ShapeBody = [], NextShape = [], Proxy = [];
    internal Shape[] Geometry = [];
    internal float[] Density = [], Friction = [], Restitution = [];
    internal bool[] Sensor = [], ShapeAlive = [];
    internal int[] ChainOf = []; // the chain a segment belongs to, or -1
    internal Filter2D[] Filters = [];
    internal Entity[] ShapeEntity = [];
    internal Aabb[] Fat = [];
    // The body pose (centre of mass, rotation) each fat box was fitted at; a NaN rotation means "refit to know".
    internal FatPose[] FatPoses = [];
    internal ShapeSource[] Sources = [];
    internal float[] Bound = []; // radius of a circle about the shape's center that holds it
    internal float[] AabbMargin = []; // how far the fat box reaches past the tight one (Box2D's: an eighth of the shape's size, at most 5 cm)

    private ulong[] _newPairs = new ulong[64];
    private int _newPairCount;

    // ---------------------------------------------------------------- contacts
    // Contacts keep their index for life (freed slots are reused); the awake ones are listed in _awakeContacts.
    internal Contact[] Contacts = new Contact[64];
    internal int ContactCount; // live contacts
    private int _contactHigh;
    private int[] _freeContacts = new int[16];
    private int _freeContactCount;
    private PairTable _pairs = new();

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
    /// <summary>Tree heights and area ratios (static, kinematic, dynamic), for diagnostics.</summary>
    public string TreeStats => $"static h{_trees[0].Height} r{_trees[0].AreaRatio():0.0} kinematic h{_trees[1].Height} dynamic h{_trees[2].Height} r{_trees[2].AreaRatio():0.0}";
    public int AwakeBodyCount => _awakeCount;
    /// <summary>Contacts with an awake body: the ones the narrowphase updates each step.</summary>
    public int AwakeContactCount => _awakeContactCount;
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

    public int CreateBody(Entity entity, in BodyDef2D def)
    {
        int b;
        if (_freeBodyCount > 0) b = _freeBodies[--_freeBodyCount];
        else
        {
            if (_bodyHigh == _bodyCapacity) GrowBodies(Math.Max(64, _bodyCapacity * 2));
            b = _bodyHigh++;
        }
        var type = def.Type;
        bool moves = type != BodyType.Static;
        BodyEntity[b] = entity; Types[b] = type; BodyAlive[b] = true; Awake[b] = moves && def.IsAwake; AllowSleep[b] = def.EnableSleep;
        FixedRotationOf[b] = def.FixedRotation; Implicit[b] = def.IsImplicit; SleepThreshold[b] = def.SleepThreshold; Bullet[b] = def.IsBullet;
        SleepTime[b] = 0; Center[b] = def.Position; LocalCenter[b] = default; Rotation[b] = def.Rotation;
        Velocity[b] = moves ? def.LinearVelocity : default; AngularVelocityOf[b] = moves && !def.FixedRotation ? def.AngularVelocity : 0;
        GravityScale[b] = def.GravityScale; LinearDamping[b] = def.LinearDamping; AngularDamping[b] = def.AngularDamping; FirstShape[b] = -1; MassDirty[b] = true;
        Written[b] = def.Position; WrittenRotation[b] = def.Rotation;
        _contactHead[b] = -1; _contactDegree[b] = 0; _jointHead[b] = -1; _jointDegree[b] = 0; _bodyColors[b] = 0;
        IslandOf[b] = -1; IslandIndex[b] = -1; _awakeIndex[b] = -1;
        if (moves)
        {
            // Every moving body starts as an island of its own, awake or asleep.
            IslandAddBody(NewIsland(Awake[b]), b);
            if (Awake[b]) AddAwakeBody(b);
        }
        UpdateMass(b);
        return b;
    }

    /// <summary>A body of a type at a pose, with the defaults for everything else.</summary>
    public int CreateBody(Entity entity, BodyType type, Vector2 position, Rot rotation, bool isImplicit = false) =>
        CreateBody(entity, BodyDef2D.Default with { Type = type, Position = position, Rotation = rotation, IsImplicit = isImplicit });

    public void DestroyBody(int b)
    {
        if (!IsAlive(b)) return;
        WakeTouching(b);
        while (FirstShape[b] >= 0) { int s = FirstShape[b]; if (ChainOf[s] >= 0) DestroyChain(ChainOf[s]); else DestroyShape(s); }
        RemoveJointsOf(b);
        RemoveBodyFromIsland(b);
        if (_awakeIndex[b] >= 0) RemoveAwakeBody(b);
        BodyAlive[b] = false;
        Awake[b] = false;
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

    /// <summary>
    /// Drive a kinematic (or any moving) body to reach <paramref name="origin"/> and <paramref name="rotation"/> after
    /// <paramref name="dt"/> seconds, by setting its velocity (it pushes what it meets on the way, unlike a teleport).
    /// </summary>
    public void SetTargetTransform(int b, Vector2 origin, Rot rotation, float dt)
    {
        if (Types[b] == BodyType.Static || dt <= 0) return;
        var center = origin + rotation.Rotate(LocalCenter[b]);
        float invDt = 1 / dt;
        var v = invDt * (center - Center[b]);
        float w = invDt * RelativeAngle(Rotation[b], rotation);
        // Exactly there already: nothing to do (and no need to wake).
        if (v == default && w == 0 && Velocity[b] == default && AngularVelocityOf[b] == 0) return;
        Velocity[b] = v;
        AngularVelocityOf[b] = FixedRotationOf[b] ? 0 : w;
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
        // Rare, so simply: its contacts go (the broadphase finds them again), it leaves its island and joins a new one.
        WakeTouching(b);
        Wake(b);
        DestroyContactsOf(b);
        for (int key = _jointHead[b]; key >= 0; key = NextJointKey(key)) UnlinkJoint(key >> 1);
        RemoveBodyFromIsland(b);
        if (_awakeIndex[b] >= 0) RemoveAwakeBody(b);
        Types[b] = type;
        _recolor = true;
        if (type == BodyType.Static) { Velocity[b] = default; AngularVelocityOf[b] = 0; Awake[b] = false; }
        else
        {
            Awake[b] = true;
            SleepTime[b] = 0;
            IslandAddBody(NewIsland(true), b);
            AddAwakeBody(b);
        }
        for (int key = _jointHead[b]; key >= 0; key = NextJointKey(key)) LinkJoint(key >> 1);
        // Shapes change trees (static bodies' shapes live in the static tree).
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) { DestroyProxy(s); CreateProxy(s); }
        MassDirty[b] = true;
    }

    public void SetDamping(int b, float linear, float angular, float gravityScale) { LinearDamping[b] = linear; AngularDamping[b] = angular; GravityScale[b] = gravityScale; }
    public void SetFixedRotation(int b, bool fixedRotation) { if (FixedRotationOf[b] != fixedRotation) { FixedRotationOf[b] = fixedRotation; MassDirty[b] = true; } }
    public void SetAllowSleep(int b, bool allow) { AllowSleep[b] = allow; if (!allow) Wake(b); }

    /// <summary>Wake a body's island (and restart its sleep timer).</summary>
    public void Wake(int b)
    {
        if (!IsAlive(b) || Types[b] == BodyType.Static) return;
        SleepTime[b] = 0;
        if (!Awake[b]) WakeIsland(IslandOf[b]);
    }

    /// <summary>Wake whatever touches the body.</summary>
    private void WakeTouching(int b)
    {
        for (int key = _contactHead[b]; key >= 0; key = NextContactKey(key))
        {
            ref readonly var contact = ref Contacts[key >> 1];
            if ((contact.Flags & ContactFlags.Touching) != 0) Wake((key & 1) == 0 ? contact.BodyB : contact.BodyA);
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
        float extent = 0, minExtent = float.MaxValue;
        for (int s = FirstShape[b]; s >= 0; s = NextShape[s])
        {
            ref readonly var g = ref Geometry[s];
            if (g.Kind == ShapeKind.Circle) { extent = Math.Max(extent, SimMath.Length(g.Center - center) + g.Radius); minExtent = F.Min(minExtent, g.Radius); continue; }
            for (int i = 0; i < g.Count; i++) extent = Math.Max(extent, SimMath.Length(g.Vertex(i) - center) + g.Radius);
            // The thinnest part: the closest face to the centroid (a segment has none; a capsule is its radius).
            float inner = g.Kind == ShapeKind.ChainSegment || g.Count == 2 ? 0 : float.MaxValue;
            for (int i = 0; i < g.Count && g.Count > 2; i++) inner = F.Min(inner, V.Dot(g.Normal(i), g.Vertex(i) - g.Center));
            minExtent = F.Min(minExtent, inner + g.Radius);
        }
        MaxExtent[b] = extent;
        MinExtent[b] = minExtent == float.MaxValue ? 0 : minExtent;
        TransformVersion[b]++; // shapes or mass changed: cached manifolds are stale
        // A moved centre of mass changes what Center means: the poses the fat boxes were fitted at no longer compare.
        if (LocalCenter[b] != oldCenter) for (int s = FirstShape[b]; s >= 0; s = NextShape[s]) FatPoses[s].Rotation.C = float.NaN;
    }

    private void GrowBodies(int capacity)
    {
        Array.Resize(ref BodyEntity, capacity); Array.Resize(ref Types, capacity); Array.Resize(ref BodyAlive, capacity); Array.Resize(ref Awake, capacity);
        Array.Resize(ref AllowSleep, capacity); Array.Resize(ref FixedRotationOf, capacity); Array.Resize(ref Implicit, capacity); Array.Resize(ref SleepTime, capacity); Array.Resize(ref SleepThreshold, capacity);
        Array.Resize(ref Center, capacity); Array.Resize(ref LocalCenter, capacity); Array.Resize(ref Velocity, capacity); Array.Resize(ref Rotation, capacity);
        Array.Resize(ref AngularVelocityOf, capacity); Array.Resize(ref InvMass, capacity); Array.Resize(ref InvInertia, capacity); Array.Resize(ref Mass, capacity);
        Array.Resize(ref GravityScale, capacity); Array.Resize(ref LinearDamping, capacity); Array.Resize(ref AngularDamping, capacity); Array.Resize(ref FirstShape, capacity);
        Array.Resize(ref Written, capacity); Array.Resize(ref WrittenRotation, capacity); Array.Resize(ref MassDirty, capacity); Array.Resize(ref _justSlept, capacity);
        Array.Resize(ref MaxExtent, capacity); Array.Resize(ref MinExtent, capacity); Array.Resize(ref TransformVersion, capacity);
        Array.Resize(ref Center0, capacity); Array.Resize(ref Rotation0, capacity); Array.Resize(ref Bullet, capacity);
        GrowSolver(capacity);
        GrowIslandBodies(capacity);
        _bodyCapacity = capacity;
    }

    // ---------------------------------------------------------------- shapes

    public int CreateShape(int body, Entity entity, in Shape shape, in ShapeDef2D def)
    {
        int s;
        if (_freeShapeCount > 0) s = _freeShapes[--_freeShapeCount];
        else
        {
            if (_shapeHigh == _shapeCapacity) GrowShapes(Math.Max(64, _shapeCapacity * 2));
            s = _shapeHigh++;
        }
        ShapeBody[s] = body; ShapeEntity[s] = entity; Geometry[s] = shape; Density[s] = def.Density; Friction[s] = def.Friction; Restitution[s] = def.Restitution;
        Sensor[s] = def.IsSensor; Filters[s] = def.Filter; ShapeAlive[s] = true; ChainOf[s] = -1;
        Bound[s] = BoundOf(shape);
        AabbMargin[s] = MarginOf(shape);
        NextShape[s] = FirstShape[body];
        FirstShape[body] = s;
        CreateProxy(s, def.InvokeContactCreation);
        MassDirty[body] = true;
        return s;
    }

    public int CreateShape(int body, Entity entity, in Shape shape, float density, float friction, float restitution, bool sensor = false, Filter2D? filter = null) =>
        CreateShape(body, entity, shape, new ShapeDef2D { Density = density, Friction = friction, Restitution = restitution, IsSensor = sensor, Filter = filter ?? Filter2D.Default, InvokeContactCreation = true });

    public void DestroyShape(int s)
    {
        if (!ShapeAlive[s]) return;
        int b = ShapeBody[s];
        DestroyShapeContacts(s, wake: true); // they end now
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
        bool refilter = Sensor[s] != sensor || Filters[s].Category != filter.Category || Filters[s].Mask != filter.Mask || Filters[s].Group != filter.Group;
        Geometry[s] = shape; Density[s] = density; Friction[s] = friction; Restitution[s] = restitution; Filters[s] = filter;
        Bound[s] = BoundOf(shape);
        AabbMargin[s] = MarginOf(shape);
        FatPoses[s].Rotation.C = float.NaN; // new geometry: test its box exactly
        if (refilter)
        {
            // What it collides with changed: its contacts go, and its next query finds the pairs it should have.
            Sensor[s] = sensor;
            DestroyShapeContacts(s, wake: true);
            TreeOf(s).SetCategoryBits(Proxy[s], filter.Category);
            MarkMoved(s);
        }
        MassDirty[ShapeBody[s]] = true;
        RefreshProxy(s, 0, force: true);
        Wake(ShapeBody[s]);
    }

    /// <summary>Box2D's fat box margin: an eighth of the shape's size (its reach from its centre), at most 5 cm.</summary>
    private static float MarginOf(in Shape shape)
    {
        float size = shape.Kind == ShapeKind.Circle ? shape.Radius : shape.Count == 2 ? 0.5f * SimMath.Distance(shape.Vertex(0), shape.Vertex(1)) + shape.Radius : BoundOf(shape) - shape.Radius;
        return F.Min(MaxAabbMargin, AabbMarginFraction * size);
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
        Array.Resize(ref ShapeAlive, capacity); Array.Resize(ref ProxyTree, capacity); Array.Resize(ref Filters, capacity);
        Array.Resize(ref ShapeEntity, capacity); Array.Resize(ref Fat, capacity); Array.Resize(ref FatPoses, capacity); Array.Resize(ref Sources, capacity); Array.Resize(ref Bound, capacity); Array.Resize(ref ChainOf, capacity); Array.Resize(ref AabbMargin, capacity);
        _shapeCapacity = capacity;
    }

    private Aabb ShapeBox(int s)
    {
        int b = ShapeBody[s];
        return Geometry[s].Bounds(GetPosition(b), Rotation[b]);
    }

    /// <summary>The shape's box grown by the speculative distance: what its fat box must contain.</summary>
    private Aabb SpeculativeBox(int s)
    {
        var box = ShapeBox(s);
        box.Min -= new Vector2(Speculative); box.Max += new Vector2(Speculative);
        return box;
    }

    /// <summary>Fat box margin: the shape's own for moving bodies, just the speculative distance for static ones (they rarely move).</summary>
    private float FatMargin(int s) => Types[ShapeBody[s]] == BodyType.Static ? Speculative : AabbMargin[s];

    private bool[] _needsMove = [];
    private ulong[] _moveBits = new ulong[4]; // bodies with a shape that left its fat box this step
    private Aabb[] _swept = [];

    /// <summary>The shape's speculative box, and whether it left its fat box (no tree changes: safe in parallel).</summary>
    private bool Sweep(int s, float dt)
    {
        // Every point of the shape has moved at most |Δc| + extent·|Δq| since the reference pose (|Δq| being the chord
        // between the two rotations): while that stays well inside the fat box's clearance at that pose, the box is
        // certainly still contained and the shape's geometry is never touched. Conservative (L1 norms, a quarter of the
        // clearance to spare), so the outcome is the exact test's.
        int b = ShapeBody[s];
        ref var pose = ref FatPoses[s];
        var dc = Center[b] - pose.Center; var q = Rotation[b];
        float moved = MathF.Abs(dc.X) + MathF.Abs(dc.Y) + MaxExtent[b] * (MathF.Abs(q.C - pose.Rotation.C) + MathF.Abs(q.S - pose.Rotation.S));
        if (moved < 0.75f * pose.Slack) { _needsMove[s] = false; return false; }
        var tight = SpeculativeBox(s);
        var box = tight;
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

    // ---------------------------------------------------------------- step

    private static readonly ProfileMarker PairsMarker = ProfileMarker.Get("Physics2D.Broadphase", ProfileCategory.Engine);
    private static readonly ProfileMarker SweepMarker = ProfileMarker.Get("Physics2D.Sweep", ProfileCategory.Engine);
    private static readonly ProfileMarker TreeMarker = ProfileMarker.Get("Physics2D.TreeMoves", ProfileCategory.Engine);
    private static readonly ProfileMarker QueryMarker = ProfileMarker.Get("Physics2D.PairQueries", ProfileCategory.Engine);
    private static readonly ProfileMarker AddPairsMarker = ProfileMarker.Get("Physics2D.AddPairs", ProfileCategory.Engine);
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
        using (SolveMarker.Scope()) Solve(dt);
        using (IslandMarker.Scope()) UpdateSleep(dt);
    }

    private void AddContact(int shapeA, int shapeB)
    {
        int c;
        if (_freeContactCount > 0) c = _freeContacts[--_freeContactCount];
        else
        {
            if (_contactHigh == Contacts.Length) { Array.Resize(ref Contacts, Contacts.Length * 2); Array.Resize(ref Links, Contacts.Length); }
            c = _contactHigh++;
        }
        ContactCount++;
        int a = ShapeBody[shapeA], b = ShapeBody[shapeB];
        Contacts[c] = new Contact
        {
            ShapeA = shapeA, ShapeB = shapeB, BodyA = a, BodyB = b,
            Flags = Sensor[shapeA] || Sensor[shapeB] ? ContactFlags.Sensor : 0,
            Friction = SimMath.Sqrt(Friction[shapeA] * Friction[shapeB]),
            Restitution = Math.Max(Restitution[shapeA], Restitution[shapeB]),
        };
        Links[c] = new ContactLink { AwakeIndex = -1, Island = -1, IslandIndex = -1 };
        AddContactEdges(c, a, b);
        _pairs.Add((ulong)(uint)shapeA << 32 | (uint)shapeB, c);
        // Between sleeping (or static) bodies it waits, untouched, until one of them wakes.
        if (Awake[a] || Awake[b]) AddAwakeContact(c);
    }

    private struct CollideJob : IJobFor
    {
        public PhysicsWorld2D World;
        public float Dt;
        public readonly void Execute(int start, int end)
        {
            var w = World;
            var contacts = w.Contacts; var types = w.Types; var awake = w.Awake; var list = w._awakeContacts;
            ulong word = 0;
            for (int k = start; k < end; k++)
            {
                ref var contact = ref contacts[list[k]];
                int a = contact.BodyA, b = contact.BodyB;
                var typeA = types[a]; var typeB = types[b]; bool awakeA = awake[a], awakeB = awake[b];
                w.UpdateContact(ref contact, a, b, Dt); // awake contacts have an awake body
                // Graph membership changes (see Solvable), from the bodies' state already in hand.
                bool solvable = (contact.Flags & (ContactFlags.Touching | ContactFlags.Sensor | ContactFlags.Destroy)) == ContactFlags.Touching
                    && ((typeA == BodyType.Dynamic && awakeA) || (typeB == BodyType.Dynamic && awakeB));
                if (solvable != (contact.GraphColor != 0)) contact.Flags |= ContactFlags.GraphDirty;
                if ((contact.Flags & Changes) != 0) word |= 1UL << k;
                if ((k & 63) == 63 || k == end - 1) { if (word != 0) w._contactBits[k >> 6] |= word; word = 0; }
            }
        }
    }

    /// <summary>Narrowphase for every awake contact (in parallel: each writes only itself), then events and removals in list order.</summary>
    private void Collide(float dt)
    {
        int count = _awakeContactCount;
        int words = (count + 63) >> 6;
        if (_contactBits.Length < words) _contactBits = new ulong[words * 2];
        var job = new CollideJob { World = this, Dt = dt };
        // Batches of 64: each batch owns one word of _contactBits, so the flags need no synchronisation.
        if (Jobs is { } jobs) jobs.ParallelFor(count, 64, ref job);
        else job.Execute(0, count);
        // Only flagged contacts (began, ended, destroyed, or changing colour) need the serial pass, in list order.
        // Waking islands here only appends to the awake list, past the flagged range.
        _destroyedCount = 0;
        for (int w = 0; w < words; w++)
        {
            ulong bits = _contactBits[w];
            _contactBits[w] = 0;
            while (bits != 0)
            {
                int k = (w << 6) + BitOperations.TrailingZeroCount(bits);
                bits &= bits - 1;
                ContactChanged(_awakeContacts[k]);
            }
        }
        for (int k = 0; k < _destroyedCount; k++) RemoveContact(_destroyed[k]);
    }

    private const ContactFlags Changes = ContactFlags.Began | ContactFlags.Ended | ContactFlags.Destroy | ContactFlags.GraphDirty;
    private ulong[] _contactBits = new ulong[4];
    private int[] _destroyed = new int[16];
    private int _destroyedCount;

    private void ContactChanged(int c)
    {
        ref var contact = ref Contacts[c];
        if ((contact.Flags & ContactFlags.Began) != 0)
        {
            contact.Flags &= ~ContactFlags.Began;
            if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorBegins, ref _sensorBeginCount, SensorEvent(contact));
            else
            {
                Push(ref _begins, ref _beginCount, BeginEvent(contact));
                LinkContact(c); // wakes a sleeping side and joins the islands
            }
        }
        if ((contact.Flags & ContactFlags.Ended) != 0)
        {
            contact.Flags &= ~ContactFlags.Ended;
            if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorEnds, ref _sensorEndCount, SensorEvent(contact));
            else
            {
                Push(ref _ends, ref _endCount, new ContactEvent2D { A = ShapeEntity[contact.ShapeA], B = ShapeEntity[contact.ShapeB] });
                UnlinkContact(c);
            }
        }
        if ((contact.Flags & ContactFlags.GraphDirty) != 0)
        {
            // Against the state now (waking above may already have added it).
            contact.Flags &= ~ContactFlags.GraphDirty;
            if (!_recolor)
            {
                bool solvable = Solvable(contact);
                if (solvable && contact.GraphColor == 0) AddToGraph(c);
                else if (!solvable && contact.GraphColor != 0) RemoveFromGraph(c);
            }
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
        if (!Fat[contact.ShapeA].Overlaps(Fat[contact.ShapeB]))
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
        float margin = sensor ? 0 : Speculative;
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

    /// <summary>End and remove every contact of shape <paramref name="s"/> now (outside the step), waking the other bodies.</summary>
    private void DestroyShapeContacts(int s, bool wake)
    {
        int b = ShapeBody[s];
        for (int key = _contactHead[b]; key >= 0;)
        {
            int c = key >> 1;
            key = NextContactKey(key);
            ref readonly var contact = ref Contacts[c];
            if (contact.ShapeA != s && contact.ShapeB != s) continue;
            EndNow(c, wake);
        }
    }

    /// <summary>End and remove every contact of body <paramref name="b"/> now.</summary>
    private void DestroyContactsOf(int b)
    {
        while (_contactHead[b] >= 0) EndNow(_contactHead[b] >> 1, wake: true);
    }

    /// <summary>End and remove every contact between two bodies now (a joint that turns their collision off).</summary>
    private void DestroyContactsBetween(int a, int b)
    {
        if (_contactDegree[b] < _contactDegree[a]) (a, b) = (b, a);
        for (int key = _contactHead[a]; key >= 0;)
        {
            int c = key >> 1;
            key = NextContactKey(key);
            ref readonly var contact = ref Contacts[c];
            if (contact.BodyA == b || contact.BodyB == b) EndNow(c, wake: false);
        }
    }

    private void EndNow(int c, bool wake)
    {
        ref readonly var contact = ref Contacts[c];
        if ((contact.Flags & ContactFlags.Touching) != 0)
        {
            var e = (contact.Flags & ContactFlags.Sensor) != 0 ? SensorEvent(contact) : new ContactEvent2D { A = ShapeEntity[contact.ShapeA], B = ShapeEntity[contact.ShapeB] };
            if ((contact.Flags & ContactFlags.Sensor) != 0) Push(ref _sensorEnds, ref _sensorEndCount, e);
            else
            {
                Push(ref _ends, ref _endCount, e);
                if (wake) { Wake(contact.BodyA); Wake(contact.BodyB); }
            }
        }
        RemoveContact(c);
    }

    /// <summary>Remove contact <paramref name="c"/>: out of the graph, its island, the awake list, its bodies' lists and the pair table.</summary>
    private void RemoveContact(int c)
    {
        ref var contact = ref Contacts[c];
        if (contact.GraphColor != 0) { if (!_recolor) RemoveFromGraph(c); contact.GraphColor = 0; }
        UnlinkContact(c);
        if (Links[c].AwakeIndex >= 0) RemoveAwakeContact(c);
        RemoveContactEdges(c, contact.BodyA, contact.BodyB);
        _pairs.Remove((ulong)(uint)contact.ShapeA << 32 | (uint)contact.ShapeB);
        contact = new Contact { ShapeA = -1, ShapeB = -1 };
        ContactCount--;
        if (_freeContactCount == _freeContacts.Length) Array.Resize(ref _freeContacts, _freeContactCount * 2);
        _freeContacts[_freeContactCount++] = c;
    }

    // ---------------------------------------------------------------- queries

    private struct RayQuery : ITreeCast
    {
        public PhysicsWorld2D World;
        public ulong Mask;
        public int Shape;
        public Vector2 Normal;
        public float Fraction;
        public float Hit(Vector2 origin, Vector2 translation, float maxFraction, int proxy, int s)
        {
            var w = World;
            if (w.Sensor[s] || (w.Filters[s].Category & Mask) == 0) return -1;
            int b = w.ShapeBody[s];
            var q = w.Rotation[b]; var p = w.GetPosition(b);
            if (!ShapeRay.Cast(w.Geometry[s], q.InvRotate(origin - p), q.InvRotate(translation), maxFraction, out float f, out var n) || f >= Fraction) return -1;
            Fraction = f; Shape = s; Normal = q.Rotate(n);
            return f; // clip the ray to the closest hit so far
        }
    }

    /// <summary>The closest collider hit by the segment from <paramref name="origin"/> to <paramref name="origin"/> + <paramref name="translation"/>.</summary>
    public bool RayCast(Vector2 origin, Vector2 translation, out RayHit2D hit, ulong mask = ulong.MaxValue)
    {
        var q = new RayQuery { World = this, Mask = mask, Shape = -1, Fraction = 1 };
        for (int t = 0; t < 3; t++) _trees[t].CastRay(origin, translation, q.Fraction, ulong.MaxValue, ref q);
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
        for (int t = 0; t < 3; t++) _trees[t].Query(q.Box, ref q);
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
        w.AllowSleep = C(AllowSleep); w.FixedRotationOf = C(FixedRotationOf); w.Implicit = C(Implicit); w.SleepTime = C(SleepTime); w.SleepThreshold = C(SleepThreshold);
        w.Center = C(Center); w.LocalCenter = C(LocalCenter); w.Velocity = C(Velocity); w.Rotation = C(Rotation); w.AngularVelocityOf = C(AngularVelocityOf);
        w.InvMass = C(InvMass); w.InvInertia = C(InvInertia); w.Mass = C(Mass); w.GravityScale = C(GravityScale); w.LinearDamping = C(LinearDamping);
        w.AngularDamping = C(AngularDamping); w.FirstShape = C(FirstShape); w.Written = C(Written); w.WrittenRotation = C(WrittenRotation); w.MassDirty = C(MassDirty); w._justSlept = C(_justSlept); w.MaxExtent = C(MaxExtent); w.MinExtent = C(MinExtent); w.Center0 = C(Center0); w.Rotation0 = C(Rotation0); w.Bullet = C(Bullet); w.TransformVersion = C(TransformVersion);
        w._freeShapes = C(_freeShapes); w.ShapeBody = C(ShapeBody); w.NextShape = C(NextShape); w.Proxy = C(Proxy); w.Geometry = C(Geometry);
        w.Density = C(Density); w.Friction = C(Friction); w.Restitution = C(Restitution); w.Sensor = C(Sensor); w.ShapeAlive = C(ShapeAlive);
        w.Filters = C(Filters); w.ShapeEntity = C(ShapeEntity); w.Fat = C(Fat); w.FatPoses = C(FatPoses); w.Sources = C(Sources); w.Bound = C(Bound); w.ChainOf = C(ChainOf); w.AabbMargin = C(AabbMargin); w._needsMove = new bool[_needsMove.Length]; w._swept = new Aabb[_swept.Length];
        CloneBroadPhase(w); w._newPairs = C(_newPairs);
        w._threadPairs = new ulong[_threadPairs.Length][]; for (int t = 0; t < _threadPairs.Length; t++) w._threadPairs[t] = new ulong[_threadPairs[t].Length];
        w._threadPairCount = new int[_threadPairCount.Length];
        w.Contacts = C(Contacts); w._freeContacts = C(_freeContacts); w._contactBits = new ulong[_contactBits.Length]; w._destroyed = new int[_destroyed.Length]; w._moveBits = new ulong[_moveBits.Length]; w._pairs = _pairs.Clone();
        w._begins = C(_begins); w._ends = C(_ends); w._sensorBegins = C(_sensorBegins); w._sensorEnds = C(_sensorEnds); w._boxResults = new Entity[_boxResults.Length];
        CloneSolver(w);
        CloneJoints(w);
        CloneChains(w);
        CloneIslands(w);
        return w;
    }
}
