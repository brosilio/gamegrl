# gamegrl — guide for agents (and people)

gamegrl is a modular, NativeAOT-first C# game engine on .NET 10. Read `docs/DESIGN.md` before changing anything: it is
the source of truth for decisions and their reasons. When you make a decision (a new approach, a changed API, a
rejected alternative), add a row to its decision log and update its Status table.

## Setup

```sh
git clone --recursive <repo url>        # or: git submodule update --init
./scripts/ci.sh                         # build, tests under the JIT and NativeAOT, determinism, audio smoke, sample
```

Needs the .NET 10 SDK, Vulkan 1.3 (a software driver works), `glslc`, and for desktop runs SDL3. Linux is the
platform developed and tested on. `external/ansidotnet` is a submodule with its own build settings; `external/`
deliberately stops MSBuild from importing the engine's `Directory.Build.*` files.

## Ground rules

- **NativeAOT always works.** Every project builds with `IsAotCompatible` and `TreatWarningsAsErrors`. No reflection,
  `Reflection.Emit`, `dynamic` or runtime loading in engine or game code; the source generators do that work.
  `scripts/ci.sh` runs the tests as a NativeAOT binary: "it builds" is not "it works under AOT".
- **Few dependencies.** BCL only, plus SDL3 (behind `IPlatform`), the platform audio APIs (ALSA's `libasound` on
  Linux), OpenSSL (QUIC), the Khronos OpenXR loader (XR module only), an editor-only shader compiler, and the
  `external/ansidotnet` submodule (editor only). No NuGet packages in engine code. Tests are dependency-free
  (`tests/Gamegrl.Tests/Harness.cs`).
- **Best implementation over backwards compatibility.** Nothing is released: change any API when it makes the code
  clearly better, and update its callers in the same change.
- **Efficiency is the default path.** Hot paths are written like C: spans, pointers into component columns, pooled or
  ring-allocated memory, no LINQ and no per-frame allocations. Components are unmanaged structs.
- **Structural changes never happen during iteration**; use the world's `CommandBuffer`.
- **Determinism.** Fixed-tick code (`FixedUpdate` systems, physics) follows "Simulation determinism" in the design doc:
  no `MathF` or fusing `System.Numerics` members, no wall clock, no hash-map iteration where order matters, results
  independent of thread count. The analyzer enforces the worst of it; the golden-hash tests catch the rest.
- **Native interop** uses `LibraryImport` or unmanaged function pointers. Vulkan struct layouts are checked against
  `vulkan_core.h` by `VkLayoutTests`; regenerate the expected sizes when adding structs.

## Verifying a change

- **Always:** `./scripts/ci.sh` passes. On a busy machine, `GAMEGRL_BENCH=0 ./scripts/ci.sh` skips the benchmark
  gate, whose baselines (`perf/baselines/<cpu>.json`) are per machine.
- **Tests come with features**, checked against references where one exists (textbook formulas, Box2D's or Jolt's
  behaviour, analytic results), not just "it runs". When a test is meant to catch a bug, make sure it fails without
  the fix.
- **Rendering:** screenshots capture only the game window (`xwininfo -name "<title>"`, then
  `import -window <id> out.png`), never the whole desktop.
- **Audio:** never play test sounds on someone's speakers unannounced. Render offline (samples' `--wav`, the
  `Gamegrl.AudioSmoke` tool), or play into a PulseAudio null sink and record its monitor
  (`pactl load-module module-null-sink sink_name=gamegrl_test`, run with `PULSE_SINK=gamegrl_test`,
  `parec -d gamegrl_test.monitor ...`, then unload the module). Audio captures (`AudioCapture`, F12 in Cave Gunfight)
  hold the output and a report of every voice; `--capture-dir` keeps your own away from other people's in
  `artifacts/captures/`.
- **Performance claims** come with numbers from the benchmark tools (`tests/Gamegrl.Bench`,
  `tests/Gamegrl.PhysicsBench`, `scripts/box2d-compare.sh`), best of several runs on an idle machine.

## Third-party code

- The project is BSD Zero Clause. Code ported from Box2D (`src/Gamegrl.Physics2D`, `tests/Gamegrl.PhysicsBench`) and
  Jolt Physics (Physics 3D) stays MIT: every such file starts with the two-line header pointing at
  `THIRD_PARTY_NOTICES.md`. Add the header to any file you port into, and list new sources in the notices.
- Only port from permissively licensed code (MIT, BSD, zlib, Apache 2.0) and record it the same way. Never copy from
  GPL or unlicensed code.
- Reference projects (Box2D, Jolt, the .NET runtime) are cloned outside this repository, at a pinned commit, and
  never vendored into it.

## Commits

- One logical change per commit, and each commit passes CI. Commit as yourself.
- The message: a short summary line, then what changed and why, and how it was verified (tests added, numbers
  measured). Note anything not verified.
- Never commit secrets, personal details or machine-specific paths. Build output, `artifacts/` (including audio
  captures) and per-project `Library/` folders are git-ignored.
