using System.Numerics;

namespace Gamegrl;

/// <summary>An entity's position, rotation and scale relative to its parent (or the world, for a root).</summary>
public struct LocalTransform : IComponent
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static LocalTransform Identity => new() { Rotation = Quaternion.Identity, Scale = Vector3.One };
    public static LocalTransform Default => Identity;

    public static LocalTransform At(Vector3 position, float uniformScale = 1) =>
        new() { Position = position, Rotation = Quaternion.Identity, Scale = new Vector3(uniformScale) };

    public readonly Matrix4x4 ToMatrix() =>
        Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateFromQuaternion(Rotation) * Matrix4x4.CreateTranslation(Position);
}

/// <summary>The world-space matrix, derived from the hierarchy of <see cref="LocalTransform"/>s every frame. Renderers read this.</summary>
public struct LocalToWorld : IComponent
{
    [NoSerialize] public Matrix4x4 Value; // derived every frame

    public readonly Vector3 Position => Value.Translation;
}

/// <summary>
/// Smooths an entity the fixed-tick simulation moves: the transforms after the last two fixed steps, so frames
/// between steps draw the blend at <see cref="Time.FixedAlpha"/> instead of stepping at the tick rate. The simulation
/// keeps reading and writing <see cref="LocalTransform"/>; only <see cref="LocalToWorld"/> is blended. If anything
/// else moved the entity since the last step (a teleport, an editor drag), the actual transform is drawn.
/// </summary>
public struct InterpolatedTransform : IComponent
{
    [NoSerialize] public LocalTransform Previous, Current;
    [NoSerialize] public bool Primed;

    /// <summary>The transform to draw at <paramref name="alpha"/> (0 = previous step, 1 = latest).</summary>
    public readonly LocalTransform Blend(in LocalTransform actual, float alpha)
    {
        if (!Primed || !Same(actual, Current)) return actual;
        var q = Current.Rotation;
        if (Quaternion.Dot(Previous.Rotation, q) < 0) q = -q; // shortest arc
        return new LocalTransform
        {
            Position = Vector3.Lerp(Previous.Position, Current.Position, alpha),
            Rotation = Quaternion.Normalize(Quaternion.Lerp(Previous.Rotation, q, alpha)),
            Scale = Vector3.Lerp(Previous.Scale, Current.Scale, alpha),
        };
    }

    private static unsafe bool Same(in LocalTransform a, in LocalTransform b)
    {
        fixed (LocalTransform* pa = &a) fixed (LocalTransform* pb = &b)
            return new ReadOnlySpan<byte>(pa, sizeof(LocalTransform)).SequenceEqual(new ReadOnlySpan<byte>(pb, sizeof(LocalTransform)));
    }
}

/// <summary>Records <see cref="InterpolatedTransform"/> history after each fixed step, once the simulation has run.</summary>
internal sealed unsafe class TransformHistorySystem : ISystem
{
    private Query _query = null!;
    public string Name => "Gamegrl.TransformHistory";
    public Phase Phase => Phase.FixedUpdate;
    public int Order => int.MaxValue - 1; // after the simulation, before tick hashing
    public void Initialize(World world) => _query = world.Query(new QueryDesc().With<LocalTransform>().With<InterpolatedTransform>());

    public void Run(World world)
    {
        foreach (var a in _query.Archetypes)
        {
            var locals = a.GetSpan<LocalTransform>();
            var history = a.GetSpan<InterpolatedTransform>();
            for (int i = 0; i < locals.Length; i++)
            {
                ref var h = ref history[i];
                h.Previous = h.Primed ? h.Current : locals[i];
                h.Current = locals[i];
                h.Primed = true;
            }
        }
    }
}

/// <summary>
/// Computes <see cref="LocalToWorld"/> down the hierarchy: roots in parallel, each root walking its own subtree
/// (subtrees are disjoint, so no locks). A child without a <see cref="LocalTransform"/> inherits its parent's matrix.
/// </summary>
internal sealed unsafe class TransformPropagationSystem : ISystem
{
    private Query _query = null!;

    public string Name => "Gamegrl.TransformPropagation";
    public Phase Phase => Phase.PostUpdate;
    public int Order => 0;

    public void Initialize(World world) => _query = world.Query(new QueryDesc().With<LocalTransform>().With<LocalToWorld>());

    private struct Job : IJobFor
    {
        public World World;
        public Entity* Entities;
        public LocalTransform* Locals;
        public LocalToWorld* Worlds;
        public InterpolatedTransform* History; // null when the archetype has none
        public float Alpha;

        public readonly void Execute(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var e = Entities[i];
                if (World.HasParent(e)) continue; // its root computes it
                var m = (History is null ? Locals[i] : History[i].Blend(Locals[i], Alpha)).ToMatrix();
                Worlds[i].Value = m;
                Propagate(World, e, m);
            }
        }

        private readonly void Propagate(World world, Entity parent, in Matrix4x4 parentMatrix)
        {
            foreach (var child in world.Children(parent))
            {
                var m = parentMatrix;
                if (world.Has<LocalTransform>(child))
                {
                    var local = world.Get<LocalTransform>(child);
                    if (world.Has<InterpolatedTransform>(child)) local = world.Get<InterpolatedTransform>(child).Blend(local, Alpha);
                    m = local.ToMatrix() * parentMatrix;
                }
                if (world.Has<LocalToWorld>(child)) world.Get<LocalToWorld>(child).Value = m;
                Propagate(world, child, m);
            }
        }
    }

    public void Run(World world)
    {
        world.BeginIteration();
        try
        {
            foreach (var a in _query.Archetypes)
            {
                int count = a.Count;
                if (count == 0) continue;
                fixed (Entity* entities = a.Entities)
                {
                    var job = new Job
                    {
                        World = world, Entities = entities, Locals = a.GetPointer<LocalTransform>(), Worlds = a.GetPointer<LocalToWorld>(),
                        History = a.Has<InterpolatedTransform>() ? a.GetPointer<InterpolatedTransform>() : null,
                        Alpha = world.Resource<Time>().FixedAlpha,
                    };
                    if (world.Jobs is { } jobs) jobs.ParallelFor(count, 256, ref job);
                    else job.Execute(0, count);
                }
            }
        }
        finally
        {
            world.EndIteration();
        }
    }
}

/// <summary>The core module: transforms and their interpolation.</summary>
public sealed class CoreModule : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.AddSystem(new TransformPropagationSystem());
        builder.AddSystem(new TransformHistorySystem());
    }
}
