# gamegrl

A modular, AOT-first C# game engine on .NET 10: the Unity 5 editor workflow with data-oriented, allocation-free
APIs. Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## Build and run

Needs the .NET 10 SDK, Vulkan 1.3 (hardware or software), and `glslc` (shaderc). The editor references the sibling `../ansidotnet` repository. SDL3 and a TrueType monospace font are needed for desktop mode; terminal mode needs neither.

```sh
./scripts/ci.sh                                   # build, test (JIT + NativeAOT), benchmarks, publish the sample
dotnet run --project src/Gamegrl.Editor -- samples/Spinners # desktop cell editor
dotnet run --project src/Gamegrl.Editor -- samples/Spinners --terminal # real TTY / SSH PTY
dotnet run --project samples/Spinners/Build -- run      # dev: run from the asset database, edits hot-reload
dotnet run --project samples/Spinners/Build -- author   # regenerate the sample's prefabs and main.scene
./artifacts/spinners/Spinners                           # the NativeAOT player + spinners.ggpak from ci.sh
```

Escape quits the sample.

The desktop editor uses DejaVu Sans Mono on Linux; set `GAMEGRL_FONT` to another TrueType font path if needed.
Right-drag orbits the Scene camera, middle-drag pans, the wheel zooms, and F frames the selection.
Click a mesh to select it; drag its colored axis handles to move it. Project prefab rows instantiate prefabs.
Ctrl+P opens the command palette: type part of a command name, choose with the arrows, Enter runs it. Every editor
action is there, with its shortcut. Ctrl+N creates a scene, Ctrl+S saves, Ctrl+Shift+S saves under a new path,
Ctrl+Z/Ctrl+Shift+Z undo/redo, Del deletes the selection, Ctrl+B hides the sidebar, F5 enters or stops play mode and
F6 pauses it. A game system that throws pauses play and logs its stack trace instead of closing the editor.
New/Open/Close offer Save, Discard and Cancel for unsaved edits.

The top line shows the scene (a pink dot means unsaved), play controls and background work. Hierarchy and Project
are collapsible sections in the sidebar; click a header to fold it. The other panels are boxed panes with their tabs in
the top border. Drag any border to resize, drag tabs or section headers between regions, and scroll a crowded tab strip
with the mouse wheel. Build and import progress appears in Tasks; Console filters by level.
Inspector fields support `[Header("Section")]`, `[Tooltip("Help text")]` and numeric `[Range(0, 720)]`
attributes. These are generated without reflection; ranges constrain edits while preserving loaded values.
Add component uses a component's public static `Default` (field or property) when it declares one, else `new T()`.

```sh
dotnet run --project tests/Gamegrl.Editor.Tests -- --graphics # optional desktop smoke test, uses temporary assets
```

### Profiling

The editor always profiles. Open the Profiler tab (or "Profiler: Show" in the palette) for a frame graph, the split of
each frame between game, engine, editor, waiting and GPU, and the call tree with time, self time and calls per frame.
Click a frame in the graph to pause on it; F9 pauses and resumes; "Profiler: Export trace" writes a Chrome trace to
`Library/Profiles` for [Perfetto](https://ui.perfetto.dev). Time your own code with a marker:

```csharp
static readonly ProfileMarker Pathing = ProfileMarker.Get("AI.Pathing");
using (Pathing.Scope()) { /* ... */ }
```

Players record with `GAMEGRL_PROFILE=1` and write a trace on exit with `GAMEGRL_PROFILE_OUT=trace.json`.

Benchmarks gate CI against this machine's baseline in `perf/baselines/`:

```sh
./artifacts/bench-aot/Gamegrl.Bench --tree          # engine scenarios (NativeAOT, built by ci.sh)
dotnet run --project tests/Gamegrl.Editor.Tests -c Release -- --bench --tree # editor scenarios
# add --update to record a new baseline after an intended change, --quick for a short run
```

## Layout

| Path | What |
| --- | --- |
| `src/Gamegrl.Core` | Entities, systems, jobs, the app loop, transforms, the profiler |
| `src/Gamegrl.Generators` | Roslyn source generators: `[System]` loops and registration, component serializers |
| `src/Gamegrl.Platform`, `.Sdl3` | Windows and input behind `IPlatform`; the SDL3 backend |
| `src/Gamegrl.Rhi`, `.Vulkan` | The render hardware interface; the Vulkan 1.3 backend |
| `src/Gamegrl.UI` | The pixel UI games ship (`UiModule`): TrueType text, windows and widgets, rendering; also the editor's in-view overlays |
| `src/Gamegrl.Editor` | Shared AnsiDotNet cell interface, desktop/TTY hosts, headless viewports, scene editing, jobs and collectible game code |
| `src/Gamegrl.Rendering` | 3D mesh and 2D sprite renderers |
| `src/Gamegrl.Assets` | Runtime asset formats, the `.ggpak` archive, `IAssetSource` |
| `src/Gamegrl.AssetPipeline` | Editor-only: `.meta` files, importers (PNG, glTF, scenes, prefabs), cache, packing, watching |
| `tests/Gamegrl.Tests` | Dependency-free tests, also run as a NativeAOT binary |
| `tests/Gamegrl.Editor.Tests` | Headless editor tests, optional SDL/Vulkan interaction smoke, editor benchmarks (`--bench`) |
| `tests/Gamegrl.Bench` | Engine benchmark scenarios under the profiler (NativeAOT); baselines in `perf/baselines` |
| `samples/Spinners` | `Game` (library), `Player` (NativeAOT), `Build` (dev tool), `Assets/` |

Editor-only extensions implement `IEditorExtension.Register(EditorRegistry)`. Register panels with
`AddPanel`, command palette entries with `AddCommand`, and component Inspectors with `AddDrawer<T>`. Use
`EditorContext.Edit` for undoable structural changes, `Document.BeginEntityEdit(entity)` before editing one
entity's component values (it records only the bytes that change), and `EditorContext.Jobs` for background work.
A panel, drawer or command that throws is logged once and replaced by an inline error. Keep the extension
in a separate JIT assembly referencing `Gamegrl.Editor`, then pass `--extension path/to/extension.dll`.
It loads and unloads with game code, sharing game component identities. Build the extension assembly
before launching or reloading it; player projects do not reference editor extensions.

Game-view input activates on click during play; clicking editor panels or opening a dialog releases it.
Gameplay reads the view-local `Input` resource or named bindings through `ActionMap`; F5 remains an editor
shortcut. Keyboard/mouse bindings are available now; other device bindings follow later.

Module resources registered through `SetResource(value)` checkpoint automatically when unmanaged. For mutable
objects use `SetResource(value, clone)` with a deep-copy function, or `SetResourceFactory(() => new State())`
for a fresh object per play session. Stop restores edit state and removes play-only resources. Game-code reload
resets resources to new-code defaults while preserving serialized entities. Modules manage disposable resource
handles through their lifecycle callbacks.

Editor builds live in leased session workspaces under the temporary directory. Replaced and failed builds are
removed in the background, with the current build retained for rollback. Owned stale sessions are reclaimed
on later startup/build; unrelated directories and live editor sessions are left alone.

The interface is a shared character grid: desktop mode renders sharp glyphs and full-resolution viewport
textures, while terminal mode sends diffed ANSI cells with half-block color previews. Standard SSH with a PTY
runs `--terminal` directly. `--stdio --columns 120 --rows 40` provides the same editor through stdin/stdout
streams for an external transport; that stream mode uses the supplied size. `--colors truecolor|256|16|none`
and `--ascii` select fallbacks. Terminal preview output keeps only the latest pending frame, so a slow
connection does not build a video queue or stall editor jobs. Vulkan is required for scene rendering, but a
display server is not; a software Vulkan driver also works.

Tab or arrow keys move focus, Enter/Space activate controls, Escape cancels fields/dialogs, and mouse reporting
supports selection, scrolling, docking and splitters. Ctrl+Q requests a clean close. Classic terminal key events
are pulses; desktop mode retains precise held-key gameplay. Unsaved scenes on terminal EOF or Unix hangup go to
`Library/recovery` and can be reopened with the "File: Recover disconnected scene" command. Terminal clipboard
copy/paste uses an internal session buffer plus bracketed paste; it does not read the operator's OS clipboard.

`EditorContext.Ui` is `CellUi`; extension rectangles and widget sizes are character cells. Fonts and ANSI
transports stay outside extension code.

Games draw HUDs, menus and debug windows with `Gamegrl.UI`. Add `UiModule` from the game's module (after
rendering) and call the `Ui` resource from any system; it draws over the window in the player and over the Game
view in the editor:

```csharp
builder.AddModule(new UiModule(new UiOptions { Font = "Assets/fonts/ui.ttf" }));

[System(Phase.Update)]
static void DebugWindow([Res] in Ui ui, [Res] in Time time, [Res] ref SpinTuning tuning)
{
    if (ui.BeginWindow("Spinners", new Rect(12, 12, 230, 120)))
    {
        ui.Label($"{1 / time.Delta:0} fps");   // formatted into frame scratch, no allocation
        ui.Slider(ui.Property("Speed"), "speed", ref tuning.Speed, 0, 4);
    }
    ui.EndWindow();
}
```

```sh
dotnet run --project tests/Gamegrl.Editor.Tests -- --terminal-smoke # headless Vulkan + stream terminal end to end
```

For the full Linux transport check after a Release build, run `python3 scripts/terminal-cli-smoke.py`.
It uses temporary sample copies and verifies real pipes, PTY resize, EOF/hangup recovery and terminal mode
restoration. Set `GAMEGRL_TERMINAL_SMOKE=1` when running `scripts/ci.sh` to also run the headless Vulkan editor
workflow. Terminal preview downsampling preserves a separate logical screen size for sprites and game mouse
coordinates, so reducing output resolution does not enlarge sprites or change input units.
