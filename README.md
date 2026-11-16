# gamegrl

A modular, AOT-first C# game engine on .NET 10: the Unity 5 editor workflow with data-oriented, allocation-free
APIs. Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## Build and run

Needs the .NET 10 SDK, SDL3, a Vulkan 1.3 driver and `glslc` (shaderc).

```sh
./scripts/ci.sh                                   # build, test (JIT + NativeAOT), publish the sample
dotnet run --project samples/Spinners/Build -- run      # dev: run from the asset database, edits hot-reload
dotnet run --project samples/Spinners/Build -- author   # regenerate the sample's prefabs and main.scene
./artifacts/spinners/Spinners                           # the NativeAOT player + spinners.ggpak from ci.sh
```

Escape quits the sample.

## Layout

| Path | What |
| --- | --- |
| `src/Gamegrl.Core` | Entities, systems, jobs, the app loop, transforms |
| `src/Gamegrl.Generators` | Roslyn source generators: `[System]` loops and registration, component serializers |
| `src/Gamegrl.Platform`, `.Sdl3` | Windows and input behind `IPlatform`; the SDL3 backend |
| `src/Gamegrl.Rhi`, `.Vulkan` | The render hardware interface; the Vulkan 1.3 backend |
| `src/Gamegrl.Rendering` | 3D mesh and 2D sprite renderers |
| `src/Gamegrl.Assets` | Runtime asset formats, the `.ggpak` archive, `IAssetSource` |
| `src/Gamegrl.AssetPipeline` | Editor-only: `.meta` files, importers (PNG, glTF, scenes, prefabs), cache, packing, watching |
| `tests/Gamegrl.Tests` | Dependency-free tests, also run as a NativeAOT binary |
| `samples/Spinners` | `Game` (library), `Player` (NativeAOT), `Build` (dev tool), `Assets/` |
