using System.Numerics;
using System.Text;
using AnsiDotNet;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Rect = Gamegrl.UI.Rect;
using Key = Gamegrl.Platform.Key;
namespace Gamegrl.Tests;
internal static class CellUiTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Cell UI: Unicode cells, ansidotnet layout, clipping and popup composition", () =>
        {
            var ui = new CellUi(); var input = new Input();
            ui.Begin(input, null, new Vector2(80, 24), 1);
            ui.BeginArea(new Rect(0, 0, 40, 4)); ui.Row(8, -1);
            Check(ui.Next() == new Rect(0, 0, 8, 1)); Check(ui.Next() == new Rect(9, 0, 31, 1)); ui.EndArea();
            ui.Draw.Text(new Vector2(1, 5), "A界B", ui.Theme.Text);
            Check(ui.Draw.Surface[2, 5].Rune.Value == '界' && ui.Draw.Surface[3, 5].IsContinuation && ui.Draw.Surface[4, 5].Rune.Value == 'B');
            ui.BeginPopup(new Rect(10, 8, 20, 5)); ui.Label("Popup ▾ ✓"); ui.EndPopup();
            ui.Draw.Rect(new Rect(0, 0, 80, 24), ui.Theme.Background); ui.End();
            Check(ui.Draw.Surface[11, 9].Rune.Value == 'P');
            var layout = new EditorCellLayout(); layout.Arrange(120, 40, 26, 38, 12);
            Check(layout.Region(DockRegion.Left).Width == 26 && layout.Region(DockRegion.Right).Width == 38);
            Check(layout.Region(DockRegion.Center).Right <= layout.Region(DockRegion.Right).Left);
            layout.Arrange(25, 10, 26, 38, 12);
            foreach (var region in Enum.GetValues<DockRegion>()) Check(layout.Region(region).Width >= 0 && layout.Region(region).Height >= 0);
        });
        test("Cell UI: keyboard navigation, selection, paste and surrogate-safe text editing", () =>
        {
            var input = new Input(); var ui = new CellUi(); string value = "start";
            void Draw()
            {
                ui.Begin(input, null, new Vector2(60, 20), 1);
                ui.Button("First"); ui.TextField(ui.Next(), "edit", ref value); ui.Button("Last"); ui.End();
            }
            Draw(); input.BeginFrame(); input.OnKey((int)Key.Tab, true, false); Draw();
            input.BeginFrame(); input.OnKey((int)Key.Tab, false, false); Draw();
            input.BeginFrame(); input.OnKey((int)Key.Tab, true, false); Draw();
            input.BeginFrame(); input.OnKey((int)Key.Tab, false, false); input.OnKey((int)Key.Enter, true, false); Draw();
            Check(ui.WantsKeyboard);
            input.BeginFrame(); input.OnKey((int)Key.Enter, false, false); input.OnText("🦊Z"); Draw();
            input.BeginFrame(); input.OnKey((int)Key.Backspace, true, false); Draw();
            input.BeginFrame(); input.OnKey((int)Key.Backspace, false, false); input.OnKey((int)Key.Backspace, true, false); Draw();
            ui.CommitTextEdit(); input.BeginFrame(); input.OnKey((int)Key.Backspace, false, false); Draw();
            Check(value == "" && !ui.WantsKeyboard);
        });
        test("Terminal viewport: half-block colors, BGRA order, clipping and low color output", () =>
        {
            var surface = new Surface(5, 3);
            byte[] bgra = [0, 0, 255, 255, 255, 0, 0, 255];
            TerminalViewport.PaintPixels(surface, new Rect(2, 1, 1, 1), bgra, 1, 2, true);
            var cell = surface[2, 1]; Check(cell.Rune.Value == '▀');
            Check(cell.Style.Foreground.ToRgb() == ((byte)255, (byte)0, (byte)0));
            Check(cell.Style.Background.ToRgb() == ((byte)0, (byte)0, (byte)255));
            using var stream = new MemoryStream(); var writer = new AnsiWriter(stream) { ColorMode = ColorMode.Basic16 };
            var renderer = new ScreenRenderer(writer); renderer.Render(surface);
            long first = stream.Length; renderer.Render(surface);
            Check(stream.Length - first < first);
            Check(!Encoding.UTF8.GetString(stream.ToArray()).Contains("38;2", StringComparison.Ordinal));
            TerminalViewport.PaintPixels(surface, new Rect(2, 1, 1, 1), bgra, 1, 2, true, ascii: true);
            Check(surface[2, 1].Rune.IsAscii);
            var ascii = new CellUi(ascii: true); ascii.Begin(new Input(), null, new Vector2(40, 10), 1);
            ascii.Label("▾ ✓ ─"); ascii.End();
            Check(ascii.Draw.Surface.GetRow(0).ToArray().All(c => c.Rune.IsAscii));
        });
        test("Terminal output: a slow writer retains only the latest snapshot and does not block publishing", () =>
        {
            using var backend = new SlowBackend(); using var terminal = Terminal.Open(backend);
            using var display = new TerminalDisplay(terminal);
            var surface = new Surface(80, 24); surface.Print(0, 0, "first");
            backend.Block = true; display.Publish(surface);
            Check(backend.Entered.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                for (int i = 0; i < 100; i++) { surface.Print(0, 0, "frame " + i); display.Publish(surface); }
                Check(watch.Elapsed < TimeSpan.FromSeconds(1));
                surface.Print(0, 0, "latest snapshot"); display.Publish(surface);
            }
            finally { backend.Release.Set(); }
            Check(SpinWait.SpinUntil(() => backend.Text.Contains("latest snapshot", StringComparison.Ordinal), 5000));
        });
        test("Terminal input: modifiers, paste, SGR mouse, focus loss and key pulses", () =>
        {
            using var backend = new Backend(); using var terminal = Terminal.Open(backend);
            using var platform = new TerminalPlatform(terminal); var input = new Input();
            backend.Input!.OnInput("\u001b[<0;12;8M"u8); platform.PumpEvents(input);
            Check(input.IsDown(Gamegrl.Platform.MouseButton.Left) && input.MousePosition == new Vector2(11.5f, 15));
            platform.Feed(InputEvent.ForPaste("text"), input); Check(input.TypedText == "text");
            platform.Feed(InputEvent.ForChar(new Rune('s'), KeyModifiers.Control), input); Check(input.Ctrl && input.WasPressed(Key.S));
            input.BeginFrame(); platform.PumpEvents(input); Check(!input.Ctrl && input.WasReleased(Key.S));
            platform.Feed(InputEvent.Simple(InputEventKind.FocusOut), input); Check(!input.IsDown(Gamegrl.Platform.MouseButton.Left));
            platform.Feed(InputEvent.ForResize(90, 30), input); Check(platform.CreateWindow(default).PixelSize == (90, 60));
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Cell interface assertion failed"); }
    private sealed class SlowBackend : ITerminalBackend
    {
        private readonly Lock _gate = new(); private readonly MemoryStream _bytes = new();
        public bool Block; public ManualResetEventSlim Entered = new(), Release = new();
        public string Text { get { lock (_gate) return Encoding.UTF8.GetString(_bytes.ToArray()); } }
        public TerminalSize Size => new(80, 24);
        public void Start(ITerminalInput input) { }
        public void Write(ReadOnlySpan<byte> data)
        {
            if (Block) { Entered.Set(); Release.Wait(); }
            lock (_gate) _bytes.Write(data);
        }
        public void Dispose() => Release.Set();
    }
    private sealed class Backend : ITerminalBackend
    {
        public TerminalSize Size => new(80, 24); public ITerminalInput? Input;
        public void Start(ITerminalInput input) => Input = input;
        public void Write(ReadOnlySpan<byte> data) { }
        public void Dispose() { }
    }
}
