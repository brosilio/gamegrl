using System.Numerics;

namespace Gamegrl;

/// <summary>An entity's position, rotation and scale relative to its parent (or the world, for now).</summary>
public partial struct LocalTransform : IComponent
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

/// <summary>The world-space matrix, derived from <see cref="LocalTransform"/> every frame. Renderers read this.</summary>
public partial struct LocalToWorld : IComponent
{
    public Matrix4x4 Value;
}

/// <summary>
/// Built-in transform systems. TODO(milestone 2): parent/child hierarchy with propagation down the tree.
/// </summary>
public static partial class TransformSystems
{
    [System(Phase.PostUpdate, Parallel = true)]
    private static void ComputeLocalToWorld(in LocalTransform local, ref LocalToWorld world) =>
        world.Value = local.ToMatrix();
}

/// <summary>The core module: transforms.</summary>
public sealed class CoreModule : IModule
{
    public void Build(EngineBuilder builder) => Gamegrl.Generated.GamegrlCoreSystems.AddTo(builder.Schedule);
}
