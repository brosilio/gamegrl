using System.Globalization;
using System.Numerics;
using Gamegrl.Platform;
using Gamegrl.Rhi;

namespace Gamegrl.UI;

public sealed partial class Ui
{
    // ------------------------------------------------------------------ basic

    public void Label(string text, bool dim = false)
    {
        var r = Next();
        TextIn(r with { X = r.X - Theme.Padding, W = r.W + Theme.Padding }, text, dim ? Theme.TextDim : Theme.Text);
    }

    public void Header(string text)
    {
        var r = Next();
        Target.Rect(r, Theme.PanelHeader);
        TextIn(r, text, Theme.Text);
    }

    public void Separator()
    {
        var r = Next(1);
        Target.Rect(r, Theme.Border);
    }

    public bool Button(string label) => Button(Next(), label);

    public bool Button(Rect r, string label, bool active = false, string? id = null)
    {
        uint wid = Id(id ?? label);
        bool clicked = Behavior(r, wid, out bool hovered, out bool held);
        var color = active ? Theme.Accent : held ? Theme.WidgetActive : hovered ? Theme.WidgetHover : Theme.Widget;
        Target.Rect(r, color);
        TextIn(r, label, Theme.Text, 0.5f);
        return clicked;
    }

    public bool Checkbox(string label, ref bool value)
    {
        var r = Property(label);
        return Checkbox(r, label, ref value);
    }

    public bool Checkbox(Rect r, string id, ref bool value)
    {
        float s = MathF.Min(r.H, 16);
        var box = new Rect(r.X, r.Y + (r.H - s) * 0.5f, s, s);
        bool clicked = Behavior(new Rect(r.X, r.Y, s + 4, r.H), Id(id + "#check"), out bool hovered, out _);
        Target.Rect(box, hovered ? Theme.WidgetHover : Theme.Field);
        Target.RectOutline(box, Theme.Border);
        if (value) Target.Rect(box.Shrink(4), Theme.Accent);
        if (clicked) value = !value;
        return clicked;
    }

    public void Image(Rect r, GpuTexture texture) => Target.Image(r, texture, Color32.White, Vector2.Zero, Vector2.One);

    // ------------------------------------------------------------------ numbers

    /// <summary>
    /// A number dragged horizontally (hold Shift for fine steps); double-click to type a value. Returns true while
    /// the value changes. <paramref name="label"/> is an id, not drawn.
    /// </summary>
    public bool DragFloat(Rect r, string label, ref float value, float speed = 0.05f, string? prefix = null, Color32? prefixColor = null)
    {
        uint id = Id(label + "#drag");
        if (_editId == id)
        {
            if (TextEditBox(r, id, out bool commit, numeric: true))
            {
                if (commit && float.TryParse(_editBuffer.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed != value)
                {
                    value = parsed;
                    return true;
                }
            }
            return false;
        }

        bool clicked = Behavior(r, id, out bool hovered, out bool held);
        if (hovered || held) SetCursor(CursorShape.ResizeHorizontal);
        bool changed = false;
        if (held && MouseDelta.X != 0)
        {
            value += MouseDelta.X * speed * (_input.Shift ? 0.1f : 1f);
            changed = true;
        }
        if (hovered && DoubleClicked)
        {
            BeginTextEdit(id, Format(value));
            Active = 0;
        }
        _ = clicked;

        Target.Rect(r, held ? Theme.WidgetActive : hovered ? Theme.WidgetHover : Theme.Field);
        float x = r.X;
        if (prefix is not null)
        {
            var tag = new Rect(r.X, r.Y, 14, r.H);
            Target.Rect(tag, prefixColor ?? Theme.Widget);
            TextIn(tag with { X = tag.X - 3, W = tag.W + 6 }, prefix, Theme.Text, 0.5f);
            x += 14;
        }
        Target.PushClip(new Rect(x, r.Y, r.Right - x, r.H));
        TextIn(new Rect(x, r.Y, r.Right - x, r.H), Format(value), Theme.Text);
        Target.PopClip();
        return changed;
    }

    public bool DragInt(Rect r, string label, ref int value, float speed = 0.2f)
    {
        float f = value;
        uint accId = Id(label + "#acc");
        if (Active != Id(label + "#drag")) SetFloat(accId, value);
        float acc = GetFloat(accId, value);
        bool changed = DragFloat(r, label, ref acc, speed);
        SetFloat(accId, acc);
        int rounded = (int)MathF.Round(acc);
        if (changed && rounded != value)
        {
            value = rounded;
            return true;
        }
        _ = f;
        return false;
    }

    public bool Vector(Rect r, string label, Span<float> components, float speed = 0.05f)
    {
        int n = components.Length;
        float w = (r.W - Theme.Spacing * (n - 1)) / n;
        bool changed = false;
        ReadOnlySpan<string> names = ["X", "Y", "Z", "W"];
        ReadOnlySpan<Color32> colors = [Theme.AxisX, Theme.AxisY, Theme.AxisZ, Theme.Widget];
        for (int i = 0; i < n; i++)
        {
            var cell = new Rect(r.X + i * (w + Theme.Spacing), r.Y, w, r.H);
            changed |= DragFloat(cell, label + names[i], ref components[i], speed, names[i], colors[i]);
        }
        return changed;
    }

    public bool Vector3(Rect r, string label, ref Vector3 v, float speed = 0.05f)
    {
        Span<float> c = [v.X, v.Y, v.Z];
        if (!Vector(r, label, c, speed)) return false;
        v = new Vector3(c[0], c[1], c[2]);
        return true;
    }

    /// <summary>An sRGB-ish color edited as RGBA in 0..1, with a swatch.</summary>
    public bool Color(Rect r, string label, ref Vector4 rgba)
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
    public bool TextField(Rect r, string label, ref string value)
    {
        uint id = Id(label + "#text");
        if (_editId != id)
        {
            bool clicked = Behavior(r, id, out bool hovered, out _);
            if (hovered) SetCursor(CursorShape.Text);
            Target.Rect(r, hovered ? Theme.WidgetHover : Theme.Field);
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
        if (TextEditBox(r, id, out bool commit, numeric: false) && commit)
        {
            string s = _editBuffer.ToString();
            if (s != value)
            {
                value = s;
                return true;
            }
        }
        return false;
    }

    private void BeginTextEdit(uint id, string text)
    {
        _editId = id;
        _editBuffer.Clear().Append(text);
        _editCursor = text.Length;
        _editAnchor = 0; // select all
        _editScroll = 0;
        ClaimFocus(id);
    }

    /// <summary>Draws and edits the active text buffer. Returns true when editing ends; <paramref name="commit"/> says whether to keep it.</summary>
    private bool TextEditBox(Rect r, uint id, out bool commit, bool numeric)
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
        if (ctrl && _input.WasPressed(Key.A)) { _editAnchor = 0; _editCursor = _editBuffer.Length; }
        else if (ctrl && _input.WasPressed(Key.C)) _platform?.SetClipboardText(Selected());
        else if (ctrl && _input.WasPressed(Key.X)) { _platform?.SetClipboardText(Selected()); DeleteSelection(); }
        else if (ctrl && _input.WasPressed(Key.V)) Insert(_platform?.GetClipboardText().Replace("\n", " ") ?? "");
        else if (typed.Length > 0) Insert(typed);

        if (_input.WasPressedOrRepeated(Key.Backspace))
        {
            if (_editCursor != _editAnchor) DeleteSelection();
            else if (_editCursor > 0) { _editBuffer.Remove(_editCursor - 1, 1); _editAnchor = --_editCursor; }
        }
        if (_input.WasPressedOrRepeated(Key.Left)) Move(-1);
        if (_input.WasPressedOrRepeated(Key.Right)) Move(+1);
        if (_input.WasPressedOrRepeated(Key.Home)) MoveTo(0);
        if (_input.WasPressedOrRepeated(Key.End)) MoveTo(_editBuffer.Length);
        if (_input.WasPressedOrRepeated(Key.Delete))
        {
            if (_editCursor != _editAnchor) DeleteSelection();
            else if (_editCursor < _editBuffer.Length) _editBuffer.Remove(_editCursor, 1);
        }
        if (IsHovered(r) && _input.WasPressed(MouseButton.Left))
        {
            _editCursor = Font.HitTest(_editBuffer.ToString(), Mouse.X - r.X - Theme.Padding + _editScroll);
            if (!_input.Shift) _editAnchor = _editCursor;
        }
        else if (IsHovered(r) && _input.IsDown(MouseButton.Left) && MouseDelta != Vector2.Zero)
            _editCursor = Font.HitTest(_editBuffer.ToString(), Mouse.X - r.X - Theme.Padding + _editScroll);

        // Draw: field, selection, text, caret (scrolled to keep the caret visible).
        string text = _editBuffer.ToString();
        float caretX = Font.Measure(text.AsSpan(0, _editCursor));
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
            float sx = x0 + Font.Measure(text.AsSpan(0, a)), ex = x0 + Font.Measure(text.AsSpan(0, b));
            Target.Rect(new Rect(sx, r.Y + 3, ex - sx, r.H - 6), Theme.Selection);
        }
        Target.Text(new Vector2(x0, MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), text, Theme.Text);
        if ((int)(_time * 2) % 2 == 0) Target.Rect(new Rect(MathF.Round(x0 + caretX), r.Y + 3, 1, r.H - 6), Theme.Text);
        Target.PopClip();
        _ = numeric;
        return false;
    }

    private bool Finish(bool keep, out bool commit)
    {
        commit = keep;
        _editId = 0;
        Focus = 0;
        return true;
    }

    private string Selected()
    {
        int a = Math.Min(_editCursor, _editAnchor), b = Math.Max(_editCursor, _editAnchor);
        return _editBuffer.ToString(a, b - a);
    }

    private void DeleteSelection()
    {
        int a = Math.Min(_editCursor, _editAnchor), b = Math.Max(_editCursor, _editAnchor);
        _editBuffer.Remove(a, b - a);
        _editCursor = _editAnchor = a;
    }

    private void Insert(string s)
    {
        if (_editCursor != _editAnchor) DeleteSelection();
        _editBuffer.Insert(_editCursor, s);
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
        MoveTo(Math.Clamp(_editCursor + d, 0, _editBuffer.Length));
    }

    private void MoveTo(int at)
    {
        _editCursor = at;
        if (!_input.Shift) _editAnchor = at;
    }

    // ------------------------------------------------------------------ lists and trees

    /// <summary>A full-width selectable row. Returns true when clicked.</summary>
    public bool Selectable(Rect r, string label, bool selected, string? id = null, Color32? textColor = null)
    {
        bool clicked = Behavior(r, Id(id ?? label), out bool hovered, out _);
        if (selected) Target.Rect(r, Theme.Selection);
        else if (hovered) Target.Rect(r, Theme.WidgetHover.WithAlpha(120));
        Target.PushClip(r);
        TextIn(r, label, textColor ?? Theme.Text);
        Target.PopClip();
        return clicked;
    }

    /// <summary>
    /// A tree row with an expand arrow when it has children. Returns what was clicked: the row (select) or
    /// nothing; the arrow toggles <paramref name="open"/> itself.
    /// </summary>
    public bool TreeNode(Rect r, string id, string label, bool hasChildren, bool selected, ref bool open, float depth, Color32? textColor = null)
    {
        float indent = depth * Theme.Indent;
        var arrow = new Rect(r.X + indent, r.Y, 14, r.H);
        bool rowClicked = Behavior(r, Id(id), out bool hovered, out _);
        if (hasChildren && Behavior(arrow, Id(id + "#arrow"), out _, out _))
        {
            open = !open;
            rowClicked = false;
        }
        if (selected) Target.Rect(r, Theme.Selection);
        else if (hovered) Target.Rect(r, Theme.WidgetHover.WithAlpha(120));
        if (hasChildren)
        {
            var c = arrow.Center;
            if (open) Target.Triangle(c + new Vector2(-4, -2), c + new Vector2(4, -2), c + new Vector2(0, 3), Theme.TextDim);
            else Target.Triangle(c + new Vector2(-2, -4), c + new Vector2(3, 0), c + new Vector2(-2, 4), Theme.TextDim);
        }
        Target.PushClip(r);
        Target.Text(new Vector2(arrow.Right + 2, MathF.Round(r.Y + (r.H - Font.LineHeight) * 0.5f)), label, textColor ?? Theme.Text);
        Target.PopClip();
        return rowClicked;
    }

    /// <summary>A drop-down: shows <paramref name="items"/>[selected]; returns true when the choice changes.</summary>
    public bool Combo(Rect r, string label, ref int selected, IReadOnlyList<string> items)
    {
        uint id = Id(label + "#combo");
        bool open = GetBool(id);
        if (Behavior(r, id, out bool hovered, out _)) open = !open;
        Target.Rect(r, hovered ? Theme.WidgetHover : Theme.Widget);
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
            BeginScroll(Remaining, label + "#popup-scroll");
            for (int i = 0; i < items.Count; i++)
            {
                if (Selectable(Next(), items[i], i == selected, label + "#item" + i))
                {
                    changed = i != selected;
                    selected = i;
                    open = false;
                }
            }
            EndScroll();
            EndPopup();
            if (_input.WasPressed(MouseButton.Left) && !popup.Contains(Mouse) && !r.Contains(Mouse)) open = false;
            if (_input.WasPressed(Key.Escape)) open = false;
        }
        SetBool(id, open);
        return changed;
    }

    // ------------------------------------------------------------------ containers

    /// <summary>Tabs across the top of <paramref name="r"/>; returns the content area below them.</summary>
    public Rect Tabs(Rect r, string id, IReadOnlyList<string> names, ref int selected)
    {
        var strip = r with { H = Theme.TabHeight };
        Target.Rect(strip, Theme.PanelHeader);
        float x = strip.X;
        for (int i = 0; i < names.Count; i++)
        {
            float w = Font.Measure(names[i]) + Theme.Padding * 4;
            var tab = new Rect(x, strip.Y, w, strip.H);
            bool clicked = Behavior(tab, Id(id + "#tab" + i), out bool hovered, out _);
            if (clicked) selected = i;
            Target.Rect(tab, i == selected ? Theme.Panel : hovered ? Theme.WidgetHover : Theme.PanelHeader);
            if (i == selected) Target.Rect(tab with { H = 2 }, Theme.Accent);
            TextIn(tab, names[i], i == selected ? Theme.Text : Theme.TextDim, 0.5f);
            x += w + 1;
        }
        var content = new Rect(r.X, strip.Bottom, r.W, r.H - strip.H);
        Target.Rect(content, Theme.Panel);
        return content;
    }

    /// <summary>A draggable divider. Returns the new position (pixels from <paramref name="min"/>'s side).</summary>
    public float Splitter(Rect r, string id, float position, bool vertical, float min, float max)
    {
        uint wid = Id(id + "#split");
        Behavior(r, wid, out bool hovered, out bool held);
        if (hovered || held) SetCursor(vertical ? CursorShape.ResizeHorizontal : CursorShape.ResizeVertical);
        if (held) position = Math.Clamp(position + (vertical ? MouseDelta.X : MouseDelta.Y), min, max);
        Target.Rect(r, held ? Theme.Accent : hovered ? Theme.WidgetHover : Theme.Border);
        return position;
    }

    /// <summary>Begin a vertically scrolling region; widgets inside lay out as usual. Call <see cref="EndScroll"/>.</summary>
    public void BeginScroll(Rect r, string id)
    {
        uint wid = Id(id + "#scroll");
        float content = GetFloat(wid ^ 0x5bd1e995);
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
        float used = EndArea() + GetFloat(_scrolls.Peek().Id);
        var (id, r) = _scrolls.Pop();
        SetFloat(id ^ 0x5bd1e995, used);
        if (used > r.H)
        {
            float scroll = GetFloat(id);
            float barH = MathF.Max(20, r.H * r.H / used);
            float barY = r.Y + (r.H - barH) * (scroll / (used - r.H));
            Target.Rect(new Rect(r.Right - Theme.ScrollbarWidth + 2, barY, Theme.ScrollbarWidth - 4, barH), Theme.WidgetHover);
        }
    }
}
