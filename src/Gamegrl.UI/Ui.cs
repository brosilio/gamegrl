using System.Globalization;
using System.Numerics;
using System.Text;
using Gamegrl.Platform;
using Gamegrl.Rhi;
using Gamegrl.UI.Fonts;

namespace Gamegrl.UI;

/// <summary>
/// An immediate-mode UI: call widget methods every frame; they draw, handle input and return what happened.
/// State that must persist (open tree nodes, scroll offsets, the text being edited) lives in small dictionaries
/// keyed by widget id. Ids come from labels hashed with an id stack (<see cref="PushId(string)"/>).
/// Popups draw into an overlay layer on top, and block clicks to what's beneath them.
/// </summary>
public sealed partial class Ui
{
    private readonly Stack<uint> _idStack = new();
    private readonly Stack<Layout> _layouts = new();
    private readonly Dictionary<uint, bool> _bools = new();
    private readonly Dictionary<uint, float> _floats = new();
    private readonly List<Rect> _overlayRects = new(), _overlayRectsPrev = new();
    private Input _input = null!;
    private IPlatform? _platform;
    private CursorShape _cursor;
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
    public Rect Screen { get; private set; }

    public uint Hot { get; private set; }
    public uint Active { get; set; }
    public uint Focus { get; set; }

    /// <summary>The mouse is over (or captured by) the UI: the game/scene shouldn't react to it.</summary>
    public bool WantsMouse { get; private set; }

    /// <summary>A text field has keyboard focus: shortcuts shouldn't fire.</summary>
    public bool WantsKeyboard => Focus != 0;

    // ------------------------------------------------------------------ frame

    public void Begin(Input input, IPlatform? platform, Vector2 screenSize, double time, GpuTexture? fontTexture)
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
        _overlayRectsPrev.Clear();
        _overlayRectsPrev.AddRange(_overlayRects);
        _overlayRects.Clear();
        Hot = 0;
        _cursor = CursorShape.Arrow;
        _focusClaimed = false;
        if (Focus == 0) _editId = 0;
        WantsMouse = Active != 0 || OverOverlay(Mouse);
        Draw.Reset(Font, fontTexture, Screen);
        Overlay.Reset(Font, fontTexture, Screen);
        _idStack.Clear();
        _idStack.Push(2166136261);
        _layouts.Clear();
        _layouts.Push(new Layout(Screen));
    }

    public void End()
    {
        _commitTextEdit = false;
        if (_input.WasReleased(MouseButton.Left)) Active = 0;
        if (_input.WasPressed(MouseButton.Left) && !_focusClaimed) Focus = 0; // clicking elsewhere drops text focus
        Draw.Append(Overlay);
        _platform?.SetCursor(_cursor);
    }

    public void SetCursor(CursorShape shape) => _cursor = shape;

    // ------------------------------------------------------------------ ids

    public uint Id(string label)
    {
        uint h = _idStack.Peek();
        foreach (char c in label)
        {
            h ^= c;
            h *= 16777619;
        }
        return h == 0 ? 1 : h;
    }

    public uint Id(int value) => Id(value.ToString(CultureInfo.InvariantCulture) + "#i");

    public void PushId(string s) => _idStack.Push(Id(s));
    public void PushId(int i) => _idStack.Push(Id(i));
    public void PushId(ulong v) => _idStack.Push(Id(v.ToString("x", CultureInfo.InvariantCulture) + "#u"));
    public void PopId() => _idStack.Pop();

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

    /// <summary>Claim keyboard focus for a text-editing widget this frame.</summary>
    public void ClaimFocus(uint id)
    {
        Focus = id;
        _focusClaimed = true;
    }

    // ------------------------------------------------------------------ layout

    private sealed class Layout(Rect area)
    {
        public Rect Area = area;
        public float CursorY = area.Y;
        public float Indent;
        // A horizontal row: the cells still to hand out.
        public float[]? RowWidths;
        public int RowIndex;
        public float RowY, RowHeight, RowX;
        public float ContentBottom = area.Y;
    }

    private Layout L => _layouts.Peek();

    /// <summary>The space left in the current area.</summary>
    public Rect Remaining => new(L.Area.X + L.Indent, L.CursorY, L.Area.W - L.Indent, MathF.Max(0, L.Area.Bottom - L.CursorY));

    /// <summary>Lay out widgets inside <paramref name="area"/> (top to bottom), clipped to it.</summary>
    public void BeginArea(Rect area, bool clip = true)
    {
        _layouts.Push(new Layout(area));
        if (clip) Target.PushClip(area);
        _clipStack.Push(clip);
    }

    private readonly Stack<bool> _clipStack = new();

    /// <summary>Ends the area; returns the height its content used.</summary>
    public float EndArea()
    {
        var l = _layouts.Pop();
        if (_clipStack.Pop()) Target.PopClip();
        return MathF.Max(l.CursorY, l.ContentBottom) - l.Area.Y;
    }

    /// <summary>The next rect in the current layout: a full-width row, or the next cell of a <see cref="Row"/>.</summary>
    public Rect Next(float height = 0)
    {
        var l = L;
        if (height <= 0) height = Theme.RowHeight;
        if (l.RowWidths is { } widths)
        {
            float w = widths[l.RowIndex];
            var cell = new Rect(l.RowX, l.RowY, w, l.RowHeight);
            l.RowX += w + Theme.Spacing;
            if (++l.RowIndex == widths.Length)
            {
                l.RowWidths = null;
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
    /// Split the next row into cells. Positive widths are pixels, negative are shares of what remains (−1, −2 = one
    /// third and two thirds).
    /// </summary>
    public void Row(params float[] widths) => RowWithHeight(Theme.RowHeight, widths);

    public void RowWithHeight(float height, params float[] widths)
    {
        var l = L;
        float total = l.Area.W - l.Indent - Theme.Spacing * (widths.Length - 1);
        float fixedSum = 0, shares = 0;
        foreach (var w in widths)
            if (w > 0) fixedSum += w; else shares += -w;
        var resolved = new float[widths.Length];
        for (int i = 0; i < widths.Length; i++)
            resolved[i] = widths[i] > 0 ? widths[i] : MathF.Max(0, (total - fixedSum) * (-widths[i] / MathF.Max(shares, 1e-6f)));
        l.RowWidths = resolved;
        l.RowIndex = 0;
        l.RowX = l.Area.X + l.Indent;
        l.RowY = l.CursorY;
        l.RowHeight = height;
        l.ContentBottom = MathF.Max(l.ContentBottom, l.RowY + height);
    }

    public void Space(float pixels = 6) => L.CursorY += pixels;
    public void Indent() => L.Indent += Theme.Indent;
    public void Unindent() => L.Indent = MathF.Max(0, L.Indent - Theme.Indent);

    /// <summary>A labeled property row: the label on the left, the returned rect for the value on the right.</summary>
    public Rect Property(string label, float height = 0)
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
    public void TextIn(Rect r, string text, Color32 color, float align = 0)
    {
        float w = Font.Measure(text);
        float x = r.X + Theme.Padding + (r.W - 2 * Theme.Padding - w) * align;
        Target.Text(new Vector2(MathF.Round(x), MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), text, color);
    }

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
    public void Tooltip(string text)
    {
        float w = Font.Measure(text) + Theme.Padding * 2;
        var r = new Rect(MathF.Min(Mouse.X + 14, Screen.Right - w - 2), Mouse.Y + 18, w, Font.LineHeight + 8);
        bool wasOverlay = _inOverlay;
        _inOverlay = true;
        Overlay.Rect(r, Theme.PanelHeader);
        Overlay.RectOutline(r, Theme.Border);
        Overlay.Text(new Vector2(r.X + Theme.Padding, r.Y + 4), text, Theme.Text);
        _inOverlay = wasOverlay;
    }

    // ------------------------------------------------------------------ text editing state

    private uint _editId;
    private bool _commitTextEdit;
    /// <summary>Commit a focused field when it is drawn this frame, before a host opens a close dialog.</summary>
    public void CommitTextEdit() => _commitTextEdit = true;
    private readonly StringBuilder _editBuffer = new();
    private int _editCursor, _editAnchor;
    private float _editScroll;

    internal static string Format(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}
