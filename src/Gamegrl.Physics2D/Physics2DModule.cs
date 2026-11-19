using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>
/// A 2D rigid body on a root entity with a <see cref="LocalTransform"/> (x, y and rotation about z; scale is ignored).
/// Its shapes are the <see cref="Collider2D"/>s on it and on its descendants (a compound body). Velocities are read
/// before each physics step and written after it, so gameplay can set them; moving the transform from outside the
/// simulation teleports the body.
/// </summary>
public struct RigidBody2D : IComponent
{
    public BodyType Type;
    public Vector2 LinearVelocity;
    public float AngularVelocity;
    public float GravityScale;
    public float LinearDamping, AngularDamping;
    public bool FixedRotation;
    /// <summary>Keep simulating at rest (sleeping bodies cost nothing until something touches them).</summary>
    public bool NeverSleep;
    [NoSerialize] public int Handle; // body index + 1; 0 before the body exists

    public static RigidBody2D Default => new() { Type = BodyType.Dynamic, GravityScale = 1 };
    public static RigidBody2D Static => new() { Type = BodyType.Static, GravityScale = 1 };
    public static RigidBody2D Kinematic => new() { Type = BodyType.Kinematic, GravityScale = 1 };
}

public enum ColliderShape2D : byte { Circle, Box, Capsule }

/// <summary>
/// A shape: a circle (<see cref="Size"/>.X is the radius), a box (<see cref="Size"/> is width and height) or an upright
/// capsule (<see cref="Size"/> is its width and total height). It belongs to the body on its entity, or on its nearest
/// ancestor; with no body anywhere above it, it's static scenery. A sensor detects overlaps without colliding.
/// </summary>
public struct Collider2D : IComponent
{
    public ColliderShape2D Shape;
    public Vector2 Size;
    public Vector2 Offset;
    /// <summary>The shape's rotation on its entity, in radians.</summary>
    public float Angle;
    [Range(0, 1000)] public float Density;
    [Range(0, 2)] public float Friction;
    [Range(0, 1)] public float Restitution;
    public bool IsSensor;
    /// <summary>Collision filtering: collides with another collider when each one's category is in the other's mask.</summary>
    public uint Category, Mask;
    [NoSerialize] public int Handle; // shape index + 1

    public static Collider2D Default => Box(Vector2.One);
    public static Collider2D Box(Vector2 size, float density = 1) => new() { Shape = ColliderShape2D.Box, Size = size, Density = density, Friction = 0.6f, Category = 1, Mask = uint.MaxValue };
    public static Collider2D Circle(float radius, float density = 1) => new() { Shape = ColliderShape2D.Circle, Size = new Vector2(radius), Density = density, Friction = 0.6f, Category = 1, Mask = uint.MaxValue };
    public static Collider2D Capsule(float width, float height, float density = 1) => new() { Shape = ColliderShape2D.Capsule, Size = new Vector2(width, height), Density = density, Friction = 0.6f, Category = 1, Mask = uint.MaxValue };

    /// <summary>The shape in its entity's frame.</summary>
    public readonly Shape ToShape()
    {
        var rotation = Rot.FromAngle(Angle);
        switch (Shape)
        {
            case ColliderShape2D.Circle: return Physics2D.Shape.Circle(Size.X, Offset);
            case ColliderShape2D.Capsule:
            {
                float radius = 0.5f * Size.X, half = Math.Max(0, 0.5f * Size.Y - radius);
                return Physics2D.Shape.Capsule(Offset + rotation.Rotate(new Vector2(0, -half)), Offset + rotation.Rotate(new Vector2(0, half)), radius);
            }
            default: return Physics2D.Shape.Box(Size * 0.5f, Offset, rotation);
        }
    }

    internal readonly Filter2D Filter => new() { Category = Category, Mask = Mask };
}

/// <summary>
/// A joint between two bodies' entities (or, for <see cref="JointType2D.Target"/>, pulling <see cref="BodyB"/> toward
/// <see cref="JointDef2D.Target"/>). Put it on its own entity or on either body's; its settings can change any frame.
/// </summary>
public struct Joint2D : IComponent
{
    public Entity BodyA, BodyB;
    public JointDef2D Def;
    [NoSerialize] public int Handle; // joint index + 1

    public static Joint2D Default => new() { Def = new JointDef2D { Type = JointType2D.Revolute, DampingRatio = 0.7f } };
}

/// <summary>
/// 2D physics: adds the <see cref="PhysicsWorld2D"/> and <see cref="PhysicsSettings2D"/> resources and a fixed-step
/// system that syncs entities with bodies, shapes and joints, steps the world, and writes poses back. Gameplay systems
/// that set velocities run before it (order below 1000); systems that read results or contact events run after.
/// </summary>
public sealed class Physics2DModule : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.AddModule<CoreModule>();
        builder.SetResource(PhysicsSettings2D.Default);
        builder.SetResource(new PhysicsWorld2D(), world => world.Clone());
        builder.AddSystem(new Physics2DStep());
    }
}

internal sealed class Physics2DStep : ISystem
{
    private Query _bodies = null!, _colliders = null!, _joints = null!;
    public string Name => "Physics2D.Step";
    public Phase Phase => Phase.FixedUpdate;
    public int Order => 1000;
    private static readonly ProfileMarker SyncMarker = ProfileMarker.Get("Physics2D.Sync", ProfileCategory.Engine);

    public void Initialize(World world)
    {
        _bodies = world.Query(new QueryDesc().With<RigidBody2D>().With<LocalTransform>());
        _colliders = world.Query(new QueryDesc().With<Collider2D>().With<LocalTransform>());
        _joints = world.Query(new QueryDesc().With<Joint2D>());
    }

    public void Run(World world)
    {
        var physics = world.Resource<PhysicsWorld2D>();
        physics.Settings = world.Resource<PhysicsSettings2D>();
        physics.Jobs = world.Jobs;
        using (SyncMarker.Scope()) Sync(world, physics);
        physics.Step(world.Resource<Time>().FixedDelta);
        WriteBack(world, physics);
    }

    private static (Vector2 Position, Rot Rotation) Pose(in LocalTransform t) => (new Vector2(t.Position.X, t.Position.Y), Rot.FromQuaternion(t.Rotation));

    private void Sync(World world, PhysicsWorld2D physics)
    {
        // Everything the passes below visit gets this step's stamp; whatever is left unstamped lost its entity or its
        // component, and goes at the end. No per-object lookups into the world.
        int stamp = ++_stamp;

        // Stamps must cover every existing index before the parallel passes write them.
        if (_bodyStamps.Length < physics.BodyCapacity) Array.Resize(ref _bodyStamps, physics.BodyCapacity * 2 + 64);
        if (_shapeStamps.Length < physics.ShapeCapacity) Array.Resize(ref _shapeStamps, physics.ShapeCapacity * 2 + 64);

        // Bodies: create, settings, velocities set by gameplay, teleports. The common row (body exists, not moved from
        // outside, same type) only writes its own body, so those run in parallel; the rest run serially, in row order.
        foreach (var a in _bodies.Archetypes)
        {
            var bodies = a.GetSpan<RigidBody2D>(); var transforms = a.GetSpan<LocalTransform>();
            var entities = a.Entities;
            FastBodies(world, physics, a, stamp);
            for (int r = 0; r < bodies.Length; r++)
            {
                if (!_serialRows[r]) continue;
                ref var rb = ref bodies[r];
                var (position, rotation) = Pose(transforms[r]);
                int b = rb.Handle - 1;
                if (!physics.IsAlive(b) || physics.EntityOf(b) != entities[r] || physics.IsImplicit(b))
                {
                    b = physics.CreateBody(entities[r], rb.Type, position, rotation);
                    rb.Handle = b + 1;
                }
                else if (position != physics.Written[b] || rotation.C != physics.WrittenRotation[b].C || rotation.S != physics.WrittenRotation[b].S)
                {
                    physics.SetTransform(b, position, rotation);
                    physics.Written[b] = position; physics.WrittenRotation[b] = rotation;
                }
                Stamp(ref _bodyStamps, b, stamp);
                physics.SetType(b, rb.Type);
                physics.SetDamping(b, rb.LinearDamping, rb.AngularDamping, rb.GravityScale);
                physics.SetFixedRotation(b, rb.FixedRotation);
                if (physics.AllowSleep[b] == rb.NeverSleep) physics.SetAllowSleep(b, !rb.NeverSleep);
                physics.SetVelocity(b, rb.LinearVelocity, rb.AngularVelocity);
            }
        }

        // Shapes: on the entity's body, its nearest ancestor's, or an implicit static body of its own.
        foreach (var a in _colliders.Archetypes)
        {
            var colliders = a.GetSpan<Collider2D>(); var transforms = a.GetSpan<LocalTransform>();
            var entities = a.Entities;
            // Colliders beside their own body (the common case) read its handle from the same archetype.
            var ownBodies = a.Has<RigidBody2D>() ? a.GetSpan<RigidBody2D>() : default;
            FastColliders(world, physics, a, stamp);
            for (int r = 0; r < colliders.Length; r++)
            {
                if (!_serialRows[r]) continue;
                ref var collider = ref colliders[r];
                var e = entities[r];
                int s = collider.Handle - 1;
                int body = -1;
                var offset = Vector2.Zero; var turn = Rot.Identity;
                if (!ownBodies.IsEmpty) body = ownBodies[r].Handle - 1;
                else
                {
                    // Walk up, accumulating the collider's pose relative to the body.
                    var (p, q) = Pose(transforms[r]);
                    offset = p; turn = q;
                    for (var parent = world.HasParent(e) ? world.GetParent(e) : default; !parent.IsNull; parent = world.HasParent(parent) ? world.GetParent(parent) : default)
                    {
                        if (world.Has<RigidBody2D>(parent))
                        {
                            body = world.Get<RigidBody2D>(parent).Handle - 1;
                            if (!physics.IsAlive(body) || physics.EntityOf(body) != parent) body = -2; // appears next step
                            break;
                        }
                        if (!world.Has<LocalTransform>(parent)) continue;
                        var (pp, pq) = Pose(world.Get<LocalTransform>(parent));
                        offset = pp + pq.Rotate(offset);
                        turn = Rot.Mul(pq, turn);
                    }
                    if (body < 0)
                    {
                        // Static scenery: an implicit body at the collider's world pose.
                        if (physics.IsShapeAlive(s) && physics.EntityOfShape(s) == e && physics.IsImplicit(physics.BodyOfShape(s)))
                        {
                            body = physics.BodyOfShape(s);
                            if (offset != physics.Written[body] || turn.C != physics.WrittenRotation[body].C || turn.S != physics.WrittenRotation[body].S)
                            {
                                physics.SetTransform(body, offset, turn);
                                physics.Written[body] = offset; physics.WrittenRotation[body] = turn;
                            }
                        }
                        else body = physics.CreateBody(e, BodyType.Static, offset, turn, isImplicit: true);
                        Stamp(ref _bodyStamps, body, stamp);
                        offset = Vector2.Zero; turn = Rot.Identity;
                    }
                }
                if (body < 0 || !physics.IsAlive(body)) continue; // its body appears next step
                var source = new ShapeSource { Collider = collider with { Handle = 0 }, Offset = offset, Turn = turn };
                if (physics.IsShapeAlive(s) && physics.EntityOfShape(s) == e && physics.BodyOfShape(s) == body)
                {
                    if (!source.Same(physics.Sources[s]))
                    {
                        physics.UpdateShape(s, Build(source), collider.Density, collider.Friction, collider.Restitution, collider.IsSensor, collider.Filter);
                        physics.Sources[s] = source;
                    }
                    Stamp(ref _shapeStamps, s, stamp);
                    continue;
                }
                if (physics.IsShapeAlive(s) && physics.EntityOfShape(s) == e) physics.DestroyShape(s); // moved to another body
                s = physics.CreateShape(body, e, Build(source), collider.Density, collider.Friction, collider.Restitution, collider.IsSensor, collider.Filter);
                physics.Sources[s] = source;
                collider.Handle = s + 1;
                Stamp(ref _shapeStamps, s, stamp);
            }
        }

        // Joints.
        foreach (var a in _joints.Archetypes)
        {
            var joints = a.GetSpan<Joint2D>();
            var entities = a.Entities;
            for (int r = 0; r < joints.Length; r++)
            {
                ref var joint = ref joints[r];
                int j = joint.Handle - 1;
                int bodyA = BodyOf(world, physics, joint.BodyA), bodyB = BodyOf(world, physics, joint.BodyB);
                bool valid = bodyB >= 0 && (bodyA >= 0 || joint.Def.Type == JointType2D.Target);
                if (physics.IsJointAlive(j) && physics.OwnerOfJoint(j) == entities[r])
                {
                    var (ja, jb) = physics.JointBodies(j);
                    if (valid && jb == bodyB && (ja == bodyA || joint.Def.Type == JointType2D.Target)) { physics.UpdateJoint(j, joint.Def); Stamp(ref _jointStamps, j, stamp); continue; }
                    physics.DestroyJoint(j);
                }
                joint.Handle = valid ? physics.CreateJoint(entities[r], bodyA, bodyB, joint.Def) + 1 : 0;
                if (valid) Stamp(ref _jointStamps, joint.Handle - 1, stamp);
            }
        }

        // What nothing visited: joints, then shapes, then bodies (a body takes its remaining shapes and joints with it).
        for (int j = 0; j < physics.JointCapacity; j++)
            if (physics.IsJointAlive(j) && Stamped(_jointStamps, j) != stamp) physics.DestroyJoint(j);
        for (int s = 0; s < physics.ShapeCapacity; s++)
            if (physics.IsShapeAlive(s) && Stamped(_shapeStamps, s) != stamp) physics.DestroyShape(s);
        for (int b = 0; b < physics.BodyCapacity; b++)
            if (physics.IsAlive(b) && Stamped(_bodyStamps, b) != stamp) physics.DestroyBody(b);
    }

    private bool[] _serialRows = new bool[64];

    private unsafe struct FastBodyJob : IJobFor
    {
        public PhysicsWorld2D Physics;
        public RigidBody2D* Bodies;
        public LocalTransform* Transforms;
        public Entity* Entities;
        public bool[] Serial;
        public int[] Stamps;
        public int Stamp;
        public readonly void Execute(int start, int end)
        {
            var physics = Physics;
            for (int r = start; r < end; r++)
            {
                ref var rb = ref Bodies[r];
                int b = rb.Handle - 1;
                Serial[r] = true;
                if (!physics.IsAlive(b) || physics.EntityOf(b) != Entities[r] || physics.IsImplicit(b) || physics.TypeOf(b) != rb.Type) continue;
                var (position, rotation) = Pose(Transforms[r]);
                if (position != physics.Written[b] || rotation.C != physics.WrittenRotation[b].C || rotation.S != physics.WrittenRotation[b].S) continue;
                Serial[r] = false;
                Stamps[b] = Stamp;
                physics.SetDamping(b, rb.LinearDamping, rb.AngularDamping, rb.GravityScale);
                physics.SetFixedRotation(b, rb.FixedRotation);
                if (physics.AllowSleep[b] == rb.NeverSleep) physics.SetAllowSleep(b, !rb.NeverSleep);
                physics.SetVelocity(b, rb.LinearVelocity, rb.AngularVelocity);
            }
        }
    }

    private unsafe void FastBodies(World world, PhysicsWorld2D physics, Archetype a, int stamp)
    {
        if (_serialRows.Length < a.Count) _serialRows = new bool[a.Count * 2];
        fixed (Entity* entities = a.Entities)
        {
            var job = new FastBodyJob
            {
                Physics = physics, Bodies = a.GetPointer<RigidBody2D>(), Transforms = a.GetPointer<LocalTransform>(), Entities = entities,
                Serial = _serialRows, Stamps = _bodyStamps, Stamp = stamp,
            };
            if (world.Jobs is { } jobs) jobs.ParallelFor(a.Count, 512, ref job); else job.Execute(0, a.Count);
        }
    }

    private unsafe struct FastColliderJob : IJobFor
    {
        public PhysicsWorld2D Physics;
        public Collider2D* Colliders;
        public RigidBody2D* Bodies; // the same entity's body
        public Entity* Entities;
        public bool[] Serial;
        public int[] Stamps;
        public int Stamp;
        public readonly void Execute(int start, int end)
        {
            var physics = Physics;
            for (int r = start; r < end; r++)
            {
                Serial[r] = true;
                if (Bodies == null) continue; // child and scenery colliders take the full path
                ref readonly var collider = ref Colliders[r];
                int s = collider.Handle - 1, body = Bodies[r].Handle - 1;
                var e = Entities[r];
                if (!physics.IsAlive(body) || physics.EntityOf(body) != e || !physics.IsShapeAlive(s) || physics.EntityOfShape(s) != e || physics.BodyOfShape(s) != body) continue;
                var source = new ShapeSource { Collider = collider with { Handle = 0 }, Offset = Vector2.Zero, Turn = Rot.Identity };
                if (!source.Same(physics.Sources[s])) continue;
                Serial[r] = false;
                Stamps[s] = Stamp;
            }
        }
    }

    private unsafe void FastColliders(World world, PhysicsWorld2D physics, Archetype a, int stamp)
    {
        if (_serialRows.Length < a.Count) _serialRows = new bool[a.Count * 2];
        fixed (Entity* entities = a.Entities)
        {
            var job = new FastColliderJob
            {
                Physics = physics, Colliders = a.GetPointer<Collider2D>(), Bodies = a.Has<RigidBody2D>() ? a.GetPointer<RigidBody2D>() : null,
                Entities = entities, Serial = _serialRows, Stamps = _shapeStamps, Stamp = stamp,
            };
            if (world.Jobs is { } jobs) jobs.ParallelFor(a.Count, 512, ref job); else job.Execute(0, a.Count);
        }
    }

    private int _stamp;
    private int[] _bodyStamps = [], _shapeStamps = [], _jointStamps = [];

    private static void Stamp(ref int[] stamps, int index, int stamp)
    {
        if (index >= stamps.Length) Array.Resize(ref stamps, Math.Max(64, (index + 1) * 2));
        stamps[index] = stamp;
    }

    private static int Stamped(int[] stamps, int index) => index < stamps.Length ? stamps[index] : 0;

    private static int BodyOf(World world, PhysicsWorld2D physics, Entity e)
    {
        if (e.IsNull || !world.IsAlive(e) || !world.Has<RigidBody2D>(e)) return -1;
        int b = world.Get<RigidBody2D>(e).Handle - 1;
        return physics.IsAlive(b) && physics.EntityOf(b) == e ? b : -1;
    }

    private static Shape Build(in ShapeSource source)
    {
        var shape = source.Collider.ToShape();
        return source.Offset == default && source.Turn.C == 1 && source.Turn.S == 0 ? shape : shape.Transformed(source.Offset, source.Turn);
    }

    private unsafe struct WriteBackJob : IJobFor
    {
        public PhysicsWorld2D Physics;
        public RigidBody2D* Bodies;
        public LocalTransform* Transforms;
        public readonly void Execute(int start, int end)
        {
            var physics = Physics;
            for (int r = start; r < end; r++)
            {
                ref var rb = ref Bodies[r];
                int b = rb.Handle - 1;
                if (!physics.IsAlive(b) || physics.TypeOf(b) == BodyType.Static || !physics.IsAwake(b) && physics.SleptBefore(b)) continue;
                ref var t = ref Transforms[r];
                var p = physics.GetPosition(b);
                t.Position = new Vector3(p.X, p.Y, t.Position.Z);
                t.Rotation = physics.GetRotation(b).ToQuaternion();
                rb.LinearVelocity = physics.GetVelocity(b);
                rb.AngularVelocity = physics.GetAngularVelocity(b);
                // What the transform reads back as (after the quaternion round trip), to tell our writes from teleports.
                physics.Written[b] = p;
                physics.WrittenRotation[b] = Rot.FromQuaternion(t.Rotation);
            }
        }
    }

    /// <summary>Poses and velocities back to the entities, in parallel (each row writes only its own entity and body).</summary>
    private unsafe void WriteBack(World world, PhysicsWorld2D physics)
    {
        foreach (var a in _bodies.Archetypes)
        {
            if (a.Count == 0) continue;
            var job = new WriteBackJob { Physics = physics, Bodies = a.GetPointer<RigidBody2D>(), Transforms = a.GetPointer<LocalTransform>() };
            if (world.Jobs is { } jobs) jobs.ParallelFor(a.Count, 512, ref job); else job.Execute(0, a.Count);
        }
    }
}

/// <summary>What a shape was built from, kept with the shape so a change in its collider is noticed (and snapshots carry it).</summary>
internal struct ShapeSource
{
    public Collider2D Collider;
    public Vector2 Offset;
    public Rot Turn;

    public readonly unsafe bool Same(in ShapeSource other)
    {
        var a = this; var b = other;
        // Field by field: the collider has padding, whose bytes are arbitrary.
        ref readonly var c = ref a.Collider; ref readonly var d = ref b.Collider;
        return c.Shape == d.Shape && c.Size == d.Size && c.Offset == d.Offset && c.Angle == d.Angle && c.Density == d.Density && c.Friction == d.Friction
            && c.Restitution == d.Restitution && c.IsSensor == d.IsSensor && c.Category == d.Category && c.Mask == d.Mask
            && a.Offset == b.Offset && a.Turn.C == b.Turn.C && a.Turn.S == b.Turn.S;
    }
}
