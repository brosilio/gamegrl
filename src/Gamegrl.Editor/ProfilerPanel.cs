using System.Globalization;
using Gamegrl.Platform;
using Gamegrl.UI;

namespace Gamegrl.Editor;

/// <summary>
/// The Profiler panel: a frame-time graph, a summary that splits each frame between the game, the engine, the editor
/// and waiting, and the merged call tree (or a flat list by self time) over a range of frames. Clicking the graph
/// pauses recording on that frame. Export writes the history as a Chrome trace for Perfetto.
/// </summary>
internal sealed partial class EditorHost
{
    private readonly ProfileTree _profile = new();
    private long _profileBuiltAt = -1, _profileSelected = -1;
    private int _profileRange = 1, _profileView; // range: index into ProfileRanges; view: 0 tree, 1 flat
    private bool _profileGame = true, _profileEngine = true, _profileEditor = true, _profileWait = true;
    private (int Range, int View, long Selected) _profileKey;
    private readonly Dictionary<ulong, bool> _profileOpen = new();
    private int[] _flat = new int[64];
    private long[] _flatTicks = new long[64];
    private long[] _flatCalls = new long[64];
    private static readonly int[] ProfileRanges = [1, 60, 300];
    private static readonly string[] ProfileRangeLabels = ["1 frame", "60 frames", "300 frames"];
    private static readonly string[] ProfileViews = ["tree", "flat"];
    private const double Budget = 1000.0 / 60;

    private void ProfilerPanel(Rect area)
    {
        if (!Profiler.Enabled)
        {
            _ui.TextIn(new Rect(area.X, area.Y, area.W, 1), "profiling is off", _ui.Theme.Dim);
            if (_ui.Link(new Rect(area.X + 18, area.Y, 6, 1), "profiler-on", "turn on", _ui.Theme.Accent)) Profiler.Enabled = true;
            return;
        }
        if (area.H < 6) return;
        var theme = _ui.Theme;

        // Controls.
        float x = area.X;
        bool paused = Profiler.Paused;
        if (_ui.Link(new Rect(x, area.Y, 8, 1), "profiler-pause", paused ? "‖ paused" : "● live", paused ? theme.Warning : theme.Accent)) ToggleProfilerPause();
        x += 10;
        _ui.Combo(new Rect(x, area.Y, 13, 1), "profiler-range", ref _profileRange, ProfileRangeLabels);
        x += 15;
        _ui.Combo(new Rect(x, area.Y, 7, 1), "profiler-view", ref _profileView, ProfileViews);
        x += 9;
        if (_profileView == 1)
        {
            x = ProfileFilter(x, area.Y, "profiler-game", "game", ref _profileGame, theme.Accent);
            x = ProfileFilter(x, area.Y, "profiler-engine", "engine", ref _profileEngine, theme.Text);
            x = ProfileFilter(x, area.Y, "profiler-editor", "editor", ref _profileEditor, theme.AxisZ);
            ProfileFilter(x, area.Y, "profiler-wait", "wait", ref _profileWait, theme.Dim);
        }
        if (_ui.Link(new Rect(area.Right - 6, area.Y, 6, 1), "profiler-export", "export", theme.Dim)) ExportProfile();

        // The range: the selected frame while paused on one, else the latest frames.
        long newest = Profiler.FrameCount - 1;
        if (_profileSelected > newest || _profileSelected < Profiler.OldestFrame) _profileSelected = -1;
        int range = ProfileRanges[_profileRange];
        long first = _profileSelected >= 0 ? _profileSelected : newest - range + 1;
        int count = _profileSelected >= 0 ? 1 : range;
        // Rebuilding merges every zone of the range; while live, a few frames' staleness is invisible.
        var key = (_profileRange, _profileView, _profileSelected);
        if (key != _profileKey || _profileBuiltAt < 0 || newest - _profileBuiltAt >= 6 || (paused && _profileBuiltAt != newest))
        {
            _profile.Build(first, count);
            _profileBuiltAt = newest;
            _profileKey = key;
        }

        Summary(new Rect(area.X, area.Y + 1, area.W, 1));
        Graph(new Rect(area.X, area.Y + 2, area.W, 3));
        var table = new Rect(area.X, area.Y + 5, area.W, area.H - 5);
        float name = Math.Max(10, table.W - 27);
        _ui.TextIn(new Rect(table.X, table.Y, name, 1), _profileSelected >= 0 ? "zone · selected frame" : "zone · mean per frame", theme.Dim);
        _ui.TextIn(new Rect(table.X + name, table.Y, 8, 1), "ms", theme.Dim, 1);
        _ui.TextIn(new Rect(table.X + name + 9, table.Y, 8, 1), "self", theme.Dim, 1);
        _ui.TextIn(new Rect(table.X + name + 18, table.Y, 8, 1), "calls", theme.Dim, 1);
        _ui.BeginScroll(table with { Y = table.Y + 1, H = Math.Max(0, table.H - 1) }, "profiler-scroll");
        if (_profileView == 0)
        {
            ProfileRows(ProfileTree.Root, 0, 0, name);
            ProfileRows(_profile.Workers, 0, 1, name);
            ProfileRows(_profile.Gpu, 0, 2, name);
        }
        else FlatRows(name);
        _ui.EndScroll();
    }

    private float ProfileFilter(float x, float y, string label, string name, ref bool value, Color32 dot)
    {
        _ui.Toggle(new Rect(x, y, name.Length + 2, 1), label, name, ref value, dot);
        return x + name.Length + 4;
    }

    private void ToggleProfilerPause()
    {
        Profiler.Paused = !Profiler.Paused;
        if (!Profiler.Paused) _profileSelected = -1;
    }

    private void Summary(Rect r)
    {
        ref readonly var s = ref _profile.Stats;
        if (_profile.Frames == 0) { _ui.TextIn(r, "no frames yet", _ui.Theme.Dim); return; }
        double wait = _profile.CategoryMs(ProfileCategory.Wait);
        double game = _profile.CategoryMs(ProfileCategory.Game), engine = _profile.CategoryMs(ProfileCategory.Engine), editor = _profile.CategoryMs(ProfileCategory.Editor);
        Span<char> buffer = stackalloc char[256];
        var text = _profileSelected >= 0
            ? SpanFormat.Write(buffer, $"frame {_profileSelected}  {s.MeanMs:0.00} ms")
            : SpanFormat.Write(buffer, $"{s.MeanMs:0.00} ms  {1000 / Math.Max(0.001, s.MeanMs):0} fps  p95 {s.P95Ms:0.0}  max {s.MaxMs:0.0}");
        float x = r.X;
        // Vsync-paced frames land a hair over the budget; warn only past a tenth over.
        x = _ui.Draw.Text(new System.Numerics.Vector2(x, r.Y), text, s.P95Ms > Budget * 1.1 ? _ui.Theme.Warning : _ui.Theme.Text) + 2;
        x = Part(x, r.Y, "game", game, _ui.Theme.Accent);
        x = Part(x, r.Y, "engine", engine, _ui.Theme.Text);
        x = Part(x, r.Y, "editor", editor, _ui.Theme.AxisZ);
        x = Part(x, r.Y, "wait", wait, _ui.Theme.Dim);
        if (s.GpuMeanMs > 0) x = Part(x, r.Y, "gpu", s.GpuMeanMs, _ui.Theme.AxisY);
        var (alloc, allocUnit) = Scaled(s.AllocatedPerFrame);
        var (heap, heapUnit) = Scaled(s.HeapBytes);
        text = SpanFormat.Write(buffer, $"alloc {alloc:0.#} {allocUnit}/f  gc {s.Gen0}·{s.Gen1}·{s.Gen2}  heap {heap:0.#} {heapUnit}");
        _ui.Draw.Text(new System.Numerics.Vector2(x, r.Y), text, s.AllocatedPerFrame > 0 ? _ui.Theme.Warning : _ui.Theme.Dim);
    }

    private float Part(float x, float y, string label, double ms, Color32 color)
    {
        Span<char> buffer = stackalloc char[32];
        _ui.Draw.Glyph(x, y, '●', color);
        return _ui.Draw.Text(new System.Numerics.Vector2(x + 2, y), SpanFormat.Write(buffer, $"{label} {ms:0.00}"), _ui.Theme.Text) + 2;
    }

    private static (double Value, string Unit) Scaled(double bytes) =>
        bytes < 1024 ? (bytes, "B") : bytes < 1 << 20 ? (bytes / 1024, "KB") : (bytes / (1 << 20), "MB");

    /// <summary>One column per frame, newest on the right, three cells tall in eighths; over budget turns warning-colored.</summary>
    private void Graph(Rect r)
    {
        ReadOnlySpan<char> blocks = " ▁▂▃▄▅▆▇█";
        int columns = (int)r.W;
        long newest = Profiler.FrameCount - 1, oldest = Math.Max(Profiler.OldestFrame, newest - columns + 1);
        double max = Budget * 2;
        for (long n = oldest; n <= newest; n++) max = Math.Max(max, Profiler.Frame(n).Milliseconds);
        int levels = (int)r.H * 8;
        uint id = _ui.Id("profiler-graph");
        bool clicked = _ui.Behavior(r, id, out bool hover, out _);
        for (long n = oldest; n <= newest; n++)
        {
            int column = (int)(r.X + columns - 1 - (newest - n));
            double ms = Profiler.Frame(n).Milliseconds;
            int level = Math.Clamp((int)Math.Round(ms / max * levels), 1, levels);
            bool selected = n == _profileSelected, hovered = hover && (int)_ui.Mouse.X == column;
            var color = selected ? _ui.Theme.Accent : hovered ? _ui.Theme.Text : ms > Budget * 2 ? _ui.Theme.Error : ms > Budget * 1.1 ? _ui.Theme.Warning : _ui.Theme.Dim;
            for (int row = 0; row < r.H; row++)
            {
                int fill = Math.Clamp(level - row * 8, 0, 8);
                if (fill > 0) _ui.Draw.Glyph(column, r.Bottom - 1 - row, blocks[fill], color);
            }
            if (clicked && (int)_ui.Mouse.X == column)
            {
                _profileSelected = n;
                Profiler.Paused = true;
            }
        }
        // The budget as a faint tick on the left edge.
        int budgetRow = (int)Math.Clamp(r.Bottom - 1 - Math.Floor(Budget / max * levels / 8), r.Y, r.Bottom - 1);
        _ui.Draw.Glyph(r.X, budgetRow, '╶', _ui.Theme.Border);
    }

    private static ulong ProfileKey(ulong parent, int marker) => (parent ^ (uint)marker) * 0x100000001b3UL + 0x9e3779b97f4a7c15UL;

    private void ProfileRows(int node, ulong parentKey, int depth, float nameWidth)
    {
        ref readonly var n = ref _profile[node];
        if (node > _profile.Gpu && n.Ticks == 0 && n.Calls == 0) return;
        if (node <= _profile.Gpu && n.FirstChild < 0) return;
        ulong key = ProfileKey(parentKey, node <= _profile.Gpu ? -1 - node : n.Marker);
        bool children = n.FirstChild >= 0;
        bool open = _profileOpen.GetValueOrDefault(key, depth < 2 && node != _profile.Workers);
        if (_ui.RowVisible())
        {
            var row = _ui.Next();
            var nameRect = row with { W = nameWidth };
            var category = node <= _profile.Gpu ? ProfileCategory.Engine : Profiler.Marker(n.Marker).Category;
            _ui.TreeNode(nameRect, key, _profile.Name(node), children, false, ref open, depth);
            if (node > _profile.Gpu) _ui.Draw.Recolor(nameRect with { X = nameRect.X + depth + 2, W = Math.Max(0, nameRect.W - depth - 2) }, CategoryColor(category));
            Number(row.X + nameWidth, row.Y, _profile.Ms(node), 2);
            Number(row.X + nameWidth + 9, row.Y, _profile.SelfMs(node), 2);
            if (node > _profile.Gpu) Number(row.X + nameWidth + 18, row.Y, (double)n.Calls / Math.Max(1, _profile.Frames), _profile.Frames > 1 ? 1 : 0);
        }
        else _ui.Next();
        _profileOpen[key] = open;
        if (!open) return;
        for (int child = n.FirstChild; child >= 0; child = _profile[child].NextSibling) ProfileRows(child, key, depth + 1, nameWidth);
    }

    private Color32 CategoryColor(ProfileCategory category) => category switch
    {
        ProfileCategory.Game => _ui.Theme.Accent,
        ProfileCategory.Editor => _ui.Theme.AxisZ,
        ProfileCategory.Wait => _ui.Theme.Dim,
        ProfileCategory.Gpu => _ui.Theme.AxisY,
        _ => _ui.Theme.Text,
    };

    private void Number(float x, float y, double value, int decimals)
    {
        Span<char> text = stackalloc char[24];
        value.TryFormat(text, out int n, decimals == 2 ? "0.00" : decimals == 1 ? "0.0" : "0", CultureInfo.InvariantCulture);
        _ui.TextIn(new Rect(x, y, 8, 1), text[..n], value == 0 ? _ui.Theme.Dim : _ui.Theme.Text, 1);
    }

    /// <summary>Every marker once, by self time across the CPU threads, filtered by category.</summary>
    private void FlatRows(float nameWidth)
    {
        int markers = Profiler.MarkerCount;
        if (_flatTicks.Length < markers) { _flatTicks = new long[markers * 2]; _flatCalls = new long[markers * 2]; _flat = new int[markers * 2]; }
        Array.Clear(_flatTicks, 0, markers);
        Array.Clear(_flatCalls, 0, markers);
        foreach (ref readonly var n in _profile.Nodes)
            if (n.Marker >= 0) { _flatTicks[n.Marker] += n.SelfTicks; _flatCalls[n.Marker] += n.Calls; }
        int count = 0;
        for (int m = 0; m < markers; m++)
        {
            if (_flatCalls[m] == 0) continue;
            var category = Profiler.Marker(m).Category;
            bool shown = category switch
            {
                ProfileCategory.Game => _profileGame,
                ProfileCategory.Editor => _profileEditor,
                ProfileCategory.Wait => _profileWait,
                _ => _profileEngine,
            };
            if (shown) _flat[count++] = m;
        }
        // Insertion sort, largest first: a few hundred markers at most, and no allocation.
        for (int i = 1; i < count; i++)
        {
            int m = _flat[i], j = i - 1;
            while (j >= 0 && _flatTicks[_flat[j]] < _flatTicks[m]) { _flat[j + 1] = _flat[j]; j--; }
            _flat[j + 1] = m;
        }
        int frames = Math.Max(1, _profile.Frames);
        for (int i = 0; i < count; i++)
        {
            var row = _ui.Next();
            if (!_ui.RowVisible()) continue;
            var marker = Profiler.Marker(_flat[i]);
            _ui.TextIn(row with { W = nameWidth }, marker.Name, CategoryColor(marker.Category));
            double ms = _flatTicks[_flat[i]] * Profiler.TicksToMs / frames;
            Number(row.X + nameWidth + 9, row.Y, ms, 2);
            Number(row.X + nameWidth + 18, row.Y, (double)_flatCalls[_flat[i]] / frames, frames > 1 ? 1 : 0);
        }
    }

    /// <summary>Write the whole history as a Chrome trace under Library/Profiles and log where.</summary>
    private void ExportProfile()
    {
        string directory = Path.Combine(_root, "Library", "Profiles");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "profile-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".json");
        long oldest = Profiler.OldestFrame;
        using (var writer = new StreamWriter(path)) ProfileTree.WriteChromeTrace(writer, oldest, (int)(Profiler.FrameCount - oldest));
        Log.Info($"Profile written to {path} (open it in ui.perfetto.dev)");
    }
}
