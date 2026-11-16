using System.Numerics;
using Gamegrl.Assets;
using Gamegrl.Platform;
using Gamegrl.Rendering;
using Gamegrl.UI;

namespace Gamegrl.Editor;

internal sealed partial class EditorHost
{
    private int _newKind, _componentChoice;
    private void Hierarchy(Rect area)
    {
        _ui.BeginArea(area);
        _ui.Row(-1, -1);
        if (_ui.Button("Create")) Document.Edit(CreateEntity);
        if (_ui.Button("Delete") && _app.World.IsAlive(Document.Selected))
            Document.Edit(() => { _app.World.Destroy(Document.Selected); Document.Selected = default; });
        _ui.Combo(_ui.Next(), "create-kind", ref _newKind, ["Empty", "Cube", "Camera", "Sprite"]);
        var rest = _ui.Remaining;
        _ui.BeginScroll(rest, "hierarchy-scroll");
        var roots = new List<Entity>();
        foreach (var archetype in _app.World.Archetypes)
            foreach (var e in archetype.Entities)
                if (!_app.World.HasParent(e)) roots.Add(e);
        foreach (var e in roots) EntityRow(e, 0);
        _ui.EndScroll();
        _ui.EndArea();
    }
    private void CreateEntity()
    {
        var world = _app.World;
        var e = world.Spawn(SceneId.New(), new Name { Value = new FixedString64(new[] { "Entity", "Cube", "Camera", "Sprite" }[_newKind]) }, LocalTransform.Identity, new LocalToWorld());
        if (_newKind == 1) world.Add(e, new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = Vector4.One });
        if (_newKind == 2) world.Add(e, Camera.Perspective());
        if (_newKind == 3) world.Add(e, Sprite.Of(BuiltinAssets.White, new Vector2(64)));
        Document.Selected = e;
    }
    private void EntityRow(Entity e, int depth)
    {
        var world = _app.World;
        ulong id = world.Has<SceneId>(e) ? world.Get<SceneId>(e).Value : e.Index;
        bool open = _expanded.GetValueOrDefault(id, true), children = false;
        foreach (var unused in world.Children(e)) { children = true; break; }
        string label = world.Has<Name>(e) ? world.Get<Name>(e).Value.ToString() : "Entity " + e.Index;
        if (_ui.TreeNode(_ui.Next(), id.ToString("x"), label, children, Document.Selected == e, ref open, depth)) Document.Selected = e;
        _expanded[id] = open;
        if (open) foreach (var child in world.Children(e)) EntityRow(child, depth + 1);
    }
    private unsafe void Inspector(Rect area)
    {
        _ui.BeginScroll(area, "inspector-scroll");
        var world = _app.World;
        var e = Document.Selected;
        if (!world.IsAlive(e)) { _ui.Label("Select an entity"); _ui.EndScroll(); return; }
        if (Document.Playing)
        {
            _ui.Label("Stop play mode to edit", true);
            _ui.EndScroll(); return;
        }
        if (_ui.Input.WasPressed(MouseButton.Left) || _ui.Input.WasReleased(MouseButton.Left)) Document.BeginEdit();
        _ui.PushId(world.Has<SceneId>(e) ? world.Get<SceneId>(e).Value : e.Index);
        if (_ui.Button("Make root")) world.SetParent(e, default);
        if (_ui.Button("Parent to next root"))
        {
            foreach (var a in world.Archetypes)
            {
                bool found = false;
                foreach (var parent in a.Entities)
                    if (parent != e && !world.HasParent(parent)) { world.SetParent(e, parent); found = true; break; }
                if (found) break;
            }
        }
        var (archetype, row) = world.Locate(e);
        int remove = -1;
        foreach (int type in archetype.Types)
        {
            var info = ComponentRegistry.Get(type);
            if (type == Component<SceneId>.Id || type == Component<PrefabInstance>.Id || type == Component<LocalToWorld>.Id) continue;
            _ui.PushId(type);
            _ui.Row(-1, 2);
            _ui.Header(info.Name[(info.Name.LastIndexOf('.') + 1)..]);
            if (_ui.Button("×")) remove = type;
            var pointer = archetype.Ptr(archetype.ColumnOf(type), row);
            var checkpoint = _ui.Save();
            try { if (!Registry.DrawComponent(info.StableId, (nint)pointer, _context)) info.Serializer?.Visit(pointer, _inspector); }
            catch (Exception ex)
            {
                _ui.Rewind(checkpoint);
                Report("Inspector " + info.Name, ex);
                _ui.TextIn(_ui.Next(), "Drawer failed: " + ex.Message, _ui.Theme.Error);
            }
            _ui.PopId();
            _ui.Separator();
        }
        if (remove >= 0) world.RemoveRaw(e, remove);
        var available = Enumerable.Range(0, ComponentRegistry.Count).Select(ComponentRegistry.Get)
            .Where(c => !c.Released && c.Serializer is not null && !archetype.Set.Contains(c.Id)
                && c.Id != Component<SceneId>.Id && c.Id != Component<PrefabInstance>.Id).ToArray();
        if (available.Length > 0)
        {
            _componentChoice = Math.Clamp(_componentChoice, 0, available.Length - 1);
            _ui.Combo(_ui.Next(), "component-choice", ref _componentChoice, available.Select(c => c.Name).ToArray());
            if (_ui.Button("Add component"))
            {
                var info = available[_componentChoice];
                var data = new byte[info.Size];
                fixed (byte* p = data) world.AddRaw(e, info.Id, p);
                if (info.Id == Component<LocalTransform>.Id) world.Get<LocalTransform>(e) = LocalTransform.Identity;
                if (info.Id == Component<Camera>.Id) world.Get<Camera>(e) = Camera.Perspective();
                if (info.Id == Component<MeshRenderer>.Id) world.Get<MeshRenderer>(e) = new MeshRenderer { Mesh = BuiltinAssets.Cube, Color = Vector4.One };
                if (info.Id == Component<Sprite>.Id) world.Get<Sprite>(e) = Sprite.Of(BuiltinAssets.White, new Vector2(64));
            }
        }
        _ui.PopId();
        if (_ui.Active == 0 && !_ui.WantsKeyboard || _ui.Input.WasReleased(MouseButton.Left) && !_ui.WantsKeyboard) Document.CommitEdit();
        _ui.EndScroll();
    }
    private void Project(Rect area)
    {
        _ui.BeginScroll(area.Shrink(1), "project-scroll");
        foreach (string path in _assets.Files)
        {
            if (!_ui.Selectable(_ui.Next(), path, path == Files.CurrentPath)) continue;
            if (path.EndsWith(".scene", StringComparison.Ordinal))
            {
                Files.RequestOpen(path);
            }
            else if (path.EndsWith(".prefab", StringComparison.Ordinal))
                Document.Edit(() =>
                {
                    var entities = Scene.LoadText(_app.World, $"gamegrl scene 1\nentity {SceneId.New().Value:x16}\n  prefab asset:{_assets.FindByPath(path):N}\n", _assets.PrefabText, Log.Warn);
                    if (entities.Length > 0) Document.Selected = entities[0];
                });
        }
        _ui.EndScroll();
    }
    private void ConsolePanel(Rect area)
    {
        _ui.BeginArea(area.Shrink(1));
        if (_ui.Button("Clear")) Log.Clear();
        _ui.BeginScroll(_ui.Remaining, "console-scroll");
        foreach (var entry in Log.Snapshot())
        {
            var color = entry.Level == LogLevel.Error ? _ui.Theme.Error : entry.Level == LogLevel.Warning ? _ui.Theme.Warning : _ui.Theme.Text;
            foreach (string line in entry.Message.Split('\n')) _ui.TextIn(_ui.Next(), line.TrimEnd('\r'), color);
        }
        _ui.EndScroll(); _ui.EndArea();
    }
    private void Tasks(Rect area)
    {
        _ui.BeginArea(area.Shrink(1));
        if (_ui.Button("Cancel tasks")) Jobs.CancelAll();
        foreach (string job in Jobs.Running) _ui.Label(job);
        if (!Jobs.Busy) _ui.Label("No running tasks", true);
        _ui.EndArea();
    }
    private int _dragAxis = -1;
    private Vector2 _axisScreen;
    private float _axisScale;
    private void SceneInteraction(Input input)
    {
        if (_sceneArea.W < 1 || _sceneArea.H < 1) return;
        _camera.Update(input, _sceneArea, new Vector2(CellWidth, CellHeight));
        var world = _app.World;
        var selected = Document.Selected;
        if (!world.IsAlive(selected)) selected = default;
        if (input.WasPressed(Key.F) && !_ui.WantsKeyboard && !selected.IsNull && world.Has<LocalToWorld>(selected))
        { _camera.Target = world.Get<LocalToWorld>(selected).Position; _camera.Distance = 6; }
        var matrix = _camera.Matrix((_sceneArea.W * CellWidth) / (_sceneArea.H * CellHeight));
        for (int i = -20; i <= 20; i++)
        {
            var color = i == 0 ? new Vector4(0.3f, 0.35f, 0.4f, 1) : new Vector4(0.16f, 0.18f, 0.22f, 1);
            _render.Debug.Line(new Vector3(i, 0, -20), new Vector3(i, 0, 20), color);
            _render.Debug.Line(new Vector3(-20, 0, i), new Vector3(20, 0, i), color);
        }
        bool overGizmo = false;
        if (!selected.IsNull && world.Has<LocalToWorld>(selected) && world.Has<LocalTransform>(selected))
        {
            var transform = world.Get<LocalToWorld>(selected).Value;
            var position = transform.Translation;
            if (world.Has<MeshRenderer>(selected) && _render.TryGetBounds(world.Get<MeshRenderer>(selected).Mesh, out var min, out var max))
                _render.Debug.Box(transform, min, max, new Vector4(1, 0.65f, 0.15f, 1));
            float length = Math.Max(0.1f, Vector3.Distance(_camera.Position, position) * 0.12f);
            if (SceneCamera.Project(position, matrix, _sceneArea, out var center) && !Document.Playing)
            {
                _ui.Draw.PushClip(_sceneArea);
                for (int axis = 0; axis < 3; axis++)
                {
                    var direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    if (!SceneCamera.Project(position + direction * length, matrix, _sceneArea, out var end)) continue;
                    var segment = end - center;
                    float t = Math.Clamp(Vector2.Dot(input.MousePosition - center, segment) / Math.Max(segment.LengthSquared(), 0.001f), 0, 1);
                    bool hit = Vector2.Distance(input.MousePosition, center + t * segment) < 0.6f && _ui.IsHovered(_sceneArea);
                    overGizmo |= hit;
                    var color = axis == 0 ? _ui.Theme.AxisX : axis == 1 ? _ui.Theme.AxisY : _ui.Theme.AxisZ;
                    _ui.Draw.Line(center, end, hit || _dragAxis == axis ? Color32.White : color, 1);
                    _ui.Draw.Rect(new Rect(end.X, end.Y, 1, 1), color);
                    if (hit && input.WasPressed(MouseButton.Left) && _dragAxis < 0)
                    {
                        Document.BeginEdit(); _dragAxis = axis;
                        _axisScreen = Vector2.Normalize(segment); _axisScale = length / Math.Max(1, segment.Length());
                        _ui.Active = _ui.Id("translate");
                    }
                }
                _ui.Draw.PopClip();
            }
            if (_dragAxis >= 0)
            {
                var direction = _dragAxis == 0 ? Vector3.UnitX : _dragAxis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                var delta = direction * Vector2.Dot(input.MouseDelta, _axisScreen) * _axisScale;
                if (world.HasParent(selected) && world.Has<LocalToWorld>(world.GetParent(selected)))
                {
                    Matrix4x4.Invert(world.Get<LocalToWorld>(world.GetParent(selected)).Value, out var inverse);
                    delta = Vector3.TransformNormal(delta, inverse);
                }
                world.Get<LocalTransform>(selected).Position += delta;
                if (!input.IsDown(MouseButton.Left)) { _dragAxis = -1; Document.CommitEdit(); }
            }
        }
        if (!overGizmo && _dragAxis < 0 && _ui.IsHovered(_sceneArea) && input.WasPressed(MouseButton.Left) && _ui.Active == 0)
        {
            var ray = SceneCamera.Ray(input.MousePosition, matrix, _sceneArea);
            float nearest = float.PositiveInfinity;
            Entity picked = default;
            foreach (var a in world.Archetypes)
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
            Document.Selected = picked;
        }
    }
}
