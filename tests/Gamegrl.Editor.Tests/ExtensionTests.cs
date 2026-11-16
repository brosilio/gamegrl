using Gamegrl.Editor;
using Gamegrl.UI;
namespace Gamegrl.Tests;
internal static class ExtensionTests
{
    public static void Register(Action<string, Action> test)
    {
        test("Dock layout: move, reorder, select, remove extensions, and refill empty regions", () =>
        {
            using var registry = new EditorRegistry();
            registry.AddPanel("one", "One", DockRegion.Left, (_, _) => { });
            registry.AddPanel("two", "Two", DockRegion.Center, (_, _) => { });
            registry.AddPanel("three", "Three", DockRegion.Center, (_, _) => { });
            var docks = new DockLayout(); docks.Sync(registry.Panels);
            Check(docks.Selected(DockRegion.Left) == "one");
            docks.Move("one", DockRegion.Center, 1);
            Check(docks.Selected(DockRegion.Left) is null && docks.Tabs(DockRegion.Center).SequenceEqual(new[] { "two", "one", "three" }));
            docks.Move("three", DockRegion.Center, 0); Check(docks.Selected(DockRegion.Center) == "three");
            registry.RemovePanel("three"); docks.Sync(registry.Panels);
            Check(docks.Selected(DockRegion.Center) == "two");
            registry.AddPanel("new", "New", DockRegion.Left, (_, _) => { }); docks.Sync(registry.Panels);
            Check(docks.Selected(DockRegion.Left) == "new");
        });
        test("Editor registry: extension scopes, transactional failures, menus and custom drawers", () =>
        {
            using var registry = new EditorRegistry();
            registry.AddPanel("builtin", "Built-in", DockRegion.Center, (_, _) => { });
            using (registry.Register(new Extension()))
            {
                Check(registry.FindPanel("extra") is not null && registry.Commands.Count == 1);
                var builder = EngineBuilder.Create(); builder.WorkerThreads = 0;
                using var app = builder.Build();
                var document = new SceneDocument(app.World);
                var context = new EditorContext(app, null!, document, new EditorJobs());
                registry.Commands.Single().Execute(context); Check(app.World.EntityCount == 1);
                unsafe
                {
                    var value = LocalTransform.Identity;
                    Check(registry.DrawComponent(ComponentRegistry.Get(Component<LocalTransform>.Id).StableId, (nint)(&value), context));
                    Check(value.Position.X == 17);
                }
                context.Jobs.Dispose();
            }
            Check(registry.FindPanel("extra") is null && registry.Commands.Count == 0 && registry.FindPanel("builtin") is not null);
            try { registry.Register(new Broken()); throw new Exception("Expected extension failure"); }
            catch (InvalidOperationException) { }
            Check(registry.FindPanel("partial") is null && registry.FindPanel("builtin") is not null);
        });
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Extension assertion failed"); }
    private sealed class Extension : IEditorExtension
    {
        public void Register(EditorRegistry registry)
        {
            registry.AddPanel("extra", "Extra", DockRegion.Bottom, (_, _) => { });
            registry.AddCommand("spawn", "Create: Empty", context => context.Edit(() => context.World.Spawn()));
            registry.AddDrawer<LocalTransform>((ref LocalTransform value, EditorContext _) => value.Position.X = 17);
        }
    }
    private sealed class Broken : IEditorExtension
    {
        public void Register(EditorRegistry registry)
        { registry.AddPanel("partial", "Partial", DockRegion.Left, (_, _) => { }); throw new InvalidOperationException("broken extension"); }
    }
}
