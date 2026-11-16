using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Rhi;

namespace Gamegrl.Rendering;

/// <summary>Extract systems: copy what's visible out of the world into the frame's instance lists.</summary>
public static partial class RenderExtract
{
    [System(Phase.Render, Order = -500)]
    private static void CollectMeshes(in LocalToWorld transform, in MeshRenderer renderer, [Res] in RenderContext ctx) =>
        ctx.MeshInstances.Add(new MeshInstance { Model = transform.Value, Color = renderer.Color, Mesh = renderer.Mesh.Id });

    [System(Phase.Render, Order = -500)]
    private static void CollectSprites(in LocalTransform transform, in Sprite sprite, [Res] in RenderContext ctx)
    {
        float angle = 2f * MathF.Atan2(transform.Rotation.Z, transform.Rotation.W); // rotation about z
        var (sin, cos) = MathF.SinCos(angle);
        ctx.SpriteInstances.Add(new SpriteInstance
        {
            PosSize = new Vector4(transform.Position.X, transform.Position.Y, sprite.Size.X * transform.Scale.X, sprite.Size.Y * transform.Scale.Y),
            Uv = sprite.UvRect,
            Color = sprite.Color,
            RotPivot = new Vector4(cos, sin, sprite.Pivot.X, sprite.Pivot.Y),
            Texture = sprite.Texture.Id,
        });
    }
}

/// <summary>Acquires the swapchain image and opens the frame's render pass.</summary>
internal sealed class BeginFrameSystem : ISystem
{
    private Query _cameras = null!;

    public string Name => "Rendering.BeginFrame";
    public Phase Phase => Phase.Render;
    public int Order => -1000;

    public void Initialize(World world) => _cameras = world.Query(new QueryDesc().With<Camera>());

    public void Run(World world)
    {
        var ctx = world.Resource<RenderContext>();
        ctx.MeshInstances.Clear();
        ctx.SpriteInstances.Clear();
        ctx.HasFrame = ctx.Device.BeginFrame(ctx.Swapchain, out var frame);
        if (!ctx.HasFrame) return;
        ctx.Frame = frame;
        ctx.Ring.BeginFrame(frame.FrameIndex);
        ctx.EnsureDepth(frame.Backbuffer.Width, frame.Backbuffer.Height);
        world.Resource<ScreenSize>() = new ScreenSize { Width = frame.Backbuffer.Width, Height = frame.Backbuffer.Height };

        var clear = new Vector4(0.05f, 0.06f, 0.09f, 1);
        foreach (var a in _cameras.Archetypes)
            if (a.Count > 0)
            {
                clear = a.GetSpan<Camera>()[0].ClearColor;
                break;
            }

        frame.Commands.BeginRenderPass(new RenderPassDesc
        {
            Color = new ColorAttachment { Texture = frame.Backbuffer, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = clear },
            Depth = new DepthAttachment { Texture = ctx.Depth, Load = LoadOp.Clear, Store = StoreOp.DontCare, ClearDepth = 0 },
        });
    }
}

internal sealed class EndFrameSystem : ISystem
{
    public string Name => "Rendering.EndFrame";
    public Phase Phase => Phase.Render;
    public int Order => 1000;
    public void Initialize(World world) { }

    public void Run(World world)
    {
        var ctx = world.Resource<RenderContext>();
        if (!ctx.HasFrame) return;
        ctx.Frame.Commands.EndRenderPass();
        ctx.Device.EndFrame(ctx.Frame);
        ctx.HasFrame = false;
    }
}

/// <summary>Draws mesh instances: sorted by mesh, uploaded once into the ring, one instanced draw per mesh.</summary>
internal sealed class MeshRendererSystem : ISystem, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CameraConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 LightDir;
        public Vector4 Ambient;
    }

    private GraphicsPipeline _pipeline = null!;
    private BindGroupLayout _layout = null!;
    private BindGroup _group = null!;
    private Query _cameras = null!;
    private MeshInstanceGpu[] _scratch = new MeshInstanceGpu[1024];

    public string Name => "Rendering.Meshes";
    public Phase Phase => Phase.Render;
    public int Order => 0;

    public void Initialize(World world)
    {
        var ctx = world.Resource<RenderContext>();
        var device = ctx.Device;
        _cameras = world.Query(new QueryDesc().With<Camera>().With<LocalToWorld>());
        _layout = device.CreateBindGroupLayout([new BindingLayout(0, BindingType.UniformBufferDynamic, ShaderStages.Graphics)]);
        _group = device.CreateBindGroup(_layout, [BindGroupEntry.Of(0, ctx.Ring.Buffer, 0, Marshal.SizeOf<CameraConstants>())]);
        using var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Shaders.Load("mesh.vert")));
        using var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Shaders.Load("mesh.frag")));
        _pipeline = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            Name = "mesh",
            VertexShader = vs,
            FragmentShader = fs,
            BindGroupLayouts = [_layout],
            VertexBuffers =
            [
                new VertexBufferLayout(24, VertexStep.Vertex, new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Float3, 12)),
                new VertexBufferLayout(80, VertexStep.Instance,
                    new VertexAttribute(2, VertexFormat.Float4, 0), new VertexAttribute(3, VertexFormat.Float4, 16),
                    new VertexAttribute(4, VertexFormat.Float4, 32), new VertexAttribute(5, VertexFormat.Float4, 48),
                    new VertexAttribute(6, VertexFormat.Float4, 64)),
            ],
            ColorFormats = [ctx.Swapchain.Format],
            DepthFormat = RenderContext.DepthFormat,
        });
    }

    public void Run(World world)
    {
        var ctx = world.Resource<RenderContext>();
        if (!ctx.HasFrame || ctx.MeshInstances.Count == 0) return;

        Camera camera = default;
        Matrix4x4 cameraWorld = Matrix4x4.Identity;
        bool found = false;
        foreach (var a in _cameras.Archetypes)
            if (a.Count > 0)
            {
                camera = a.GetSpan<Camera>()[0];
                cameraWorld = a.GetSpan<LocalToWorld>()[0].Value;
                found = true;
                break;
            }
        if (!found) return;

        var frame = ctx.Frame;
        Matrix4x4.Invert(cameraWorld, out var view);
        float aspect = frame.Backbuffer.Width / (float)Math.Max(1, frame.Backbuffer.Height);
        var constants = new CameraConstants
        {
            ViewProj = view * camera.Projection(aspect),
            LightDir = new Vector4(Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)), 0),
            Ambient = new Vector4(0.18f, 0.2f, 0.25f, 0),
        };
        uint cameraOffset = (uint)ctx.Ring.Push(constants, ctx.Device.Limits.MinUniformBufferOffsetAlignment);

        var instances = CollectionsMarshal.AsSpan(ctx.MeshInstances);
        instances.Sort(static (a, b) => a.Mesh.CompareTo(b.Mesh));
        if (_scratch.Length < instances.Length) _scratch = new MeshInstanceGpu[instances.Length * 2];
        for (int i = 0; i < instances.Length; i++) _scratch[i] = new MeshInstanceGpu { Model = instances[i].Model, Color = instances[i].Color };
        long instanceOffset = ctx.Ring.Push<MeshInstanceGpu>(_scratch.AsSpan(0, instances.Length));

        var cmd = frame.Commands;
        cmd.SetPipeline(_pipeline);
        cmd.SetBindGroup(0, _group, [cameraOffset]);
        int start = 0;
        while (start < instances.Length)
        {
            int meshId = instances[start].Mesh;
            int end = start;
            while (end < instances.Length && instances[end].Mesh == meshId) end++;
            var mesh = ctx.GetMesh(new MeshHandle(meshId));
            cmd.SetVertexBuffer(0, mesh.Vertices);
            cmd.SetVertexBuffer(1, ctx.Ring.Buffer, instanceOffset);
            cmd.SetIndexBuffer(mesh.Indices, IndexFormat.UInt16);
            cmd.DrawIndexed(mesh.IndexCount, end - start, 0, 0, start);
            start = end;
        }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _group.Dispose();
        _layout.Dispose();
    }
}

/// <summary>Draws sprites: sorted by texture, one instanced draw (6 vertices per quad) per texture.</summary>
internal sealed class SpriteRendererSystem : ISystem, IDisposable
{
    private GraphicsPipeline _pipeline = null!;
    private BindGroupLayout _screenLayout = null!;
    private BindGroup _screenGroup = null!;
    private SpriteInstanceGpu[] _scratch = new SpriteInstanceGpu[1024];

    public string Name => "Rendering.Sprites";
    public Phase Phase => Phase.Render;
    public int Order => 100;

    public void Initialize(World world)
    {
        var ctx = world.Resource<RenderContext>();
        var device = ctx.Device;
        _screenLayout = device.CreateBindGroupLayout([new BindingLayout(0, BindingType.UniformBufferDynamic, ShaderStages.Vertex)]);
        _screenGroup = device.CreateBindGroup(_screenLayout, [BindGroupEntry.Of(0, ctx.Ring.Buffer, 0, 64)]);
        using var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Shaders.Load("sprite.vert")));
        using var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Shaders.Load("sprite.frag")));
        _pipeline = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            Name = "sprite",
            VertexShader = vs,
            FragmentShader = fs,
            BindGroupLayouts = [_screenLayout, ctx.SpriteTextureLayout!],
            VertexBuffers =
            [
                new VertexBufferLayout(64, VertexStep.Instance,
                    new VertexAttribute(0, VertexFormat.Float4, 0), new VertexAttribute(1, VertexFormat.Float4, 16),
                    new VertexAttribute(2, VertexFormat.Float4, 32), new VertexAttribute(3, VertexFormat.Float4, 48)),
            ],
            CullMode = CullMode.None,
            DepthTest = false,
            DepthWrite = false,
            Blend = BlendMode.Alpha,
            ColorFormats = [ctx.Swapchain.Format],
            DepthFormat = RenderContext.DepthFormat,
        });
    }

    public void Run(World world)
    {
        var ctx = world.Resource<RenderContext>();
        if (!ctx.HasFrame || ctx.SpriteInstances.Count == 0) return;
        var frame = ctx.Frame;

        // Pixels, origin bottom-left, y up.
        var proj = Matrix4x4.CreateOrthographicOffCenter(0, frame.Backbuffer.Width, 0, frame.Backbuffer.Height, -1, 1);
        uint screenOffset = (uint)ctx.Ring.Push(proj, ctx.Device.Limits.MinUniformBufferOffsetAlignment);

        var sprites = CollectionsMarshal.AsSpan(ctx.SpriteInstances);
        sprites.Sort(static (a, b) => a.Texture.CompareTo(b.Texture));
        if (_scratch.Length < sprites.Length) _scratch = new SpriteInstanceGpu[sprites.Length * 2];
        for (int i = 0; i < sprites.Length; i++)
        {
            ref readonly var s = ref sprites[i];
            _scratch[i] = new SpriteInstanceGpu { PosSize = s.PosSize, Uv = s.Uv, Color = s.Color, RotPivot = s.RotPivot };
        }
        long offset = ctx.Ring.Push<SpriteInstanceGpu>(_scratch.AsSpan(0, sprites.Length));

        var cmd = frame.Commands;
        cmd.SetPipeline(_pipeline);
        cmd.SetBindGroup(0, _screenGroup, [screenOffset]);
        cmd.SetVertexBuffer(0, ctx.Ring.Buffer, offset);
        int start = 0;
        while (start < sprites.Length)
        {
            int tex = sprites[start].Texture;
            int end = start;
            while (end < sprites.Length && sprites[end].Texture == tex) end++;
            cmd.SetBindGroup(1, ctx.GetTexture(new TextureHandle(tex)).Group);
            cmd.Draw(6, end - start, 0, start);
            start = end;
        }
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        _screenGroup.Dispose();
        _screenLayout.Dispose();
    }
}

internal static class Shaders
{
    public static byte[] Load(string name)
    {
        using var s = typeof(Shaders).Assembly.GetManifestResourceStream("shaders/" + name + ".spv")
            ?? throw new InvalidOperationException($"Shader {name} is not embedded.");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }
}
