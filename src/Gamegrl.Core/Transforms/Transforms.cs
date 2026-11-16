using System.Numerics;

namespace Gamegrl;

/// <summary>An entity's position, rotation and scale relative to its parent (or the world, for a root).</summary>
public struct LocalTransform : IComponent
{
    public Vector3 Position;
    public Quaternion Rotation;
    public Vector3 Scale;

    public static LocalTransform Identity => new() { Rotation = Quaternion.Identity, Scale = Vector3.One };

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

        public readonly void Execute(int start, int end)
        {
            for (int i = start; i < end; i++)
            {
                var e = Entities[i];
                if (World.HasParent(e)) continue; // its root computes it
                var m = Locals[i].ToMatrix();
                Worlds[i].Value = m;
                Propagate(World, e, m);
            }
        }

        private static void Propagate(World world, Entity parent, in Matrix4x4 parentMatrix)
        {
            foreach (var child in world.Children(parent))
            {
                var m = world.Has<LocalTransform>(child) ? world.Get<LocalTransform>(child).ToMatrix() * parentMatrix : parentMatrix;
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
                    var job = new Job { World = world, Entities = entities, Locals = a.GetPointer<LocalTransform>(), Worlds = a.GetPointer<LocalToWorld>() };
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

/// <summary>The core module: transforms.</summary>
public sealed class CoreModule : IModule
{
    public void Build(EngineBuilder builder) => builder.AddSystem(new TransformPropagationSystem());
}
