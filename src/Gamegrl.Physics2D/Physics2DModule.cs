using System.Numerics;

namespace Gamegrl.Physics2D;

/// <summary>
/// A 2D rigid body on an entity with a <see cref="LocalTransform"/> (x, y and rotation about z) and a
/// <see cref="Collider2D"/>. Velocities are read before each physics step and written after it, so gameplay can set
/// them; moving the transform from outside the simulation teleports the body.
/// </summary>
public struct RigidBody2D : IComponent
{
    public BodyType Type;
    public Vector2 LinearVelocity;
    public float AngularVelocity;
    public float GravityScale;
    public float LinearDamping, AngularDamping;
    public bool FixedRotation;
    [NoSerialize] public int Handle; // body index + 1; 0 before the body exists

    public static RigidBody2D Default => new() { Type = BodyType.Dynamic, GravityScale = 1 };
    public static RigidBody2D Static => new() { Type = BodyType.Static, GravityScale = 1 };
}

public enum ColliderShape2D : byte { Circle, Box }

/// <summary>The body's shape: a circle (<see cref="Size"/>.X is the radius) or a box (<see cref="Size"/> is the full width and height).</summary>
public struct Collider2D : IComponent
{
    public ColliderShape2D Shape;
    public Vector2 Size;
    public Vector2 Offset;
    [Range(0.001, 1000)] public float Density;
    [Range(0, 2)] public float Friction;
    [Range(0, 1)] public float Restitution;

    public static Collider2D Default => Box(Vector2.One);
    public static Collider2D Box(Vector2 size, float density = 1) => new() { Shape = ColliderShape2D.Box, Size = size, Density = density, Friction = 0.6f };
    public static Collider2D Circle(float radius, float density = 1) => new() { Shape = ColliderShape2D.Circle, Size = new Vector2(radius), Density = density, Friction = 0.6f };

    internal readonly Shape ToShape() => Shape == ColliderShape2D.Circle
        ? Physics2D.Shape.Circle(Size.X, Offset)
        : Physics2D.Shape.Box(Size * 0.5f, Offset, Rot.Identity);
}

/// <summary>
/// 2D physics: adds the <see cref="PhysicsWorld2D"/> and <see cref="PhysicsSettings2D"/> resources and a fixed-step
/// system that syncs entities with bodies, steps the world, and writes poses back. Gameplay systems that set velocities
/// run before it (order below 1000); systems that read the results run after.
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
    private Query _bodies = null!;
    public string Name => "Physics2D.Step";
    public Phase Phase => Phase.FixedUpdate;
    public int Order => 1000;

    public void Initialize(World world) => _bodies = world.Query(new QueryDesc().With<RigidBody2D>().With<Collider2D>().With<LocalTransform>());

    public void Run(World world)
    {
        var physics = world.Resource<PhysicsWorld2D>();
        physics.Settings = world.Resource<PhysicsSettings2D>();
        // Bodies whose entity is gone or lost its components.
        for (int i = 0; i < physics.BodyCapacity; i++)
        {
            if (!physics.IsAlive(i)) continue;
            var e = physics.EntityOf(i);
            if (!world.IsAlive(e) || !world.Has<RigidBody2D>(e) || !world.Has<Collider2D>(e) || world.Get<RigidBody2D>(e).Handle != i + 1)
                physics.DestroyBody(i);
        }
        // New bodies, then what gameplay changed: velocities, settings, teleports.
        foreach (var a in _bodies.Archetypes)
        {
            var bodies = a.GetSpan<RigidBody2D>(); var colliders = a.GetSpan<Collider2D>(); var transforms = a.GetSpan<LocalTransform>();
            var entities = a.Entities;
            for (int r = 0; r < bodies.Length; r++)
            {
                ref var rb = ref bodies[r];
                ref readonly var t = ref transforms[r];
                int i = rb.Handle - 1;
                var position = new Vector2(t.Position.X, t.Position.Y);
                var rotation = Rot.FromQuaternion(t.Rotation);
                // A handle copied with the component (an instantiated prefab, a duplicated entity) belongs to another entity.
                if (i < 0 || !physics.IsAlive(i) || physics.EntityOf(i) != entities[r])
                {
                    ref readonly var c = ref colliders[r];
                    i = physics.CreateBody(entities[r], rb.Type, c.ToShape(), c.Density, c.Friction, c.Restitution, position, rotation);
                    rb.Handle = i + 1;
                }
                else if (physics.TypeOf(i) != rb.Type) physics.SetType(i, rb.Type, colliders[r].Density);
                ref readonly var written = ref physics.WrittenRotation(i);
                if (position != physics.WrittenPosition(i) || rotation.C != written.C || rotation.S != written.S)
                {
                    physics.Position(i) = position;
                    physics.Rotation(i) = rotation;
                }
                physics.Velocity(i) = rb.LinearVelocity;
                physics.AngularVelocity(i) = rb.FixedRotation ? 0 : rb.AngularVelocity;
                physics.GravityScaleOf(i) = rb.GravityScale;
                physics.LinearDampingOf(i) = rb.LinearDamping;
                physics.AngularDampingOf(i) = rb.AngularDamping;
                physics.FrictionOf(i) = colliders[r].Friction;
                physics.RestitutionOf(i) = colliders[r].Restitution;
                if (rb.FixedRotation) physics.FixRotation(i);
            }
        }

        physics.Step(world.Resource<Time>().FixedDelta);

        foreach (var a in _bodies.Archetypes)
        {
            var bodies = a.GetSpan<RigidBody2D>(); var transforms = a.GetSpan<LocalTransform>();
            for (int r = 0; r < bodies.Length; r++)
            {
                ref var rb = ref bodies[r];
                int i = rb.Handle - 1;
                if (rb.Type == BodyType.Static) continue;
                ref var t = ref transforms[r];
                var p = physics.Position(i);
                var q = physics.Rotation(i);
                t.Position = new Vector3(p.X, p.Y, t.Position.Z);
                t.Rotation = q.ToQuaternion();
                rb.LinearVelocity = physics.Velocity(i);
                rb.AngularVelocity = physics.AngularVelocity(i);
                // Remember what the transform will read back as, after the quaternion round trip.
                physics.WrittenPosition(i) = p;
                physics.WrittenRotation(i) = Rot.FromQuaternion(t.Rotation);
            }
        }
    }
}
