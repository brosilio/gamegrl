using System.Collections.Concurrent;
using System.Numerics;
using AnsiDotNet;
using Gamegrl.Platform;
using Key = Gamegrl.Platform.Key;
using MouseButton = Gamegrl.Platform.MouseButton;
namespace Gamegrl.Editor;

/// <summary>A terminal is a real platform host: no SDL window, font, or display server is required.</summary>
internal sealed class TerminalPlatform : IPlatform
{
    private readonly Terminal _terminal;
    private readonly HashSet<int> _pulses = new();
    private string _clipboard = "";
    private readonly TerminalWindow _window;
    public TerminalPlatform(Terminal terminal) { _terminal = terminal; _window = new TerminalWindow(terminal.Size); }
    public string Name => "Terminal";
    public bool QuitRequested { get; private set; }
    public bool Disconnected { get; private set; }
    public IWindow CreateWindow(in WindowDesc desc) => _window;
    public void CancelQuitRequest() => QuitRequested = false;
    public void PumpEvents(Input input)
    {
        foreach (int key in _pulses) input.OnKey(key, false, false); _pulses.Clear();
        int budget = 256;
        while (budget-- > 0 && _terminal.TryReadEvent(out var ev)) Feed(ev, input);
    }
    internal void Feed(InputEvent ev, Input input)
    {
        switch (ev.Kind)
        {
            case InputEventKind.Resize: _window.Resize(ev.Width, ev.Height); break;
            case InputEventKind.Closed: Disconnected = QuitRequested = true; break;
            case InputEventKind.FocusOut: input.ReleaseAll(); break;
            case InputEventKind.Paste: input.OnText(ev.Text ?? ""); break;
            case InputEventKind.Mouse:
                var point = new Vector2(ev.X + 0.5f, (ev.Y + 0.5f) * 2);
                input.OnMouseMove(point, point - input.MousePosition);
                if (ev.Action == MouseAction.Wheel) input.OnWheel(new Vector2(ev.Button == AnsiDotNet.MouseButton.WheelRight ? 1 : ev.Button == AnsiDotNet.MouseButton.WheelLeft ? -1 : 0, ev.Button == AnsiDotNet.MouseButton.WheelUp ? 1 : ev.Button == AnsiDotNet.MouseButton.WheelDown ? -1 : 0));
                else if (ev.Action is MouseAction.Press or MouseAction.Release)
                {
                    if (ev.Button == AnsiDotNet.MouseButton.None)
                        for (int i = 0; i < 3; i++) input.OnMouseButton(i, false);
                    else
                    {
                        int button = ev.Button switch { AnsiDotNet.MouseButton.Left => 0, AnsiDotNet.MouseButton.Middle => 1, AnsiDotNet.MouseButton.Right => 2, _ => -1 };
                        if (button >= 0) input.OnMouseButton(button, ev.Action == MouseAction.Press);
                    }
                }
                break;
            case InputEventKind.Key:
                Modifier(Key.LeftCtrl, ev.Modifiers.HasFlag(KeyModifiers.Control), input);
                Modifier(Key.LeftShift, ev.Modifiers.HasFlag(KeyModifiers.Shift), input);
                Modifier(Key.LeftAlt, ev.Modifiers.HasFlag(KeyModifiers.Alt), input);
                Key key = ev.Key switch
                {
                    AnsiDotNet.Key.Enter => Key.Enter, AnsiDotNet.Key.Escape => Key.Escape, AnsiDotNet.Key.Backspace => Key.Backspace,
                    AnsiDotNet.Key.Tab => Key.Tab, AnsiDotNet.Key.Left => Key.Left, AnsiDotNet.Key.Right => Key.Right,
                    AnsiDotNet.Key.Up => Key.Up, AnsiDotNet.Key.Down => Key.Down, AnsiDotNet.Key.Home => Key.Home,
                    AnsiDotNet.Key.End => Key.End, AnsiDotNet.Key.Delete => Key.Delete, AnsiDotNet.Key.PageUp => Key.PageUp,
                    AnsiDotNet.Key.PageDown => Key.PageDown, >= AnsiDotNet.Key.F1 and <= AnsiDotNet.Key.F12 => (Key)((int)Key.F1 + (ev.Key - AnsiDotNet.Key.F1)),
                    _ => Key.None,
                };
                if (ev.Key == AnsiDotNet.Key.Char)
                {
                    int c = char.ToUpperInvariant((char)ev.Rune.Value);
                    if (c >= 'A' && c <= 'Z') key = (Key)((int)Key.A + (c - 'A')); else if (c == ' ') key = Key.Space;
                    if ((ev.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt)) == 0) input.OnText(ev.Rune.ToString());
                }
                if (key != Key.None) { input.OnKey((int)key, true, false); _pulses.Add((int)key); }
                break;
        }
    }
    private void Modifier(Key key, bool down, Input input)
    { input.OnKey((int)key, down, false); if (down) _pulses.Add((int)key); }
    public string GetClipboardText() => _clipboard;
    public void SetClipboardText(string text) => _clipboard = text;
    public void SetCursor(Gamegrl.Platform.CursorShape shape) { }
    public void Dispose() { }
    private sealed class TerminalWindow(TerminalSize size) : IOffscreenWindow
    {
        public (int Width, int Height) PixelSize { get; private set; } = (Math.Max(1, size.Width), Math.Max(1, size.Height) * 2);
        public void Resize(int width, int height) => PixelSize = (Math.Max(1, width), Math.Max(1, height) * 2);
        public bool CloseRequested => false;
        public bool Minimized => false;
        public string[] VulkanInstanceExtensions => [];
        public void CancelCloseRequest() { }
        public ulong CreateVulkanSurface(nint instance) => throw new NotSupportedException("Terminal windows are offscreen.");
        public void Dispose() { }
    }
}

/// <summary>One pending snapshot: slow terminal writes never queue frames or block the editor's frame thread.</summary>
internal sealed class TerminalDisplay : IDisposable
{
    private readonly Terminal _terminal;
    private readonly Lock _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Surface _last = new(0, 0);
    private Surface _pending = new(1, 1), _spare = new(1, 1);
    private bool _hasPending, _stopping;
    private readonly Thread _writer;
    public TerminalDisplay(Terminal terminal)
    { _terminal = terminal; _writer = new Thread(WriteLoop) { IsBackground = true, Name = "editor terminal output" }; _writer.Start(); }
    public void Publish(Surface surface)
    {
        lock (_gate)
        {
            bool changed = _last.Size != surface.Size;
            for (int y = 0; !changed && y < surface.Height; y++) changed = !surface.GetRow(y).SequenceEqual(_last.GetRow(y));
            if (!changed) return;
            _last.Resize(surface.Width, surface.Height); _pending.Resize(surface.Width, surface.Height);
            for (int y = 0; y < surface.Height; y++)
            { surface.GetRow(y).CopyTo(_pending.GetRow(y)); surface.GetRow(y).CopyTo(_last.GetRow(y)); }
            _hasPending = true;
        }
        _wake.Set();
    }
    private void WriteLoop()
    {
        try
        {
            while (true)
            {
                _wake.WaitOne(); Surface frame;
                lock (_gate)
                {
                    if (!_hasPending && _stopping) return;
                    if (!_hasPending) continue;
                    (_spare, _pending) = (_pending, _spare); frame = _spare; _hasPending = false;
                }
                _terminal.Render(frame);
                lock (_gate) { if (_stopping) return; }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        lock (_gate) _stopping = true;
        _wake.Set();
        // Shutdown occurs after the UI loop. Terminal mode restoration follows the last write.
        _writer.Join(); _wake.Dispose();
    }
}
