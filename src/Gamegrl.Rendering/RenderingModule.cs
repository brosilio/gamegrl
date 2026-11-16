using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.Rhi;

namespace Gamegrl.Rendering;

/// <summary>
/// 3D meshes and 2D sprites into the main window. Needs a platform module (the window) and a GPU backend module
/// (a <see cref="GpuDevice"/> resource) added first.
/// TODO(milestone 2): split into Rendering2D/Rendering3D on a shared render graph.
/// </summary>
public sealed class RenderingModule(bool vsync = true) : IModule
{
    public void Build(EngineBuilder builder)
    {
        builder.AddModule<CoreModule>();
        var device = builder.World.Resource<GpuDevice>();
        var window = builder.World.Resource<PlatformContext>().MainWindow;
        var ctx = new RenderContext(device, window, vsync);
        ctx.SpriteTextureLayout = device.CreateBindGroupLayout(
        [
            new BindingLayout(0, BindingType.SampledTexture, ShaderStages.Fragment),
            new BindingLayout(1, BindingType.Sampler, ShaderStages.Fragment),
        ]);
        ctx.SpriteSampler = device.CreateSampler(new SamplerDesc(Filter.Linear, AddressMode.ClampToEdge));
        builder.SetResource(ctx);
        builder.SetResource(new ScreenSize { Width = ctx.Swapchain.Width, Height = ctx.Swapchain.Height });

        var meshes = new MeshRendererSystem();
        var sprites = new SpriteRendererSystem();
        builder.AddSystem(new BeginFrameSystem());
        builder.AddSystem(meshes);
        builder.AddSystem(sprites);
        builder.AddSystem(new EndFrameSystem());
        Gamegrl.Generated.GamegrlRenderingSystems.AddTo(builder.Schedule);

        builder.OnStop(_ =>
        {
            device.WaitIdle();
            meshes.Dispose();
            sprites.Dispose();
            ctx.SpriteSampler.Dispose();
            ctx.SpriteTextureLayout.Dispose();
            ctx.Dispose();
        });
    }
}

/// <summary>Built-in meshes.</summary>
public static class Primitives
{
    /// <summary>A unit cube centered on the origin: 24 vertices (flat normals), 36 indices, counter-clockwise outward.</summary>
    public static (MeshVertex[] Vertices, ushort[] Indices) Cube()
    {
        var v = new List<MeshVertex>(24);
        var i = new List<ushort>(36);
        void Face(Vector3 n, Vector3 u, Vector3 w)
        {
            // u × w = n, so (−u−w, u−w, u+w, −u+w) is counter-clockwise seen from outside.
            ushort b = (ushort)v.Count;
            Vector3 c = n * 0.5f;
            v.Add(new MeshVertex(c - u * 0.5f - w * 0.5f, n));
            v.Add(new MeshVertex(c + u * 0.5f - w * 0.5f, n));
            v.Add(new MeshVertex(c + u * 0.5f + w * 0.5f, n));
            v.Add(new MeshVertex(c - u * 0.5f + w * 0.5f, n));
            i.AddRange([b, (ushort)(b + 1), (ushort)(b + 2), b, (ushort)(b + 2), (ushort)(b + 3)]);
        }
        Face(Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        Face(-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY);
        Face(Vector3.UnitY, Vector3.UnitZ, Vector3.UnitX);
        Face(-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ);
        Face(Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY);
        Face(-Vector3.UnitZ, Vector3.UnitY, Vector3.UnitX);
        return (v.ToArray(), i.ToArray());
    }
}
