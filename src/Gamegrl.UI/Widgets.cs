using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using Gamegrl.Platform;
using Gamegrl.Rhi;

namespace Gamegrl.UI;

public sealed partial class Ui
{
    // ------------------------------------------------------------------ basic

    public void Label(ReadOnlySpan<char> text, bool dim = false)
    {
        var r = Next();
        TextIn(r with { X = r.X - Theme.Padding, W = r.W + Theme.Padding }, text, dim ? Theme.TextDim : Theme.Text);
    }

    /// <summary>A label with interpolated values, formatted without allocating: <c>ui.Label($"{fps:0} fps")</c>.</summary>
    public void Label([InterpolatedStringHandlerArgument("")] ref UiText text, bool dim = false) => Label(text.Text, dim);

    /// <summary>A dim caption followed by a rule, separating groups of rows.</summary>
    public void Header(ReadOnlySpan<char> text)
    {
        var r = Next();
        float w = Font.Measure(text);
        Target.Text(new Vector2(r.X, MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), text, Theme.TextDim);
        if (r.W > w + Theme.Spacing * 2) Target.Rect(new Rect(r.X + w + Theme.Spacing * 2, MathF.Round(r.Center.Y), r.W - w - Theme.Spacing * 2, 1), Theme.Border);
    }

    public void Separator() => Target.Rect(Next(1), Theme.Border);

    public bool Button(string label) => Button(Next(), label);

    public bool Button(Rect r, ReadOnlySpan<char> label, bool active = false, ReadOnlySpan<char> id = default)
    {
        uint wid = Id(id.IsEmpty ? label : id);
        bool clicked = Behavior(r, wid, out bool hovered, out bool held);
        var fill = active ? Theme.Selection : held ? Theme.WidgetActive : hovered ? Theme.WidgetHover : Theme.Widget;
        Target.Rect(r, fill);
        Target.RectOutline(r, hovered || active ? Theme.Accent : Theme.Border);
        TextIn(r, label, active ? Theme.Accent : Theme.Text, 0.5f);
        return clicked;
    }

    public bool Checkbox(string label, ref bool value) => Checkbox(Property(label), label, ref value);

    public bool Checkbox(Rect r, ReadOnlySpan<char> id, ref bool value)
    {
        float s = MathF.Min(r.H - 6, 14);
        var box = new Rect(r.X, MathF.Round(r.Y + (r.H - s) * 0.5f), s, s);
        bool clicked = Behavior(new Rect(r.X, r.Y, s + 4, r.H), Id(id, "#check"), out bool hovered, out _);
        Target.Rect(box, Theme.Field);
        Target.RectOutline(box, hovered ? Theme.Accent : Theme.Border);
        if (value) Target.Rect(box.Shrink(3), Theme.Accent);
        if (clicked) value = !value;
        return clicked;
    }

    public void Image(Rect r, GpuTexture texture) => Target.Image(r, texture, Color32.White, Vector2.Zero, Vector2.One);

    /// <summary>A bar filled to <paramref name="fraction"/> (0..1), with optional text centered on it.</summary>
    public void ProgressBar(Rect r, float fraction, ReadOnlySpan<char> text = default, Color32? color = null)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        Target.Rect(r, Theme.Field);
        Target.Rect(r with { W = MathF.Round(r.W * fraction) }, color ?? Theme.Accent);
        Target.RectOutline(r, Theme.Border);
        if (!text.IsEmpty) TextIn(r, text, Theme.Text, 0.5f);
    }

    // ------------------------------------------------------------------ numbers

    /// <summary>
    /// A number dragged horizontally (hold Shift for fine steps); double-click to type a value. Returns true while
    /// the value changes. <paramref name="label"/> is an id, not drawn.
    /// </summary>
    public bool DragFloat(Rect r, ReadOnlySpan<char> label, ref float value, float speed = 0.05f, string? prefix = null, Color32? prefixColor = null) =>
        DragFloat(r, Id(label, "#drag"), ref value, speed, prefix, prefixColor);

    private bool DragFloat(Rect r, uint id, ref float value, float speed, string? prefix, Color32? prefixColor)
    {
        if (_editId == id)
        {
            if (TextEditBox(r, id, out bool commit) && commit
                && float.TryParse(EditText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed != value)
            {
                value = parsed;
                return true;
            }
            return false;
        }

        Behavior(r, id, out bool hovered, out bool held);
        if (hovered || held) SetCursor(CursorShape.ResizeHorizontal);
        bool changed = false;
        if (held && MouseDelta.X != 0)
        {
            value += MouseDelta.X * speed * (_input.Shift ? 0.1f : 1f);
            changed = true;
        }
        Span<char> text = stackalloc char[32];
        int length = FormatNumber(text, value);
        if (hovered && DoubleClicked)
        {
            BeginTextEdit(id, text[..length]);
            Active = 0;
        }

        Target.Rect(r, held ? Theme.WidgetActive : hovered ? Theme.WidgetHover : Theme.Field);
        float x = r.X;
        if (prefix is not null)
        {
            var tag = new Rect(r.X, r.Y, 14, r.H);
            Target.Rect(tag with { W = 2 }, prefixColor ?? Theme.Border);
            TextIn(tag with { X = tag.X - 3, W = tag.W + 6 }, prefix, prefixColor ?? Theme.TextDim, 0.5f);
            x += 14;
        }
        Target.PushClip(new Rect(x, r.Y, r.Right - x, r.H));
        TextIn(new Rect(x, r.Y, r.Right - x, r.H), text[..length], Theme.Text);
        Target.PopClip();
        return changed;
    }

    public bool DragInt(Rect r, ReadOnlySpan<char> label, ref int value, float speed = 0.2f)
    {
        uint dragId = Id(label, "#drag"), accId = Id(label, "#acc");
        if (Active != dragId) SetFloat(accId, value);
        float acc = GetFloat(accId, value);
        bool changed = DragFloat(r, dragId, ref acc, speed, null, null);
        SetFloat(accId, acc);
        int rounded = (int)MathF.Round(acc);
        if (changed && rounded != value)
        {
            value = rounded;
            return true;
        }
        return false;
    }

    /// <summary>A horizontal slider between <paramref name="min"/> and <paramref name="max"/>; returns true while it changes.</summary>
    public bool Slider(Rect r, ReadOnlySpan<char> label, ref float value, float min, float max)
    {
        uint id = Id(label, "#slider");
        Behavior(r, id, out bool hovered, out bool held);
        // The value gets a fixed column right of the track, so it never covers the knob and the track doesn't jitter.
        float column = MathF.Ceiling(Font.Measure("-00.00")) + Theme.Spacing;
        var bar = r with { W = MathF.Max(1, r.W - column) };
        bool changed = false;
        if (held && max > min)
        {
            float v = Math.Clamp(min + (Mouse.X - bar.X) / bar.W * (max - min), min, max);
            changed = v != value;
            value = v;
        }
        float t = max > min ? Math.Clamp((value - min) / (max - min), 0, 1) : 0;
        var track = new Rect(bar.X + 3, MathF.Round(r.Center.Y) - 1, bar.W - 6, 2);
        Target.Rect(track, Theme.Border);
        Target.Rect(track with { W = MathF.Round(track.W * t) }, Theme.Accent);
        var knob = new Rect(MathF.Round(track.X + track.W * t) - 3, r.Y + 4, 6, r.H - 8);
        Target.Rect(knob, held || hovered ? Theme.Accent : Theme.Text);
        Span<char> text = stackalloc char[32];
        int length = FormatNumber(text, value);
        TextIn(r, text[..length], Theme.TextDim, 1);
        return changed;
    }

    public bool Vector(Rect r, ReadOnlySpan<char> label, Span<float> components, float speed = 0.05f)
    {
        int n = components.Length;
        float w = (r.W - Theme.Spacing * (n - 1)) / n;
        bool changed = false;
        ReadOnlySpan<string> names = ["X", "Y", "Z", "W"];
        ReadOnlySpan<Color32> colors = [Theme.AxisX, Theme.AxisY, Theme.AxisZ, Theme.TextDim];
        for (int i = 0; i < n; i++)
        {
            var cell = new Rect(r.X + i * (w + Theme.Spacing), r.Y, w, r.H);
            changed |= DragFloat(cell, Id(label, names[i]), ref components[i], speed, names[i], colors[i]);
        }
        return changed;
    }

    public bool Vector3(Rect r, ReadOnlySpan<char> label, ref Vector3 v, float speed = 0.05f)
    {
        Span<float> c = [v.X, v.Y, v.Z];
        if (!Vector(r, label, c, speed)) return false;
        v = new Vector3(c[0], c[1], c[2]);
        return true;
    }

    /// <summary>An sRGB-ish color edited as RGBA in 0..1, with a swatch.</summary>
    public bool Color(Rect r, ReadOnlySpan<char> label, ref Vector4 rgba)
    {
        var swatch = new Rect(r.X, r.Y + 2, r.H * 1.4f, r.H - 4);
        Target.Rect(swatch, new Color32(ToByte(rgba.X), ToByte(rgba.Y), ToByte(rgba.Z), 255));
        Target.RectOutline(swatch, Theme.Border);
        var rest = new Rect(swatch.Right + Theme.Spacing, r.Y, r.Right - swatch.Right - Theme.Spacing, r.H);
        Span<float> c = [rgba.X, rgba.Y, rgba.Z, rgba.W];
        if (!Vector(rest, label, c, 0.005f)) return false;
        rgba = new Vector4(Math.Clamp(c[0], 0, 1), Math.Clamp(c[1], 0, 1), Math.Clamp(c[2], 0, 1), Math.Clamp(c[3], 0, 1));
        return true;
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(v * 255f + 0.5f, 0, 255);

    // ------------------------------------------------------------------ text

    /// <summary>A single-line text field. Returns true when the edit is committed (Enter or focus lost) with a change.</summary>
    public bool TextField(Rect r, ReadOnlySpan<char> label, ref string value)
    {
        uint id = Id(label, "#text");
        if (_editId != id)
        {
            bool clicked = Behavior(r, id, out bool hovered, out _);
            if (hovered) SetCursor(CursorShape.Text);
            Target.Rect(r, Theme.Field);
            Target.RectOutline(r, hovered ? Theme.Accent : Theme.Border);
            Target.PushClip(r);
            TextIn(r, value, Theme.Text);
            Target.PopClip();
            if (clicked || (hovered && _input.WasPressed(MouseButton.Left)))
            {
                BeginTextEdit(id, value);
                _editCursor = Font.HitTest(value, Mouse.X - r.X - Theme.Padding);
                _editAnchor = _editCursor;
            }
            return false;
        }
        if (TextEditBox(r, id, out bool commit) && commit && !EditText.SequenceEqual(value))
        {
            value = EditText.ToString();
            return true;
        }
        return false;
    }

    private void BeginTextEdit(uint id, ReadOnlySpan<char> text)
    {
        _editId = id;
        _editLength = _editCursor = _editAnchor = 0;
        Insert(text);
        _editAnchor = 0; // select all
        _editScroll = 0;
        ClaimFocus(id);
    }

    /// <summary>Draws and edits the active text buffer. Returns true when editing ends; <paramref name="commit"/> says whether to keep it.</summary>
    private bool TextEditBox(Rect r, uint id, out bool commit)
    {
        commit = false;
        ClaimFocus(id);
        SetCursor(CursorShape.Text);
        WantsMouse |= IsHovered(r);

        // Clicking outside commits; Escape cancels; Enter commits.
        if (_input.WasPressed(MouseButton.Left) && !IsHovered(r)) return Finish(true, out commit);
        if (_input.WasPressed(Key.Escape)) return Finish(false, out commit);
        if (_input.WasPressed(Key.Enter)) return Finish(true, out commit);

        string typed = _input.TypedText;
        bool ctrl = _input.Ctrl;
        if (ctrl && _input.WasPressed(Key.A)) { _editAnchor = 0; _editCursor = _editLength; }
        else if (ctrl && _input.WasPressed(Key.C)) _platform?.SetClipboardText(Selected().ToString());
        else if (ctrl && _input.WasPressed(Key.X)) { _platform?.SetClipboardText(Selected().ToString()); DeleteSelection(); }
        else if (ctrl && _input.WasPressed(Key.V)) Insert((_platform?.GetClipboardText() ?? "").Replace("\n", " "));
        else if (typed.Length > 0) Insert(typed);

        if (_input.WasPressedOrRepeated(Key.Backspace))
        {
            if (_editCursor != _editAnchor) DeleteSelection();
            else if (_editCursor > 0) { Remove(_editCursor - 1, 1); _editAnchor = --_editCursor; }
        }
        if (_input.WasPressedOrRepeated(Key.Left)) Move(-1);
        if (_input.WasPressedOrRepeated(Key.Right)) Move(+1);
        if (_input.WasPressedOrRepeated(Key.Home)) MoveTo(0);
        if (_input.WasPressedOrRepeated(Key.End)) MoveTo(_editLength);
        if (_input.WasPressedOrRepeated(Key.Delete))
        {
            if (_editCursor != _editAnchor) DeleteSelection();
            else if (_editCursor < _editLength) Remove(_editCursor, 1);
        }
        if (IsHovered(r) && _input.WasPressed(MouseButton.Left))
        {
            _editCursor = Font.HitTest(EditText, Mouse.X - r.X - Theme.Padding + _editScroll);
            if (!_input.Shift) _editAnchor = _editCursor;
        }
        else if (IsHovered(r) && _input.IsDown(MouseButton.Left) && MouseDelta != Vector2.Zero)
            _editCursor = Font.HitTest(EditText, Mouse.X - r.X - Theme.Padding + _editScroll);

        if (_commitTextEdit) return Finish(true, out commit);
        // Draw: field, selection, text, caret (scrolled to keep the caret visible).
        var text = EditText;
        float caretX = Font.Measure(text[.._editCursor]);
        float inner = r.W - Theme.Padding * 2;
        if (caretX - _editScroll > inner) _editScroll = caretX - inner;
        if (caretX - _editScroll < 0) _editScroll = caretX;
        Target.Rect(r, Theme.Field);
        Target.RectOutline(r, Theme.Accent);
        Target.PushClip(r.Shrink(1));
        float x0 = r.X + Theme.Padding - _editScroll;
        if (_editCursor != _editAnchor)
        {
            int a = Math.Min(_editCursor, _editAnchor), b = Math.Max(_editCursor, _editAnchor);
            float sx = x0 + Font.Measure(text[..a]), ex = x0 + Font.Measure(text[..b]);
            Target.Rect(new Rect(sx, r.Y + 3, ex - sx, r.H - 6), Theme.Selection);
        }
        Target.Text(new Vector2(x0, MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), text, Theme.Text);
        if ((int)(_time * 2) % 2 == 0) Target.Rect(new Rect(MathF.Round(x0 + caretX), r.Y + 3, 1, r.H - 6), Theme.Accent);
        Target.PopClip();
        return false;
    }

    private bool Finish(bool keep, out bool commit)
    {
        commit = keep;
        _editId = 0;
        Focus = 0;
        return true;
    }

    private ReadOnlySpan<char> Selected()
    {
        int a = Math.Min(_editCursor, _editAnchor), b = Math.Max(_editCursor, _editAnchor);
        return EditText[a..b];
    }

    private void DeleteSelection()
    {
        int a = Math.Min(_editCursor, _editAnchor), b = Math.Max(_editCursor, _editAnchor);
        Remove(a, b - a);
        _editCursor = _editAnchor = a;
    }

    private void Remove(int at, int count)
    {
        _edit.AsSpan(at + count, _editLength - at - count).CopyTo(_edit.AsSpan(at));
        _editLength -= count;
    }

    private void Insert(ReadOnlySpan<char> s)
    {
        if (_editCursor != _editAnchor) DeleteSelection();
        _editCursor = Math.Min(_editCursor, _editLength);
        if (_editLength + s.Length > _edit.Length) Array.Resize(ref _edit, Math.Max(_edit.Length * 2, _editLength + s.Length));
        _edit.AsSpan(_editCursor, _editLength - _editCursor).CopyTo(_edit.AsSpan(_editCursor + s.Length));
        s.CopyTo(_edit.AsSpan(_editCursor));
        _editLength += s.Length;
        _editCursor += s.Length;
        _editAnchor = _editCursor;
    }

    private void Move(int d)
    {
        if (_editCursor != _editAnchor && !_input.Shift)
        {
            _editCursor = d < 0 ? Math.Min(_editCursor, _editAnchor) : Math.Max(_editCursor, _editAnchor);
            _editAnchor = _editCursor;
            return;
        }
        MoveTo(Math.Clamp(_editCursor + d, 0, _editLength));
    }

    private void MoveTo(int at)
    {
        _editCursor = at;
        if (!_input.Shift) _editAnchor = at;
    }

    // ------------------------------------------------------------------ lists and trees

    /// <summary>A full-width selectable row. Returns true when clicked.</summary>
    public bool Selectable(Rect r, ReadOnlySpan<char> label, bool selected, ReadOnlySpan<char> id = default, Color32? textColor = null) =>
        Selectable(r, label, selected, Id(id.IsEmpty ? label : id), textColor);

    private bool Selectable(Rect r, ReadOnlySpan<char> label, bool selected, uint id, Color32? textColor)
    {
        bool clicked = Behavior(r, id, out bool hovered, out _);
        if (selected) Target.Rect(r, Theme.Selection);
        else if (hovered) Target.Rect(r, Theme.WidgetHover);
        Target.PushClip(r);
        TextIn(r, label, textColor ?? (selected ? Theme.Accent : Theme.Text));
        Target.PopClip();
        return clicked;
    }

    /// <summary>
    /// A tree row with an expand arrow when it has children. Returns true when the row is clicked (select); the
    /// arrow toggles <paramref name="open"/> itself.
    /// </summary>
    public bool TreeNode(Rect r, ReadOnlySpan<char> id, ReadOnlySpan<char> label, bool hasChildren, bool selected, ref bool open, float depth, Color32? textColor = null)
    {
        float indent = depth * Theme.Indent;
        var arrow = new Rect(r.X + indent, r.Y, 14, r.H);
        bool rowClicked = Behavior(r, Id(id), out bool hovered, out _);
        if (hasChildren && Behavior(arrow, Id(id, "#arrow"), out _, out _))
        {
            open = !open;
            rowClicked = false;
        }
        if (selected) Target.Rect(r, Theme.Selection);
        else if (hovered) Target.Rect(r, Theme.WidgetHover);
        if (hasChildren)
        {
            var c = arrow.Center;
            if (open) Target.Triangle(c + new Vector2(-4, -2), c + new Vector2(4, -2), c + new Vector2(0, 3), Theme.TextDim);
            else Target.Triangle(c + new Vector2(-2, -4), c + new Vector2(3, 0), c + new Vector2(-2, 4), Theme.TextDim);
        }
        Target.PushClip(r);
        Target.Text(new Vector2(arrow.Right + 2, MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), label, textColor ?? (selected ? Theme.Accent : Theme.Text));
        Target.PopClip();
        return rowClicked;
    }

    /// <summary>A drop-down: shows <paramref name="items"/>[selected]; returns true when the choice changes.</summary>
    public bool Combo(Rect r, ReadOnlySpan<char> label, ref int selected, IReadOnlyList<string> items)
    {
        uint id = Id(label, "#combo");
        bool open = GetBool(id);
        if (Behavior(r, id, out bool hovered, out _)) open = !open;
        Target.Rect(r, Theme.Widget);
        Target.RectOutline(r, hovered || open ? Theme.Accent : Theme.Border);
        TextIn(r with { W = r.W - 14 }, selected >= 0 && selected < items.Count ? items[selected] : "", Theme.Text);
        var c = new Vector2(r.Right - 9, r.Center.Y);
        Target.Triangle(c + new Vector2(-4, -2), c + new Vector2(4, -2), c + new Vector2(0, 3), Theme.TextDim);
        bool changed = false;
        if (open)
        {
            float h = MathF.Min(items.Count * (Theme.RowHeight + Theme.Spacing) + 4, 360);
            var popup = new Rect(r.X, r.Bottom + 1, MathF.Max(r.W, 160), h);
            if (popup.Bottom > Screen.Bottom) popup = popup with { Y = r.Y - h - 1 };
            BeginPopup(popup);
            _ids.Push(id);
            BeginScroll(Remaining, "popup");
            for (int i = 0; i < items.Count; i++)
            {
                if (Selectable(Next(), items[i], i == selected, Id(i), null))
                {
                    changed = i != selected;
                    selected = i;
                    open = false;
                }
            }
            EndScroll();
            _ids.Pop();
            EndPopup();
            if (_input.WasPressed(MouseButton.Left) && !popup.Contains(Mouse) && !r.Contains(Mouse)) open = false;
            if (_input.WasPressed(Key.Escape)) open = false;
        }
        SetBool(id, open);
        return changed;
    }

    // ------------------------------------------------------------------ containers

    /// <summary>Tabs across the top of <paramref name="r"/>; returns the content area below them.</summary>
    public Rect Tabs(Rect r, ReadOnlySpan<char> id, IReadOnlyList<string> names, ref int selected)
    {
        var strip = r with { H = Theme.TabHeight };
        Target.Rect(new Rect(strip.X, strip.Bottom - 1, strip.W, 1), Theme.Border);
        float x = strip.X;
        PushId(id);
        for (int i = 0; i < names.Count; i++)
        {
            float w = Font.Measure(names[i]) + Theme.Padding * 4;
            var tab = new Rect(x, strip.Y, w, strip.H);
            bool clicked = Behavior(tab, Id(i), out bool hovered, out _);
            if (clicked) selected = i;
            if (i == selected) Target.Rect(new Rect(tab.X, tab.Bottom - 2, tab.W, 2), Theme.Accent);
            TextIn(tab, names[i], i == selected ? Theme.Text : hovered ? Theme.Text : Theme.TextDim, 0.5f);
            x += w + 1;
        }
        PopId();
        return new Rect(r.X, strip.Bottom, r.W, r.H - strip.H);
    }

    /// <summary>A draggable divider. Returns the new position (units from <paramref name="min"/>'s side).</summary>
    public float Splitter(Rect r, ReadOnlySpan<char> id, float position, bool vertical, float min, float max)
    {
        Behavior(r, Id(id, "#split"), out bool hovered, out bool held);
        if (hovered || held) SetCursor(vertical ? CursorShape.ResizeHorizontal : CursorShape.ResizeVertical);
        if (held) position = Math.Clamp(position + (vertical ? MouseDelta.X : MouseDelta.Y), min, max);
        Target.Rect(r, held || hovered ? Theme.Accent : Theme.Border);
        return position;
    }

    /// <summary>Begin a vertically scrolling region; widgets inside lay out as usual. Call <see cref="EndScroll"/>.</summary>
    public void BeginScroll(Rect r, ReadOnlySpan<char> id)
    {
        uint wid = Id(id, "#scroll");
        float content = GetFloat(wid ^ ScrollContentSalt);
        float max = MathF.Max(0, content - r.H);
        float scroll = Math.Clamp(GetFloat(wid), 0, max);
        if (IsHovered(r) && _input.Wheel.Y != 0) scroll = Math.Clamp(scroll - _input.Wheel.Y * Theme.RowHeight * 3, 0, max);
        SetFloat(wid, scroll);
        _scrolls.Push((wid, r));
        BeginArea(r);
        L.CursorY -= scroll;
    }

    private readonly Stack<(uint Id, Rect Rect)> _scrolls = new();

    public void EndScroll()
    {
        var (id, r) = _scrolls.Pop();
        float used = EndArea() + GetFloat(id);
        SetFloat(id ^ ScrollContentSalt, used);
        if (used > r.H)
        {
            float scroll = GetFloat(id);
            float barH = MathF.Max(20, r.H * r.H / used);
            float barY = r.Y + (r.H - barH) * (scroll / (used - r.H));
            Target.Rect(new Rect(r.Right - Theme.ScrollbarWidth, barY, Theme.ScrollbarWidth - 2, barH), Theme.Border);
        }
    }
}
