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
Ctrl+S saves, Ctrl+Z/Ctrl+Shift+Z undo/redo, and F5 enters or stops play mode. Build and import progress
appears in Tasks; failures appear in Console. The panel regions resize with their splitters; movable docking
and additional Inspector metadata are still pending (see the design status).

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
