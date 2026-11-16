using System.Numerics;

namespace Gamegrl.Platform;

/// <summary>Physical keys, numbered by USB HID usage (layout-independent: <see cref="W"/> is where W is on QWERTY).</summary>
public enum Key : ushort
{
    None = 0,
    A = 4, B, C, D, E, F, G, H, I, J, K, L, M, N, O, P, Q, R, S, T, U, V, W, X, Y, Z,
    D1 = 30, D2, D3, D4, D5, D6, D7, D8, D9, D0,
    Enter = 40, Escape = 41, Backspace = 42, Tab = 43, Space = 44,
    F1 = 58, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
    Insert = 73, Home = 74, PageUp = 75, Delete = 76, End = 77, PageDown = 78,
    Right = 79, Left = 80, Down = 81, Up = 82,
    LeftCtrl = 224, LeftShift = 225, LeftAlt = 226, LeftMeta = 227,
    RightCtrl = 228, RightShift = 229, RightAlt = 230, RightMeta = 231,
}

public enum MouseButton : byte { Left = 0, Middle = 1, Right = 2, X1 = 3, X2 = 4 }

/// <summary>
/// Raw device state for the current frame, as a world resource. Gameplay should prefer actions.
/// TODO(milestone 3): action maps (actions bound to keys, buttons, gamepads and XR controllers).
/// </summary>
public sealed class Input
{
    public const int KeyCount = 512;

    private readonly bool[] _down = new bool[KeyCount];
    private readonly bool[] _pressed = new bool[KeyCount];
    private readonly bool[] _released = new bool[KeyCount];
    private byte _mouseDown, _mousePressed, _mouseReleased;

    public Vector2 MousePosition { get; private set; }
    public Vector2 MouseDelta { get; private set; }
    public Vector2 Wheel { get; private set; }

    private readonly bool[] _repeated = new bool[KeyCount];
    private readonly System.Text.StringBuilder _text = new();

    public bool IsDown(Key k) => _down[(int)k];
    public bool WasPressed(Key k) => _pressed[(int)k];
    public bool WasReleased(Key k) => _released[(int)k];

    /// <summary>Pressed this frame, or auto-repeated by the OS while held (for text editing and navigation).</summary>
    public bool WasPressedOrRepeated(Key k) => _pressed[(int)k] || _repeated[(int)k];

    public bool Ctrl => _down[(int)Key.LeftCtrl] || _down[(int)Key.RightCtrl];
    public bool Shift => _down[(int)Key.LeftShift] || _down[(int)Key.RightShift];
    public bool Alt => _down[(int)Key.LeftAlt] || _down[(int)Key.RightAlt];

    /// <summary>Text typed this frame (layout- and IME-aware, from the OS), for text fields.</summary>
    public string TypedText => _text.ToString();

    public bool IsDown(MouseButton b) => (_mouseDown & (1 << (int)b)) != 0;
    public bool WasPressed(MouseButton b) => (_mousePressed & (1 << (int)b)) != 0;
    public bool WasReleased(MouseButton b) => (_mouseReleased & (1 << (int)b)) != 0;

    /// <summary>Clear per-frame edges; the host calls this before pumping events.</summary>
    public void BeginFrame()
    {
        Array.Clear(_pressed);
        Array.Clear(_released);
        Array.Clear(_repeated);
        _text.Clear();
        _mousePressed = _mouseReleased = 0;
        MouseDelta = Vector2.Zero;
        Wheel = Vector2.Zero;
    }

    // Platform backends feed events through these.
    public void OnKey(int hidUsage, bool down, bool repeat)
    {
        if ((uint)hidUsage >= KeyCount) return;
        if (repeat)
        {
            if (down) _repeated[hidUsage] = true;
            return;
        }
        if (down && !_down[hidUsage]) _pressed[hidUsage] = true;
        if (!down && _down[hidUsage]) _released[hidUsage] = true;
        _down[hidUsage] = down;
    }

    public void OnText(string text) => _text.Append(text);

    /// <summary>Forget held keys and buttons (focus lost: the releases will never arrive).</summary>
    public void ReleaseAll()
    {
        Array.Clear(_down);
        _mouseDown = 0;
    }

    public void OnMouseButton(int button, bool down)
    {
        if ((uint)button > 7) return;
        byte bit = (byte)(1 << button);
        if (down && (_mouseDown & bit) == 0) _mousePressed |= bit;
        if (!down && (_mouseDown & bit) != 0) _mouseReleased |= bit;
        _mouseDown = down ? (byte)(_mouseDown | bit) : (byte)(_mouseDown & ~bit);
    }

    public void OnMouseMove(Vector2 position, Vector2 delta)
    {
        MousePosition = position;
        MouseDelta += delta;
    }

    public void OnWheel(Vector2 delta) => Wheel += delta;
}
