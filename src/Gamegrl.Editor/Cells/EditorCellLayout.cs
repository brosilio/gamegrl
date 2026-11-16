using AnsiDotNet;
using AnsiDotNet.Layout;
namespace Gamegrl.Editor;

/// <summary>AnsiDotNet's dock solver owns the panel and viewport rectangles in both presentations.</summary>
internal sealed class EditorCellLayout
{
    private readonly Stage _stage;
    private readonly Node _left, _right, _bottom, _center;
    public EditorCellLayout()
    {
        _left = new Node("left") { Dock = Edge.Left };
        _right = new Node("right") { Dock = Edge.Right };
        _bottom = new Node("bottom") { Dock = Edge.Bottom };
        _center = new Node("center");
        var body = new Node { Layout = LayoutKind.Dock }.Add(_left, new Node { Dock = Edge.Left, Width = 1 }, _right,
            new Node { Dock = Edge.Right, Width = 1 }, new Node { Layout = LayoutKind.Dock }.Add(_bottom, new Node { Dock = Edge.Bottom, Height = 1 }, _center));
        _stage = new Stage(new Node { Layout = LayoutKind.Dock }.Add(new Node { Dock = Edge.Top, Height = 2 }, new Node { Dock = Edge.Bottom, Height = 1 }, body));
    }
    public void Arrange(int width, int height, int left, int right, int bottom)
    {
        _left.Width = left; _right.Width = right; _bottom.Height = bottom;
        _stage.Layout(new Rect(0, 0, width, height));
    }
    public Rect Region(DockRegion region) => (region switch { DockRegion.Left => _left, DockRegion.Right => _right, DockRegion.Bottom => _bottom, _ => _center }).Bounds;
}
