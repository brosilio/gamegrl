using System.Globalization;
using System.Numerics;
using System.Text;
using AnsiDotNet.Layout;
using Gamegrl.Platform;
using Gamegrl.Rhi;
using Gamegrl.UI;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Editor;

/// <summary>Editor controls over an AnsiDotNet Surface. All layout and hit rectangles use character cells.</summary>
public sealed class CellUi
{
    private sealed class Area(Rect rect) { public Rect Rect = rect; public float Y = rect.Y, Indent; public Rect[]? Row; public int Column; }
    private readonly Stack<Area> _areas = new();
    private readonly Stack<uint> _ids = new();
    private readonly Dictionary<uint, float> _floats = new();
    private readonly Dictionary<uint, bool> _bools = new();
    private readonly List<(uint Id, Rect Area, uint Scroll)> _focusable = new();
    private readonly List<(uint Id, Rect Area, uint Scroll)> _previousFocusable = new();
    private readonly Stack<(uint Id, Rect Area, float Start)> _scroll = new();
    private Rect? _popup, _previousPopup;
    private bool _inPopup, _claimed, _commit;
    private uint _editId, _keyboardFocus;
    private string _buffer = "";
    private byte _heldPressed, _heldReleased;
    private int _caret, _anchor;
    private IPlatform? _platform;
    private double _lastClick = -100, _time;
    private Vector2 _lastClickPosition;
    public CellUi(bool ascii = false) { _main.Ascii = _overlay.Ascii = ascii; Draw.Reset(1, 1); }
    private readonly CellDraw _main = new(), _overlay = new();
    private readonly List<Rect> _overlays = new();
    private readonly Dictionary<string, Rect> _controls = new(StringComparer.Ordinal);
    public CellDraw Draw => _inPopup ? _overlay : _main;
    public Rect? ControlRect(string label) => _controls.TryGetValue(label, out var area) ? area : null;
    public CellDraw Target => Draw;
    public CellFont Font { get; } = new();
    public Theme Theme { get; } = new() { RowHeight = 1, Padding = 0, Spacing = 0, Indent = 1, TabHeight = 1, SplitterThickness = 1, ScrollbarWidth = 1 };
    public Input Input { get; private set; } = null!;
    public Rect Screen { get; private set; }
    public Vector2 Mouse => Input.MousePosition;
    public Vector2 MouseDelta => Input.MouseDelta;
    public uint Active { get; set; }
    public uint Focus { get; private set; }
    public bool KeyboardNavigation { get; set; } = true;
    public bool WantsKeyboard => Focus != 0;
    public bool DoubleClicked { get; private set; }
    public Rect Remaining => new(A.Rect.X + A.Indent, A.Y, Math.Max(0, A.Rect.W - A.Indent), Math.Max(0, A.Rect.Bottom - A.Y));
    private Area A => _areas.Peek();
    public void Begin(Input input, IPlatform? platform, Vector2 size, double time)
    {
        Input = input; _platform = platform; _time = time;
        Screen = new Rect(0, 0, Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
        _main.Reset((int)Screen.W, (int)Screen.H); _overlay.Reset((int)Screen.W, (int)Screen.H);
        _overlays.Clear(); _controls.Clear();
        _previousPopup = _popup; _popup = null; _inPopup = false; _claimed = false;
        _previousFocusable.Clear(); _previousFocusable.AddRange(_focusable); _focusable.Clear();
        _areas.Clear(); _areas.Push(new Area(Screen)); _ids.Clear(); _ids.Push(2166136261);
        if (Focus == 0) _editId = 0;
        // A click outside the active text field first commits the field, then reaches its target one frame later,
        // so whatever the click changes (selection, tabs, play, save) always sees the committed value.
        if (_heldPressed != 0 || _heldReleased != 0)
        {
            input.AddMouseEdges(_heldPressed, _heldReleased);
            _heldPressed = _heldReleased = 0;
        }
        else if (_editId != 0 && input.AnyMousePressed && !FocusableArea(_editId).Contains(input.MousePosition))
        {
            (_heldPressed, _heldReleased) = input.TakeMouseEdges();
            _commit = true;
        }
        DoubleClicked = input.WasPressed(MouseButton.Left) && time - _lastClick < 0.3 && Vector2.Distance(_lastClickPosition, Mouse) < 1;
        if (input.WasPressed(MouseButton.Left)) { _lastClick = time; _lastClickPosition = Mouse; }
        bool step = input.WasPressed(Key.Tab) || Focus == 0 && (input.WasPressed(Key.Up) || input.WasPressed(Key.Down));
        if (KeyboardNavigation && step && _previousFocusable.Count > 0)
        {
            int count = _previousFocusable.Count;
            int index = IndexOfFocusable(_keyboardFocus);
            index = (index + (input.Shift || input.WasPressed(Key.Up) ? -1 : 1) + count) % count;
            _keyboardFocus = _previousFocusable[index].Id;
        }
    }
    private int IndexOfFocusable(uint id)
    {
        for (int i = 0; i < _previousFocusable.Count; i++) if (_previousFocusable[i].Id == id) return i;
        return -1;
    }
    /// <summary>Last frame's rectangle for a focusable control; empty if it was not drawn.</summary>
    private Rect FocusableArea(uint id) => IndexOfFocusable(id) is int i and >= 0 ? _previousFocusable[i].Area : default;
    public void End()
    {
        foreach (var rectangle in _overlays) _main.Composite(_overlay, rectangle);
        if (Input.WasReleased(MouseButton.Left)) Active = 0;
        if (!_claimed) Focus = 0;
        _commit = false;
    }
    public void CommitTextEdit() => _commit = true;
    /// <summary>Stack depths before a caller-supplied draw, so a throwing panel or drawer cannot unbalance the frame.</summary>
    public readonly record struct Checkpoint(int Areas, int Ids, int Scrolls, int Clips, int OverlayClips, bool InPopup);
    public Checkpoint Save() => new(_areas.Count, _ids.Count, _scroll.Count, _main.ClipDepth, _overlay.ClipDepth, _inPopup);
    public void Rewind(in Checkpoint checkpoint)
    {
        while (_areas.Count > checkpoint.Areas) _areas.Pop();
        while (_ids.Count > checkpoint.Ids) _ids.Pop();
        while (_scroll.Count > checkpoint.Scrolls) _scroll.Pop();
        _main.PopClipsTo(checkpoint.Clips); _overlay.PopClipsTo(checkpoint.OverlayClips);
        _inPopup = checkpoint.InPopup;
    }
    public uint Id(string text)
    {
        uint hash = _ids.Peek(); foreach (char c in text) hash = (hash ^ c) * 16777619;
        return hash == 0 ? 1 : hash;
    }
    public void PushId(int id) => _ids.Push(Id(id.ToString(CultureInfo.InvariantCulture)));
    public void PushId(ulong id) => _ids.Push(Id(id.ToString(CultureInfo.InvariantCulture)));
    public void PushId(string id) => _ids.Push(Id(id));
    public void PopId() => _ids.Pop();
    public float GetFloat(uint id, float fallback = 0) => _floats.GetValueOrDefault(id, fallback);
    public void SetFloat(uint id, float value) => _floats[id] = value;
    public bool IsHovered(Rect rect) => rect.Contains(Mouse) && Draw.Clip.Contains(Mouse) && (_inPopup || _previousPopup is not { } popup || !popup.Contains(Mouse));
    public bool Behavior(Rect rect, uint id, out bool hover, out bool held)
    {
        hover = IsHovered(rect) && (Active == 0 || Active == id);
        if (hover && Input.WasPressed(MouseButton.Left)) { Active = id; _keyboardFocus = id; }
        held = Active == id && Input.IsDown(MouseButton.Left);
        return hover && Active == id && Input.WasReleased(MouseButton.Left);
    }
    private bool Activate(Rect rect, uint id, out bool hover, out bool held)
    {
        _focusable.Add((id, rect, _scroll.TryPeek(out var scroll) ? scroll.Id : 0));
        bool click = Behavior(rect, id, out hover, out held);
        return click || KeyboardNavigation && Focus == 0 && _keyboardFocus == id && (Input.WasPressed(Key.Enter) || Input.WasPressed(Key.Space));
    }
    public void BeginArea(Rect area) { _areas.Push(new Area(area)); Draw.PushClip(area); }
    public float EndArea() { var area = _areas.Pop(); Draw.PopClip(); return area.Y - area.Rect.Y; }
    public Rect Next(float height = 1)
    {
        if (A.Row is { } row)
        {
            var result = row[A.Column++]; if (A.Column == row.Length) { A.Row = null; A.Y = result.Bottom; }
            return result;
        }
        var rect = new Rect(A.Rect.X + A.Indent, A.Y, Math.Max(0, A.Rect.W - A.Indent), height);
        A.Y += height; return rect;
    }
    public void Row(params float[] widths)
    {
        var lengths = widths.Select(w => w > 0 ? Length.Fixed((int)w) : Length.Fill(-w)).ToArray();
        var result = new AnsiDotNet.Rect[widths.Length];
        // AnsiDotNet owns cell rounding, constraints and fill distribution for every control row.
        CellDraw.To(Remaining with { H = 1 }).Split(Axis.Horizontal, lengths, result, gap: 1);
        A.Row = result.Select(CellDraw.From).ToArray(); A.Column = 0;
    }
    public void Indent() => A.Indent++;
    public void Unindent() => A.Indent = Math.Max(0, A.Indent - 1);
    public Rect Property(string name)
    {
        var r = Next(); int label = (int)(r.W * Theme.LabelWidthRatio);
        TextIn(r with { W = label }, name, Theme.TextDim);
        return new Rect(r.X + label, r.Y, Math.Max(0, r.W - label), 1);
    }
    public void TextIn(Rect rect, string text, Color32 color, float align = 0)
    { Draw.PushClip(rect); Draw.Text(new Vector2(rect.X + Math.Max(0, (rect.W - Font.Measure(text)) * align), rect.Y), text, color); Draw.PopClip(); }
    public void Label(string text, bool dim = false) => TextIn(Next(), text, dim ? Theme.TextDim : Theme.Text);
    public void Header(string text) { var r = Next(); Draw.Rect(r, Theme.PanelHeader); TextIn(r, text, Theme.Text); }
    public void Separator() { var r = Next(); TextIn(r, new string('─', Math.Max(0, (int)r.W)), Theme.Border); }
    public bool Button(string label) => Button(Next(), label);
    public bool Button(Rect rect, string label)
    {
        _controls[label] = rect;
        bool result = Activate(rect, Id(label), out bool hover, out bool held);
        Draw.Rect(rect, held ? Theme.WidgetActive : hover ? Theme.WidgetHover : Theme.Widget);
        TextIn(rect, _keyboardFocus == Id(label) && KeyboardNavigation ? ">" + label : label, _keyboardFocus == Id(label) ? Theme.Accent : Theme.Text, 0.5f); return result;
    }
    public bool Selectable(Rect rect, string label, bool selected)
    {
        bool result = Activate(rect, Id(label), out bool hover, out _);
        if (selected || hover || _keyboardFocus == Id(label)) Draw.Rect(rect, selected ? Theme.Selection : Theme.WidgetHover);
        TextIn(rect, label, Theme.Text); return result;
    }
    public bool Checkbox(Rect rect, string id, ref bool value)
    {
        bool clicked = Activate(rect, Id(id), out _, out _);
        if (clicked) value = !value; TextIn(rect, value ? "[✓]" : "[ ]", Theme.Text); return clicked;
    }
    public bool TreeNode(Rect rect, string id, string label, bool hasChildren, bool selected, ref bool open, float depth)
    {
        rect = rect with { X = rect.X + depth, W = Math.Max(0, rect.W - depth) };
        bool clicked = Activate(rect, Id(id), out bool hover, out _);
        bool focused = KeyboardNavigation && _keyboardFocus == Id(id);
        if (selected || hover || focused) Draw.Rect(rect, selected ? Theme.Selection : Theme.WidgetHover);
        if (KeyboardNavigation && _keyboardFocus == Id(id) && hasChildren)
        { if (Input.WasPressed(Key.Left)) open = false; if (Input.WasPressed(Key.Right)) open = true; }
        if (hasChildren && clicked && (Mouse.X < rect.X + 2 || DoubleClicked || Input.WasPressed(Key.Space))) { open = !open; clicked = false; }
        TextIn(rect, (hasChildren ? open ? "▾" : "▸" : " ") + (selected ? "*" : focused ? ">" : " ") + label, Theme.Text); return clicked;
    }
    public void Image(Rect rect, GpuTexture texture) => Draw.Image(rect, texture);
    public float Splitter(Rect rect, string id, float position, bool vertical, float min, float max)
    {
        uint key = Id(id);
        Behavior(rect, key, out bool hover, out bool held);
        Draw.Rect(rect, hover || held ? Theme.Accent : Theme.Border);
        // Track the drag from its anchor: sub-cell mouse motion accumulates instead of rounding away each frame.
        float mouse = vertical ? Mouse.X : Mouse.Y;
        if (held && Input.WasPressed(MouseButton.Left)) SetFloat(key ^ 0x9e3779b9, position - mouse);
        if (held) position = Math.Clamp(mouse + GetFloat(key ^ 0x9e3779b9), min, max);
        return MathF.Round(position);
    }
    public void BeginScroll(Rect rect, string id)
    {
        uint key = Id(id); float content = GetFloat(key ^ 0x5bd1e995);
        float offset = Math.Clamp(GetFloat(key), 0, Math.Max(0, content - rect.H));
        if (IsHovered(rect)) offset = Math.Clamp(offset - Input.Wheel.Y * 3, 0, Math.Max(0, content - rect.H));
        SetFloat(key, offset); _scroll.Push((key, rect, offset)); BeginArea(rect); A.Y -= offset;
        var focused = _previousFocusable.FirstOrDefault(f => f.Id == _keyboardFocus && f.Scroll == key);
        if (Focus == 0 && focused.Id != 0)
        {
            if (focused.Area.Y < rect.Y) SetFloat(key, Math.Max(0, offset + focused.Area.Y - rect.Y));
            if (focused.Area.Bottom > rect.Bottom) SetFloat(key, Math.Min(Math.Max(0, content - rect.H), offset + focused.Area.Bottom - rect.Bottom));
        }
    }
    public void EndScroll()
    {
        var (key, rect, offset) = _scroll.Pop(); float used = EndArea() + offset;
        SetFloat(key ^ 0x5bd1e995, used);
        if (used > rect.H && rect.W > 0) Draw.Text(new Vector2(rect.Right - 1, rect.Y + (rect.H - 1) * offset / Math.Max(1, used - rect.H)), "█", Theme.TextDim);
    }
    public void BeginPopup(Rect rect)
    {
        _overlays.Add(rect);
        _inPopup = true; _popup = rect; Draw.PushClip(Screen); Draw.Rect(rect, Theme.PanelHeader); Draw.RectOutline(rect, Theme.Border);
        BeginArea(rect.Shrink(1));
    }
    public void EndPopup() { EndArea(); Draw.PopClip(); _inPopup = false; }
    public void Tooltip(string text)
    {
        var r = new Rect(Math.Clamp(Mouse.X + 1, 0, Math.Max(0, Screen.W - Font.Measure(text) - 2)), Math.Min(Screen.H - 1, Mouse.Y + 1), Math.Min(Screen.W, Font.Measure(text) + 2), 1);
        bool popup = _inPopup; _inPopup = true; _overlays.Add(r);
        Draw.PushClip(Screen); Draw.Rect(r, Theme.PanelHeader); TextIn(r, text, Theme.Text); Draw.PopClip(); _inPopup = popup;
    }
    public bool Combo(Rect rect, string label, ref int selected, IReadOnlyList<string> items)
    {
        _controls[label] = rect;
        uint id = Id(label); bool open = _bools.GetValueOrDefault(id);
        if (Activate(rect, id, out _, out _)) open = !open;
        Draw.Rect(rect, Theme.Widget); TextIn(rect, (selected >= 0 && selected < items.Count ? items[selected] : "") + " ▾", Theme.Text);
        bool changed = false;
        if (open)
        {
            float height = Math.Min(items.Count + 2, Math.Max(3, Screen.H / 2));
            var popup = new Rect(Math.Clamp(rect.X, 0, Math.Max(0, Screen.W - rect.W)), rect.Bottom, Math.Max(10, rect.W), height);
            if (popup.Bottom > Screen.Bottom) popup = popup with { Y = Math.Max(0, rect.Y - height) };
            BeginPopup(popup); BeginScroll(Remaining, label + "#items");
            for (int i = 0; i < items.Count; i++)
            { PushId(i); if (Selectable(Next(), items[i], i == selected)) { changed = i != selected; selected = i; open = false; } PopId(); }
            EndScroll(); EndPopup();
            if (Input.WasPressed(Key.Escape) || Input.WasPressed(MouseButton.Left) && !popup.Contains(Mouse) && !rect.Contains(Mouse)) open = false;
        }
        _bools[id] = open; return changed;
    }
    public bool TextField(Rect rect, string label, ref string value)
    {
        _controls[label] = rect;
        uint id = Id(label + "#text");
        bool changed = false, started = false;
        bool click = Activate(rect, id, out bool hover, out bool held);
        if (_editId != id && (click || hover && Input.WasPressed(MouseButton.Left)))
        {
            _editId = id; Focus = id; _buffer = value; _caret = _buffer.Length; _anchor = 0; started = true;
        }
        if (_editId == id)
        {
            _claimed = true; Focus = id;
            bool cancel = Input.WasPressed(Key.Escape);
            bool finish = _commit || !started && (Input.WasPressed(Key.Enter) || Input.WasPressed(Key.Tab)) || Input.WasPressed(MouseButton.Left) && !hover;
            if (Input.Ctrl && Input.WasPressed(Key.A)) { _anchor = 0; _caret = _buffer.Length; }
            string selection = _buffer[Math.Min(_caret, _anchor)..Math.Max(_caret, _anchor)];
            if (Input.Ctrl && (Input.WasPressed(Key.C) || Input.WasPressed(Key.X))) _platform?.SetClipboardText(selection);
            if (Input.Ctrl && Input.WasPressed(Key.X)) DeleteSelection();
            if (Input.Ctrl && Input.WasPressed(Key.V)) Insert((_platform?.GetClipboardText() ?? "").Replace('\n', ' ').Replace('\r', ' '));
            else if (Input.TypedText.Length > 0) Insert(Input.TypedText.Replace('\n', ' ').Replace('\r', ' '));
            if (Input.WasPressedOrRepeated(Key.Backspace)) { if (!DeleteSelection() && _caret > 0) { int prev = Previous(_caret); _buffer = _buffer.Remove(prev, _caret - prev); _caret = _anchor = prev; } }
            if (Input.WasPressedOrRepeated(Key.Delete)) { if (!DeleteSelection() && _caret < _buffer.Length) _buffer = _buffer.Remove(_caret, NextRune(_caret) - _caret); }
            if (Input.WasPressedOrRepeated(Key.Left)) Move(Previous(_caret));
            if (Input.WasPressedOrRepeated(Key.Right)) Move(NextRune(_caret));
            if (Input.WasPressed(Key.Home)) Move(0); if (Input.WasPressed(Key.End)) Move(_buffer.Length);
            if (hover && held && Input.MouseDelta != Vector2.Zero) { _caret = Font.HitTest(_buffer, Mouse.X - rect.X); }
            if (finish || cancel)
            { if (!cancel && _buffer != value) { value = _buffer; changed = true; } _editId = 0; Focus = 0; }
        }
        Draw.Rect(rect, hover ? Theme.WidgetHover : Theme.Field);
        string visible = _editId == id ? _buffer : value;
        int start = 0;
        if (_editId == id) while (Font.Measure(visible[start.._caret]) >= rect.W && start < _caret) start = NextRune(start);
        TextIn(rect, visible[start..], _keyboardFocus == id && KeyboardNavigation ? Theme.Accent : Theme.Text);
        if (_editId == id && ((int)(_time * 2) & 1) == 0) Draw.Text(new Vector2(rect.X + Font.Measure(visible[start.._caret]), rect.Y), "▏", Theme.Accent);
        return changed;
    }
    private int Previous(int offset) => offset <= 0 ? 0 : offset > 1 && char.IsLowSurrogate(_buffer[offset - 1]) && char.IsHighSurrogate(_buffer[offset - 2]) ? offset - 2 : offset - 1;
    private int NextRune(int offset) => offset >= _buffer.Length ? _buffer.Length : offset + (char.IsHighSurrogate(_buffer[offset]) && offset + 1 < _buffer.Length && char.IsLowSurrogate(_buffer[offset + 1]) ? 2 : 1);
    private void Move(int caret) { _caret = caret; if (!Input.Shift) _anchor = caret; }
    private bool DeleteSelection()
    { int a = Math.Min(_caret, _anchor), b = Math.Max(_caret, _anchor); if (a == b) return false; _buffer = _buffer.Remove(a, b - a); _caret = _anchor = a; return true; }
    private void Insert(string text) { DeleteSelection(); _buffer = _buffer.Insert(_caret, text); _caret += text.Length; _anchor = _caret; }
    public bool DragFloat(Rect rect, string label, ref float value, float speed = 0.05f)
    {
        uint id = Id(label + "#drag"); Behavior(rect, id, out bool hover, out bool held);
        if (hover && DoubleClicked)
        { Active = 0; _editId = Id(label + "#text"); Focus = _editId; _buffer = value.ToString("0.###", CultureInfo.InvariantCulture); _caret = _buffer.Length; _anchor = 0; }
        if (held && MouseDelta.X != 0 && !DoubleClicked) { value += MouseDelta.X * speed * 8 * (Input.Shift ? 0.1f : 1); TextIn(rect, value.ToString("0.###", CultureInfo.InvariantCulture), Theme.Text); return true; }
        string text = value.ToString("0.###", CultureInfo.InvariantCulture);
        if (TextField(rect, label, ref text) && float.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) { value = parsed; return true; }
        return false;
    }
    public bool DragInt(Rect rect, string label, ref int value)
    {
        string text = value.ToString(CultureInfo.InvariantCulture);
        if (TextField(rect, label, ref text) && int.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) { value = parsed; return true; }
        return false;
    }
    public bool Vector(Rect rect, string label, Span<float> values)
    {
        bool changed = false; float width = MathF.Floor(rect.W / values.Length);
        for (int i = 0; i < values.Length; i++) changed |= DragFloat(new Rect(rect.X + i * width, rect.Y, width - 1, 1), label + i, ref values[i]);
        return changed;
    }
    public bool Vector3(Rect rect, string label, ref Vector3 value)
    { Span<float> v = stackalloc float[] { value.X, value.Y, value.Z }; bool changed = Vector(rect, label, v); if (changed) value = new(v[0], v[1], v[2]); return changed; }
}
