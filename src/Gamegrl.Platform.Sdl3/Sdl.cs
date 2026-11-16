using System.Runtime.InteropServices;

namespace Gamegrl.Platform.Sdl3;

/// <summary>The slice of the SDL3 C API we use. Values from SDL 3.2+ headers (SDL_init.h, SDL_video.h, SDL_events.h).</summary>
internal static unsafe partial class Sdl
{
    private const string Lib = "SDL3";

    public const uint InitVideo = 0x20, InitEvents = 0x4000;
    public const ulong WindowResizable = 0x20, WindowHighPixelDensity = 0x2000, WindowVulkan = 0x10000000;

    public const uint EventQuit = 0x100;
    public const uint EventWindowShown = 0x202, EventWindowMinimized = 0x209, EventWindowMaximized = 0x20A,
        EventWindowRestored = 0x20B, EventWindowFocusLost = 0x20F, EventWindowCloseRequested = 0x210;
    public const uint EventKeyDown = 0x300, EventKeyUp = 0x301, EventTextInput = 0x303;
    public const uint EventMouseMotion = 0x400, EventMouseButtonDown = 0x401, EventMouseButtonUp = 0x402, EventMouseWheel = 0x403;

    private static bool _loaded;

    /// <summary>Resolve "SDL3" to the platform's library name (libSDL3.so.0, SDL3.dll, libSDL3.dylib).</summary>
    public static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        NativeLibrary.SetDllImportResolver(typeof(Sdl).Assembly, static (name, assembly, path) =>
        {
            if (name != Lib) return 0;
            string[] candidates = OperatingSystem.IsWindows() ? ["SDL3.dll"]
                : OperatingSystem.IsMacOS() ? ["libSDL3.0.dylib", "libSDL3.dylib"]
                : ["libSDL3.so.0", "libSDL3.so"];
            foreach (var c in candidates)
                if (NativeLibrary.TryLoad(c, assembly, path, out var h)) return h;
            return 0;
        });
    }

    public static string Error() => Marshal.PtrToStringUTF8(GetError()) ?? "(no error)";

    [LibraryImport(Lib, EntryPoint = "SDL_Init")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool Init(uint flags);

    [LibraryImport(Lib, EntryPoint = "SDL_Quit")]
    public static partial void Quit();

    [LibraryImport(Lib, EntryPoint = "SDL_GetError")]
    private static partial nint GetError();

    [LibraryImport(Lib, EntryPoint = "SDL_CreateWindow")]
    public static partial nint CreateWindow(byte* title, int w, int h, ulong flags);

    [LibraryImport(Lib, EntryPoint = "SDL_DestroyWindow")]
    public static partial void DestroyWindow(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowID")]
    public static partial uint GetWindowID(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowSizeInPixels")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool GetWindowSizeInPixels(nint window, int* w, int* h);

    [LibraryImport(Lib, EntryPoint = "SDL_PollEvent")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool PollEvent(SdlEvent* e);

    [LibraryImport(Lib, EntryPoint = "SDL_StartTextInput")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool StartTextInput(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetWindowPixelDensity")]
    public static partial float GetWindowPixelDensity(nint window);

    [LibraryImport(Lib, EntryPoint = "SDL_GetClipboardText")]
    public static partial nint GetClipboardText();

    [LibraryImport(Lib, EntryPoint = "SDL_SetClipboardText")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetClipboardText(byte* text);

    [LibraryImport(Lib, EntryPoint = "SDL_free")]
    public static partial void Free(nint memory);

    [LibraryImport(Lib, EntryPoint = "SDL_CreateSystemCursor")]
    public static partial nint CreateSystemCursor(int id);

    [LibraryImport(Lib, EntryPoint = "SDL_SetCursor")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool SetCursor(nint cursor);

    [LibraryImport(Lib, EntryPoint = "SDL_Vulkan_GetInstanceExtensions")]
    public static partial byte** VulkanGetInstanceExtensions(uint* count);

    [LibraryImport(Lib, EntryPoint = "SDL_Vulkan_CreateSurface")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static partial bool VulkanCreateSurface(nint window, nint instance, nint allocator, ulong* surface);
}

// SDL_Event is a 128-byte union; the variants we read share its first fields.
[StructLayout(LayoutKind.Explicit, Size = 128)]
internal struct SdlEvent
{
    [FieldOffset(0)] public uint Type;
    [FieldOffset(0)] public SdlWindowEvent Window;
    [FieldOffset(0)] public SdlKeyboardEvent Key;
    [FieldOffset(0)] public SdlMouseMotionEvent Motion;
    [FieldOffset(0)] public SdlMouseButtonEvent Button;
    [FieldOffset(0)] public SdlMouseWheelEvent Wheel;
    [FieldOffset(0)] public SdlTextInputEvent Text;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlTextInputEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId;
    public nint Text; // const char*, valid until the next poll
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlWindowEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId;
    public int Data1, Data2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlKeyboardEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId, Which;
    public uint Scancode, Keycode;
    public ushort Mod, Raw;
    public byte Down, Repeat;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlMouseMotionEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId, Which, State;
    public float X, Y, XRel, YRel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlMouseButtonEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId, Which;
    public byte Button, Down, Clicks, Padding;
    public float X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SdlMouseWheelEvent
{
    public uint Type, Reserved;
    public ulong Timestamp;
    public uint WindowId, Which;
    public float X, Y;
}
