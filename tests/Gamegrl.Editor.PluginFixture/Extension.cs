using Gamegrl;
using Gamegrl.Editor;
using Spinners;
namespace Gamegrl.Editor.PluginFixture;
public struct EditorTag : IComponent { public int Value; }
public sealed class Extension : IEditorExtension
{
    public void Register(EditorRegistry registry)
    {
        registry.AddPanel("fixture", "Fixture", DockRegion.Right, (context, area) =>
        { context.Ui.BeginArea(area); context.Ui.Label("Editor extension"); context.Ui.EndArea(); });
        registry.AddMenu("fixture.create", "Fixture / Create", context => context.Edit(() => context.World.Spawn()));
        registry.AddDrawer<Spinner>((ref Spinner spinner, EditorContext context) =>
            context.Ui.DragFloat(context.Ui.Property("Speed"), "speed", ref spinner.DegreesPerSecond));
    }
}
