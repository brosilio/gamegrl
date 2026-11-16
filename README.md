# gamegrl

A modular, AOT-first C# game engine on .NET 10: the Unity 5 editor workflow with data-oriented, allocation-free
APIs. Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## Build and run

Needs the .NET 10 SDK, SDL3, a Vulkan 1.3 driver and `glslc` (shaderc).

```sh
./scripts/ci.sh                                   # build, test (JIT + NativeAOT), publish the sample
dotnet run --project src/Gamegrl.Editor -- samples/Spinners # JIT editor
dotnet run --project samples/Spinners/Build -- run      # dev: run from the asset database, edits hot-reload
dotnet run --project samples/Spinners/Build -- author   # regenerate the sample's prefabs and main.scene
./artifacts/spinners/Spinners                           # the NativeAOT player + spinners.ggpak from ci.sh
```

Escape quits the sample.

The editor uses DejaVu Sans on Linux; set `GAMEGRL_FONT` to another TrueType font path if needed.
Right-drag orbits the Scene camera, middle-drag pans, the wheel zooms, and F frames the selection.
Click a mesh to select it; drag its colored axis handles to move it. Project prefab rows instantiate prefabs.
Ctrl+N creates a scene, Ctrl+S saves, Ctrl+Shift+S saves under a new path, Ctrl+Z/Ctrl+Shift+Z undo/redo,
and F5 enters or stops play mode. New/Open/Close offer Save, Discard and Cancel for unsaved edits. Build and import progress
appears in Tasks; failures appear in Console. Resize panel regions with splitters, or drag tabs between regions and reorder them. Scroll a crowded tab
strip with the mouse wheel. Menu includes a layout reset command.
Inspector fields support `[Header("Section")]`, `[Tooltip("Help text")]` and numeric `[Range(0, 720)]`
attributes. These are generated without reflection; ranges constrain edits while preserving loaded values.

```sh
dotnet run --project tests/Gamegrl.Editor.Tests -- --graphics # optional desktop smoke test, uses temporary assets
```

## Layout

| Path | What |
| --- | --- |
| `src/Gamegrl.Core` | Entities, systems, jobs, the app loop, transforms |
| `src/Gamegrl.Generators` | Roslyn source generators: `[System]` loops and registration, component serializers |
| `src/Gamegrl.Platform`, `.Sdl3` | Windows and input behind `IPlatform`; the SDL3 backend |
| `src/Gamegrl.Rhi`, `.Vulkan` | The render hardware interface; the Vulkan 1.3 backend |
| `src/Gamegrl.UI` | TrueType text, immediate-mode widgets and UI rendering |
| `src/Gamegrl.Editor` | JIT editor host, scene editing, jobs and collectible game code |
| `src/Gamegrl.Rendering` | 3D mesh and 2D sprite renderers |
| `src/Gamegrl.Assets` | Runtime asset formats, the `.ggpak` archive, `IAssetSource` |
| `src/Gamegrl.AssetPipeline` | Editor-only: `.meta` files, importers (PNG, glTF, scenes, prefabs), cache, packing, watching |
| `tests/Gamegrl.Tests` | Dependency-free tests, also run as a NativeAOT binary |
| `tests/Gamegrl.Editor.Tests` | Headless editor tests and optional SDL/Vulkan interaction smoke |
| `samples/Spinners` | `Game` (library), `Player` (NativeAOT), `Build` (dev tool), `Assets/` |

Editor-only extensions implement `IEditorExtension.Register(EditorRegistry)`. Register panels with
`AddPanel`, commands with `AddMenu`, and component Inspectors with `AddDrawer<T>`. Use
`EditorContext.Edit` for undoable changes and `EditorContext.Jobs` for background work. Keep the extension
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
