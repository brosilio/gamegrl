namespace Gamegrl.Platform;

public struct WindowDesc
{
    public string Title;
    public int Width;
    public int Height;
    public bool Resizable;

    public static WindowDesc Default(string title) => new() { Title = title, Width = 1280, Height = 720, Resizable = true };
}

/// <summary>An OS window. Engine code never sees the backend's types.</summary>
public interface IWindow : IDisposable
{
    /// <summary>The drawable size in pixels (not points), which is what swapchains need.</summary>
    (int Width, int Height) PixelSize { get; }

    bool CloseRequested { get; }

    bool Minimized { get; }

    /// <summary>Instance extensions a Vulkan instance needs to present to this window.</summary>
    string[] VulkanInstanceExtensions { get; }

    /// <summary>Create a VkSurfaceKHR for this window on <paramref name="vkInstance"/>.</summary>
    ulong CreateVulkanSurface(nint vkInstance);
}

/// <summary>The OS layer: windows and the event pump. One per process.</summary>
public interface IPlatform : IDisposable
{
    string Name { get; }

    IWindow CreateWindow(in WindowDesc desc);

    /// <summary>Drain OS events into <paramref name="input"/>; called by the frame host before each tick.</summary>
    void PumpEvents(Input input);

    /// <summary>The OS asked the app to quit (last window closed, SIGTERM, ...).</summary>
    bool QuitRequested { get; }
}

/// <summary>The platform and main window, as a world resource.</summary>
public sealed class PlatformContext(IPlatform platform, IWindow mainWindow)
{
    public IPlatform Platform { get; } = platform;
    public IWindow MainWindow { get; } = mainWindow;
}

/// <summary>
/// Runs an app on a desktop OS: pump events, tick, repeat, until the window closes or something asks to quit.
/// Other hosts (the browser, an XR runtime) drive <see cref="App.Tick()"/> their own way.
/// </summary>
public static class DesktopHost
{
    public static void Run(App app)
    {
        var ctx = app.World.Resource<PlatformContext>();
        var input = app.World.Resource<Input>();
        while (!app.QuitRequested)
        {
            input.BeginFrame();
            ctx.Platform.PumpEvents(input);
            if (ctx.Platform.QuitRequested || ctx.MainWindow.CloseRequested) break;
            if (ctx.MainWindow.Minimized)
            {
                Thread.Sleep(16);
                continue;
            }
            app.Tick();
        }
    }
}
