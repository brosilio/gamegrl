using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.UI;
using Gamegrl.UI.Fonts;
using static Gamegrl.Tests.Harness;
namespace Gamegrl.Tests;

/// <summary>The pixel UI games ship: widgets, layout, windows, formatting and the module, without a GPU.</summary>
internal static class UiTests
{
    private static FontAtlas? _font;
    private static FontAtlas Font => _font ??= FontAtlas.Bake(new TrueTypeFont(File.ReadAllBytes(
        FontAtlas.FindSystemFont() ?? throw new Exception("no system font; set GAMEGRL_FONT"))), 14);

    public static void Register()
    {
        Test("ui: glyph coverage, clipping, button clicks and text input", () =>
        {
            Assert.True(Font.Measure("Hello") > Font.Measure("Hi"));
            bool partial = false;
            for (int i = 3; i < Font.Rgba.Length && !partial; i += 4) partial = Font.Rgba[i] is > 0 and < 255;
            Assert.True(partial, "antialiased glyph coverage");
            var input = new Input();
            var ui = new Ui(Font);
            var rect = new Rect(5, 5, 100, 22);
            void Begin() => ui.Begin(input, new Vector2(400, 300), 1);
            input.OnMouseMove(new Vector2(10, 10), Vector2.Zero);
            input.OnMouseButton(0, true);
            Begin(); Assert.True(!ui.Button(rect, "Click")); ui.End();
            input.BeginFrame(); input.OnMouseButton(0, false);
            Begin(); Assert.True(ui.Button(rect, "Click")); ui.End();
            Assert.True(ui.Draw.Indices.Length > 0);
            Begin();
            ui.BeginArea(new Rect(0, 0, 200, 100));
            ui.Row(50, -1);
            var first = ui.Next(); var second = ui.Next();
            Assert.True(first.W == 50 && first.H == ui.Theme.RowHeight && second.X == 50 + ui.Theme.Spacing);
            ui.EndArea(); ui.End();
            foreach (var command in ui.Draw.Commands) Assert.True(command.Clip.Intersect(ui.Screen) == command.Clip);
            input.BeginFrame(); input.OnMouseButton(0, true);
            string text = "";
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            input.BeginFrame(); input.OnMouseButton(0, false); input.OnText("Hello");
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            input.BeginFrame(); input.OnKey((int)Key.Left, true, false);
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            input.BeginFrame(); input.OnKey((int)Key.Left, false, false); input.OnKey((int)Key.Backspace, true, false);
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            input.BeginFrame(); input.OnKey((int)Key.Backspace, false, false); input.OnKey((int)Key.Enter, true, false);
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            Assert.Equal("Helo", text);
            input.BeginFrame(); input.OnKey((int)Key.Enter, false, false); input.OnMouseButton(0, true);
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            input.BeginFrame(); input.OnMouseButton(0, false); input.OnText("!");
            ui.CommitTextEdit();
            Begin(); ui.TextField(rect, "text", ref text); ui.End();
            Assert.True(text.Contains('!') && !ui.WantsKeyboard);
        });

        Test("ui: windows move by their title, fold, block the mouse and fit their content", () =>
        {
            var input = new Input();
            var ui = new Ui(Font);
            var initial = new Rect(10, 10, 200, 300);
            Rect content = default;
            void Frame()
            {
                ui.Begin(input, new Vector2(800, 600), 1);
                if (ui.BeginWindow("Debug", initial)) { content = ui.Next(); ui.Label("one"); }
                ui.EndWindow();
                ui.End();
            }
            input.OnMouseMove(new Vector2(100, 20), Vector2.Zero);
            Frame();
            Assert.True(ui.WantsMouse, "a window's title bar takes the mouse");
            Assert.True(content.Y > 10 + ui.Theme.TitleHeight && content.X > 10);
            input.BeginFrame(); input.OnMouseButton(0, true); Frame();
            input.BeginFrame(); input.OnMouseMove(new Vector2(150, 70), new Vector2(50, 50)); Frame();
            input.BeginFrame(); input.OnMouseButton(0, false); Frame();
            Assert.True(MathF.Abs(content.X - (60 + ui.Theme.Padding)) < 0.01f, "dragged 50 units right");
            // Content is two rows; the window shrinks to them instead of the 300 units it was given.
            input.BeginFrame(); input.OnMouseMove(new Vector2(150, 400), Vector2.Zero); Frame();
            Assert.True(!ui.WantsMouse, "the shrunk window no longer covers y = 400");
            // Fold with the arrow at the title's left end.
            input.BeginFrame(); input.OnMouseMove(new Vector2(70, 70), Vector2.Zero); input.OnMouseButton(0, true); Frame();
            input.BeginFrame(); input.OnMouseButton(0, false); Frame();
            content = default;
            input.BeginFrame(); Frame();
            Assert.Equal(default(Rect), content);
        });

        Test("ui: sliders follow the mouse and interpolated labels format without allocating", () =>
        {
            var input = new Input();
            var ui = new Ui(Font);
            float speed = 1;
            var track = new Rect(0, 0, 100, 20);
            void Frame()
            {
                ui.Begin(input, new Vector2(400, 300), 1);
                ui.Slider(track, "speed", ref speed, 0, 4);
                ui.End();
            }
            // The value column on the right isn't part of the track, so the track's middle is left of x = 50.
            float trackMiddle = (100 - (MathF.Ceiling(ui.Font.Measure("-00.00")) + ui.Theme.Spacing)) / 2;
            input.OnMouseMove(new Vector2(trackMiddle, 10), Vector2.Zero); input.OnMouseButton(0, true); Frame();
            Assert.True(MathF.Abs(speed - 2) < 0.001f, $"the track's middle is half way, got {speed}");
            input.BeginFrame(); input.OnMouseMove(new Vector2(500, 10), new Vector2(450, 0)); Frame();
            Assert.Equal(4f, speed);
            ui.Begin(input, new Vector2(400, 300), 1);
            var a = ui.Format($"{12.5f:0.0} ms");
            var b = ui.Format($"{3} of {"ten"}");
            Assert.True(a.SequenceEqual("12.5 ms") && b.SequenceEqual("3 of ten"), "two formats coexist in one frame");
            ui.End();
        });

        Test("ui: a warmed-up frame of windows, widgets and formatted text allocates nothing", () =>
        {
            var input = new Input();
            var ui = new Ui(Font);
            float speed = 1, drag = 2;
            bool frozen = false;
            int choice = 0;
            var position = new Vector3(1, 2, 3);
            IReadOnlyList<string> items = ["a", "b"];
            void Frame(int frame)
            {
                ui.Begin(input, new Vector2(800, 600), frame);
                if (ui.BeginWindow("Debug", new Rect(10, 10, 260, 400)))
                {
                    ui.Label($"{1000f / (frame + 1):0} fps · frame {frame}", dim: true);
                    ui.Header("Tuning");
                    ui.Slider(ui.Property("Speed"), "speed", ref speed, 0, 4);
                    ui.Checkbox("Frozen", ref frozen);
                    ui.DragFloat(ui.Property("Drag"), "drag", ref drag);
                    ui.Vector3(ui.Property("Position"), "position", ref position);
                    ui.Combo(ui.Property("Choice"), "choice", ref choice, items);
                    ui.ProgressBar(ui.Next(), 0.5f, ui.Format($"{50}%"));
                    if (ui.Button("Reset")) speed = 1;
                }
                ui.EndWindow();
                ui.TextAt(new Vector2(400, 300), ui.Format($"entity {frame}"), ui.Theme.Accent, new Vector2(0.5f, 1));
                ui.End();
                input.BeginFrame();
            }
            input.OnMouseMove(new Vector2(100, 100), Vector2.Zero);
            for (int i = 0; i < 4; i++) Frame(i);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 4; i < 64; i++) Frame(i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(allocated == 0, $"steady-state UI frames allocated {allocated} bytes");
        });

        Test("ui: the module runs headless, follows the frame and exposes the Ui resource", () =>
        {
            var builder = EngineBuilder.Create().AddModule<CoreModule>();
            builder.WorkerThreads = 0;
            builder.SetResource(new Gamegrl.Rendering.ScreenSize { Width = 640, Height = 360 });
            builder.AddModule(new UiModule());
            int drawn = 0;
            builder.AddSystem(new Probe(world =>
            {
                var ui = world.Resource<Ui>();
                ui.Label($"tick {world.Resource<Time>().Frame}");
                drawn = ui.Draw.Vertices.Length;
            }));
            using var app = builder.Build();
            app.Tick(); app.Tick();
            Assert.True(drawn > 0, "a system drew into the frame's Ui");
            Assert.True(app.World.HasResource<UiLayer>());
        });
    }

    private sealed class Probe(Action<World> run) : ISystem
    {
        public string Name => "Test.UiProbe";
        public Phase Phase => Phase.Update;
        public int Order => 0;
        public void Initialize(World world) { }
        public void Run(World world) => run(world);
    }
}
