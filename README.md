# gamegrl

A modular, AOT-first C# game engine on .NET 10: the Unity 5 editor workflow with data-oriented, allocation-free
APIs. Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## Build and run

Needs the .NET 10 SDK, SDL3, a Vulkan 1.3 driver and `glslc` (shaderc).

```sh
./scripts/ci.sh                                   # build, test (JIT + NativeAOT), publish the sample
dotnet run --project samples/Spinners             # run the sample under the JIT
./artifacts/spinners/Spinners                     # or the NativeAOT build from ci.sh
```

Escape quits the sample.

## Layout

| Path | What |
| --- | --- |
| `src/Gamegrl.Core` | Entities, systems, jobs, the app loop, transforms |
| `src/Gamegrl.Generators` | Roslyn source generator: `[System]` methods into loops and registration |
| `src/Gamegrl.Platform`, `.Sdl3` | Windows and input behind `IPlatform`; the SDL3 backend |
| `src/Gamegrl.Rhi`, `.Vulkan` | The render hardware interface; the Vulkan 1.3 backend |
| `src/Gamegrl.Rendering` | 3D mesh and 2D sprite renderers |
| `tests/Gamegrl.Tests` | Dependency-free tests, also run as a NativeAOT binary |
| `samples/Spinners` | Milestone 1 sample |
