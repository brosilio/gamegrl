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
public interface IOffscreenWindow : IWindow;

public interface IWindow : IDisposable
{
    /// <summary>The drawable size in pixels (not points), which is what swapchains need.</summary>
    (int Width, int Height) PixelSize { get; }

    bool CloseRequested { get; }
    /// <summary>Dismiss a close request after a host chooses to keep the window open.</summary>
    void CancelCloseRequest();

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
    /// <summary>Dismiss a quit request (for example, while an editor resolves unsaved work).</summary>
    void CancelQuitRequest();

    /// <summary>The system clipboard's text (empty if none).</summary>
    string GetClipboardText();

    void SetClipboardText(string text);

    /// <summary>The mouse cursor shape (editors change it over splitters and text fields).</summary>
    void SetCursor(CursorShape shape);
}

public enum CursorShape { Arrow, Text, ResizeHorizontal, ResizeVertical, Hand, Move }

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
    private static readonly ProfileMarker PumpMarker = ProfileMarker.Get("Host.PumpEvents", ProfileCategory.Engine);
    public static void Run(App app)
    {
        var ctx = app.World.Resource<PlatformContext>();
        var input = app.World.Resource<Input>();
        // GAMEGRL_RECORD=session.ggin records input and time steps; GAMEGRL_REPLAY=session.ggin replays them (the
        // window's own events are ignored, and the run ends with the recording).
        string? recordPath = Environment.GetEnvironmentVariable("GAMEGRL_RECORD");
        string? replayPath = Environment.GetEnvironmentVariable("GAMEGRL_REPLAY");
        InputRecording? replay = replayPath is { Length: > 0 } ? LoadRecording(replayPath) : null;
        InputRecording? record = replay is null && recordPath is { Length: > 0 } ? new InputRecording() : null;
        input.Recording = record;
        var discard = replay is null ? null : new Input();
        long last = System.Diagnostics.Stopwatch.GetTimestamp();
        for (int frame = 0; !app.QuitRequested; frame++)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double dt = System.Diagnostics.Stopwatch.GetElapsedTime(last, now).TotalSeconds;
            last = now;
            if (replay is not null)
            {
                if (frame >= replay.FrameCount) break;
                dt = replay.Delta(frame);
            }
            // A frame starts before its events are pumped, so they're recorded against the frame that sees them.
            record?.BeginFrame(dt);
            input.BeginFrame();
            using (PumpMarker.Scope())
            {
                if (replay is null) ctx.Platform.PumpEvents(input);
                else { discard!.BeginFrame(); ctx.Platform.PumpEvents(discard); replay.Apply(frame, input); }
            }
            if (ctx.Platform.QuitRequested || ctx.MainWindow.CloseRequested) { record?.DiscardLastFrame(); break; }
            // Every recorded frame must tick, so recording and replay don't idle while minimized.
            if (ctx.MainWindow.Minimized && record is null && replay is null)
            {
                Thread.Sleep(16);
                continue;
            }
            app.Tick(dt);
        }
        if (record is not null)
        {
            using (var file = File.Create(recordPath!)) record.Save(file);
            Console.WriteLine($"gamegrl: recorded {record.FrameCount} frames of input to {recordPath}");
        }
        // GAMEGRL_PROFILE=1 GAMEGRL_PROFILE_OUT=trace.json profiles a player run (NativeAOT included) for Perfetto.
        if (Profiler.Enabled && Environment.GetEnvironmentVariable("GAMEGRL_PROFILE_OUT") is { Length: > 0 } path)
        {
            long oldest = Profiler.OldestFrame;
            using (var writer = new StreamWriter(path)) ProfileTree.WriteChromeTrace(writer, oldest, (int)(Profiler.FrameCount - oldest));
            Console.WriteLine($"gamegrl: profile of the last {Profiler.FrameCount - oldest} frames written to {path}");
        }
    }

    private static InputRecording LoadRecording(string path)
    {
        using var file = File.OpenRead(path);
        return InputRecording.Load(file);
    }
}
