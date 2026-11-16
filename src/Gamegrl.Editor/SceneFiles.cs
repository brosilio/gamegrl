namespace Gamegrl.Editor;

internal enum SceneFileDialog { None, Unsaved, SaveAs, Overwrite }

/// <summary>Scene navigation waits for successful saves; workers never touch the edit world.</summary>
internal sealed class SceneFiles(string root, SceneDocument document, EditorJobs jobs, Action saved)
{
    private Action? _pending;
    private Action? _afterSave;
    private int _generation;
    public string? CurrentPath { get; private set; }
    public SceneFileDialog Dialog { get; private set; }
    public string Destination = "Assets/untitled.scene";
    public string? Error { get; private set; }
    public bool Busy { get; private set; }
    public bool ExitRequested { get; private set; }

    public void RequestOpen(string path) => Navigate(() => Open(path));
    public void RequestNew() => Navigate(() =>
    {
        document.Open(Scene.TextHeader + "\n");
        document.MarkSaved(""); // An untitled scene must be saved before it has a disk identity.
        CurrentPath = null;
        _generation++;
    });
    public void RequestRecovered(string text) => Navigate(() =>
    {
        document.Open(text); document.MarkSaved(""); CurrentPath = null; _generation++;
    });
    public void RequestClose() => Navigate(() => ExitRequested = true);
    private void Navigate(Action action)
    {
        if (Busy || Dialog != SceneFileDialog.None || document.Playing) return;
        document.CommitEdit();
        if (document.Dirty) { _pending = action; Dialog = SceneFileDialog.Unsaved; Error = null; }
        else action();
    }
    public void Cancel()
    {
        if (Busy) return;
        Dialog = SceneFileDialog.None; Error = null; _pending = _afterSave = null;
    }
    public void Discard()
    {
        if (Busy || Dialog != SceneFileDialog.Unsaved) return;
        var action = _pending; _pending = null; Dialog = SceneFileDialog.None; action?.Invoke();
    }
    public void SaveChanges()
    {
        if (Busy || Dialog != SceneFileDialog.Unsaved) return;
        _afterSave = _pending; _pending = null; Dialog = SceneFileDialog.None;
        Save();
    }
    public void SaveAs()
    {
        if (Busy || document.Playing) return;
        Destination = CurrentPath ?? "Assets/untitled.scene";
        Dialog = SceneFileDialog.SaveAs; Error = null;
    }
    public void Save()
    {
        if (Busy || document.Playing) return;
        if (CurrentPath is null) { SaveAs(); return; }
        Write(CurrentPath, overwrite: true);
    }
    public void ConfirmDestination(bool overwrite = false)
    {
        if (Busy) return;
        try { Write(ValidatePath(root, Destination), overwrite); }
        catch (ArgumentException e) { Error = e.Message; }
    }
    internal static string ValidatePath(string root, string path)
    {
        path = path.Replace('\\', '/').Trim();
        string full = Path.GetFullPath(Path.Combine(root, path));
        string assets = Path.GetFullPath(Path.Combine(root, "Assets")) + Path.DirectorySeparatorChar;
        if (!full.StartsWith(assets, StringComparison.Ordinal) || !path.EndsWith(".scene", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a .scene path inside Assets/.");
        // Do not follow a directory symlink out of the project.
        for (string? dir = Path.GetDirectoryName(full); dir is not null && dir.Length >= assets.Length - 1; dir = Path.GetDirectoryName(dir))
            if (new DirectoryInfo(dir).LinkTarget is not null) throw new ArgumentException("Scene directories cannot be symbolic links.");
        return Path.GetRelativePath(root, full).Replace('\\', '/');
    }
    private void Write(string path, bool overwrite)
    {
        path = ValidatePath(root, path);
        document.CommitEdit();
        string text = document.Text;
        int generation = _generation;
        Busy = true; Error = null;
        bool applied = false;
        Action? continuation = null;
        jobs.Run("Save " + path, stop =>
        {
            string full = Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            string temporary = full + "." + Guid.NewGuid().ToString("N") + ".saving";
            try
            {
                File.WriteAllText(temporary, text);
                stop.ThrowIfCancellationRequested();
                try { File.Move(temporary, full, overwrite); }
                catch (IOException) when (!overwrite && File.Exists(full)) { return false; }
                return true;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }, written =>
        {
            applied = true;
            if (!written) { Destination = path; Dialog = SceneFileDialog.Overwrite; return; }
            if (generation != _generation) return;
            CurrentPath = path; document.MarkSaved(text);
            Dialog = SceneFileDialog.None;
            Log.Info("Saved " + path); saved();
            var next = _afterSave; _afterSave = null;
            if (!document.Dirty) continuation = next;
            else if (next is not null) { _pending = next; Dialog = SceneFileDialog.Unsaved; }
        }, () =>
        {
            Busy = false;
            continuation?.Invoke();
            if (!applied) { Error = "Save failed or was cancelled. See Console for details."; Dialog = SceneFileDialog.SaveAs; Destination = path; }
        });
    }
    private void Open(string path)
    {
        path = ValidatePath(root, path);
        Busy = true;
        long before = document.Revision;
        jobs.Run("Open " + path, _ => File.ReadAllText(Path.Combine(root, path)), text =>
        {
            if (document.Playing || document.Revision != before) { Log.Warn("Open cancelled because the scene changed while loading."); return; }
            document.Open(text); CurrentPath = path; _generation++;
            Log.Info("Opened " + path);
        }, () => Busy = false);
    }
}
