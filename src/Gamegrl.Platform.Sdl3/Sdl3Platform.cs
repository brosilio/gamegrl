using System.Numerics;
using System.Runtime.InteropServices;

namespace Gamegrl.Platform.Sdl3;

/// <summary>Windows, input and events through SDL3. Nothing outside this assembly sees an SDL type.</summary>
public sealed unsafe class Sdl3Platform : IPlatform
{
    private readonly List<Sdl3Window> _windows = new();
    private bool _disposed;

    public Sdl3Platform()
    {
        Sdl.Load();
        if (!Sdl.Init(Sdl.InitVideo | Sdl.InitEvents)) throw new InvalidOperationException("SDL_Init failed: " + Sdl.Error());
    }

    public string Name => "SDL3";

    public bool QuitRequested { get; private set; }

    public IWindow CreateWindow(in WindowDesc desc)
    {
        ulong flags = Sdl.WindowVulkan | Sdl.WindowHighPixelDensity | (desc.Resizable ? Sdl.WindowResizable : 0);
        nint handle;
        fixed (byte* title = Utf8(desc.Title)) handle = Sdl.CreateWindow(title, desc.Width, desc.Height, flags);
        if (handle == 0) throw new InvalidOperationException("SDL_CreateWindow failed: " + Sdl.Error());
        var w = new Sdl3Window(this, handle);
        _windows.Add(w);
        return w;
    }

    internal void Forget(Sdl3Window w) => _windows.Remove(w);

    public void PumpEvents(Input input)
    {
        SdlEvent e;
        while (Sdl.PollEvent(&e))
        {
            switch (e.Type)
            {
                case Sdl.EventQuit:
                    QuitRequested = true;
                    break;
                case Sdl.EventWindowCloseRequested:
                    Find(e.Window.WindowId)?.OnClose();
                    break;
                case Sdl.EventWindowMinimized:
                    Find(e.Window.WindowId)?.SetMinimized(true);
                    break;
                case Sdl.EventWindowRestored:
                case Sdl.EventWindowMaximized:
                case Sdl.EventWindowShown:
                    Find(e.Window.WindowId)?.SetMinimized(false);
                    break;
                case Sdl.EventKeyDown:
                case Sdl.EventKeyUp:
                    input.OnKey((int)e.Key.Scancode, e.Key.Down != 0, e.Key.Repeat != 0); // SDL scancodes are USB HID usages
                    break;
                case Sdl.EventMouseMotion:
                    input.OnMouseMove(new Vector2(e.Motion.X, e.Motion.Y), new Vector2(e.Motion.XRel, e.Motion.YRel));
                    break;
                case Sdl.EventMouseButtonDown:
                case Sdl.EventMouseButtonUp:
                    input.OnMouseButton(e.Button.Button - 1, e.Button.Down != 0); // SDL buttons are 1-based
                    break;
                case Sdl.EventMouseWheel:
                    input.OnWheel(new Vector2(e.Wheel.X, e.Wheel.Y));
                    break;
            }
        }
    }

    private Sdl3Window? Find(uint id)
    {
        foreach (var w in _windows)
            if (w.Id == id) return w;
        return null;
    }

    internal static byte[] Utf8(string s)
    {
        var bytes = new byte[System.Text.Encoding.UTF8.GetByteCount(s) + 1];
        System.Text.Encoding.UTF8.GetBytes(s, bytes);
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var w in _windows.ToArray()) w.Dispose();
        Sdl.Quit();
    }
}

internal sealed unsafe class Sdl3Window : IWindow
{
    private readonly Sdl3Platform _platform;
    private nint _handle;

    public Sdl3Window(Sdl3Platform platform, nint handle)
    {
        _platform = platform;
        _handle = handle;
        Id = Sdl.GetWindowID(handle);
    }

    public uint Id { get; }
    public bool CloseRequested { get; private set; }
    public bool Minimized { get; private set; }

    internal void OnClose() => CloseRequested = true;
    internal void SetMinimized(bool m) => Minimized = m;

    public (int Width, int Height) PixelSize
    {
        get
        {
            int w, h;
            Sdl.GetWindowSizeInPixels(_handle, &w, &h);
            return (w, h);
        }
    }

    public string[] VulkanInstanceExtensions
    {
        get
        {
            uint count;
            byte** names = Sdl.VulkanGetInstanceExtensions(&count);
            if (names == null) throw new InvalidOperationException("SDL_Vulkan_GetInstanceExtensions failed: " + Sdl.Error());
            var result = new string[count];
            for (int i = 0; i < count; i++) result[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
            return result;
        }
    }

    public ulong CreateVulkanSurface(nint vkInstance)
    {
        ulong surface;
        if (!Sdl.VulkanCreateSurface(_handle, vkInstance, 0, &surface))
            throw new InvalidOperationException("SDL_Vulkan_CreateSurface failed: " + Sdl.Error());
        return surface;
    }

    public void Dispose()
    {
        if (_handle == 0) return;
        Sdl.DestroyWindow(_handle);
        _handle = 0;
        _platform.Forget(this);
    }
}

/// <summary>The SDL platform module: creates the platform, the main window and the input resource.</summary>
public sealed class Sdl3PlatformModule(WindowDesc window) : IModule
{
    public void Build(EngineBuilder builder)
    {
        var platform = new Sdl3Platform();
        var main = platform.CreateWindow(window);
        builder.SetResource(new PlatformContext(platform, main));
        builder.SetResource(new Input());
        builder.OnStop(_ => platform.Dispose());
    }
}
