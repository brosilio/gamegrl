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
            Check(layout.Region(DockRegion.Center).Right == layout.Region(DockRegion.Right).Left, "boxed panes sit edge to edge");
            Check(layout.Top.Height == 1 && layout.Rule.X == 26 && layout.Rule.Width == 1 && layout.Region(DockRegion.Center).X == 27);
            Check(layout.Region(DockRegion.Bottom).Y == 40 - 12 && layout.Region(DockRegion.Center).Bottom == 28);
            layout.Arrange(120, 40, 0, 38, 12);
            Check(layout.Rule.Width == 0 && layout.Region(DockRegion.Center).X == 0, "a hidden sidebar takes its rule with it");
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
            var accent = CellDraw.Color(ui.Theme.Accent);
            Check(ui.Draw.Surface.GetRow(0).ToArray().Any(cell => cell.Rune.Value == '[' && cell.Style.Foreground == accent), "the focused button's brackets take the accent");
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
        test("Cell UI: rewinding after a throwing panel restores areas, ids, clips and popup state", () =>
        {
            var ui = new CellUi(); var input = new Input();
            ui.Begin(input, null, new Vector2(40, 10), 1);
            var checkpoint = ui.Save(); uint before = ui.Id("probe");
            try
            {
                ui.BeginArea(new Rect(2, 2, 10, 3)); ui.PushId("panel"); ui.BeginScroll(new Rect(2, 2, 10, 3), "s");
                ui.BeginPopup(new Rect(5, 5, 8, 3));
                throw new InvalidOperationException("panel bug");
            }
            catch (InvalidOperationException) { ui.Rewind(checkpoint); }
            Check(ui.Id("probe") == before && ui.Remaining == new Rect(0, 0, 40, 10));
            Check(ui.Draw.Clip == new Rect(0, 0, 40, 10));
            ui.Label("after"); ui.End();
            Check(ui.Draw.Surface[0, 0].Rune.Value == 'a');
        });
        test("Cell UI: clicking another item while editing commits the edit before the click changes context", () =>
        {
            var input = new Input(); var ui = new CellUi();
            string[] names = ["alpha", "beta"]; int selected = 0;
            void Draw()
            {
                ui.Begin(input, null, new Vector2(40, 10), 1);
                // The list draws first, like the Hierarchy: its click changes what the field below edits.
                for (int i = 0; i < names.Length; i++) { ui.PushId(i); if (ui.Selectable(ui.Next(), names[i], i == selected)) selected = i; ui.PopId(); }
                ui.PushId(selected); ui.TextField(new Rect(0, 5, 20, 1), "name", ref names[selected]); ui.PopId();
                ui.End();
            }
            void Click(float x, float y, bool down)
            { input.BeginFrame(); input.OnMouseMove(new Vector2(x, y), Vector2.Zero); input.OnMouseButton(0, down); Draw(); }
            Draw(); Click(2, 5.5f, true); Click(2, 5.5f, false);
            Check(ui.WantsKeyboard);
            input.BeginFrame(); input.OnKey((int)Key.End, true, false); Draw();
            input.BeginFrame(); input.OnKey((int)Key.End, false, false); input.OnText("!"); Draw();
            Click(2, 1.5f, true);
            Check(names[0] == "alpha!" && selected == 0, "the edit commits on the frame the outside click arrives");
            Click(2, 1.5f, false);
            Check(selected == 1 && !ui.WantsKeyboard, "the held click reaches the list on the next frame");
            Draw(); Draw();
            Check(names[0] == "alpha!" && names[1] == "beta", "nothing stale is applied later");
        });
        test("Cell UI: slow sub-cell splitter drags accumulate", () =>
        {
            var input = new Input(); var ui = new CellUi(); float position = 10;
            void Frame(float x, bool? button = null)
            {
                input.BeginFrame(); input.OnMouseMove(new Vector2(x, 5), Vector2.Zero);
                if (button is { } down) input.OnMouseButton(0, down);
                ui.Begin(input, null, new Vector2(40, 10), 1);
                position = ui.Splitter(new Rect(position, 0, 1, 10), "split", position, true, 2, 30); ui.End();
            }
            Frame(10.5f, true);
            for (int i = 1; i <= 10; i++) Frame(10.5f + i * 0.3f);
            Check(position == 13, "ten 0.3-cell steps move the splitter three cells");
            Frame(40, false); Check(position == 13);
        });
        test("Cell UI: overflowing scroll views put a half-row thumb on the border beside them and scroll by dragging it", () =>
        {
            var input = new Input(); var ui = new CellUi();
            void Frame()
            {
                ui.Begin(input, null, new Vector2(20, 12), 1);
                ui.Draw.Box(new Rect(0, 0, 12, 12), ui.Theme.Border);
                ui.BeginScroll(new Rect(1, 1, 10, 10), "list");
                for (int i = 0; i < 40; i++) ui.Label("0123456789");
                ui.EndScroll(); ui.End();
            }
            Frame(); Frame();
            static bool Bar(int rune) => rune is '▕' or '▐' or '▝' or '▗';
            Check(Bar(ui.Draw.Surface[11, 1].Rune.Value) && Bar(ui.Draw.Surface[11, 10].Rune.Value), "the bar replaces the box's right edge");
            Check(ui.Draw.Surface[11, 1].Rune.Value == '▐' && ui.Draw.Surface[11, 10].Rune.Value == '▕', "the thumb starts at the top");
            Check(ui.Draw.Surface[10, 1].Rune.Value == '9', "content keeps its last column");
            input.BeginFrame(); input.OnMouseMove(new Vector2(11.5f, 10.9f), Vector2.Zero); input.OnMouseButton(0, true); Frame();
            input.BeginFrame(); Frame();
            Check(ui.GetFloat(ui.Id("list")) == 30, "dragging the thumb to the bottom scrolls to the end");
            Check(ui.Draw.Surface[11, 10].Rune.Value is '▐' or '▗' && ui.Draw.Surface[11, 1].Rune.Value == '▕');
            input.BeginFrame(); input.OnMouseButton(0, false); Frame();
        });
        test("Cell UI: splitters recolor the caller's border on hover; disabled links stay inert", () =>
        {
            var input = new Input(); var ui = new CellUi(); bool clicked = false;
            void Frame()
            {
                ui.Begin(input, null, new Vector2(20, 6), 1);
                ui.Draw.Glyph(5, 2, '│', ui.Theme.Border);
                ui.Splitter(new Rect(5, 0, 1, 6), "split", 5, true, 0, 20);
                clicked |= ui.Link(new Rect(10, 0, 4, 1), "Pause", "‖", ui.Theme.Text, enabled: false);
                ui.End();
            }
            input.OnMouseMove(new Vector2(5.5f, 2.5f), Vector2.Zero); Frame();
            Check(ui.Draw.Surface[5, 2].Rune.Value == '│' && ui.Draw.Surface[5, 2].Style.Foreground == CellDraw.Color(ui.Theme.Accent));
            input.BeginFrame(); input.OnMouseMove(new Vector2(11, 0.5f), Vector2.Zero); input.OnMouseButton(0, true); Frame();
            input.BeginFrame(); input.OnMouseButton(0, false); Frame();
            Check(!clicked && ui.Draw.Surface[5, 2].Style.Foreground == CellDraw.Color(ui.Theme.Border));
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
        test("Terminal pipe: input EOF still allows terminal mode restoration on stdout", () =>
        {
            using var input = new MemoryStream(); using var output = new MemoryStream();
            using var terminal = Terminal.Open(new EditorPipeBackend(input, output, new TerminalSize(80, 24)));
            Check(SpinWait.SpinUntil(() => terminal.TryReadEvent(out var ev) && ev.Kind == InputEventKind.Closed, 5000));
            terminal.Dispose();
            Check(Encoding.UTF8.GetString(output.ToArray()).Contains("\u001b[?1049l", StringComparison.Ordinal));
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
    private static void Check(bool condition, string message = "Cell interface assertion failed") { if (!condition) throw new Exception(message); }
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
