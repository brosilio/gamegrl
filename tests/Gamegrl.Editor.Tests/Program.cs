using System.Numerics;
using Gamegrl;
using Gamegrl.Editor;
using Gamegrl.Platform;
using Gamegrl.UI;
using Gamegrl.UI.Fonts;

if (args.Contains("--graphics") || args.Contains("--terminal-smoke")) return Gamegrl.Tests.GraphicsSmoke.Run(args.Contains("--terminal-smoke"));

int failed = 0, total = 0;
void Check(bool condition, string message = "Assertion failed") { if (!condition) throw new Exception(message); }
void Test(string name, Action body)
{
    total++;
    try { body(); Console.WriteLine("  ok    " + name); }
    catch (Exception e) { failed++; Console.WriteLine("  FAIL  " + name + "\n" + e); }
}
Gamegrl.Tests.SceneFilesTests.Register(Test);
Gamegrl.Tests.ExtensionTests.Register(Test);
Gamegrl.Tests.BuildWorkspaceTests.Register(Test);
Gamegrl.Tests.CellUiTests.Register(Test);

Test("Undo/redo coalesces a drag, restores hierarchy and selection, branches history", () =>
{
    using var world = new World();
    var parent = world.Spawn(new Name("Parent"), SceneId.New(), LocalTransform.Identity);
    var child = world.Spawn(new Name("Child"), SceneId.New(), LocalTransform.Identity);
    world.SetParent(child, parent);
    var doc = new SceneDocument(world) { Selected = child };
    doc.Open(doc.Text);
    string original = doc.Text;
    doc.BeginEdit();
    world.Get<LocalTransform>(doc.Selected).Position.X = 1;
    doc.BeginEdit();
    world.Get<LocalTransform>(doc.Selected).Position.X = 2;
    doc.CommitEdit();
    Check(doc.Dirty && doc.CanUndo);
    doc.Undo();
    Check(doc.Text == original && !world.IsAlive(child));
    Check(world.Get<LocalTransform>(doc.Selected).Position.X == 0);
    Check(world.Get<Name>(world.GetParent(doc.Selected)).ToString() == "Parent");
    Check(!doc.CanUndo && doc.CanRedo);
    doc.Redo();
    Check(world.Get<LocalTransform>(doc.Selected).Position.X == 2);
    doc.Undo();
    doc.Edit(() => world.Get<LocalTransform>(doc.Selected).Position.Y = 5);
    Check(!doc.CanRedo);
});
Test("Create/delete history restores the command's selection on undo and redo", () =>
{
    using var world = new World();
    var doc = new SceneDocument(world);
    doc.Open(doc.Text);
    doc.Edit(() => doc.Selected = world.Spawn(new Name("New"), SceneId.New()));
    ulong id = doc.SelectedId;
    doc.Undo();
    Check(world.EntityCount == 0 && doc.Selected.IsNull);
    doc.Redo();
    Check(world.EntityCount == 1 && doc.SelectedId == id);
    doc.Edit(() => { world.Destroy(doc.Selected); doc.Selected = default; });
    doc.Undo();
    Check(doc.SelectedId == id);
    doc.Redo();
    Check(world.EntityCount == 0 && doc.Selected.IsNull);
});
Test("Play restores entities and time without adding history", () =>
{
    using var world = new World();
    world.SetResource(new Time { Elapsed = 10, FixedDelta = 1f / 60 });
    var e = world.Spawn(new Name("Before"), SceneId.New(), LocalTransform.Identity);
    var doc = new SceneDocument(world) { Selected = e };
    doc.Open(doc.Text);
    doc.Edit(() => world.Get<LocalTransform>(doc.Selected).Position.Z = 3);
    string edit = doc.Text;
    doc.Play();
    Check(doc.Playing && !doc.CanUndo && world.Resource<Time>().Elapsed == 0);
    world.Destroy(doc.Selected);
    world.Spawn(new Name("Runtime"));
    world.Resource<Time>().Elapsed = 40;
    doc.Stop();
    Check(!doc.Playing && doc.Text == edit && doc.CanUndo);
    Check(world.Resource<Time>().Elapsed == 10);
    doc.Undo();
    Check(world.Get<LocalTransform>(world.Archetypes.First(a => a.Has<LocalTransform>() && a.Count > 0).Entities[0]).Position.Z == 0);
});
Test("A held scene survives failed code loads verbatim, blocks edits and returns on restore", () =>
{
    using var world = new World();
    world.SetResource(new Time { Elapsed = 7, FixedDelta = 1f / 60 });
    var e = world.Spawn(new Name("Kept"), SceneId.New(), LocalTransform.Identity);
    var doc = new SceneDocument(world) { Selected = e };
    doc.Open(doc.Text);
    doc.Edit(() => world.Get<LocalTransform>(doc.Selected).Position.X = 4);
    string edit = doc.Text;
    doc.Play();
    world.Spawn(new Name("Runtime"));
    doc.Hold(doc.Text);
    Check(!doc.Playing && world.EntityCount == 0 && doc.Text == edit, "the edit snapshot is held, not the play world");
    Check(world.Resource<Time>().Elapsed == 7 && !doc.CanUndo && doc.Dirty);
    doc.Edit(() => world.Spawn(new Name("Blocked")));
    Check(world.EntityCount == 0 && doc.Text == edit);
    doc.Restore(doc.Text);
    Check(doc.Held is null && world.EntityCount == 1 && doc.Text == edit);
});
Test("Entity value edits record component bytes, keep entity references across rebuilds and track dirty by history", () =>
{
    using var world = new World();
    var target = world.Spawn(new Name("Target"), SceneId.New(), LocalTransform.Identity);
    var mover = world.Spawn(new Name("Mover"), SceneId.New(), LocalTransform.Identity, new Gamegrl.Tests.Follow { Target = target, Speed = 1 });
    var doc = new SceneDocument(world) { Selected = mover };
    doc.Open(doc.Text);
    string original = doc.Text;
    long revision = doc.Revision;
    doc.BeginEntityEdit(doc.Selected);
    world.Get<Gamegrl.Tests.Follow>(doc.Selected).Speed = 2;
    world.Get<LocalTransform>(doc.Selected).Position.Y = 3;
    doc.CommitEdit();
    Check(doc.Dirty && doc.CanUndo && doc.Revision > revision);
    doc.BeginEntityEdit(doc.Selected); doc.CommitEdit(); // an idle click records nothing
    // A structural edit on top, then undo it: the world is rebuilt and every handle changes.
    doc.Edit(() => world.Spawn(new Name("Extra"), SceneId.New()));
    doc.Undo();
    Check(world.EntityCount == 2);
    doc.Undo();
    Check(doc.Text == original && !doc.Dirty, "undoing back to the saved state is clean again");
    var follow = world.Get<Gamegrl.Tests.Follow>(doc.Selected);
    Check(follow.Speed == 1 && world.IsAlive(follow.Target) && world.Get<Name>(follow.Target).ToString() == "Target", "entity fields resolve to the rebuilt target");
    doc.Redo();
    Check(world.Get<Gamegrl.Tests.Follow>(doc.Selected).Speed == 2 && world.Get<LocalTransform>(doc.Selected).Position.Y == 3 && doc.Dirty);
    doc.Redo();
    Check(world.EntityCount == 3 && !doc.CanRedo);
    doc.MarkSaved(doc.Text);
    Check(!doc.Dirty);
    doc.BeginEntityEdit(doc.Selected);
    world.Remove<Gamegrl.Tests.Follow>(doc.Selected); // structural change bypassing Edit
    doc.CommitEdit();
    Check(!doc.CanUndo && doc.Dirty, "untracked structure resets history and stays dirty");
});
Test("A warmed-up editor frame of controls and Inspector fields allocates nothing", () =>
{
    var ui = new CellUi(); var input = new Input();
    var choices = new AssetChoices(["None", "Cube"], [Guid.Empty, Gamegrl.Rendering.BuiltinAssets.Cube.Guid]);
    var inspector = new InspectorVisitor(ui, () => choices);
    // Arrays keep the components at stable addresses for the visitor without capturing addresses of locals.
    var sprite = new[] { Gamegrl.Rendering.Sprite.Default }; var camera = new[] { Gamegrl.Rendering.Camera.Default };
    var transform = new[] { LocalTransform.Default }; var name = new[] { new Name("Inspected") }; var follow = new[] { new Gamegrl.Tests.Follow { Speed = 2 } };
    string[] kinds = ["A", "B"]; int kind = 0; bool open = true; string text = "field"; int count = 3;
    unsafe void Frame()
    {
        ui.Begin(input, null, new Vector2(80, 40), 1);
        ui.BeginArea(new Rect(0, 0, 40, 40));
        ui.Row(6, -1, 2);
        ui.Button("Save"); ui.Combo(ui.Next(), "kind", ref kind, kinds); ui.Button("×");
        ui.BeginScroll(ui.Remaining, "list");
        for (ulong i = 0; i < 20; i++) ui.TreeNode(ui.Next(), i, "Entity", i % 3 == 0, i == 4, ref open, (float)(i % 4));
        ui.EndScroll(); ui.EndArea();
        ui.BeginArea(new Rect(40, 0, 40, 40));
        ui.TextField(ui.Next(), "text", ref text); ui.NumberField(ui.Next(), "count", ref count); ui.Separator();
        fixed (void* a = sprite) Visit(Component<Gamegrl.Rendering.Sprite>.Id, a);
        fixed (void* a = camera) Visit(Component<Gamegrl.Rendering.Camera>.Id, a);
        fixed (void* a = transform) Visit(Component<LocalTransform>.Id, a);
        fixed (void* a = name) Visit(Component<Name>.Id, a);
        fixed (void* a = follow) Visit(Component<Gamegrl.Tests.Follow>.Id, a);
        ui.EndArea(); ui.End();
    }
    unsafe void Visit(int type, void* pointer) { ui.PushId(type); ComponentRegistry.Get(type).Serializer!.Visit(pointer, inspector); ui.PopId(); }
    for (int i = 0; i < 3; i++) Frame();
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 10; i++) Frame();
    long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    Check(allocated == 0, $"steady-state frames allocated {allocated} bytes");
});
Test("Invalid scene does not destroy the edit world", () =>
{
    using var world = new World();
    world.Spawn(new Name("Keep"));
    var doc = new SceneDocument(world);
    string before = doc.Text;
    try { doc.Open("wrong header"); } catch (FormatException) { }
    Check(doc.Text == before);
});
Test("Clear invalidates handles, discards commands and keeps query objects usable", () =>
{
    using var world = new World();
    var e = world.Spawn(LocalTransform.Identity);
    var query = world.Query(new QueryDesc().With<LocalTransform>());
    var deferred = world.Commands.Spawn();
    world.Commands.Add(deferred, new Name("Deferred"));
    world.Clear();
    Check(!world.IsAlive(e) && world.EntityCount == 0 && world.Commands.IsEmpty);
    var next = world.Spawn(LocalTransform.At(new Vector3(2, 3, 4)));
    Check(!world.IsAlive(e) && world.IsAlive(next));
    int count = 0;
    foreach (var a in query.Archetypes) count += a.Count;
    Check(count == 1);
});
Test("Background jobs apply only when pumped and cancellation suppresses apply", () =>
{
    using var jobs = new EditorJobs();
    using var finished = new ManualResetEventSlim();
    int thread = Environment.CurrentManagedThreadId, appliedThread = 0;
    jobs.Run("test", _ => { finished.Set(); return 42; }, value => { Check(value == 42); appliedThread = Environment.CurrentManagedThreadId; });
    Check(finished.Wait(TimeSpan.FromSeconds(5)) && appliedThread == 0);
    Check(SpinWait.SpinUntil(() => { jobs.Pump(); return !jobs.Busy; }, 5000));
    Check(appliedThread == thread);
    using var entered = new ManualResetEventSlim();
    bool applied = false;
    jobs.Run("cancel", stop => { entered.Set(); stop.WaitHandle.WaitOne(); stop.ThrowIfCancellationRequested(); return 1; }, _ => applied = true);
    Check(entered.Wait(TimeSpan.FromSeconds(5)));
    jobs.CancelAll();
    Check(SpinWait.SpinUntil(() => { jobs.Pump(); return !jobs.Busy; }, 5000) && !applied);
});
Test("Camera reverse-Z ray and box picking handle parallel rays and transformed objects", () =>
{
    var camera = new SceneCamera { Target = Vector3.Zero, Yaw = 0, Pitch = 0, Distance = 10 };
    var rect = new Rect(10, 20, 800, 600);
    var matrix = camera.Matrix(4f / 3);
    Check(SceneCamera.Project(Vector3.Zero, matrix, rect, out var center));
    Check(Vector2.Distance(center, rect.Center) < 0.001f);
    var ray = SceneCamera.Ray(center, matrix, rect);
    Check(SceneCamera.HitBox(ray.Origin, ray.Direction, new Vector3(-1), new Vector3(1), out float distance));
    Check(distance > 8 && distance < 10);
    Check(!SceneCamera.HitBox(new Vector3(2, 0, 10), -Vector3.UnitZ, new Vector3(-1), new Vector3(1), out _));
});
Test("Collectible game reload preserves serialized fields and releases its load context", () =>
{
    string root = Directory.GetCurrentDirectory();
    string config = AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";
    string dll = Path.Combine(root, "samples/Spinners/Game/bin", config, "net10.0/Spinners.Game.dll");
    var builder = EngineBuilder.Create().AddModule<CoreModule>();
    builder.WorkerThreads = 0;
    using var app = builder.Build();
    var doc = new SceneDocument(app.World);
    var weak = ReloadGame(dll, app, doc);
    for (int i = 0; i < 10 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    Check(!weak.IsAlive, "Old game context remained rooted");
});
string fontPath = Environment.GetEnvironmentVariable("GAMEGRL_FONT") ?? "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf";
Test("TrueType glyph coverage, UI clipping, button clicks and text input", () =>
{
    var font = FontAtlas.Bake(new TrueTypeFont(File.ReadAllBytes(fontPath)), 14);
    Check(font.Measure("Hello") > font.Measure("Hi"));
    Check(font.Rgba.Where((_, i) => i % 4 == 3).Any(a => a > 0 && a < 255));
    var input = new Input();
    var ui = new Ui(font);
    var rect = new Rect(5, 5, 100, 22);
    void Begin() => ui.Begin(input, null, new Vector2(400, 300), 1, null);
    input.OnMouseMove(new Vector2(10, 10), Vector2.Zero);
    input.OnMouseButton(0, true);
    Begin(); Check(!ui.Button(rect, "Click")); ui.End();
    input.BeginFrame(); input.OnMouseButton(0, false);
    Begin(); Check(ui.Button(rect, "Click")); ui.End();
    Check(ui.Draw.Indices.Length > 0);
    Begin();
    ui.BeginArea(new Rect(0, 0, 200, 100));
    ui.Row(50, -1);
    var first = ui.Next(); var second = ui.Next();
    Check(first.W == 50 && first.H == ui.Theme.RowHeight && second.X == 50 + ui.Theme.Spacing);
    ui.EndArea(); ui.End();
    foreach (var command in ui.Draw.Commands) Check(command.Clip.Intersect(ui.Screen) == command.Clip);
    input.BeginFrame(); input.OnMouseButton(0, true);
    string text = "";
    Begin(); ui.TextField(rect, "text", ref text); ui.End();
    input.BeginFrame(); input.OnMouseButton(0, false); input.OnText("Hello");
    Begin(); ui.TextField(rect, "text", ref text); ui.End();
    input.BeginFrame(); input.OnKey((int)Key.Enter, true, false);
    Begin(); ui.TextField(rect, "text", ref text); ui.End();
    Check(text == "Hello");
    input.BeginFrame(); input.OnKey((int)Key.Enter, false, false); input.OnMouseButton(0, true);
    Begin(); ui.TextField(rect, "text", ref text); ui.End();
    input.BeginFrame(); input.OnMouseButton(0, false); input.OnText("!");
    ui.CommitTextEdit();
    Begin(); ui.TextField(rect, "text", ref text); ui.End();
    Check(text.Contains('!') && !ui.WantsKeyboard);
});
Test("Inspector metadata: edits clamp ranges, idle drawing preserves data and headers add layout", () =>
{
    var ui = new CellUi();
    var input = new Input();
    var inspector = new InspectorVisitor(ui, () => AssetChoices.Empty);
    float value = 5;
    void Draw(FieldMetadata metadata)
    {
        ui.Begin(input, null, new Vector2(50, 20), 5);
        inspector.Metadata(metadata);
        inspector.Field("Speed", ref value);
        ui.End();
    }
    var range = new FieldMetadata(Minimum: 0, Maximum: 10);
    input.OnMouseMove(new Vector2(25, 0.2f), Vector2.Zero); input.OnMouseButton(0, true);
    Draw(range);
    input.BeginFrame(); input.OnMouseMove(new Vector2(1025, 0.2f), new Vector2(1000, 0)); Draw(range);
    Check(value == 10);
    input.BeginFrame(); input.OnMouseMove(new Vector2(1022.5f, 0.2f), new Vector2(-2.5f, 0)); Draw(range);
    Check(value == 9);
    input.BeginFrame(); input.OnMouseButton(0, false); Draw(range);
    value = 99; input.BeginFrame(); Draw(range); Check(value == 99);
    float normalY = ui.Remaining.Y;
    Draw(new FieldMetadata(Header: "Motion", Tooltip: "Angular speed"));
    Check(ui.Remaining.Y > normalY);
    Check(InspectorVisitor.Constrain(99, range) == 10);
    Check(InspectorVisitor.Constrain(-99L, range) == 0L);
    Check(InspectorVisitor.Constrain(ulong.MaxValue, range) == 10UL);
    Check(InspectorVisitor.Constrain(double.NaN, range) == 0);
    var fractions = new FieldMetadata(Minimum: 0.1, Maximum: 0.2);
    Check((double)InspectorVisitor.Constrain(1f, fractions) <= 0.2);
    Check((double)InspectorVisitor.Constrain(0f, fractions) >= 0.1);
    Check(InspectorVisitor.Constrain(0, new FieldMetadata(Minimum: 1.2, Maximum: 4.8)) == 2);
});
Console.WriteLine($"{total - failed}/{total} passed");
return failed;

[System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
static WeakReference ReloadGame(string dll, App app, SceneDocument doc)
{
    using var game = new GameCode();
    using var registry = new EditorRegistry();
    string configuration = AppContext.BaseDirectory.Contains("/Release/", StringComparison.Ordinal) ? "Release" : "Debug";
    string plugin = Path.GetFullPath($"tests/Gamegrl.Editor.PluginFixture/bin/{configuration}/net10.0/Gamegrl.Editor.PluginFixture.dll");
    game.Load(dll, app, doc, registry: registry, extensions: [plugin]);
    if (registry.FindPanel("fixture") is null) throw new Exception("Extension panel missing");
    doc.Open("gamegrl scene 1\nentity 0000000000000001\n  Spinners.Spinner\n    Axis = 0 1 0\n    DegreesPerSecond = 123\n");
    string before = doc.Text;
    var reference = game.ContextReference!;
    app.World.Clear();
    game.Unload(app);
    if (registry.Panels.Count != 0 || registry.Commands.Count != 0) throw new Exception("Extension registrations survived unload");
    game.Load(dll, app, doc, registry: registry, extensions: [plugin]);
    if (registry.FindPanel("fixture") is null) throw new Exception("Extension did not reload");
    doc.Restore(before);
    if (doc.Text != before) throw new Exception("Reload changed serialized game state");
    app.World.Clear();
    game.Unload(app);
    return reference;
}

namespace Gamegrl.Tests
{
    /// <summary>A component with an entity field, for undo tests that rebuild the world between entries.</summary>
    public struct Follow : IComponent
    {
        public Entity Target;
        public float Speed;
    }
}
