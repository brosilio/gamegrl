using System.Numerics;
using System.Runtime.InteropServices;
using Gamegrl.Rhi;
using Gamegrl.UI.Fonts;

namespace Gamegrl.UI;

/// <summary>
/// Draws <see cref="DrawList"/>s with one pipeline: per-frame geometry goes into an upload ring, each draw command
/// sets its scissor and texture. Textures get a bind group on first use (cached; forget one when it's disposed).
/// </summary>
public sealed class UiRenderer : IDisposable
{
    private readonly GpuDevice _device;
    private readonly UploadRing _ring;
    private readonly GraphicsPipeline _pipeline;
    private readonly BindGroupLayout _screenLayout, _textureLayout;
    private readonly BindGroup _screenGroup;
    private readonly GpuSampler _linear;
    private readonly Dictionary<GpuTexture, BindGroup> _groups = new(ReferenceEqualityComparer.Instance);
    private ulong _frame = ulong.MaxValue;

    public UiRenderer(GpuDevice device, TextureFormat colorFormat, FontAtlas font)
    {
        _device = device;
        _ring = new UploadRing(device, 8 << 20, "ui ring");
        _screenLayout = device.CreateBindGroupLayout([new BindingLayout(0, BindingType.UniformBufferDynamic, ShaderStages.Vertex)]);
        _textureLayout = device.CreateBindGroupLayout(
        [
            new BindingLayout(0, BindingType.SampledTexture, ShaderStages.Fragment),
            new BindingLayout(1, BindingType.Sampler, ShaderStages.Fragment),
        ]);
        _screenGroup = device.CreateBindGroup(_screenLayout, [BindGroupEntry.Of(0, _ring.Buffer, 0, 16)]);
        _linear = device.CreateSampler(new SamplerDesc(Filter.Linear, AddressMode.ClampToEdge));
        using var vs = device.CreateShader(new ShaderDesc(ShaderStages.Vertex, Load("ui.vert")));
        using var fs = device.CreateShader(new ShaderDesc(ShaderStages.Fragment, Load("ui.frag")));
        _pipeline = device.CreateGraphicsPipeline(new GraphicsPipelineDesc
        {
            Name = "ui",
            VertexShader = vs,
            FragmentShader = fs,
            BindGroupLayouts = [_screenLayout, _textureLayout],
            VertexBuffers =
            [
                new VertexBufferLayout(20, VertexStep.Vertex, new VertexAttribute(0, VertexFormat.Float2, 0),
                    new VertexAttribute(1, VertexFormat.Float2, 8), new VertexAttribute(2, VertexFormat.UByte4Norm, 16)),
            ],
            CullMode = CullMode.None,
            DepthTest = false,
            DepthWrite = false,
            Blend = BlendMode.Alpha,
            ColorFormats = [colorFormat],
        });
        FontTexture = device.CreateTexture(new TextureDesc(font.Width, font.Height, TextureFormat.Rgba8Unorm, TextureUsage.Sampled, Name: "font atlas"));
        device.WriteTexture(FontTexture, font.Rgba);
    }

    public GpuTexture FontTexture { get; }

    /// <summary>Drop the cached bind group of a texture that's about to be disposed (e.g. a resized render target).</summary>
    public void Forget(GpuTexture texture)
    {
        if (_groups.Remove(texture, out var g)) g.Dispose();
    }

    /// <summary>
    /// Record <paramref name="list"/> into the current render pass, whose target is <paramref name="width"/> ×
    /// <paramref name="height"/> pixels. <paramref name="scale"/> is pixels per UI unit: 2 on a HiDPI window, 1/8 when
    /// a host shows the game at a reduced preview size. Several lists may be drawn in one frame.
    /// </summary>
    private static readonly ProfileMarker GpuMarker = ProfileMarker.Get("GPU.UI", ProfileCategory.Gpu);

    public void Render(Frame frame, DrawList list, int width, int height, float scale = 1)
    {
        using var gpu = frame.Commands.Zone(GpuMarker);
        if (_frame != frame.Number)
        {
            _frame = frame.Number;
            _ring.BeginFrame(frame.FrameIndex);
        }
        if (list.IsEmpty || width <= 0 || height <= 0) return;
        var cmd = frame.Commands;
        uint screenOffset = (uint)_ring.Push(new Vector4(width / scale, height / scale, scale / width, scale / height), _device.Limits.MinUniformBufferOffsetAlignment);
        long vertexOffset = _ring.Push(list.Vertices);
        long indexOffset = _ring.Push(list.Indices);

        cmd.SetPipeline(_pipeline);
        cmd.SetBindGroup(0, _screenGroup, [screenOffset]);
        cmd.SetVertexBuffer(0, _ring.Buffer, vertexOffset);
        cmd.SetIndexBuffer(_ring.Buffer, IndexFormat.UInt32, indexOffset);
        GpuTexture? bound = null;
        var screen = new Rect(0, 0, width, height);
        foreach (var c in list.Commands)
        {
            var clip = new Rect(c.Clip.X * scale, c.Clip.Y * scale, c.Clip.W * scale, c.Clip.H * scale).Intersect(screen);
            if (clip.W <= 0 || clip.H <= 0) continue;
            var texture = c.Texture ?? FontTexture;
            if (!ReferenceEquals(texture, bound))
            {
                cmd.SetBindGroup(1, GroupFor(texture));
                bound = texture;
            }
            int x = (int)clip.X, y = (int)clip.Y;
            cmd.SetScissor(x, y, (int)MathF.Ceiling(clip.Right) - x, (int)MathF.Ceiling(clip.Bottom) - y);
            cmd.DrawIndexed(c.IndexCount, 1, c.IndexStart, 0, 0);
        }
        cmd.SetScissor(0, 0, width, height);
    }

    /// <summary>Draw <paramref name="list"/> over what <paramref name="target"/> already holds, in a render pass of its own.</summary>
    public void RenderOnto(Frame frame, GpuTexture target, DrawList list, float scale = 1)
    {
        if (list.IsEmpty) return;
        frame.Commands.BeginRenderPass(new RenderPassDesc { Color = new ColorAttachment { Texture = target, Load = LoadOp.Load, Store = StoreOp.Store } });
        Render(frame, list, target.Width, target.Height, scale);
        frame.Commands.EndRenderPass();
    }

    private BindGroup GroupFor(GpuTexture texture)
    {
        if (!_groups.TryGetValue(texture, out var g))
            _groups[texture] = g = _device.CreateBindGroup(_textureLayout, [BindGroupEntry.Of(0, texture), BindGroupEntry.Of(1, _linear)]);
        return g;
    }

    private static byte[] Load(string name)
    {
        using var s = typeof(UiRenderer).Assembly.GetManifestResourceStream("shaders/" + name + ".spv")
            ?? throw new InvalidOperationException($"Shader {name} is not embedded.");
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    public void Dispose()
    {
        foreach (var g in _groups.Values) g.Dispose();
        _groups.Clear();
        FontTexture.Dispose();
        _pipeline.Dispose();
        _screenGroup.Dispose();
        _screenLayout.Dispose();
        _textureLayout.Dispose();
        _linear.Dispose();
        _ring.Dispose();
    }
}

