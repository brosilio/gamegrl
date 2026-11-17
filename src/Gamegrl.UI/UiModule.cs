using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.Rhi;
using Gamegrl.UI.Fonts;

namespace Gamegrl.UI;

/// <summary>Settings for <see cref="UiModule"/>.</summary>
public sealed class UiOptions
{
    /// <summary>
    /// Project path of a font asset (a .ttf), e.g. "Assets/fonts/ui.ttf", so the player ships it. Without one, a system
    /// font is used, which is fine for development and debug UI but may be missing on a player's machine.
    /// </summary>
    public string? Font;
    public float FontSize = 15;
    public Theme? Theme;
    /// <summary>Window pixels per UI unit (2 on a HiDPI display).</summary>
    public float Scale = 1;
}

/// <summary>
/// The pixel UI for games: adds a <see cref="Ui"/> resource that systems call during the frame (<c>[Res] in Ui ui</c>)
/// and draws it over the game's view, when the rendering module was added before it. A game adds it from its own module, so
/// the editor gets it too: there the Ui follows play mode, reads the Game view's input, and the editor draws it into
/// the Game view.
/// </summary>
public sealed class UiModule(UiOptions? options = null) : IModule
{
    public void Build(EngineBuilder builder)
    {
        var o = options ?? new UiOptions();
        var world = builder.World;
        // Without the rendering module (a headless server, a test) the UI still runs; it just isn't drawn.
        var render = world.HasResource<RenderContext>() ? world.Resource<RenderContext>() : null;
        var font = FontAtlas.Bake(LoadFont(world, o.Font), o.FontSize);
        var layer = new UiLayer(new Ui(font, o.Theme), render?.Device, render?.Swapchain.Format ?? default) { Scale = o.Scale };
        // Frame-scoped UI state isn't simulation state: play checkpoints keep the same instances.
        builder.SetResource(layer.Ui, ui => ui);
        builder.SetResource(layer, l => l);
        builder.AddSystem(new UiBeginSystem());
        builder.AddSystem(new UiEndSystem());
        if (render is not null) builder.AddSystem(new UiDrawSystem());
        builder.OnStop(_ => layer.ReleaseGpu());
    }

    private static TrueTypeFont LoadFont(World world, string? path)
    {
        if (path is not null)
        {
            var assets = world.HasResource<IAssetSource>() ? world.Resource<IAssetSource>() : null;
            Guid guid = assets?.FindByPath(path) ?? Guid.Empty;
            if (guid != Guid.Empty && assets!.TryRead(guid, out var kind, out var bytes) && kind == AssetKind.Font) return new TrueTypeFont(bytes.ToArray());
            Log.Warn($"ui: font asset {path} not found; using a system font.");
        }
        string? system = FontAtlas.FindSystemFont();
        if (system is null) throw new InvalidOperationException("No UI font: import a .ttf into the project and set UiOptions.Font.");
        return new TrueTypeFont(File.ReadAllBytes(system));
    }
}

/// <summary>
/// The game UI's frame and GPU side, as a world resource. The game's window draws it automatically; a host that draws
/// the game's view itself (the editor) calls <see cref="Render"/> with its own target.
/// </summary>
public sealed class UiLayer
{
    private readonly GpuDevice? _device;
    private readonly TextureFormat _format;
    private readonly Input _scaledInput = new();
    private UiRenderer? _renderer;

    internal UiLayer(Ui ui, GpuDevice? device, TextureFormat format)
    {
        Ui = ui;
        _device = device;
        _format = format;
    }

    public Ui Ui { get; }

    /// <summary>Target pixels per UI unit.</summary>
    public float Scale { get; set; } = 1;

    /// <summary>A frame has begun and not yet ended.</summary>
    internal bool InFrame { get; private set; }

    internal void Begin(Input input, Vector2 screenPixels, double time, IPlatform? platform)
    {
        if (Scale != 1)
        {
            _scaledInput.RouteFrom(input, true, default);
            _scaledInput.ScaleMouse(new Vector2(1 / Scale));
            input = _scaledInput;
        }
        Ui.Begin(input, screenPixels / Scale, time, platform);
        InFrame = true;
    }

    internal void End()
    {
        if (!InFrame) return;
        Ui.End();
        InFrame = false;
    }

    /// <summary>
    /// Draw this frame's UI over <paramref name="target"/>. <paramref name="viewScale"/> is target pixels per game
    /// pixel (below 1 when a host shows the game reduced).
    /// </summary>
    public void Render(Frame frame, GpuTexture target, float viewScale = 1)
    {
        if (Ui.Draw.IsEmpty || _device is null) return;
        _renderer ??= new UiRenderer(_device, _format, Ui.Font);
        _renderer.RenderOnto(frame, target, Ui.Draw, Scale * viewScale);
    }

    /// <summary>Free GPU objects; they're recreated on the next draw (the editor stops and starts play many times).</summary>
    public void ReleaseGpu()
    {
        _renderer?.Dispose();
        _renderer = null;
    }
}

/// <summary>Starts the UI frame after input is pumped, before game systems run.</summary>
internal sealed class UiBeginSystem : ISystem
{
    private readonly Input _none = new();
    public string Name => "UI.Begin";
    public Phase Phase => Phase.First;
    public int Order => 100;
    public void Initialize(World world) { }

    public void Run(World world)
    {
        var screen = world.HasResource<ScreenSize>() ? world.Resource<ScreenSize>() : default;
        var platform = world.HasResource<PlatformContext>() ? world.Resource<PlatformContext>().Platform : null;
        var input = world.HasResource<Input>() ? world.Resource<Input>() : _none;
        world.Resource<UiLayer>().Begin(input, new Vector2(screen.Width, screen.Height), world.Resource<Time>().Elapsed, platform);
    }
}

/// <summary>Ends the UI frame once game systems have had their say, before anything draws.</summary>
internal sealed class UiEndSystem : ISystem
{
    public string Name => "UI.End";
    public Phase Phase => Phase.Render;
    public int Order => -900;
    public void Initialize(World world) { }
    public void Run(World world) => world.Resource<UiLayer>().End();
}

/// <summary>Draws the UI over the window, after the main camera.</summary>
internal sealed class UiDrawSystem : ISystem
{
    public string Name => "UI.Draw";
    public Phase Phase => Phase.Render;
    public int Order => 900;
    public void Initialize(World world) { }

    public void Run(World world)
    {
        var render = world.Resource<RenderContext>();
        if (!render.HasFrame || !render.DrawMainCamera) return;
        world.Resource<UiLayer>().Render(render.Frame, render.Frame.Backbuffer);
    }
}
