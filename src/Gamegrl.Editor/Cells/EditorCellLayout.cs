using AnsiDotNet;
using AnsiDotNet.Layout;
namespace Gamegrl.Editor;

/// <summary>
/// AnsiDotNet's dock solver owns the editor's rectangles in both presentations: a one-row top line, an unboxed
/// sidebar with a one-column rule, and boxed panes (right, bottom, center) that sit edge to edge with no gutters.
/// </summary>
internal sealed class EditorCellLayout
{
    private readonly Stage _stage;
    private readonly Node _top, _left, _rule, _right, _bottom, _center;
    public EditorCellLayout()
    {
        _top = new Node("top") { Dock = Edge.Top, Height = 1 };
        _left = new Node("left") { Dock = Edge.Left };
        _rule = new Node("rule") { Dock = Edge.Left, Width = 1 };
        _right = new Node("right") { Dock = Edge.Right };
        _bottom = new Node("bottom") { Dock = Edge.Bottom };
        _center = new Node("center");
        var body = new Node { Layout = LayoutKind.Dock }.Add(_left, _rule, _right, new Node { Layout = LayoutKind.Dock }.Add(_bottom, _center));
        _stage = new Stage(new Node { Layout = LayoutKind.Dock }.Add(_top, body));
    }
    /// <summary>A <paramref name="left"/> of zero hides the sidebar and its rule.</summary>
    public void Arrange(int width, int height, int left, int right, int bottom)
    {
        _left.Width = left; _rule.Width = left > 0 ? 1 : 0; _right.Width = right; _bottom.Height = bottom;
        _stage.Layout(new Rect(0, 0, width, height));
    }
    public Rect Top => _top.Bounds;
    /// <summary>The column between the sidebar and the panes; it is also the sidebar's splitter.</summary>
    public Rect Rule => _rule.Bounds;
    public Rect Region(DockRegion region) => (region switch { DockRegion.Left => _left, DockRegion.Right => _right, DockRegion.Bottom => _bottom, _ => _center }).Bounds;
}
