using System.Globalization;
using System.Numerics;
using AnsiDotNet.Layout;
using Gamegrl.Platform;
using Gamegrl.Rhi;
using Gamegrl.UI;
using Rect = Gamegrl.UI.Rect;
namespace Gamegrl.Editor;

/// <summary>
/// Immediate-mode editor controls over an AnsiDotNet Surface. All layout and hit rectangles use character cells.
/// Drawing a frame allocates nothing: areas are pooled, labels are spans and numbers format on the stack.
/// </summary>
public sealed class CellUi
{
    private sealed class Area
    {
        public Rect Rect;
        public float Y, Indent;
        public Rect[] Row = new Rect[8];
        public int RowCount, Column;
        public void Reset(Rect rect) { Rect = rect; Y = rect.Y; Indent = 0; RowCount = Column = 0; }
    }

    private const uint ScrollContentSalt = 0x5bd1e995, SplitterAnchorSalt = 0x9e3779b9;
    private readonly List<Area> _areas = new();
    private int _areaDepth;
    private readonly Stack<uint> _ids = new();
    private readonly Dictionary<uint, float> _floats = new();
    private readonly Dictionary<uint, bool> _bools = new();
    private readonly List<(uint Id, Rect Area, uint Scroll)> _focusable = new();
    private readonly List<(uint Id, Rect Area, uint Scroll)> _previousFocusable = new();
    private readonly Stack<(uint Id, Rect Area, float Start)> _scroll = new();
    private readonly CellDraw _main = new(), _overlay = new();
    private readonly List<Rect> _overlays = new();
    private readonly Dictionary<string, Rect> _controls = new(StringComparer.Ordinal);
    private Rect? _popup, _previousPopup;
    private bool _inPopup, _claimed, _commit;
    private uint _editId, _keyboardFocus;
    private string _buffer = "";
    private byte _heldPressed, _heldReleased;
    private int _caret, _anchor;
    private IPlatform? _platform;
    private double _lastClick = -100, _time;
    private Vector2 _lastClickPosition;

    public CellUi(bool ascii = false)
    {
        _main.Ascii = _overlay.Ascii = ascii;
        Draw.Reset(1, 1);
    }

    public CellDraw Draw => _inPopup ? _overlay : _main;
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
    /// <summary>The last rectangle drawn for a labelled button, combo or text field; for tests and automation.</summary>
    public Rect? ControlRect(string label) => _controls.TryGetValue(label, out var area) ? area : null;
    private Area A => _areas[_areaDepth - 1];

    // ------------------------------------------------------------------ frame

    public void Begin(Input input, IPlatform? platform, Vector2 size, double time)
    {
        Input = input;
        _platform = platform;
        _time = time;
        Screen = new Rect(0, 0, Math.Max(1, (int)size.X), Math.Max(1, (int)size.Y));
        _main.Reset((int)Screen.W, (int)Screen.H);
        _overlay.Reset((int)Screen.W, (int)Screen.H);
        _overlays.Clear();
        _controls.Clear();
        _previousPopup = _popup;
        _popup = null;
        _inPopup = false;
        _claimed = false;
        _previousFocusable.Clear();
        _previousFocusable.AddRange(_focusable);
        _focusable.Clear();
        _areaDepth = 0;
        PushArea(Screen);
        _ids.Clear();
        _ids.Push(2166136261);
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
        if (input.WasPressed(MouseButton.Left))
        {
            _lastClick = time;
            _lastClickPosition = Mouse;
        }
        bool step = input.WasPressed(Key.Tab) || Focus == 0 && (input.WasPressed(Key.Up) || input.WasPressed(Key.Down));
        if (KeyboardNavigation && step && _previousFocusable.Count > 0)
        {
            int count = _previousFocusable.Count;
            int index = IndexOfFocusable(_keyboardFocus);
            index = (index + (input.Shift || input.WasPressed(Key.Up) ? -1 : 1) + count) % count;
            _keyboardFocus = _previousFocusable[index].Id;
        }
    }

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

    public Checkpoint Save() => new(_areaDepth, _ids.Count, _scroll.Count, _main.ClipDepth, _overlay.ClipDepth, _inPopup);

    public void Rewind(in Checkpoint checkpoint)
    {
        _areaDepth = Math.Min(_areaDepth, checkpoint.Areas);
        while (_ids.Count > checkpoint.Ids) _ids.Pop();
        while (_scroll.Count > checkpoint.Scrolls) _scroll.Pop();
        _main.PopClipsTo(checkpoint.Clips);
        _overlay.PopClipsTo(checkpoint.OverlayClips);
        _inPopup = checkpoint.InPopup;
    }

    private int IndexOfFocusable(uint id)
    {
        for (int i = 0; i < _previousFocusable.Count; i++)
            if (_previousFocusable[i].Id == id) return i;
        return -1;
    }

    /// <summary>Last frame's rectangle for a focusable control; empty if it was not drawn.</summary>
    private Rect FocusableArea(uint id) => IndexOfFocusable(id) is int i and >= 0 ? _previousFocusable[i].Area : default;

    // ------------------------------------------------------------------ ids and state

    /// <summary>FNV-1a of <paramref name="text"/> then <paramref name="suffix"/>, seeded by the id stack.</summary>
    public uint Id(ReadOnlySpan<char> text, ReadOnlySpan<char> suffix = default)
    {
        uint hash = _ids.Peek();
        foreach (char c in text) hash = (hash ^ c) * 16777619;
        foreach (char c in suffix) hash = (hash ^ c) * 16777619;
        return hash == 0 ? 1 : hash;
    }

    public uint Id(ulong key)
    {
        uint hash = _ids.Peek();
        for (int i = 0; i < 8; i++, key >>= 8) hash = (hash ^ (byte)key) * 16777619;
        return hash == 0 ? 1 : hash;
    }

    public void PushId(int id) => _ids.Push(Id((ulong)(uint)id));
    public void PushId(ulong id) => _ids.Push(Id(id));
    public void PushId(ReadOnlySpan<char> id) => _ids.Push(Id(id));
    public void PopId() => _ids.Pop();
    public float GetFloat(uint id, float fallback = 0) => _floats.GetValueOrDefault(id, fallback);
    public void SetFloat(uint id, float value) => _floats[id] = value;

    // ------------------------------------------------------------------ interaction

    public bool IsHovered(Rect rect) =>
        rect.Contains(Mouse) && Draw.Clip.Contains(Mouse) && (_inPopup || _previousPopup is not { } popup || !popup.Contains(Mouse));

    public bool Behavior(Rect rect, uint id, out bool hover, out bool held)
    {
        hover = IsHovered(rect) && (Active == 0 || Active == id);
        if (hover && Input.WasPressed(MouseButton.Left))
        {
            Active = id;
            _keyboardFocus = id;
        }
        held = Active == id && Input.IsDown(MouseButton.Left);
        return hover && Active == id && Input.WasReleased(MouseButton.Left);
    }

    private bool Activate(Rect rect, uint id, out bool hover, out bool held)
    {
        _focusable.Add((id, rect, _scroll.TryPeek(out var scroll) ? scroll.Id : 0));
        bool click = Behavior(rect, id, out hover, out held);
        bool key = KeyboardNavigation && Focus == 0 && _keyboardFocus == id && (Input.WasPressed(Key.Enter) || Input.WasPressed(Key.Space));
        return click || key;
    }

    private bool Focused(uint id) => KeyboardNavigation && _keyboardFocus == id;

    // ------------------------------------------------------------------ layout

    private void PushArea(Rect rect)
    {
        if (_areaDepth == _areas.Count) _areas.Add(new Area());
        _areas[_areaDepth++].Reset(rect);
    }

    public void BeginArea(Rect area)
    {
        PushArea(area);
        Draw.PushClip(area);
    }

    public float EndArea()
    {
        var area = _areas[--_areaDepth];
        Draw.PopClip();
        return area.Y - area.Rect.Y;
    }

    public Rect Next(float height = 1)
    {
        var area = A;
        if (area.Column < area.RowCount)
        {
            var cell = area.Row[area.Column++];
            if (area.Column == area.RowCount)
            {
                area.RowCount = area.Column = 0;
                area.Y = cell.Bottom;
            }
            return cell;
        }
        var rect = new Rect(area.Rect.X + area.Indent, area.Y, Math.Max(0, area.Rect.W - area.Indent), height);
        area.Y += height;
        return rect;
    }

    /// <summary>Lay out the next row: positive widths are fixed cells, negative widths are fill weights.</summary>
    public void Row(params ReadOnlySpan<float> widths)
    {
        Span<Length> lengths = stackalloc Length[widths.Length];
        Span<AnsiDotNet.Rect> cells = stackalloc AnsiDotNet.Rect[widths.Length];
        for (int i = 0; i < widths.Length; i++) lengths[i] = widths[i] > 0 ? Length.Fixed((int)widths[i]) : Length.Fill(-widths[i]);
        // AnsiDotNet owns cell rounding, constraints and fill distribution for every control row.
        CellDraw.To(Remaining with { H = 1 }).Split(Axis.Horizontal, lengths, cells, gap: 1);
        var area = A;
        if (area.Row.Length < cells.Length) area.Row = new Rect[cells.Length];
        for (int i = 0; i < cells.Length; i++) area.Row[i] = CellDraw.From(cells[i]);
        area.RowCount = cells.Length;
        area.Column = 0;
    }

    public void Indent() => A.Indent++;
    public void Unindent() => A.Indent = Math.Max(0, A.Indent - 1);

    // ------------------------------------------------------------------ text and decoration

    public Rect Property(ReadOnlySpan<char> name)
    {
        var row = Next();
        int label = (int)(row.W * Theme.LabelWidthRatio);
        TextIn(row with { W = label }, name, Theme.TextDim);
        return new Rect(row.X + label, row.Y, Math.Max(0, row.W - label), 1);
    }

    public void TextIn(Rect rect, ReadOnlySpan<char> text, Color32 color, float align = 0)
    {
        Draw.PushClip(rect);
        float x = rect.X + Math.Max(0, (rect.W - Font.Measure(text)) * align);
        Draw.Text(new Vector2(x, rect.Y), text, color);
        Draw.PopClip();
    }

    public void Label(ReadOnlySpan<char> text, bool dim = false) => TextIn(Next(), text, dim ? Theme.TextDim : Theme.Text);

    public void Header(ReadOnlySpan<char> text)
    {
        var row = Next();
        Draw.Rect(row, Theme.PanelHeader);
        TextIn(row, text, Theme.Text);
    }

    public void Separator()
    {
        var row = Next();
        Draw.Repeat(row, '─', Theme.Border);
    }

    public void Image(Rect rect, GpuTexture texture) => Draw.Image(rect, texture);

    public void Tooltip(ReadOnlySpan<char> text)
    {
        float width = Font.Measure(text);
        var rect = new Rect(Math.Clamp(Mouse.X + 1, 0, Math.Max(0, Screen.W - width - 2)), Math.Min(Screen.H - 1, Mouse.Y + 1), Math.Min(Screen.W, width + 2), 1);
        bool popup = _inPopup;
        _inPopup = true;
        _overlays.Add(rect);
        Draw.PushClip(Screen);
        Draw.Rect(rect, Theme.PanelHeader);
        TextIn(rect, text, Theme.Text);
        Draw.PopClip();
        _inPopup = popup;
    }

    // ------------------------------------------------------------------ controls

    public bool Button(string label) => Button(Next(), label);

    public bool Button(Rect rect, string label)
    {
        _controls[label] = rect;
        uint id = Id(label);
        bool result = Activate(rect, id, out bool hover, out bool held);
        bool focused = Focused(id);
        Draw.Rect(rect, held ? Theme.WidgetActive : hover ? Theme.WidgetHover : Theme.Widget);
        float width = Font.Measure(label) + (focused ? 1 : 0);
        float x = rect.X + Math.Max(0, (rect.W - width) * 0.5f);
        Draw.PushClip(rect);
        if (focused) x = Draw.Text(new Vector2(x, rect.Y), ">", Theme.Accent);
        Draw.Text(new Vector2(x, rect.Y), label, focused ? Theme.Accent : Theme.Text);
        Draw.PopClip();
        return result;
    }

    public bool Selectable(Rect rect, ReadOnlySpan<char> label, bool selected)
    {
        uint id = Id(label);
        bool result = Activate(rect, id, out bool hover, out _);
        if (selected || hover || Focused(id)) Draw.Rect(rect, selected ? Theme.Selection : Theme.WidgetHover);
        TextIn(rect, label, Theme.Text);
        return result;
    }

    public bool Checkbox(Rect rect, string id, ref bool value)
    {
        bool clicked = Activate(rect, Id(id), out _, out _);
        if (clicked) value = !value;
        TextIn(rect, value ? "[✓]" : "[ ]", Theme.Text);
        return clicked;
    }

    public bool TreeNode(Rect rect, ulong key, ReadOnlySpan<char> label, bool hasChildren, bool selected, ref bool open, float depth)
    {
        rect = rect with { X = rect.X + depth, W = Math.Max(0, rect.W - depth) };
        uint id = Id(key);
        bool clicked = Activate(rect, id, out bool hover, out _);
        bool focused = Focused(id);
        if (selected || hover || focused) Draw.Rect(rect, selected ? Theme.Selection : Theme.WidgetHover);
        if (focused && hasChildren)
        {
            if (Input.WasPressed(Key.Left)) open = false;
            if (Input.WasPressed(Key.Right)) open = true;
        }
        if (hasChildren && clicked && (Mouse.X < rect.X + 2 || DoubleClicked || Input.WasPressed(Key.Space)))
        {
            open = !open;
            clicked = false;
        }
        Draw.PushClip(rect);
        float x = Draw.Text(rect.Min, hasChildren ? open ? "▾" : "▸" : " ", Theme.Text);
        x = Draw.Text(new Vector2(x, rect.Y), selected ? "*" : focused ? ">" : " ", Theme.Text);
        Draw.Text(new Vector2(x, rect.Y), label, Theme.Text);
        Draw.PopClip();
        return clicked;
    }

    public float Splitter(Rect rect, string id, float position, bool vertical, float min, float max)
    {
        uint key = Id(id);
        Behavior(rect, key, out bool hover, out bool held);
        Draw.Rect(rect, hover || held ? Theme.Accent : Theme.Border);
        // Track the drag from its anchor: sub-cell mouse motion accumulates instead of rounding away each frame.
        float mouse = vertical ? Mouse.X : Mouse.Y;
        if (held && Input.WasPressed(MouseButton.Left)) SetFloat(key ^ SplitterAnchorSalt, position - mouse);
        if (held) position = Math.Clamp(mouse + GetFloat(key ^ SplitterAnchorSalt), min, max);
        return MathF.Round(position);
    }

    public void BeginScroll(Rect rect, string id) => BeginScroll(rect, Id(id));

    public void BeginScroll(Rect rect, uint key)
    {
        float content = GetFloat(key ^ ScrollContentSalt);
        float limit = Math.Max(0, content - rect.H);
        float offset = Math.Clamp(GetFloat(key), 0, limit);
        if (IsHovered(rect)) offset = Math.Clamp(offset - Input.Wheel.Y * 3, 0, limit);
        SetFloat(key, offset);
        _scroll.Push((key, rect, offset));
        BeginArea(rect);
        A.Y -= offset;
        if (Focus != 0) return;
        // Keep the keyboard-focused control of this scroll view visible.
        int index = IndexOfFocusable(_keyboardFocus);
        if (index < 0 || _previousFocusable[index].Scroll != key) return;
        var focused = _previousFocusable[index].Area;
        if (focused.Y < rect.Y) SetFloat(key, Math.Max(0, offset + focused.Y - rect.Y));
        if (focused.Bottom > rect.Bottom) SetFloat(key, Math.Min(limit, offset + focused.Bottom - rect.Bottom));
    }

    public void EndScroll()
    {
        var (key, rect, offset) = _scroll.Pop();
        float used = EndArea() + offset;
        SetFloat(key ^ ScrollContentSalt, used);
        if (used > rect.H && rect.W > 0)
            Draw.Text(new Vector2(rect.Right - 1, rect.Y + (rect.H - 1) * offset / Math.Max(1, used - rect.H)), "█", Theme.TextDim);
    }

    /// <summary>True if a row at the current layout position would be inside the clip; lets long lists skip drawing.</summary>
    public bool RowVisible(float height = 1) => A.Y + height > Draw.Clip.Y && A.Y < Draw.Clip.Bottom;

    public void BeginPopup(Rect rect)
    {
        _overlays.Add(rect);
        _inPopup = true;
        _popup = rect;
        Draw.PushClip(Screen);
        Draw.Rect(rect, Theme.PanelHeader);
        Draw.RectOutline(rect, Theme.Border);
        BeginArea(rect.Shrink(1));
    }

    public void EndPopup()
    {
        EndArea();
        Draw.PopClip();
        _inPopup = false;
    }

    public bool Combo(Rect rect, string label, ref int selected, ReadOnlySpan<string> items)
    {
        _controls[label] = rect;
        uint id = Id(label);
        bool open = _bools.GetValueOrDefault(id);
        if (Activate(rect, id, out _, out _)) open = !open;
        Draw.Rect(rect, Theme.Widget);
        TextIn(rect with { W = Math.Max(0, rect.W - 2) }, selected >= 0 && selected < items.Length ? items[selected] : "", Theme.Text);
        TextIn(rect with { X = rect.Right - 1, W = 1 }, "▾", Theme.Text);
        bool changed = false;
        if (open)
        {
            float height = Math.Min(items.Length + 2, Math.Max(3, Screen.H / 2));
            var popup = new Rect(Math.Clamp(rect.X, 0, Math.Max(0, Screen.W - rect.W)), rect.Bottom, Math.Max(10, rect.W), height);
            if (popup.Bottom > Screen.Bottom) popup = popup with { Y = Math.Max(0, rect.Y - height) };
            BeginPopup(popup);
            BeginScroll(Remaining, Id(label, "#items"));
            for (int i = 0; i < items.Length; i++)
            {
                PushId(i);
                if (Selectable(Next(), items[i], i == selected))
                {
                    changed = i != selected;
                    selected = i;
                    open = false;
                }
                PopId();
            }
            EndScroll();
            EndPopup();
            bool outside = Input.WasPressed(MouseButton.Left) && !popup.Contains(Mouse) && !rect.Contains(Mouse);
            if (Input.WasPressed(Key.Escape) || outside) open = false;
        }
        _bools[id] = open;
        return changed;
    }

    // ------------------------------------------------------------------ text editing

    public bool TextField(Rect rect, string label, ref string value)
    {
        _controls[label] = rect;
        if (!EditText(rect, Id(label, "#text"), value, out var committed)) return false;
        value = committed;
        return true;
    }

    /// <summary>Edit text whose display form the caller formats (no string per frame); returns the committed text.</summary>
    public bool TextField(Rect rect, string label, ReadOnlySpan<char> current, out string committed)
    {
        _controls[label] = rect;
        return EditText(rect, Id(label, "#text"), current, out committed);
    }

    public bool NumberField<T>(Rect rect, string label, ref T value) where T : INumber<T>
    {
        Span<char> chars = stackalloc char[64];
        if (!value.TryFormat(chars, out int length, default, CultureInfo.InvariantCulture)) length = 0;
        if (!TextField(rect, label, chars[..length], out var text) || !T.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    public bool DragInt(Rect rect, string label, ref int value) => NumberField(rect, label, ref value);

    public bool DragFloat(Rect rect, string label, ref float value, float speed = 0.05f)
    {
        uint dragId = Id(label, "#drag"), textId = Id(label, "#text");
        Behavior(rect, dragId, out bool hover, out bool held);
        Span<char> chars = stackalloc char[32];
        value.TryFormat(chars, out int length, "0.###", CultureInfo.InvariantCulture);
        if (hover && DoubleClicked) StartEdit(textId, chars[..length], selectAll: true, active: false);
        if (held && MouseDelta.X != 0 && !DoubleClicked)
        {
            value += MouseDelta.X * speed * 8 * (Input.Shift ? 0.1f : 1);
            value.TryFormat(chars, out length, "0.###", CultureInfo.InvariantCulture);
            Draw.Rect(rect, Theme.WidgetActive);
            TextIn(rect, chars[..length], Theme.Text);
            return true;
        }
        if (!TextField(rect, label, chars[..length], out var text) || !float.TryParse(text, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = parsed;
        return true;
    }

    public bool Vector(Rect rect, string label, Span<float> values)
    {
        bool changed = false;
        float width = MathF.Floor(rect.W / values.Length);
        Span<char> name = stackalloc char[label.Length + 1];
        label.CopyTo(name);
        for (int i = 0; i < values.Length; i++)
        {
            name[^1] = (char)('0' + i);
            PushId(name);
            changed |= DragFloat(new Rect(rect.X + i * width, rect.Y, width - 1, 1), "v", ref values[i]);
            PopId();
        }
        return changed;
    }

    public bool Vector3(Rect rect, string label, ref Vector3 value)
    {
        Span<float> v = [value.X, value.Y, value.Z];
        bool changed = Vector(rect, label, v);
        if (changed) value = new(v[0], v[1], v[2]);
        return changed;
    }

    private void StartEdit(uint id, ReadOnlySpan<char> current, bool selectAll, bool active = true)
    {
        if (!active) Active = 0;
        _editId = id;
        Focus = id;
        _buffer = current.ToString();
        _caret = _buffer.Length;
        _anchor = selectAll ? 0 : _caret;
    }

    private bool EditText(Rect rect, uint id, ReadOnlySpan<char> current, out string committed)
    {
        committed = "";
        bool changed = false, started = false;
        bool click = Activate(rect, id, out bool hover, out bool held);
        if (_editId != id && (click || hover && Input.WasPressed(MouseButton.Left)))
        {
            StartEdit(id, current, selectAll: true);
            started = true;
        }
        if (_editId == id)
        {
            _claimed = true;
            Focus = id;
            changed = EditKeys(rect, current, started, hover, held, out committed);
        }
        Draw.Rect(rect, hover ? Theme.WidgetHover : Theme.Field);
        ReadOnlySpan<char> visible = _editId == id ? _buffer : current;
        int start = 0;
        if (_editId == id)
            while (Font.Measure(visible[start.._caret]) >= rect.W && start < _caret) start = NextRune(start);
        TextIn(rect, visible[start..], Focused(id) ? Theme.Accent : Theme.Text);
        if (_editId == id && ((int)(_time * 2) & 1) == 0)
            Draw.Text(new Vector2(rect.X + Font.Measure(visible[start.._caret]), rect.Y), "▏", Theme.Accent);
        return changed;
    }

    private bool EditKeys(Rect rect, ReadOnlySpan<char> current, bool started, bool hover, bool held, out string committed)
    {
        committed = "";
        bool cancel = Input.WasPressed(Key.Escape);
        bool finish = _commit || !started && (Input.WasPressed(Key.Enter) || Input.WasPressed(Key.Tab)) || Input.WasPressed(MouseButton.Left) && !hover;
        if (Input.Ctrl && Input.WasPressed(Key.A))
        {
            _anchor = 0;
            _caret = _buffer.Length;
        }
        if (Input.Ctrl && (Input.WasPressed(Key.C) || Input.WasPressed(Key.X)))
            _platform?.SetClipboardText(_buffer[Math.Min(_caret, _anchor)..Math.Max(_caret, _anchor)]);
        if (Input.Ctrl && Input.WasPressed(Key.X)) DeleteSelection();
        if (Input.Ctrl && Input.WasPressed(Key.V)) Insert(SingleLine(_platform?.GetClipboardText() ?? ""));
        else if (Input.HasTypedText) Insert(SingleLine(Input.TypedText));
        if (Input.WasPressedOrRepeated(Key.Backspace) && !DeleteSelection() && _caret > 0)
        {
            int previous = Previous(_caret);
            _buffer = _buffer.Remove(previous, _caret - previous);
            _caret = _anchor = previous;
        }
        if (Input.WasPressedOrRepeated(Key.Delete) && !DeleteSelection() && _caret < _buffer.Length)
            _buffer = _buffer.Remove(_caret, NextRune(_caret) - _caret);
        if (Input.WasPressedOrRepeated(Key.Left)) Move(Previous(_caret));
        if (Input.WasPressedOrRepeated(Key.Right)) Move(NextRune(_caret));
        if (Input.WasPressed(Key.Home)) Move(0);
        if (Input.WasPressed(Key.End)) Move(_buffer.Length);
        if (hover && held && Input.MouseDelta != Vector2.Zero) _caret = Font.HitTest(_buffer, Mouse.X - rect.X);
        if (!finish && !cancel) return false;
        bool changed = !cancel && !current.SequenceEqual(_buffer);
        if (changed) committed = _buffer;
        _editId = 0;
        Focus = 0;
        return changed;
    }

    private static string SingleLine(string text) => text.Contains('\n') || text.Contains('\r') ? text.Replace('\n', ' ').Replace('\r', ' ') : text;

    private int Previous(int offset) =>
        offset <= 0 ? 0 : offset > 1 && char.IsLowSurrogate(_buffer[offset - 1]) && char.IsHighSurrogate(_buffer[offset - 2]) ? offset - 2 : offset - 1;

    private int NextRune(int offset)
    {
        if (offset >= _buffer.Length) return _buffer.Length;
        bool pair = char.IsHighSurrogate(_buffer[offset]) && offset + 1 < _buffer.Length && char.IsLowSurrogate(_buffer[offset + 1]);
        return offset + (pair ? 2 : 1);
    }

    private void Move(int caret)
    {
        _caret = caret;
        if (!Input.Shift) _anchor = caret;
    }

    private bool DeleteSelection()
    {
        int a = Math.Min(_caret, _anchor), b = Math.Max(_caret, _anchor);
        if (a == b) return false;
        _buffer = _buffer.Remove(a, b - a);
        _caret = _anchor = a;
        return true;
    }

    private void Insert(string text)
    {
        DeleteSelection();
        _buffer = _buffer.Insert(_caret, text);
        _caret += text.Length;
        _anchor = _caret;
    }
}
