using System.Numerics;
using System.Text;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.UI;
using Gamegrl.UI.Fonts;

namespace Gamegrl.Editor;

/// <summary>
/// The Scene view's interaction and its in-view interface. On the desktop the move gizmo, the selection's name tag,
/// the orientation axes and the view toolbar are drawn with the engine's pixel UI (<see cref="Ui"/>) straight into the
/// scene texture, the same library games use, so the editor exercises it every frame. The terminal host has no pixel
/// resolution to spare; there the gizmo falls back to cell lines and the rest is left out.
/// </summary>
internal sealed partial class EditorHost
{
    private readonly Ui? _overlay;
    private readonly UiRenderer? _overlayRenderer;
    private readonly Input _overlayInput = new();
    private bool _overlayBuilt, _showGrid = true;
    private int _dragAxis = -1;
    private Vector2 _axisScreen;
    private float _axisScale;

    private static (Ui, UiRenderer) CreateOverlay(RenderContext render, string fontPath)
    {
        var font = FontAtlas.Bake(new TrueTypeFont(File.ReadAllBytes(FontAtlas.FindSystemFont() ?? fontPath)), 13);
        return (new Ui(font), new UiRenderer(render.Device, render.Swapchain.Format, font));
    }

    /// <summary>The overlay as drawn this frame, for tests and the smoke run.</summary>
    internal Ui? SceneOverlayUi => _overlay;

    private void RenderOverlay(Rhi.Frame frame)
    {
        if (_overlay is null || !_overlayBuilt || _sceneColor is null) return;
        _overlayBuilt = false;
        _overlayRenderer!.RenderOnto(frame, _sceneColor, _overlay.Draw);
    }

    private static Vector3 Axis(int axis) => axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;

    private static readonly ProfileMarker OverlayMarker = ProfileMarker.Get("Editor.SceneOverlay", ProfileCategory.Editor);

    private void SceneInteraction(Input input)
    {
        using var zone = OverlayMarker.Scope();
        if (_sceneArea.W < 1 || _sceneArea.H < 1 || _sceneColor is null) return;
        var world = _app.World;
        var selected = Document.Selected;
        if (!world.IsAlive(selected)) selected = default;
        bool hasTransform = !selected.IsNull && world.Has<LocalToWorld>(selected) && world.Has<LocalTransform>(selected);
        bool sceneHovered = _ui.IsHovered(_sceneArea);

        // The view's own space: scene-texture pixels on the desktop, cells in the terminal.
        var ui = _overlay;
        Rect view = _sceneArea;
        Vector2 mouse = input.MousePosition;
        if (ui is not null)
        {
            var origin = PixelRect(_sceneArea).Min;
            // The cell input is in cells; the overlay wants the window's pixel input, relative to the view.
            _overlayInput.RouteFrom(world.Resource<Input>(), sceneHovered || ui.Active != 0 || _dragAxis >= 0, origin);
            ui.Begin(_overlayInput, new Vector2(_sceneColor.Width, _sceneColor.Height), _clock);
            view = ui.Screen;
            mouse = _overlayInput.MousePosition;
            _overlayBuilt = true;
            Toolbar(ui, hasTransform ? selected : default);
        }
        var matrix = _camera.Matrix(_sceneColor.Width / (float)_sceneColor.Height);
        if (ui is not null) Orientation(ui);
        bool overlayMouse = ui is { WantsMouse: true };
        if (!overlayMouse) _camera.Update(input, _sceneArea, new Vector2(CellWidth, CellHeight));
        if (input.WasPressed(Key.F) && !_ui.WantsKeyboard && hasTransform) FrameSelection(selected);

        if (_showGrid)
            for (int i = -20; i <= 20; i++)
            {
                var color = i == 0 ? new Vector4(0.3f, 0.35f, 0.4f, 1) : new Vector4(0.16f, 0.18f, 0.22f, 1);
                _render.Debug.Line(new Vector3(i, 0, -20), new Vector3(i, 0, 20), color);
                _render.Debug.Line(new Vector3(-20, 0, i), new Vector3(20, 0, i), color);
            }

        bool overGizmo = false;
        if (hasTransform)
        {
            var transform = world.Get<LocalToWorld>(selected).Value;
            var position = transform.Translation;
            float top = 0.6f;
            if (world.Has<MeshRenderer>(selected) && _render.TryGetBounds(world.Get<MeshRenderer>(selected).Mesh, out var min, out var max))
            {
                _render.Debug.Box(transform, min, max, new Vector4(1, 0.37f, 0.64f, 1));
                top = max.Y * transform.M22 + 0.15f;
            }
            if (ui is not null && world.Has<Name>(selected) && SceneCamera.Project(position + Vector3.UnitY * top, matrix, view, out var tag))
            {
                Span<char> name = stackalloc char[64];
                int length = Encoding.UTF8.GetChars(world.Get<Name>(selected).Value.Utf8, name);
                ui.TextAt(tag - new Vector2(0, 4), name[..length], ui.Theme.Accent, new Vector2(0.5f, 1));
            }
            float length3d = Math.Max(0.1f, Vector3.Distance(_camera.Position, position) * 0.12f);
            if (SceneCamera.Project(position, matrix, view, out var center) && !Document.Playing)
                overGizmo = MoveGizmo(input, ui, view, mouse, matrix, selected, position, center, length3d, sceneHovered && !overlayMouse);
            if (_dragAxis >= 0) DragGizmo(input, world, selected, ui is null ? input.MouseDelta : _overlayInput.MouseDelta);
        }
        if (!overGizmo && !overlayMouse && _dragAxis < 0 && sceneHovered && input.WasPressed(MouseButton.Left) && _ui.Active == 0)
            Document.Selected = Pick(SceneCamera.Ray(mouse, matrix, view));

        if (ui is not null)
        {
            ui.TextAt(new Vector2(8, view.H - 8), ui.Format($"{world.EntityCount} entities"), ui.Theme.TextDim, new Vector2(0, 1));
            ui.End();
        }
    }

    private bool MoveGizmo(Input input, Ui? ui, Rect view, Vector2 mouse, Matrix4x4 matrix, Entity selected, Vector3 position, Vector2 center, float length, bool canGrab)
    {
        bool over = false;
        float radius = ui is null ? 0.6f : 6;
        if (ui is null) _ui.Draw.PushClip(_sceneArea);
        for (int axis = 0; axis < 3; axis++)
        {
            if (!SceneCamera.Project(position + Axis(axis) * length, matrix, view, out var end)) continue;
            var segment = end - center;
            float t = Math.Clamp(Vector2.Dot(mouse - center, segment) / Math.Max(segment.LengthSquared(), 0.001f), 0, 1);
            bool hit = canGrab && Vector2.Distance(mouse, center + t * segment) < radius;
            over |= hit;
            var color = axis == 0 ? _ui.Theme.AxisX : axis == 1 ? _ui.Theme.AxisY : _ui.Theme.AxisZ;
            bool lit = hit || _dragAxis == axis;
            if (ui is null)
            {
                _ui.Draw.Line(center, end, lit ? Color32.White : color, 1);
                _ui.Draw.Rect(new Rect(end.X, end.Y, 1, 1), color);
            }
            else
            {
                var draw = ui.Draw;
                var tip = lit ? Color32.White : color;
                draw.Line(center, end, tip, lit ? 3 : 2);
                if (segment.LengthSquared() > 16)
                {
                    var dir = Vector2.Normalize(segment);
                    var side = new Vector2(-dir.Y, dir.X) * 5;
                    draw.Triangle(end + dir * 10, end + side, end - side, tip);
                    ui.TextAt(end + dir * 20, axis == 0 ? "X" : axis == 1 ? "Y" : "Z", color, new Vector2(0.5f, 0.5f));
                }
            }
            if (hit && input.WasPressed(MouseButton.Left) && _dragAxis < 0)
            {
                Document.BeginEntityEdit(selected); _dragAxis = axis;
                _axisScreen = Vector2.Normalize(segment); _axisScale = length / Math.Max(1, segment.Length());
                _ui.Active = _ui.Id("translate");
            }
        }
        if (ui is null) _ui.Draw.PopClip();
        else ui.Draw.Rect(new Rect(center.X - 2, center.Y - 2, 4, 4), ui.Theme.Text);
        return over;
    }

    private void DragGizmo(Input input, World world, Entity selected, Vector2 mouseDelta)
    {
        var delta = Axis(_dragAxis) * Vector2.Dot(mouseDelta, _axisScreen) * _axisScale;
        if (world.HasParent(selected) && world.Has<LocalToWorld>(world.GetParent(selected)))
        {
            Matrix4x4.Invert(world.Get<LocalToWorld>(world.GetParent(selected)).Value, out var inverse);
            delta = Vector3.TransformNormal(delta, inverse);
        }
        world.Get<LocalTransform>(selected).Position += delta;
        if (!input.IsDown(MouseButton.Left)) { _dragAxis = -1; Document.CommitEdit(); }
    }

    private Entity Pick((Vector3 Origin, Vector3 Direction) ray)
    {
        float nearest = float.PositiveInfinity;
        Entity picked = default;
        foreach (var a in _app.World.Archetypes)
        {
            if (!a.Has<MeshRenderer>() || !a.Has<LocalToWorld>()) continue;
            var meshes = a.GetSpan<MeshRenderer>(); var transforms = a.GetSpan<LocalToWorld>();
            for (int i = 0; i < a.Count; i++)
            {
                if (!_render.TryGetBounds(meshes[i].Mesh, out var min, out var max) || !Matrix4x4.Invert(transforms[i].Value, out var inverse)) continue;
                if (SceneCamera.HitBox(Vector3.Transform(ray.Origin, inverse), Vector3.TransformNormal(ray.Direction, inverse), min, max, out float distance) && distance < nearest)
                { nearest = distance; picked = a.Entities[i]; }
            }
        }
        return picked;
    }

    private void FrameSelection(Entity selected)
    {
        _camera.Target = _app.World.Get<LocalToWorld>(selected).Position;
        _camera.Distance = 6;
    }

    /// <summary>View buttons along the top-left of the Scene view.</summary>
    private void Toolbar(Ui ui, Entity frameable)
    {
        float x = 8;
        if (ui.Button(new Rect(x, 8, 46, 20), "Grid", _showGrid)) _showGrid = !_showGrid;
        x += 50;
        if (!frameable.IsNull && ui.Button(new Rect(x, 8, 54, 20), "Frame")) FrameSelection(frameable);
    }

    /// <summary>World axes in the top-right corner; click a label to look along that axis.</summary>
    private void Orientation(Ui ui)
    {
        var view = Matrix4x4.CreateLookAt(_camera.Position, _camera.Target, Vector3.UnitY);
        var center = new Vector2(ui.Screen.W - 40, 40);
        var draw = ui.Draw;
        draw.Rect(new Rect(center.X - 34, center.Y - 34, 68, 68), ui.Theme.Panel.WithAlpha(110));
        for (int axis = 0; axis < 3; axis++)
        {
            var d = Vector3.TransformNormal(Axis(axis), view);
            var end = center + new Vector2(d.X, -d.Y) * 22;
            var color = axis == 0 ? ui.Theme.AxisX : axis == 1 ? ui.Theme.AxisY : ui.Theme.AxisZ;
            draw.Rect(new Rect(center.X - d.X * 22 - 2, center.Y + d.Y * 22 - 2, 4, 4), color.WithAlpha(90));
            draw.Line(center, end, color, 2);
            var label = new Rect(end.X - 7, end.Y - 8, 14, 16);
            bool clicked = ui.Behavior(label, ui.Id(axis + 1), out bool hovered, out _);
            ui.TextAt(end, axis == 0 ? "X" : axis == 1 ? "Y" : "Z", hovered ? Color32.White : color, new Vector2(0.5f, 0.5f));
            if (!clicked) continue;
            // Look from the positive end of the axis toward the target.
            (_camera.Yaw, _camera.Pitch) = axis switch { 0 => (MathF.PI / 2, 0f), 1 => (_camera.Yaw, 1.5f), _ => (0f, 0f) };
        }
    }
}
