using Gamegrl.UI;

namespace Gamegrl.Editor;

internal sealed partial class EditorHost
{
    private void FileDialog()
    {
        float width = Math.Min(560, _ui.Screen.W - 20);
        var area = new Rect((_ui.Screen.W - width) / 2, Math.Max(10, (_ui.Screen.H - 220) / 2), width, 220);
        _ui.BeginPopup(area);
        _ui.Header(Files.Dialog == SceneFileDialog.Unsaved ? "Unsaved scene" : Files.Dialog == SceneFileDialog.Overwrite ? "Replace scene?" : "Save scene as");
        if (Files.Busy) _ui.Label("Saving…");
        else if (Files.Dialog == SceneFileDialog.Unsaved)
        {
            _ui.Label("Save changes to " + ScenePath + " before continuing?");
            _ui.Row(-1, -1, -1);
            if (_ui.Button("Save")) Files.SaveChanges();
            if (_ui.Button("Discard")) Files.Discard();
            if (_ui.Button("Cancel")) Files.Cancel();
        }
        else if (Files.Dialog == SceneFileDialog.Overwrite)
        {
            _ui.Label(Files.Destination + " already exists.");
            _ui.Row(-1, -1);
            if (_ui.Button("Replace")) Files.ConfirmDestination(overwrite: true);
            if (_ui.Button("Cancel")) Files.Cancel();
        }
        else
        {
            _ui.Label("Project path (Assets/name.scene)");
            _ui.TextField(_ui.Next(), "destination", ref Files.Destination);
            if (Files.Error is { } error) _ui.Label(error);
            _ui.Row(-1, -1);
            if (_ui.Button("Save")) Files.ConfirmDestination();
            if (_ui.Button("Cancel")) Files.Cancel();
        }
        _ui.EndPopup();
    }
}
