using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Rhi;

namespace Gamegrl.Rendering;

/// <summary>Extract systems: copy what's visible out of the world into the frame's instance lists.</summary>
public static partial class RenderExtract
{
    [System(Phase.Render, Order = -500)]
    private static void CollectMeshes(in LocalToWorld transform, in MeshRenderer renderer, [Res] in RenderContext ctx)
    {
        if (!ctx.HasFrame) return;
        int slot = ctx.ResolveMesh(renderer.Mesh);
        if (slot >= 0) ctx.MeshInstances.Add(new MeshInstance { Model = transform.Value, Color = renderer.Color, Mesh = slot });
    }

    [System(Phase.Render, Order = -500)]
    private static void CollectSprites(in LocalTransform transform, in Sprite sprite, [Res] in RenderContext ctx)
    {
        if (!ctx.HasFrame) return;
        int slot = ctx.ResolveTexture(sprite.Texture);
        if (slot < 0) return;
        float angle = 2f * MathF.Atan2(transform.Rotation.Z, transform.Rotation.W); // rotation about z
        var (sin, cos) = MathF.SinCos(angle);
        ctx.SpriteInstances.Add(new SpriteInstance
        {
            PosSize = new Vector4(transform.Position.X, transform.Position.Y, sprite.Size.X * transform.Scale.X, sprite.Size.Y * transform.Scale.Y),
            Uv = sprite.UvRect,
            Color = sprite.Color,
            RotPivot = new Vector4(cos, sin, sprite.Pivot.X, sprite.Pivot.Y),
            Texture = slot,
        });
    }
}

/// <summary>What to draw a view of the world into, and from where.</summary>
public struct ViewDesc
{
    public GpuTexture Color;
    public GpuTexture Depth;
    public Matrix4x4 ViewProjection;
    public Vector4 ClearColor;
    public bool Sprites;
    /// <summary>Logical sprite coordinates, independent of preview resolution; zero uses the target size.</summary>
    public Vector2 SpriteSize;
    public bool DebugLines;
}

/// <summary>Lines for gizmos and debugging, drawn in views that ask for them; cleared every frame.</summary>
public sealed class DebugDraw
{
    internal readonly List<LineVertex> Vertices = new();

    public void Line(Vector3 a, Vector3 b, Vector4 color)
    {
        Vertices.Add(new LineVertex(a, color));
        Vertices.Add(new LineVertex(b, color));
    }

    public void Box(Matrix4x4 transform, Vector3 min, Vector3 max, Vector4 color)
    {
        Span<Vector3> c = stackalloc Vector3[8];
        for (int i = 0; i < 8; i++)
            c[i] = Vector3.Transform(new Vector3((i & 1) != 0 ? max.X : min.X, (i & 2) != 0 ? max.Y : min.Y, (i & 4) != 0 ? max.Z : min.Z), transform);
        ReadOnlySpan<int> edges = [0, 1, 2, 3, 4, 5, 6, 7, 0, 2, 1, 3, 4, 6, 5, 7, 0, 4, 1, 5, 2, 6, 3, 7];
        for (int i = 0; i < edges.Length; i += 2) Line(c[edges[i]], c[edges[i + 1]], color);
    }

    public void Clear() => Vertices.Clear();
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct LineVertex(Vector3 Position, Vector4 Color);

/// <summary>
/// Draws views of the world: instanced meshes, sprites and debug lines into any color + depth target. The game
/// draws one view from its camera; the editor draws a Scene view, a Game view and then its UI.
/// </summary>
public sealed class SceneRenderer : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    private struct CameraConstants
    {
        public Matrix4x4 ViewProj;
        public Vector4 LightDir;
        public Vector4 Ambient;
    }

    private readonly RenderContext _ctx;
    private readonly GraphicsPipeline _meshes, _sprites, _lines;
    private readonly BindGroupLayout _cameraLayout, _screenLayout;
    private readonly BindGroup _cameraGroup, _screenGroup;
    private MeshInstanceGpu[] _meshScratch = new MeshInstanceGpu[1024];
    private SpriteInstanceGpu[] _spriteScratch = new SpriteInstanceGpu[256];
    private long _meshOffset = -1, _spriteOffset = -1, _lineOffset = -1;
    private ulong _uploadedFrame = ulong.MaxValue;

    public SceneRenderer(RenderContext ctx)
    {
        _ctx = ctx;
        var device = ctx.Device;
        var format = ctx.Swapchain.Format;
        _cameraLayout = device.CreateBindGroupLayout([new BindingLayout(0, BindingType.UniformBufferDynamic, ShaderStages.Graphics)]);
        _cameraGroup = device.CreateBindGroup(_cameraLayout, [BindGroupEntry.Of(0, ctx.Ring.Buffer, 0, Marshal.SizeOf<CameraConstants>())]);
        _screenLayout = device.CreateBindGroupLayout([new BindingLayout(0, BindingType.UniformBufferDynamic, ShaderStages.Vertex)]);
        _screenGroup = device.CreateBindGroup(_screenLayout, [BindGroupEntry.Of(0, ctx.Ring.Buffer, 0, 64)]);

        using (var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Shaders.Load("mesh.vert"))))
        using (var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Shaders.Load("mesh.frag"))))
            _meshes = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                Name = "mesh",
                VertexShader = vs,
                FragmentShader = fs,
                BindGroupLayouts = [_cameraLayout],
                VertexBuffers =
                [
                    new VertexBufferLayout(32, VertexStep.Vertex, new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Float3, 12),
                        new VertexAttribute(7, VertexFormat.Float2, 24)),
                    new VertexBufferLayout(80, VertexStep.Instance,
                        new VertexAttribute(2, VertexFormat.Float4, 0), new VertexAttribute(3, VertexFormat.Float4, 16),
                        new VertexAttribute(4, VertexFormat.Float4, 32), new VertexAttribute(5, VertexFormat.Float4, 48),
                        new VertexAttribute(6, VertexFormat.Float4, 64)),
                ],
                ColorFormats = [format],
                DepthFormat = RenderContext.DepthFormat,
            });

        using (var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Shaders.Load("sprite.vert"))))
        using (var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Shaders.Load("sprite.frag"))))
            _sprites = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
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
                ColorFormats = [format],
                DepthFormat = RenderContext.DepthFormat,
            });

        using (var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Shaders.Load("line.vert"))))
        using (var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Shaders.Load("line.frag"))))
            _lines = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
            {
                Name = "debug lines",
                VertexShader = vs,
                FragmentShader = fs,
                BindGroupLayouts = [_cameraLayout],
                VertexBuffers = [new VertexBufferLayout(28, VertexStep.Vertex, new VertexAttribute(0, VertexFormat.Float3, 0), new VertexAttribute(1, VertexFormat.Float4, 12))],
                Topology = PrimitiveTopology.LineList,
                CullMode = CullMode.None,
                DepthTest = false,
                DepthWrite = false,
                Blend = BlendMode.Alpha,
                ColorFormats = [format],
                DepthFormat = RenderContext.DepthFormat,
            });
    }

    /// <summary>Draw one view: clears the target, then meshes, sprites (screen pixels of the target) and debug lines.</summary>
    public void DrawView(CommandList cmd, in ViewDesc view)
    {
        if (!_ctx.HasFrame) return;
        UploadInstancesOnce();
        var ring = _ctx.Ring;
        int align = _ctx.Device.Limits.MinUniformBufferOffsetAlignment;

        cmd.BeginRenderPass(new RenderPassDesc
        {
            Color = new ColorAttachment { Texture = view.Color, Load = LoadOp.Clear, Store = StoreOp.Store, ClearColor = view.ClearColor },
            Depth = new DepthAttachment { Texture = view.Depth, Load = LoadOp.Clear, Store = StoreOp.DontCare, ClearDepth = 0 },
        });

        var constants = new CameraConstants
        {
            ViewProj = view.ViewProjection,
            LightDir = new Vector4(Vector3.Normalize(new Vector3(-0.4f, -1f, -0.6f)), 0),
            Ambient = new Vector4(0.18f, 0.2f, 0.25f, 0),
        };
        uint cameraOffset = (uint)ring.Push(constants, align);

        var meshes = CollectionsMarshal.AsSpan(_ctx.MeshInstances);
        if (meshes.Length > 0)
        {
            cmd.SetPipeline(_meshes);
            cmd.SetBindGroup(0, _cameraGroup, [cameraOffset]);
            int start = 0;
            while (start < meshes.Length)
            {
                int slot = meshes[start].Mesh;
                int end = start;
                while (end < meshes.Length && meshes[end].Mesh == slot) end++;
                var mesh = _ctx.GetMesh(slot);
                cmd.SetVertexBuffer(0, mesh.Vertices);
                cmd.SetVertexBuffer(1, ring.Buffer, _meshOffset);
                cmd.SetIndexBuffer(mesh.Indices, mesh.IndexFormat);
                cmd.DrawIndexed(mesh.IndexCount, end - start, 0, 0, start);
                start = end;
            }
        }

        var sprites = CollectionsMarshal.AsSpan(_ctx.SpriteInstances);
        if (view.Sprites && sprites.Length > 0)
        {
            var proj = Matrix4x4.CreateOrthographicOffCenter(0, view.SpriteSize.X > 0 ? view.SpriteSize.X : view.Color.Width, 0, view.SpriteSize.Y > 0 ? view.SpriteSize.Y : view.Color.Height, -1, 1);
            uint screenOffset = (uint)ring.Push(proj, align);
            cmd.SetPipeline(_sprites);
            cmd.SetBindGroup(0, _screenGroup, [screenOffset]);
            cmd.SetVertexBuffer(0, ring.Buffer, _spriteOffset);
            int start = 0;
            while (start < sprites.Length)
            {
                int tex = sprites[start].Texture;
                int end = start;
                while (end < sprites.Length && sprites[end].Texture == tex) end++;
                cmd.SetBindGroup(1, _ctx.GetTexture(tex).Group);
                cmd.Draw(6, end - start, 0, start);
                start = end;
            }
        }

        if (view.DebugLines && _lineOffset >= 0 && _ctx.Debug.Vertices.Count > 0)
        {
            cmd.SetPipeline(_lines);
            cmd.SetBindGroup(0, _cameraGroup, [cameraOffset]);
            cmd.SetVertexBuffer(0, ring.Buffer, _lineOffset);
            cmd.Draw(_ctx.Debug.Vertices.Count);
        }
        cmd.EndRenderPass();
    }

    /// <summary>Sort and upload the frame's instances once, however many views draw them.</summary>
    private void UploadInstancesOnce()
    {
        if (_uploadedFrame == _ctx.Frame.Number) return;
        _uploadedFrame = _ctx.Frame.Number;
        var ring = _ctx.Ring;

        var meshes = CollectionsMarshal.AsSpan(_ctx.MeshInstances);
        meshes.Sort(static (a, b) => a.Mesh.CompareTo(b.Mesh));
        if (_meshScratch.Length < meshes.Length) _meshScratch = new MeshInstanceGpu[meshes.Length * 2];
        for (int i = 0; i < meshes.Length; i++) _meshScratch[i] = new MeshInstanceGpu { Model = meshes[i].Model, Color = meshes[i].Color };
        _meshOffset = meshes.Length > 0 ? ring.Push<MeshInstanceGpu>(_meshScratch.AsSpan(0, meshes.Length)) : -1;

        var sprites = CollectionsMarshal.AsSpan(_ctx.SpriteInstances);
        sprites.Sort(static (a, b) => a.Texture.CompareTo(b.Texture));
        if (_spriteScratch.Length < sprites.Length) _spriteScratch = new SpriteInstanceGpu[sprites.Length * 2];
        for (int i = 0; i < sprites.Length; i++)
        {
            ref readonly var s = ref sprites[i];
            _spriteScratch[i] = new SpriteInstanceGpu { PosSize = s.PosSize, Uv = s.Uv, Color = s.Color, RotPivot = s.RotPivot };
        }
        _spriteOffset = sprites.Length > 0 ? ring.Push<SpriteInstanceGpu>(_spriteScratch.AsSpan(0, sprites.Length)) : -1;

        var lines = CollectionsMarshal.AsSpan(_ctx.Debug.Vertices);
        _lineOffset = lines.Length > 0 ? ring.Push<LineVertex>(lines) : -1;
    }

    public void Dispose()
    {
        _meshes.Dispose();
        _sprites.Dispose();
        _lines.Dispose();
        _cameraGroup.Dispose();
        _screenGroup.Dispose();
        _cameraLayout.Dispose();
        _screenLayout.Dispose();
    }
}

/// <summary>Acquires the swapchain image and starts the frame (no render pass yet: views and the UI open their own).</summary>
internal sealed class BeginFrameSystem : ISystem
{
    public string Name => "Rendering.BeginFrame";
    public Phase Phase => Phase.Render;
    public int Order => -1000;
    public void Initialize(World world) { }

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
        if (ctx.DrawMainCamera)
            world.Resource<ScreenSize>() = new ScreenSize { Width = frame.Backbuffer.Width, Height = frame.Backbuffer.Height };
    }
}

/// <summary>The game's view: the first camera, drawn into the window.</summary>
public sealed class MainCameraSystem : ISystem
{
    private Query _cameras = null!;

    public string Name => "Rendering.MainCamera";
    public Phase Phase => Phase.Render;
    public int Order => 0;

    public void Initialize(World world) => _cameras = world.Query(new QueryDesc().With<Camera>().With<LocalToWorld>());

    public void Run(World world)
    {
        var ctx = world.Resource<RenderContext>();
        if (!ctx.HasFrame || !ctx.DrawMainCamera) return;
        var frame = ctx.Frame;
        var view = new ViewDesc { Color = frame.Backbuffer, Depth = ctx.Depth, Sprites = true, ClearColor = new Vector4(0.05f, 0.06f, 0.09f, 1) };
        if (TryMainCamera(_cameras, frame.Backbuffer.Width / (float)Math.Max(1, frame.Backbuffer.Height), out var viewProj, out var clear))
        {
            view.ViewProjection = viewProj;
            view.ClearColor = clear;
        }
        ctx.Scene.DrawView(frame.Commands, view);
    }

    /// <summary>The first camera's view-projection and clear color, for a target with this aspect ratio.</summary>
    public static bool TryMainCamera(Query cameras, float aspect, out Matrix4x4 viewProjection, out Vector4 clear)
    {
        foreach (var a in cameras.Archetypes)
        {
            if (a.Count == 0) continue;
            var camera = a.GetSpan<Camera>()[0];
            Matrix4x4.Invert(a.GetSpan<LocalToWorld>()[0].Value, out var view);
            viewProjection = view * camera.Projection(aspect);
            clear = camera.ClearColor;
            return true;
        }
        viewProjection = Matrix4x4.Identity;
        clear = default;
        return false;
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
        ctx.Device.EndFrame(ctx.Frame);
        ctx.HasFrame = false;
        ctx.Debug.Clear();
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
