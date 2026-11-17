using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Gamegrl.Platform;
using Gamegrl.UI.Fonts;

namespace Gamegrl.UI;

/// <summary>
/// The engine's pixel UI: immediate mode, for game menus and HUDs, game debug windows and the editor's in-view overlays.
/// Call widget methods every frame; they draw, handle input and return what happened. State that must persist (open
/// windows, scroll offsets, the text being edited) lives in small tables keyed by widget id; ids hash labels with an id
/// stack (<see cref="PushId(ReadOnlySpan{char})"/>). Popups draw into an overlay layer and block clicks beneath them.
/// A warmed-up frame allocates nothing; format numbers with <see cref="Format"/> or the interpolated <see cref="Label(ref UiText, bool)"/>.
/// The editor's own chrome uses the cell UI instead; this is what a game ships.
/// </summary>
public sealed partial class Ui
{
    private const uint ScrollContentSalt = 0x5bd1e995, WindowSalt = 0x27d4eb2f;
    private const int MaxRowCells = 16;

    private readonly Stack<uint> _ids = new();
    private readonly Dictionary<uint, bool> _bools = new();
    private readonly Dictionary<uint, float> _floats = new();
    private List<Rect> _overlayRects = new(), _overlayRectsPrev = new();
    private Layout[] _layouts = new Layout[8];
    private float[] _rowWidths = new float[8 * MaxRowCells];
    private int _depth;
    private Input _input = null!;
    private IPlatform? _platform;
    private CursorShape _cursor, _cursorSet;
    private bool _inOverlay;
    private bool _focusClaimed;
    private double _time, _lastClickTime;
    private Vector2 _lastClickPos;

    public Ui(FontAtlas font, Theme? theme = null)
    {
        Font = font;
        Theme = theme ?? new Theme();
    }

    public FontAtlas Font { get; }
    public Theme Theme { get; }
    public DrawList Draw { get; } = new();
    public DrawList Overlay { get; } = new();
    public Input Input => _input;

    /// <summary>The draw list widgets currently draw into (the overlay inside popups).</summary>
    public DrawList Target => _inOverlay ? Overlay : Draw;

    public Vector2 Mouse { get; private set; }
    public Vector2 MouseDelta { get; private set; }
    public bool DoubleClicked { get; private set; }

    /// <summary>The whole UI area, in UI units, origin top-left.</summary>
    public Rect Screen { get; private set; }

    public uint Hot { get; private set; }
    public uint Active { get; set; }
    public uint Focus { get; set; }

    /// <summary>The mouse is over (or captured by) the UI: the game or view under it shouldn't react to it.</summary>
    public bool WantsMouse { get; private set; }

    /// <summary>A text field has keyboard focus: shortcuts shouldn't fire.</summary>
    public bool WantsKeyboard => Focus != 0;

    // ------------------------------------------------------------------ frame

    /// <summary>
    /// Start a frame. <paramref name="input"/> is already in UI units (a host that scales or offsets the view routes it
    /// first). <paramref name="platform"/>, when given, receives cursor shapes and serves the clipboard.
    /// </summary>
    public void Begin(Input input, Vector2 screenSize, double time, IPlatform? platform = null)
    {
        _input = input;
        _platform = platform;
        _time = time;
        Screen = new Rect(0, 0, screenSize.X, screenSize.Y);
        Mouse = input.MousePosition;
        MouseDelta = input.MouseDelta;
        DoubleClicked = false;
        if (input.WasPressed(MouseButton.Left))
        {
            DoubleClicked = time - _lastClickTime < 0.35 && Vector2.Distance(Mouse, _lastClickPos) < 6;
            _lastClickTime = DoubleClicked ? 0 : time;
            _lastClickPos = Mouse;
        }
        (_overlayRectsPrev, _overlayRects) = (_overlayRects, _overlayRectsPrev);
        _overlayRects.Clear();
        Hot = 0;
        _cursor = CursorShape.Arrow;
        _focusClaimed = false;
        _inOverlay = false;
        if (Focus == 0) _editId = 0;
        WantsMouse = Active != 0 || OverOverlay(Mouse);
        Draw.Reset(Font, null, Screen);
        Overlay.Reset(Font, null, Screen);
        _ids.Clear();
        _ids.Push(2166136261);
        _scratchUsed = 0;
        _depth = 0;
        _layouts[0] = new Layout(Screen);
    }

    /// <summary>Finish the frame: release captures, drop focus on a click elsewhere, and put the overlay on top.</summary>
    public void End()
    {
        _commitTextEdit = false;
        if (_input.WasReleased(MouseButton.Left)) Active = 0;
        if (_input.WasPressed(MouseButton.Left) && !_focusClaimed) Focus = 0; // clicking elsewhere drops text focus
        Draw.Append(Overlay);
        // Only touch the OS cursor when the UI wants a shape or gives one back, so a host's own cursor survives.
        if (_platform is not null && _cursor != _cursorSet) _platform.SetCursor(_cursor);
        _cursorSet = _cursor;
    }

    public void SetCursor(CursorShape shape) => _cursor = shape;

    // ------------------------------------------------------------------ ids

    /// <summary>FNV-1a of <paramref name="text"/> then <paramref name="suffix"/>, seeded by the id stack.</summary>
    public uint Id(ReadOnlySpan<char> text, ReadOnlySpan<char> suffix = default)
    {
        uint h = _ids.Peek();
        foreach (char c in text) h = (h ^ c) * 16777619;
        foreach (char c in suffix) h = (h ^ c) * 16777619;
        return h == 0 ? 1 : h;
    }

    public uint Id(ulong key)
    {
        uint h = _ids.Peek() ^ 0x9e3779b9;
        for (int i = 0; i < 8; i++, key >>= 8) h = (h ^ (byte)key) * 16777619;
        return h == 0 ? 1 : h;
    }

    public uint Id(int value) => Id((ulong)(uint)value);

    public void PushId(ReadOnlySpan<char> s) => _ids.Push(Id(s));
    public void PushId(int i) => _ids.Push(Id(i));
    public void PushId(ulong v) => _ids.Push(Id(v));
    public void PopId() => _ids.Pop();

    // ------------------------------------------------------------------ persistent state

    public bool GetBool(uint id, bool fallback = false) => _bools.TryGetValue(id, out var b) ? b : fallback;
    public void SetBool(uint id, bool value) => _bools[id] = value;
    public float GetFloat(uint id, float fallback = 0) => _floats.TryGetValue(id, out var f) ? f : fallback;
    public void SetFloat(uint id, float value) => _floats[id] = value;

    // ------------------------------------------------------------------ interaction

    private bool OverOverlay(Vector2 p)
    {
        foreach (var r in _overlayRectsPrev)
            if (r.Contains(p)) return true;
        return false;
    }

    /// <summary>The mouse is over <paramref name="r"/>, inside the current clip, and not blocked by a popup above it.</summary>
    public bool IsHovered(Rect r)
    {
        if (!r.Contains(Mouse) || !Target.Clip.Contains(Mouse)) return false;
        if (!_inOverlay && OverOverlay(Mouse)) return false;
        return true;
    }

    /// <summary>
    /// Standard button behavior: pressing over the rect makes the widget active; releasing over it is a click.
    /// </summary>
    public bool Behavior(Rect r, uint id, out bool hovered, out bool held, MouseButton button = MouseButton.Left)
    {
        hovered = IsHovered(r) && (Active == 0 || Active == id);
        if (hovered)
        {
            Hot = id;
            WantsMouse = true;
        }
        if (hovered && _input.WasPressed(button)) Active = id;
        held = Active == id && _input.IsDown(button);
        bool clicked = Active == id && _input.WasReleased(button) && hovered;
        return clicked;
    }

    /// <summary>Mark <paramref name="r"/> as UI so the game or view beneath ignores the mouse there (panels, HUD frames).</summary>
    public void BlockMouse(Rect r)
    {
        if (IsHovered(r)) WantsMouse = true;
    }

    /// <summary>Claim keyboard focus for a text-editing widget this frame.</summary>
    public void ClaimFocus(uint id)
    {
        Focus = id;
        _focusClaimed = true;
    }

    // ------------------------------------------------------------------ layout

    private struct Layout(Rect area)
    {
        public Rect Area = area;
        public float CursorY = area.Y;
        public float Indent;
        public bool Clipped;
        // A horizontal row: cells still to hand out (widths live in _rowWidths at this depth's slice).
        public int RowCount, RowIndex;
        public float RowY, RowHeight, RowX;
        public float ContentBottom = area.Y;
    }

    private ref Layout L => ref _layouts[_depth];

    /// <summary>The space left in the current area.</summary>
    public Rect Remaining
    {
        get
        {
            ref var l = ref L;
            return new(l.Area.X + l.Indent, l.CursorY, l.Area.W - l.Indent, MathF.Max(0, l.Area.Bottom - l.CursorY));
        }
    }

    /// <summary>Lay out widgets inside <paramref name="area"/> (top to bottom), clipped to it.</summary>
    public void BeginArea(Rect area, bool clip = true)
    {
        if (++_depth == _layouts.Length)
        {
            Array.Resize(ref _layouts, _depth * 2);
            Array.Resize(ref _rowWidths, _layouts.Length * MaxRowCells);
        }
        _layouts[_depth] = new Layout(area) { Clipped = clip };
        if (clip) Target.PushClip(area);
    }

    /// <summary>Ends the area; returns the height its content used.</summary>
    public float EndArea()
    {
        var l = _layouts[_depth--];
        if (l.Clipped) Target.PopClip();
        return MathF.Max(l.CursorY, l.ContentBottom) - l.Area.Y;
    }

    /// <summary>The next rect in the current layout: a full-width row, or the next cell of a <see cref="Row"/>.</summary>
    public Rect Next(float height = 0)
    {
        ref var l = ref L;
        if (height <= 0) height = Theme.RowHeight;
        if (l.RowCount > 0)
        {
            float w = _rowWidths[_depth * MaxRowCells + l.RowIndex];
            var cell = new Rect(l.RowX, l.RowY, w, l.RowHeight);
            l.RowX += w + Theme.Spacing;
            if (++l.RowIndex == l.RowCount)
            {
                l.RowCount = 0;
                l.CursorY = l.RowY + l.RowHeight + Theme.Spacing;
            }
            return cell;
        }
        var r = new Rect(l.Area.X + l.Indent, l.CursorY, l.Area.W - l.Indent, height);
        l.CursorY += height + Theme.Spacing;
        l.ContentBottom = MathF.Max(l.ContentBottom, r.Bottom);
        return r;
    }

    /// <summary>
    /// Split the next row into cells. Positive widths are UI units, negative are shares of what remains (−1, −2 = one
    /// third and two thirds). At most 16 cells.
    /// </summary>
    public void Row(params ReadOnlySpan<float> widths) => RowWithHeight(Theme.RowHeight, widths);

    public void RowWithHeight(float height, params ReadOnlySpan<float> widths)
    {
        if (widths.Length is 0 or > MaxRowCells) throw new ArgumentOutOfRangeException(nameof(widths), "A row has 1 to 16 cells.");
        ref var l = ref L;
        float total = l.Area.W - l.Indent - Theme.Spacing * (widths.Length - 1);
        float fixedSum = 0, shares = 0;
        foreach (var w in widths)
            if (w > 0) fixedSum += w; else shares += -w;
        var resolved = _rowWidths.AsSpan(_depth * MaxRowCells, widths.Length);
        for (int i = 0; i < widths.Length; i++)
            resolved[i] = widths[i] > 0 ? widths[i] : MathF.Max(0, (total - fixedSum) * (-widths[i] / MathF.Max(shares, 1e-6f)));
        l.RowCount = widths.Length;
        l.RowIndex = 0;
        l.RowX = l.Area.X + l.Indent;
        l.RowY = l.CursorY;
        l.RowHeight = height;
        l.ContentBottom = MathF.Max(l.ContentBottom, l.RowY + height);
    }

    public void Space(float units = 6) => L.CursorY += units;
    public void Indent() => L.Indent += Theme.Indent;
    public void Unindent() => L.Indent = MathF.Max(0, L.Indent - Theme.Indent);

    /// <summary>A labeled property row: the label on the left, the returned rect for the value on the right.</summary>
    public Rect Property(ReadOnlySpan<char> label, float height = 0)
    {
        var r = Next(height);
        float lw = MathF.Round(r.W * Theme.LabelWidthRatio);
        var (labelRect, value) = r.SplitX(lw);
        Target.PushClip(labelRect);
        Target.Text(new Vector2(labelRect.X, labelRect.Y + (labelRect.H - Font.LineHeight) * 0.5f), label, Theme.TextDim);
        Target.PopClip();
        return value;
    }

    // ------------------------------------------------------------------ text helpers

    /// <summary>One line of text in <paramref name="r"/>, vertically centered; align 0 = left, 0.5 = center, 1 = right.</summary>
    public void TextIn(Rect r, ReadOnlySpan<char> text, Color32 color, float align = 0)
    {
        float w = Font.Measure(text);
        float x = r.X + Theme.Padding + (r.W - 2 * Theme.Padding - w) * align;
        Target.Text(new Vector2(MathF.Round(x), MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), text, color);
    }

    /// <summary>
    /// Text placed freely over the view (a name tag over an entity, an axis label), outlined so it reads on any
    /// background. <paramref name="anchor"/> picks the point of the text box at <paramref name="pos"/>: (0,0) top-left,
    /// (0.5,1) bottom-center.
    /// </summary>
    public void TextAt(Vector2 pos, ReadOnlySpan<char> text, Color32 color, Vector2 anchor = default)
    {
        var size = new Vector2(Font.Measure(text), Font.LineHeight);
        var p = new Vector2(MathF.Round(pos.X - size.X * anchor.X), MathF.Round(pos.Y - size.Y * anchor.Y));
        var target = Target;
        target.Text(p + new Vector2(1, 1), text, Theme.TextShadow);
        target.Text(p + new Vector2(-1, 0), text, Theme.TextShadow);
        target.Text(p, text, color);
    }

    // ------------------------------------------------------------------ formatting

    private char[] _scratch = new char[256];
    private int _scratchUsed;

    /// <summary>
    /// Format into the frame's scratch text without allocating: <c>ui.Label(ui.Format($"{fps:0} fps"))</c>. The span
    /// stays valid until the next frame starts.
    /// </summary>
    public ReadOnlySpan<char> Format([InterpolatedStringHandlerArgument("")] ref UiText text) => text.Text;

    /// <summary>Text the interpolated <see cref="UiText"/> writes; frame-scoped (reset in <see cref="Begin"/>).</summary>
    internal Span<char> Reserve(int length)
    {
        if (_scratchUsed + length > _scratch.Length)
        {
            // Grow without moving spans already handed out this frame: start a fresh buffer.
            _scratch = new char[Math.Max(_scratch.Length * 2, length * 2)];
            _scratchUsed = 0;
        }
        return _scratch.AsSpan(_scratchUsed, length);
    }

    internal void Commit(int length) => _scratchUsed += length;

    internal static int FormatNumber(Span<char> into, float v) =>
        v.TryFormat(into, out int n, "0.###", CultureInfo.InvariantCulture) ? n : 0;

    // ------------------------------------------------------------------ popups

    /// <summary>Begin drawing a popup into the overlay at <paramref name="r"/>; clicks under it are blocked.</summary>
    public void BeginPopup(Rect r)
    {
        _inOverlay = true;
        _overlayRects.Add(r);
        Overlay.Rect(new Rect(r.X + 3, r.Y + 3, r.W, r.H), new Color32(0, 0, 0, 90));
        Overlay.Rect(r, Theme.PanelHeader);
        Overlay.RectOutline(r, Theme.Border);
        BeginArea(r.Shrink(2));
        if (r.Contains(Mouse)) WantsMouse = true;
    }

    public void EndPopup()
    {
        EndArea();
        _inOverlay = false;
    }

    /// <summary>A tooltip near the mouse.</summary>
    public void Tooltip(ReadOnlySpan<char> text)
    {
        float w = Font.Measure(text) + Theme.Padding * 2;
        var r = new Rect(MathF.Min(Mouse.X + 14, Screen.Right - w - 2), Mouse.Y + 18, w, Font.LineHeight + 8);
        Overlay.Rect(r, Theme.PanelHeader);
        Overlay.RectOutline(r, Theme.Border);
        Overlay.Text(new Vector2(r.X + Theme.Padding, r.Y + 4), text, Theme.Text);
    }

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// A movable debug window with a title bar; drag the title to move it, click the arrow to fold it. Returns true when
    /// open, in which case widgets lay out inside it until <see cref="EndWindow"/> (call EndWindow either way). The
    /// window grows to fit last frame's content, up to <paramref name="initial"/>'s height.
    /// </summary>
    public bool BeginWindow(string title, Rect initial)
    {
        uint id = Id(title, "#window");
        uint xKey = id ^ WindowSalt, yKey = xKey * 31, heightKey = xKey * 37;
        var pos = new Vector2(GetFloat(xKey, initial.X), GetFloat(yKey, initial.Y));
        bool open = GetBool(id, true);
        var bar = new Rect(pos.X, pos.Y, initial.W, Theme.TitleHeight);
        var arrow = bar with { W = bar.H };
        if (Behavior(arrow, Id(title, "#fold"), out _, out _)) open = !open;
        Behavior(bar with { X = arrow.Right, W = bar.W - arrow.W }, id, out bool hovered, out bool held);
        if (held && MouseDelta != Vector2.Zero)
        {
            pos = Vector2.Clamp(pos + MouseDelta, Vector2.Zero, Vector2.Max(Vector2.Zero, Screen.Max - bar.Size));
            bar = bar with { X = pos.X, Y = pos.Y };
            arrow = bar with { W = bar.H };
        }
        if (hovered || held) SetCursor(CursorShape.Move);
        SetFloat(xKey, pos.X);
        SetFloat(yKey, pos.Y);
        SetBool(id, open);

        float body = open ? MathF.Min(GetFloat(heightKey, initial.H - bar.H), initial.H - bar.H) : 0;
        var frame = new Rect(bar.X, bar.Y, bar.W, bar.H + body);
        BlockMouse(frame);
        var draw = Target;
        draw.Rect(frame, Theme.Panel);
        draw.Rect(bar, held ? Theme.WidgetHover : Theme.PanelHeader);
        draw.RectOutline(frame, held ? Theme.Accent : Theme.Border);
        var c = arrow.Center;
        if (open) draw.Triangle(c + new Vector2(-4, -2), c + new Vector2(4, -2), c + new Vector2(0, 3), Theme.TextDim);
        else draw.Triangle(c + new Vector2(-2, -4), c + new Vector2(3, 0), c + new Vector2(-2, 4), Theme.TextDim);
        draw.PushClip(bar);
        TextIn(bar with { X = arrow.Right - Theme.Padding, W = bar.W - arrow.W }, title, Theme.Text);
        draw.PopClip();

        _ids.Push(heightKey); // seeds ids inside the window; EndWindow reads it back to record the content height
        BeginArea(new Rect(frame.X + Theme.Padding, bar.Bottom + Theme.Padding, frame.W - Theme.Padding * 2, MathF.Max(0, body - Theme.Padding * 2)));
        return open;
    }

    public void EndWindow()
    {
        float used = EndArea();
        uint heightKey = _ids.Pop();
        if (used > 0) SetFloat(heightKey, used + Theme.Padding * 2 - Theme.Spacing); // a folded window keeps its last height
    }

    // ------------------------------------------------------------------ text editing state

    private uint _editId;
    private bool _commitTextEdit;
    /// <summary>Commit a focused field when it is drawn this frame, before a host opens a close dialog.</summary>
    public void CommitTextEdit() => _commitTextEdit = true;
    private char[] _edit = new char[64];
    private int _editLength, _editCursor, _editAnchor;
    private float _editScroll;
    private ReadOnlySpan<char> EditText => _edit.AsSpan(0, _editLength);
}

/// <summary>
/// Interpolated text written into the <see cref="Ui"/>'s frame scratch buffer, so labels with numbers don't allocate.
/// Numbers format with the invariant culture.
/// </summary>
[InterpolatedStringHandler]
public ref struct UiText
{
    private readonly Ui _ui;
    private Span<char> _buffer;
    private int _length;

    public UiText(int literalLength, int formattedCount, Ui ui)
    {
        _ui = ui;
        _buffer = ui.Reserve(literalLength + formattedCount * 12);
        _length = 0;
    }

    internal readonly ReadOnlySpan<char> Text
    {
        get
        {
            _ui.Commit(_length);
            return _buffer[.._length];
        }
    }

    public void AppendLiteral(string s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted(ReadOnlySpan<char> s)
    {
        Ensure(s.Length);
        s.CopyTo(_buffer[_length..]);
        _length += s.Length;
    }

    public void AppendFormatted(string? s) => AppendFormatted(s.AsSpan());

    public void AppendFormatted<T>(T value, string? format = null) where T : ISpanFormattable
    {
        int written;
        while (!value.TryFormat(_buffer[_length..], out written, format, CultureInfo.InvariantCulture)) Ensure(_buffer.Length + 16);
        _length += written;
    }

    private void Ensure(int extra)
    {
        if (_length + extra <= _buffer.Length) return;
        var bigger = _ui.Reserve(Math.Max(_buffer.Length * 2, _length + extra));
        _buffer[.._length].CopyTo(bigger);
        _buffer = bigger;
    }
}
