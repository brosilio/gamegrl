# gamegrl

A modular, AOT-first C# game engine on .NET 10: the Unity 5 editor workflow with data-oriented, allocation-free
APIs. Design and decisions: [docs/DESIGN.md](docs/DESIGN.md).

## Build and run

```sh
git clone --recursive https://github.com/brosilio/gamegrl.git   # or, after a plain clone: git submodule update --init
```

Needs the .NET 10 SDK, Vulkan 1.3 (hardware or software), and `glslc` (shaderc). The editor's terminal UI comes from
the `external/ansidotnet` submodule. SDL3 and a TrueType monospace font are needed for desktop mode; terminal mode
needs neither. Audio on Linux uses ALSA's `libasound` (through PulseAudio or PipeWire, or a hardware device); without
it, sound goes to a null device. Linux is the platform developed and tested on so far.

```sh
./scripts/ci.sh                                   # build, test (JIT + NativeAOT), benchmarks, publish the sample
dotnet run --project src/Gamegrl.Editor -- samples/Spinners # desktop cell editor
dotnet run --project src/Gamegrl.Editor -- samples/Spinners --terminal # real TTY / SSH PTY
dotnet run --project samples/Spinners/Build -- run      # dev: run from the asset database, edits hot-reload
dotnet run --project samples/Spinners/Build -- author   # regenerate the sample's prefabs and main.scene
./artifacts/spinners/Spinners                           # the NativeAOT player + spinners.ggpak from ci.sh
```

Escape quits the sample.

### Bog Opera

Baron Bullfrog, Madame Kazoo, and Pip Glassbell sing from three lily pads. Their bass, reed, and glass calls are
synthesized at startup. The mixer places them across the pond, shifts their pitch through a four-bar phrase,
and schedules them on an eighth-note grid at 112 BPM.

```sh
dotnet run --project samples/BogOpera/Player
dotnet run --project samples/BogOpera/Player -- --wav bog-opera.wav
```

Click squares to rewrite the score while it plays. Click a frog or press **A**, **S**, or **D** to sing over it.
**Space** hushes the choir or restarts the score. **E** switches the stone bridge's early reflections on or off
for new calls. **Escape** closes the player. The WAV command records the opening four bars in stereo and runs
without a display or audio device.

### Cave Gunfight

Walk into a cave under fire and hear it. Outside on gravel at the foot of a cliff, a tunnel leads into a rock
chamber with a pillar and a pool. Someone fires from deep in the chamber, behind the pillar from the tunnel, so you
hear the shots bend round the rock, and from outside you hear the chamber ring out of the tunnel mouth. Someone
else fires in the open field. Water drips into the pool, wind moves across the mouth, and HDR mixing lets the drips
vanish under gunfire and come back. All the sounds are generated.

```sh
dotnet run --project samples/CaveGunfight/Player
dotnet run --project samples/CaveGunfight/Player -- --tour
dotnet run --project samples/CaveGunfight/Player -- --wav cave-walk.wav --headphones
```

**WASD** walks, **arrows** or **Q**/**E** turn, right-drag looks, **Space** or a click fires, **R** switches the room
acoustics (reverb, reflections, diffraction, the other room) off and on, **Escape** quits. `--tour` plays a scripted
walk in from the field and back out; `--wav` renders that walk offline (binaural with `--headphones`). Set
`GAMEGRL_HEADPHONES=1` for binaural in the game.

**F1** shows the acoustic overlay: the reverb estimate's sample rays (amber where they hit, blue where they escape to
open sky), each voice's direct line, its path round an edge and its reflections, the decay curves and the voice list
(`--overlay` starts with it on). **F12** writes a capture to `artifacts/captures/<time>/`: the last 12 s of output
(`output.wav`), what every voice and the reverb estimate were doing (`report.txt`), and the decay curves
(`decay.csv`). `--capture-at 3,9.5,27` takes captures at those times, in the game or along the offline walk. When
something sounds wrong, press F12 and share the folder.

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

Benchmarks gate CI against the running machine's baseline in `perf/baselines/` (named after its CPU; a machine with
none only warns, and `GAMEGRL_BENCH=0` skips them on a busy machine):

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
| `src/Gamegrl.Audio` | Audio (`AudioModule`, `AudioSource`, `AudioListener`): mixer, ALSA output, spatial panning to any layout and binaural, HDR mixing, geometric acoustics (occlusion, diffraction, early reflections, ray-traced reverb, baked probes), diagnostics and captures |
| `src/Gamegrl.Physics2D` | 2D physics (`Physics2DModule`, `RigidBody2D`, `Collider2D`, `Joint2D`): compound bodies, capsules, sensors and contact events, sleeping, five joint types, a parallel SIMD soft-step solver; deterministic per `<GamegrlDeterminism>` |
| `src/Gamegrl.UI` | The pixel UI games ship (`UiModule`): TrueType text, windows and widgets, rendering; also the editor's in-view overlays |
| `src/Gamegrl.Editor` | Shared AnsiDotNet cell interface, desktop/TTY hosts, headless viewports, scene editing, jobs and collectible game code |
| `src/Gamegrl.Rendering` | 3D mesh and 2D sprite renderers |
| `src/Gamegrl.Assets` | Runtime asset formats, the `.ggpak` archive, `IAssetSource` |
| `src/Gamegrl.AssetPipeline` | Editor-only: `.meta` files, importers (PNG, glTF, scenes, prefabs), cache, packing, watching |
| `tests/Gamegrl.Tests` | Dependency-free tests, also run as a NativeAOT binary |
| `tests/Gamegrl.Editor.Tests` | Headless editor tests, optional SDL/Vulkan interaction smoke, editor benchmarks (`--bench`) |
| `tests/Gamegrl.Bench` | Engine benchmark scenarios under the profiler (NativeAOT); baselines in `perf/baselines` |
| `tests/Gamegrl.PhysicsBench` | Box2D's benchmark scenes for Physics 2D, with Box2D's command line (`scripts/box2d-compare.sh`) |
| `tests/Gamegrl.AudioSmoke` | Audio device smoke test, offline analysis of recordings, room renders, mixer benchmark |
| `tests/Gamegrl.DeterminismFixture`, `tests/Gamegrl.Editor.PluginFixture` | Fixtures: code the determinism analyzer must reject; an editor extension to load |
| `external/ansidotnet` | Submodule: the ANSI terminal library behind the editor's cell UI |
| `samples/BogOpera` | Interactive frog choir, sample-timed sequencer, and offline WAV player |
| `samples/CaveGunfight` | First-person cave walk under fire: geometric acoustics, diffraction, HDR, offline tour |
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

## Contributing

Agents and people alike: start with [AGENTS.md](AGENTS.md) (the ground rules and how to verify a change), then
[docs/DESIGN.md](docs/DESIGN.md), which records every decision and why. `./scripts/ci.sh` must pass before a change
lands.

## License

BSD Zero Clause (see [LICENSE](LICENSE)). Code ported from Box2D and Jolt Physics stays under their MIT licenses; see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
