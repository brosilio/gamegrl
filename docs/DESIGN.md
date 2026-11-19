# gamegrl design

Living design doc. This file is the source of truth; update the decision log whenever a decision is made.
Started 2026-11-16.

## Principles

**gamegrl** is a modular, AOT-first C# game engine on .NET 10. It keeps the Unity 5 editor workflow but replaces Unity's programming model with data-oriented, allocation-free APIs.

1. **Best implementation over backwards compatibility.** If an underlying API would make the code we're writing clearly better, we change the API. Nothing is frozen until we choose to freeze it.
2. **AOT is the target.** The shipping player is NativeAOT, so there's no runtime reflection, `Reflection.Emit` or dynamic loading. Source generators do that work at compile time.
3. **Efficiency is the default path.** The easy way to write something should also be the fast way. Allocation-free, span-based and `ref`-returning APIs come first, and a costly operation looks costly at the call site.
4. **Few dependencies.** We take one only when it saves months of work and can sit behind our own interface. Today that's SDL3, OpenSSL (TLS for QUIC), the Khronos OpenXR loader (XR module only), an editor-only shader compiler, and our own sibling libraries consumed by project reference: httpgrl (QUIC) and AnsiDotNet (editor cell surfaces, layout and terminal I/O; editor only).
5. **Modular.** Features are modules composed at compile time, and trimming removes anything unused.
6. **Multiplayer and web are design constraints from day one,** even though they ship later. They shape the simulation loop, entity identity, the RHI and I/O.

## Decision log

| Date | Area | Decision | Notes |
| --- | --- | --- | --- |
| 2026-11-16 | Name | gamegrl; assemblies and namespaces are `Gamegrl.*` | Sibling of httpgrl |
| 2026-11-16 | Runtime | .NET 10 | LTS |
| 2026-11-16 | Object model | Entities with struct components; logic as systems or batched behaviours | No reflection, no virtual Update per object |
| 2026-11-16 | Component storage | Archetypes by default; a component can opt into sparse-set storage | For components added and removed often |
| 2026-11-16 | Scripting | C# only | Visual tools, if any, generate C# |
| 2026-11-16 | Editor / player | JIT editor with hot reload; NativeAOT player | Both run the same generated code |
| 2026-11-16 | Windowing and input | SDL3 behind our own platform interface | Swappable later |
| 2026-11-16 | Graphics | Our own thin RHI, Vulkan backend first | D3D12, Metal and WebGPU later |
| 2026-11-16 | Shaders | Compiled offline to bytecode (Slang); pipelines compiled and cached on the client | See Platform and graphics |
| 2026-11-16 | Physics | Hand-rolled, separate 2D and 3D engines | Speed is the top priority |
| 2026-11-16 | Physics determinism | Bit-identical across platforms and architectures | Needed for lockstep and rollback crossplay (consoles included). Superseded 2026-11-18 by "Simulation determinism is per game" |
| 2026-11-16 | Editor UI | AnsiDotNet cell surfaces and layout, shared between desktop and terminal hosts | Desktop composites full-resolution viewports aligned to cell boundaries; terminal uses asynchronous colored-cell previews |
| 2026-11-16 | Editor responsiveness | The editor UI thread never blocks; long work runs as cancellable background jobs | A direct fix for Unity's editor freezes |
| 2026-11-16 | Asset loading | Streaming: on-demand, ref-counted, chunked, budgeted | See Assets and serialization |
| 2026-11-16 | Scene format | Our own line-oriented text format | Merge-friendly, generated parser |
| 2026-11-16 | Audio | First-class and hand-rolled: our own mixer, DSP, spatialization and acoustics | See Audio |
| 2026-11-16 | Audio output | Our own low-latency backends per platform; SDL3 audio is not used | SDL3 stays for windows and input |
| 2026-11-16 | Audio determinism | The beat clock is deterministic; the mix is not required to be | Replays and lockstep depend only on the beat clock |
| 2026-11-16 | 2D and 3D | Both first-class from the first milestone | Including 2D rendered into offscreen targets |
| 2026-11-16 | Multiplayer | First-class, considered in every subsystem | Ships on the roadmap |
| 2026-11-16 | Transport | QUIC (our own C# stack from httpgrl) natively; WebTransport in browsers; raw UDP fallback | Adds OpenSSL as a dependency; see Multiplayer |
| 2026-11-16 | httpgrl libraries | Consumed by csproj project reference (no NuGet) | httpgrl is splitting out `Httpgrl.Quic` and `Httpgrl.Http3` |
| 2026-11-16 | Platform order | Linux, then web, then Windows, then macOS | WebGPU is therefore the second RHI backend |
| 2026-11-16 | Consoles | Deferred; code stays friendly to a future C#-to-C++ step | Revisit with devkit access |
| 2026-11-16 | Serialized fields | Public instance fields serialize by default (the Unity convention); `[NoSerialize]` opts out | Private serialized fields can come later via `partial` |
| 2026-11-16 | Component identity | Stable id = 64-bit FNV-1a of the full type name; runtime ids stay dense for archetype masks | Scenes, assets and packets use stable ids |
| 2026-11-16 | Hierarchy | Intrusive parent/child links inside the world; destroying a parent destroys its subtree | No per-entity allocation |
| 2026-11-16 | Prefabs | `.prefab` files use the scene format; instances store only differences; inner entities have path-derived ids; packing flattens prefabs | Players never expand prefabs |
| 2026-11-16 | Play resources | Checkpoint unmanaged values; require explicit cloning or fresh-play factories for mutable references | Hot reload releases checkpoints and uses new-code resource defaults; entity state still migrates through serialization |
| 2026-11-16 | Editor extensions | Separate JIT extension assemblies share the collectible game context and use the built-in registration API | Four resizable dock regions; extension compilation and persistent layouts are follow-ups |
| 2026-11-16 | Build output ownership | Marked session directories with exclusive leases; retire outputs asynchronously after successful replacement or failure | Preserve current rollback output; reclaim only owned stale sessions |
| 2026-11-16 | Inspector hints | Generated `FieldMetadata` travels with the existing field visitor; serializers ignore it via a default interface method | `[Range]` constrains Inspector edits, never scene loading; headers/tooltips are constant strings |
| 2026-11-16 | Editor jobs | Workers produce imported bytes or build outputs; the host applies results between frames | No asset-database I/O in rendering; code reload resets undo history when the schema can change |
| 2026-11-16 | Game startup | Game modules register gameplay; `SceneBootModule` selects a player's startup scene | Editor chooses its open scene and calls game startup on entering play |
| 2026-11-16 | XR | VR as an optional `Gamegrl.XR` module on OpenXR; its constraints apply from milestone 1 | See XR |
| 2026-11-16 | XR targets | PC VR (Linux first, via Monado or SteamVR) and Meta Quest (standalone Android) | Quest adds an Android target: NativeAOT `linux-bionic-arm64` |
| 2026-11-16 | Web player | Feasible; kept possible by current design choices | See Web target |
| 2026-11-16 | Editor fault isolation | A throwing play system pauses play mode; panels, drawers and menu commands run behind a UI checkpoint and show inline errors | The editor never exits because of game or extension code; repeated faults log once per source |
| 2026-11-16 | Undo granularity | Value edits record changed component bytes of one entity (entity fields as scene ids); structural edits record whole-scene text splices | Clicks and drags never serialize the scene; dirty state is a history position, not a text comparison |
| 2026-11-16 | Text edit commit | A click outside an active field is held for one frame while the field commits, then delivered | Whatever the click changes sees the committed value |
| 2026-11-16 | Held scenes | If game code and its rollback both fail to load, the scene is held as verbatim text until code loads | Saves write the held text; edits are blocked |
| 2026-11-16 | Component defaults | Generated `IComponentSerializer.Reset` writes a component's public static `Default`, else `new T()` | Used by Add component; no editor special cases |
| 2026-11-16 | Editor layout | Not Unity's layout. Lines and glyphs instead of filled blocks: an unboxed sidebar of stacked sections, boxed panes edge to edge with tabs in their top border, borders as splitters, one top line instead of toolbar, menu and status bar | Learned from herdr's terminal UI. Fewer bytes per frame over a terminal and more rows for content |
| 2026-11-16 | Editor commands | A Ctrl+P command palette over `EditorRegistry.AddCommand` entries (with optional shortcut text) replaces menus | Built-ins and extensions register the same way; selection actions live here instead of in panels |
| 2026-11-16 | Cell theme | `CellTheme` semantic tokens: one background, raised, selection, border, dim, text, a single `#ff5fa2` accent, warning, error and axis colors | Tokens are chosen to stay distinct at 256 and 16 colors; selected text also takes the accent so selection survives without background color |
| 2026-11-16 | Editor UI allocation | Steady-state editor frames allocate nothing | Enforced by a test; caches rebuild on registry, catalog, log or code-generation changes |
| 2026-11-17 | UI frameworks | Two, both public. The cell UI (`CellUi`) is the editor's chrome and what extensions draw panels with. The pixel UI (`Gamegrl.UI`, added with `UiModule`) is what games ship for HUDs, menus and debug windows, and what the editor draws inside its Scene and Game views | Grid-aligned cells suit panels and terminals but not overlays on a 3D view. The editor dogfoods the pixel UI every frame through its Scene view overlay and by hosting the game's UI in the Game view |
| 2026-11-17 | One palette | `Gamegrl.UI.Palette` feeds both `CellTheme` and the pixel `Theme` | In-view overlays, game debug windows and editor panels read as one tool |
| 2026-11-17 | Fonts are assets | `.ttf` files import as `AssetKind.Font` and ship in the archive; the UI bakes them at the size it needs | A system-font fallback is for development only |
| 2026-11-18 | Profiler | One process-wide profiler in `Gamegrl.Core`: interned `ProfileMarker`s, per-thread lock-free zone rings drained once per frame, automatic zones per phase, system, job batch and command playback, per-zone allocation bytes, GC figures per frame, GPU timestamps from the RHI, a 1024-frame history | The editor and a shipped player read the same data; zones are cheap enough (two timestamps, two allocation-counter reads, one store) to leave on in the editor |
| 2026-11-18 | Profile categories | Each zone is Engine, Game, Editor, Wait or Gpu. Generated systems outside `Gamegrl.*` assemblies are Game; editor host systems and panels are Editor; fence, acquire and present waits are Wait | Answers "is it my game, the engine, the editor, or vsync" at a glance |
| 2026-11-18 | Performance is tested | `tests/Gamegrl.Bench` (NativeAOT engine scenarios) and `Gamegrl.Editor.Tests --bench` (editor scenarios) run under the profiler and gate CI against `perf/baselines/<machine>.json`: allocations and gen0 collections exactly, times within 25% (p95 50%) plus 0.05 ms | Numbers that aren't checked drift. Baselines are per machine and per code generator (aot/jit) because neither transfers |
| 2026-11-18 | Simulation determinism is per game | A project sets `<GamegrlDeterminism>Local</GamegrlDeterminism>` (default) or `CrossPlatform`. Both are bit-exact on the same build and CPU features (replays, tests, rollback resimulation). `CrossPlatform` is also bit-exact on every platform, by restricting `Gamegrl.Math` to correctly rounded IEEE operations, fixed SIMD lane order and our own transcendentals; `Local` may use FMA, estimates, the widest SIMD and libm. The setting is a .NET feature switch, so NativeAOT keeps one path and the JIT folds the other away. Peers exchange a simulation fingerprint (engine version, game build, mode, CPU feature level) and refuse mismatches | Cross-platform peer-to-peer rollback and lockstep stay possible for games that need them, without taxing games that don't. Only the fixed-tick simulation is in scope; rendering, audio, UI and interpolation always take the fast path |
| 2026-11-18 | Resource-only systems run once | A `[System]` with no component, `Entity` or filter parameters runs once per tick | It used to loop over an unfiltered query, so once per entity: the profiler found Spinners' debug window drawn 606 times a frame |

## Object model

The editor shows a familiar GameObject-and-component hierarchy. Underneath, entities are generational handles and components are structs in archetype storage.

- **Entity:** a 64-bit handle made of an index and a generation. You check it with `world.IsAlive(e)`. There's no fake-null overloading.
- **Storage:** archetype chunks by default, for the fastest iteration. A component type can opt into sparse-set storage (`[SparseStorage]`) when it's added and removed often, such as tags or short-lived status effects. Queries span both kinds transparently.
- **Components:** `partial struct` types implementing `IComponent`. You access them by `ref` (`ref var t = ref e.Get<Transform>()`), so writing a field works the way it reads.
- **Systems:** static methods marked `[System]`. The generator writes the query loop and declares which components are read and written, so the scheduler can run systems in parallel.
- **Behaviours:** `partial struct` types implementing `IBehaviour` with an `Update(ref BehaviourContext)` method. This is the familiar per-entity style, but every instance of a type is updated in one loop with no virtual dispatch.
- **Structural changes** (spawn, destroy, add or remove a component) are recorded in command buffers and applied at sync points.
- **Resources** such as `Time` and `Input` are passed into systems explicitly. There are no static singletons.
- **Hierarchy** is built-in data (`Parent` plus a child list) with a transform-propagation system.

```csharp
public partial struct Spinner : IComponent
{
    [Range(0, 720)] public float DegreesPerSecond;
}

[System(Phase.Update)]
static void Spin(ref LocalTransform t, in Spinner s, [Res] in Time time)
    => t.Rotation *= Quaternion.CreateFromAxisAngle(Vector3.UnitY,
           float.DegreesToRadians(s.DegreesPerSecond) * time.Delta);
```

Because world state lives in contiguous component storage, taking a snapshot and restoring it is cheap. Multiplayer rollback and the editor's play-mode reset both depend on this.

## AOT strategy

The editor runs on the JIT and the player ships as NativeAOT. Source generators keep the two behaving the same.

- **Why split:** NativeAOT can't load managed assemblies at runtime, and editor hot reload needs to. The editor loads game code into a collectible `AssemblyLoadContext` and reloads it when files change.
- **One code path:** generators emit serialization, Inspector metadata, system scheduling and component registration. The editor and the player run the same generated code, never a reflection fallback.
- **Enforced early:** every engine and game project sets `IsAotCompatible` with trim analyzers enabled. Code that wouldn't work under AOT fails to compile, even in editor builds.
- **Interop:** `LibraryImport`, function pointers and `[UnmanagedCallersOnly]`. No `DllImport` marshalling stubs.
- **Custom analyzers** (planned): flag allocations in systems and behaviours, misuse of `in` and `ref` on components, and structural changes made outside a command buffer.

## Platform and graphics

SDL3 handles windows, input and events behind our own `IPlatform` interface. Rendering goes through a thin RHI with Vulkan as the first backend. Shaders are compiled offline, and GPU pipelines are compiled and cached on the player's machine.

### Platform layer

- Our interface covers windows, input devices, the event pump, timers, file and memory-mapped I/O, and threads. Engine code never sees an SDL type.
- **Input is action-based.** Gameplay reads actions (`Jump`, `Grab`, `Aim`), and bindings map them to devices: keyboard, mouse, gamepads, touch, and tracked XR controllers and hands. This matches OpenXR's model, and rebinding comes for free.
- **The frame host is pluggable.** Whoever owns frame timing calls `Tick()`: OS vsync on desktop, `requestAnimationFrame` in the browser, or the XR runtime's frame loop (`xrWaitFrame`) in VR. The engine never assumes which.
- The main loop is a `Tick()` that the host calls. The engine never owns a blocking loop, because the browser requires that. The editor follows the same rule: its UI thread never waits on long work (see Editor).

### RHI

- The RHI is designed around modern explicit APIs: Vulkan, D3D12, Metal and WebGPU. WebGPU is the lowest common denominator, so it's the reference for which features count as core.
- It's bindless where supported, falls back to descriptor sets, and records command lists from multiple threads.
- A render graph sits on top. It manages transient resources, barriers and pass culling.
- **External images:** the RHI renders into images it doesn't own, such as an XR runtime's swapchain images, as easily as into the OS swapchain.
- **Tile-based GPUs are first-class** (Quest's Adreno, mobile, Apple). The render graph merges passes into subpasses where it can, sets load and store operations correctly, resolves MSAA on-tile, and flags mid-pass readbacks.

### Shaders: what client-side compilation means

There are two compilation steps, and only the second runs on the player's machine.

1. **Source to bytecode, offline in the editor.** Slang compiles to SPIR-V, DXIL, MSL or WGSL. Doing this on the client gains nothing: the driver optimises the bytecode anyway, and shipping a compiler adds tens of MB.
2. **Bytecode to GPU machine code, always on the client.** The driver does this when a pipeline (PSO) is created. Games that "compile shaders" at startup are doing this step ahead of time. Done lazily in the middle of a frame, it causes stutter.

Our approach:

- **Know the full pipeline set at build time.** Materials declare their variants, and the build produces a manifest of every pipeline the game can use.
- **Pre-warm.** Compile all pipelines at load time or in the background on worker threads. Nothing is compiled during gameplay.
- **Persist the cache.** A `VkPipelineCache` (and the equivalent on other APIs) is saved to disk and keyed by driver version.
- **Keep variant counts down** with specialization constants and Vulkan graphics-pipeline libraries, instead of a permutation explosion.
- **In development,** a pipeline missing from the manifest is logged as an error. In release, it's compiled synchronously as a fallback.

## Rendering

2D and 3D are peer pipelines that share one render graph. Either can draw into any render target, so 2D-in-3D, 3D-in-2D and offscreen 2D compositions are all ordinary cases.

- **Cameras** each own a pipeline (2D or 3D), a target (the swapchain, a texture or an XR swapchain) and a layer mask. A camera renders **N views**, not one: each view has its own pose and an asymmetric projection, and stereo renders in a single pass using multiview (`VK_KHR_multiview` and its equivalents). Desktop rendering is the N = 1 case of the same path. A render texture is just another asset.
- **3D:** a clustered forward+ PBR pipeline, with GPU-driven culling and indirect draws as the long-term goal. Forward+ with MSAA is also the standard choice for VR. Reverse-Z depth is used everywhere.
- **2D:** a sprite batcher, tilemaps, 9-slice images, SDF text and 2D lights. It renders in pixel-perfect or scaled modes.
- **Shared:** materials, the shader manifest, post-processing passes and debug drawing.
- **Extraction:** a render-extract phase copies the visible data out of the world once per frame. Rendering never touches live simulation state, which lets simulation and rendering overlap and lets render interpolation (needed for multiplayer and fixed timesteps) slot in cleanly.

## Physics

We hand-roll two engines that share a design: Physics2D and Physics3D. A 2D engine built for 2D is much faster than a 3D engine with one axis locked. Both are built for throughput and for rollback networking.

- **Data layout:** bodies, colliders and contacts live in struct-of-arrays storage inside the physics world, keyed by dense indices. ECS components hold only handles and authoring data, and sync happens once per step.
- **Broadphase:** a dynamic AABB tree with incremental refit for moving bodies, and a separate static tree that's rebuilt rarely.
- **Narrowphase:** SAT and GJK/EPA for convex shapes, specialised pairs (sphere-sphere, capsule-box and so on), and SIMD (`Vector128/256`) wide paths over batches of contact pairs.
- **Solver:** soft-step or TGS-style substepping, with contact constraint graph colouring so batches solve in parallel without locks. Islands are used for sleeping.
- **Threading:** every stage runs as job-system tasks. Results don't depend on thread count.
- **Determinism:** chosen per game (see Simulation determinism). Physics is written once against `Gamegrl.Math` and never calls `MathF` or `System.Numerics` members that may fuse. With `Local` it's bit-exact on the same build and CPU features; with `CrossPlatform` it's bit-identical on x64, ARM64 (Quest, Apple Silicon, consoles) and wasm, so lockstep and peer-to-peer rollback crossplay work.
- **Queries:** raycasts, shape casts and overlap tests write into caller-provided `Span<T>` and return a count. They never allocate.
- **Snapshots:** the full physics state can be saved and restored, for rollback and for editor play mode.

Server-authoritative crossplay works even when clients don't agree bit for bit, because the server corrects them. Determinism is what lockstep and peer-to-peer rollback need.

## Simulation determinism

The deterministic domain is the fixed-tick simulation: `FixedUpdate` systems, physics, and the components and resources they write. Rendering, interpolation, audio, UI, particles and animation read the simulation but never feed it, so they always take the fastest path.

- **Always (both modes), same build and CPU features:** fixed iteration order (archetype and entity order follow the sequence of structural changes; parallel jobs write fixed slots and reduce in batch order, never in completion order), no hash-map iteration where order matters, no wall-clock time, seeded random only. This is what replays, tests and rollback resimulation rely on.
- **`CrossPlatform` adds,** inside `Gamegrl.Math`:
    - only correctly rounded IEEE operations (+, −, ×, ÷, sqrt); no fused multiply-add, no reciprocal or rsqrt estimates
    - SIMD with a fixed lane width and reduction order, so SSE, AVX, NEON and wasm SIMD agree with the scalar path
    - our own `sin`, `cos`, `atan2`, `exp` and `log`, because platform math libraries differ
- **`Local` may use** fused multiply-add, estimates, the widest SIMD and the platform library: fastest on its target, and only guaranteed to agree with itself.
- **The switch:** `<GamegrlDeterminism>` in the game's project becomes the runtime feature switch `Gamegrl.Determinism.CrossPlatform`, read through a `static readonly bool`. NativeAOT substitutes the constant and trims the other path; the JIT folds it in optimized code. Changing it means restarting the editor.
- **Fingerprint:** networked peers compare engine version, game build hash, mode and CPU feature level before a session, so a mismatch is refused instead of desyncing mid-match. `Local` peers must match exactly.
- **Verification:** recorded inputs replay with a world hash per tick against committed golden hashes. `CrossPlatform` hashes must match across JIT, NativeAOT at the baseline instruction set and NativeAOT with AVX2 now, and ARM64 and wasm once those targets exist; `Local` hashes must match within each configuration.
- **Analyzer:** fixed-tick systems that call `MathF`, `System.Numerics` members that may fuse, `DateTime`, or iterate a hash map get a diagnostic: an error under `CrossPlatform`, a warning under `Local`.

## Audio

Audio is first-class and hand-rolled. Games built around sound are a design target: beat-based games, surround mixes, generated audio, and real-time modelling of in-scene acoustics. Mixing, DSP, spatialization, acoustics and the device backends are all ours.

- **Output backends:** our own, one per platform, behind an `IAudioDevice` interface. SDL3 audio isn't used, because rhythm games need the lowest and most stable latency, and every layer between us and the device adds buffering.
    - **Linux:** PipeWire (native API) first, with ALSA as the direct fallback.
    - **Web:** an AudioWorklet fed from a SharedArrayBuffer ring, written by the wasm audio thread. Without cross-origin isolation, a single-threaded fallback renders inside the worklet callback.
    - **Windows:** WASAPI, exclusive mode where allowed and low-latency shared mode (`IAudioClient3`) otherwise.
    - **macOS:** CoreAudio (AUHAL) with the smallest stable I/O buffer size.
    - **Android (Quest):** AAudio in low-latency exclusive mode.
    - Every backend reports its real output and input latency and buffer size to the mixer. Device changes and hot-plugging are handled without restarting the mix.
    - Input capture (microphones, line-in) goes through the same interface.

- **Real-time audio thread:** a dedicated high-priority thread renders fixed-size blocks. It never allocates, locks or does I/O. The game talks to it through lock-free command queues, and the audio thread reports back the same way. All DSP is written with SIMD (`Vector128/256`) in mind.
- **Extraction, like rendering:** an audio-extract phase copies emitter, listener and geometry data out of the world once per tick, so the audio thread never touches live simulation state.
- **Time and rhythm:**
    - Every event is scheduled to an exact sample, never to "next frame".
    - The audio clock is the master clock for music. Gameplay reads a `BeatClock` resource (tempo map, time signature, beat and bar position, and a look-ahead window) and schedules sounds on beats.
    - Output and input latency are measured and compensated, with a calibration flow for players, so judgement windows in rhythm games stay fair.
    - **Determinism:** the beat clock is deterministic. Given the same tempo map, start tick and seek operations, it produces the same beat and bar positions and the same scheduled event times on every platform. It's driven by integer sample counts and rational tempo math, never by floating-point wall time. Replays, lockstep multiplayer and gameplay judgement read only the beat clock.
    - The mix itself (DSP, synthesis, spatialization) doesn't need to be bit-reproducible. It's free to use fast SIMD and platform-specific paths.
- **Mixer:**
    - A graph of buses with sends, ducking and sidechains, plus voice management (priorities, virtual voices, limits).
    - Parameter changes are smoothed per sample, so there's no zipper noise.
    - Built-in DSP nodes cover filters, EQ, compression and limiting, delays and reverbs. Modules can register their own nodes.
- **Spatial audio:**
    - Object-based panning (VBAP) to any speaker layout, from stereo through 5.1 and 7.1 to height layouts like 7.1.4.
    - HRTF binaural rendering for headphones, and an internal ambisonics bus for ambiences and scene beds.
    - Distance attenuation, air absorption, Doppler and source directivity cones.
- **Environmental acoustics:**
    - Occlusion and obstruction come from ray queries against the physics world, using acoustic material properties.
    - Early reflections and reverb are driven by room geometry. Probes are baked in the editor and blended in real time, with optional real-time propagation (portals and diffraction around corners) for dynamic spaces.
    - Expensive acoustic queries run as jobs, at a lower rate than the audio block, and are interpolated.
- **Generated audio:** synthesis nodes (oscillators, noise, wavetable, granular, FM, sample playback with pitch and time controls) feed the same graph. Game code can drive DSP graphs per voice with zero-allocation parameter updates. Custom nodes are plain structs, so the generator wires them up without reflection.
- **Formats and streaming:** WAV and Ogg Vorbis decoders are ours. Long music and ambiences stream in blocks through the asset streaming system.
- **Editor:** a mixer panel, meters, a voice and bus profiler, and live auditioning in play mode. Acoustic probes and materials are visualised in the Scene view.

## XR

VR is an optional module, `Gamegrl.XR`, built on OpenXR. A game that doesn't add it pays nothing, because trimming removes it. Targets are PC VR (Linux first, through Monado or SteamVR) and Meta Quest. Most of the engine already suits VR. The constraints below apply from milestone 1, so adding VR is never a retrofit.

**Constraints the rest of the engine honours:**

| Area | Constraint |
| --- | --- |
| Cameras | N views per camera, with asymmetric projections and single-pass multiview (see Rendering) |
| RHI | Renders into externally owned images (XR swapchains); handles tile-based GPUs well |
| Frame loop | A pluggable frame host; the XR runtime can own frame timing (see Platform layer) |
| Pose latency | The render-extract phase reads the head pose predicted for display time, as late as possible, and the pose is re-read just before submission. Simulation already runs separately from rendering with interpolation |
| Input | Action-based; tracked controllers and hands are ordinary devices |
| Frame budget | 72 to 120 Hz with no hitches. Shader pre-warm, streaming budgets and the never-blocking design already serve this |
| UI | The pixel UI can draw into world-space quads, and into OpenXR quad layers for crisp text |
| Audio | HRTF follows the head pose at audio rate, with no extra work |
| Multiplayer | Avatar poses are high-rate state replicated over datagrams, like any other |

**The XR module itself:**

- **OpenXR binding:** `LibraryImport` against the Khronos OpenXR loader on every platform, behind our own interface. It's small, handles runtime discovery (required on Android) and API layers. It's a dependency of the XR module only.
- **Graphics:** `XR_KHR_vulkan_enable2`, so the runtime's swapchain images are imported into the RHI. Depth is submitted for runtime reprojection.
- **Rendering features:** fixed foveated rendering first, then eye-tracked foveation through variable-rate shading where the hardware supports it. A spectator view mirrors the headset to a desktop window.
- **Interaction:** action sets and bindings, hand tracking, and haptics. Physics provides grab and hand-collision helpers.
- **Editor:** a simulated headset for testing without hardware, play mode streamed to a headset, and an XR rig with controller previews in the Scene view.
- **Later:** passthrough and AR through the same OpenXR module. WebXR (a separate browser API whose WebGPU support is still emerging) is a stretch goal.

**Quest (standalone Android):**

- **Code:** the game compiles with NativeAOT for `linux-bionic-arm64` into a native `.so`, with no Mono and no Java game code. Godot uses the same route for its Android exports.
- **App shell:** `android.app.NativeActivity` declared in the manifest, so the APK needs no Java.
- **Packaging:** the build pipeline packages and signs the APK using the Android SDK build tools. These are build-time only and never ship.
- **Hardware:** the Quest is an ARM64 tile-based GPU with thermal limits. Cross-architecture physics determinism already covers ARM, and ASTC texture compression and the tile-friendly render graph cover the GPU.
- **Platform layer:** an Android implementation of `IPlatform` (lifecycle, input, files from the APK's assets) and an AAudio audio backend.
- **Device setup:** a Meta developer account and developer mode on the headset; deployment over ADB.
- **Early spike:** right after milestone 1, once a headset is available, we build a minimal Quest app: the NativeAOT `.so` in a NativeActivity APK, clearing the screen through OpenXR. This proves the toolchain long before milestone 7.

## Multiplayer

The default model is server-authoritative, with client prediction and rollback. Every subsystem is designed so that this works without retrofitting.

| Concern | Design choice made now |
| --- | --- |
| Simulation loop | Fixed-tick simulation, separate from variable-rate rendering, with interpolation in the render-extract phase |
| Input | Input is turned into tick-stamped command structs before gameplay sees it, so the same code runs locally, predicted or on the server |
| Entity identity | Local `Entity` handles plus a separate `NetId` component; the two are mapped at the network layer |
| Replication | `[Replicated]` fields on components; the generator emits bit-packed, delta-compressed serializers with no reflection |
| Rollback | Cheap world snapshots (contiguous storage) plus deterministic physics |
| Ownership and relevance | Per-entity authority and interest management as components, so they can be queried and edited in the Inspector |
| Headless server | The server build is a module set without rendering, audio or the platform window, trimmed by AOT |
| Transport | `ITransport` interface (see below). One packet format runs over every transport |
| Editor | Play mode can start a host plus N clients in-process, with simulated latency and packet loss |

Topologies to support over time: client-server (dedicated or listen server) first, then deterministic lockstep for RTS-style games.

### Transport

- **Native (desktop and servers):** QUIC, using our own AOT-clean C# QUIC stack from httpgrl, with TLS 1.3 from OpenSSL behind an interface. Datagrams carry tick traffic, and streams carry reliable bulk data such as level and asset downloads.
- **Browsers:** WebTransport over HTTP/3. It's the same QUIC underneath, so one server stack serves native and web clients.
- **Fallbacks:** raw UDP with our own reliability layer wherever OpenSSL or QUIC isn't viable (likely consoles). Browsers fall back to WebRTC data channels (unordered, `maxRetransmits` 0), then WebSocket as a last resort.
- **Packet size:** packets stay under about 1,200 bytes, so the same reliability layer runs unchanged over raw UDP and QUIC datagrams.

### httpgrl QUIC stack: what's needed before we can use it

The stack lives in the httpgrl repository, in `src/Httpgrl.Core/Quic` and `src/Httpgrl.Core/Http3`. Reviewed 2026-11-16.

**What it already has:**
- RFC 9221 datagram frames: send, receive and negotiation.
- Both client and server connections.
- CUBIC and NewReno congestion control, pacing, and batched UDP sends on Linux.
- WebSockets over HTTP/3 (RFC 9220, extended CONNECT).
- A connection that does no I/O itself and runs on a caller-supplied clock. This fits a fixed simulation tick and deterministic network tests well.

**Gaps:**

1. **Packaging.**
    - Everything is `internal` to `Httpgrl.Core`.
    - The UDP socket loop (`Quic/Endpoint.cs`) is server-only. The client connection is only driven by tests.
    - TLS hard-codes `libssl.so.3` and needs OpenSSL 3.5 or newer.
    - Fix: pull QUIC (and later HTTP/3) out into a standalone library, with a client socket loop, per-platform library loading, and TLS behind an interface.
2. **Datagram handling for games.**
    - Datagrams wait for room in the congestion window and for pacing, like any other data (`Quic/Connection.cs` ~1358). After a burst of losses, tick packets sit in the queue going stale.
    - The 64-entry queue is first-in first-out and rejects the *newest* datagram when full. Games need drop-oldest or a maximum age, and possibly a small budget that bypasses the congestion window.
    - Ack and loss events for datagrams are tracked internally but never reach the application. Exposing them would save the game's reliability layer from sending its own acknowledgements.
    - Every datagram allocates a `byte[]` on send and on receive. Pool them.
    - There's no path MTU discovery, so payloads top out around 1,136 bytes. That's acceptable.
3. **WebTransport is missing entirely.**
    - The HTTP/3 datagram setting (`H3Datagram = 0x33`) is defined but never sent, and neither are the WebTransport settings.
    - Extended CONNECT accepts only `websocket`; any other `:protocol` gets a 501 (`Http2/RequestHead.cs:110`).
    - Also missing: HTTP datagrams (RFC 9297), the capsule protocol, WebTransport stream types and session handling.
    - `MaxDatagramFrameSize` defaults to 0 and hosting never turns it on.
    - WebTransport is still an IETF draft, so check the codepoints against what browsers ship when we implement it.

Suggested order: extraction first, then datagram fixes, then WebTransport. WebTransport also benefits httpgrl as a web server.

**Assumption (2026-11-16):** httpgrl will receive all of these changes. The engine doesn't fork or vendor the code; it references the httpgrl libraries. The user has approved the split, and the plan lives in `httpgrl/docs/plan-protocol-libraries.md`:

- **Libraries:** httpgrl extracts `Httpgrl.Quic`, `Httpgrl.Http3` (with QPACK) and a small shared field codec. HTTP/1, HTTP/2, Hosting and Pipeline stay in Core for now.
- **Dogfooding:** httpgrl's Core uses the libraries only through their public API, so the nginx-compatible server runs on the same code the engine consumes. `Httpgrl.Quic` references nothing else of ours, and `Httpgrl.Http3` references only Quic and the codec. `check-boundaries.sh` enforces this.
- **Consumption:** csproj project reference, no NuGet. The public API follows semver; while at 0.y, a breaking change bumps the minor version.
- **Defaults stay server-safe.** Game behaviours (datagram drop policy, urgent budget and so on) are opt-in per connection.

Each httpgrl milestone keeps `ci.sh` green under JIT and AOT, and adds to a smoke program that uses only the public API:

| httpgrl milestone | Scope | Engine relevance |
| --- | --- | --- |
| M0 | Restructure, no behaviour change | Libraries become referenceable |
| M1 | QUIC client endpoint; TLS behind `IQuicTlsProvider`/`IQuicTlsSession` with per-OS OpenSSL resolution; client cert verification on by default | Native game clients can connect; consoles can supply their own TLS |
| M2 | Datagram queue policy (DropNewest default, DropOldest, max age), pooled zero-alloc buffers, `DatagramAcked`/`DatagramLost` events, opt-in bounded urgent budget (never bypasses anti-amplification, still counted in flight), public `MaxDatagramFrameSize` | Tick traffic behaves well under loss |
| M3 | WebTransport over HTTP/3: browser codepoints verified first, RFC 9297 datagrams and capsules, WT streams, session lifecycle and limits; server-side WT session API, off by default | Browser clients |

Client connection migration and 0-RTT are listed but unscheduled.

**Status (2026-11-16):** M0 is done with CI green, approved for commit, and M1 has started. It produced `src/Httpgrl.Quic`, `src/Httpgrl.Http3` and `src/Httpgrl.HttpFields`. The public entry points are `QuicEndpoint` (server UDP loop, `IQuicApplication`), `QuicConnection` (client/receive/send/deadline on a caller clock), `QuicTlsContext`, and `Http3ServerConnection` with `IHttp3ServerHandler`/`IHttp3Request`. Logging goes through `IQuicLog`. `tests/Httpgrl.Libraries.Smoke` shows end-to-end use. The API changes again in M1. Because the web ships second, httpgrl's M3 sits on the engine's critical path earlier than first planned.

## Assets and serialization

The asset workflow follows Unity: an `Assets/` folder, `.meta` sidecars with GUIDs, and a `Library/` cache of imported results. Serialization is entirely generator-driven.

- **Meta files:** each one holds a GUID, the importer ID and version, and importer settings. References between assets are GUIDs, never paths.
- **Import:** editor-only importers turn source files into runtime formats. Results are cached in `Library/`, keyed by a hash of the source, settings and importer version. A file watcher triggers reimport and hot reload.
- **Formats we parse ourselves:** PNG (using BCL deflate), TGA, BMP, WAV, Ogg Vorbis (later), and glTF 2.0 (using source-generated `System.Text.Json`). JPEG comes later. Texture compression is per target: BC on desktop and ASTC on Quest and mobile, chosen by the importer per platform.
- **Scenes and prefabs:** saved in our own line-oriented text format so people can read them and they diff and merge cleanly. Every object and component has a stable ID, one property goes on each line, and ordering is deterministic, so unrelated edits never conflict. A generated parser keeps loading fast and AOT-safe. Nested prefabs with per-property overrides are supported from day one.
- **Serialization:** `[Serialize]` fields drive generated readers and writers for text (editor) and binary (player) formats, plus Inspector metadata. Version upgrades go through explicit generated migration hooks, not reflection.
- **Player builds:** assets are packed into binary archives that can be memory-mapped. All loading is async (`ValueTask`-based), which the web target requires.
- **Streaming:** assets load when game state asks for them, not with the scene.
    - An `AssetRef<T>` resolves asynchronously (Unloaded → Loading → Ready) and is ref-counted, so unused assets are evicted under a memory budget.
    - Archives are chunked so large assets stream in pieces: texture mips coarse to fine, mesh LODs, audio in blocks.
    - Reads run on I/O threads and decoding on workers. GPU uploads go through a staging ring with a per-frame byte budget, so loading never stalls a frame.
    - Gameplay can prefetch by region or trigger. Anything not yet resident shows a fallback (lowest mip, proxy mesh) instead of causing a hitch.

## UI

There are two immediate-mode UI libraries, for two different jobs:

- **Pixel UI (`Gamegrl.UI`)**, for games. `UiModule` adds a `Ui` resource that systems call during the frame (`[Res] in Ui ui`) and draws it over the game's view after the main camera. It has movable, foldable windows, buttons, sliders, checkboxes, drags, vectors, colors, text fields, combos, tabs, scroll areas, progress bars and free text placed over the view (`TextAt`). Frames allocate nothing: ids hash spans, and interpolated labels (`ui.Label($"{fps:0} fps")`, `ui.Format(...)`) write into per-frame scratch. A scale (pixels per UI unit) covers HiDPI windows and reduced previews. Fonts come from font assets. The module also runs headless, without drawing.
- **Cell UI (`CellUi`, editor only)**, for the editor's chrome and extension panels. It lays out on a character grid so the same frame works in a desktop window and over a terminal.

A game adds `UiModule` from its own module, so the editor gets it too. There the UI follows play mode, receives the Game view's input in game coordinates, and the editor draws it into the Game view. The editor's own in-view interface (the move gizmo, the selection's name tag, orientation axes, view buttons) is drawn with the pixel UI into the Scene texture, which keeps the editor using the library games depend on. The terminal host lacks the resolution for pixel overlays: game UI there is drawn at the preview's scale and the Scene gizmo falls back to cell lines.

TODO: SDF text for world-space and scaled UI, font hot reload, keyboard and gamepad navigation, and a layout pass for menus (anchoring, stacks).

## Profiling

`Profiler` (in `Gamegrl.Core`) records zones and frames for the whole process.

- **Zones.** `static readonly ProfileMarker Think = ProfileMarker.Get("AI.Think");` once, then `using (Think.Scope()) { ... }`. A zone costs two timestamp reads, two reads of the thread's allocation counter and a store into the thread's ring; when profiling is off, one branch. Markers are interned by name and category, so reloaded game code gets its ids back.
- **Automatic zones.** Every phase (`Phase.Update`), every system (its `Name`), command-buffer playback, `ParallelFor` dispatch on the frame thread and batches on workers, the host's event pump, RHI waits (`GPU.WaitForFrame`, `GPU.Acquire`, `GPU.Present`) and submit. The editor adds a zone per panel and for the Scene overlay.
- **Frames.** `App.Tick` brackets a frame; a frame runs from the previous frame's end to this one's, so host time between ticks counts. Each frame records exact bytes allocated on the frame thread, coarse bytes for the process, gen0/1/2 collections, GC pause time and heap size. The history holds 1024 frames.
- **GPU.** `CommandList.Zone(marker)` brackets GPU work with timestamp queries. Vulkan reads them when the frame's slot comes round again and reports them to the profiler, which attaches them to the CPU frame that recorded them (laid out from its start; the clocks aren't calibrated). Views, the game UI and the editor's cells have zones.
- **Reading.** `ProfileTree.Build(first, count)` merges frames into a call tree (frame thread by depth, other threads under Workers, GPU under GPU) with time, self time, calls and allocations per frame, plus frame-time percentiles. Allocation-free after warm-up. `ProfileTree.WriteChromeTrace` exports frames for ui.perfetto.dev.
- **Editor.** The editor always profiles. The Profiler panel shows a frame graph (click a frame to pause on it), a summary split into game, engine, editor, wait and GPU, and the merged tree or a flat list by self time filtered by category, over 1, 60 or 300 frames. F9 pauses; "Profiler: Export trace" writes `Library/Profiles/*.json`.
- **Player.** `GAMEGRL_PROFILE=1` turns profiling on; `GAMEGRL_PROFILE_OUT=trace.json` writes a trace when the desktop host exits. Works under NativeAOT.
- **Formatting without garbage.** `SpanFormat.Write(buffer, $"{ms:0.00} ms")` formats into a caller's buffer. The BCL's `MemoryExtensions.TryWrite` boxes values that have a format string.

### Benchmarks

`tests/Gamegrl.Bench` runs engine scenarios (`ecs.iterate` 100k entities, `ecs.hierarchy` 10k×5 transforms, `ecs.structural` 2k spawns and despawns a frame, `ui.frame` three windows of widgets and 300 labels, `render.cubes` 10k cubes offscreen with GPU times). `Gamegrl.Editor.Tests --bench` runs the editor with Spinners loaded: idle, playing, and playing with the Profiler panel open, on the terminal host (`--desktop` for an SDL window). Each scenario warms up, then measures three runs of 300 frames and keeps the median of each figure. Results compare with `perf/baselines/<machine>.json` under `aot` or `jit`; `--update` rewrites this machine's section, `--tree` prints the call trees, `--quick` does one short run. `scripts/ci.sh` runs both (`GAMEGRL_BENCH=0` skips them).

What the first runs found and fixed (2026-11-18): resource-only systems ran once per entity (Spinners' play frame in the editor went from 5.1 to 1.2 ms); readback buffers lived in device-local memory reached over PCIe, so the terminal preview cost 3 ms of uncached reads (now `MemoryKind.Readback`, cached system memory); the instance sort was a delegate comparison sort that took 6.4 ms for 10k cubes under NativeAOT (now a counting sort that also scatters into the GPU layout: `render.cubes` 7.3 to 0.9 ms); and boxed list enumerators and `TryWrite` cost the editor 320 bytes a frame (now zero).

Open findings: NativeAOT is slower than the JIT on SIMD-heavy scenarios (`ui.frame` 0.45 vs 0.26 ms, `render.cubes` 0.94 vs 0.67 ms), likely because the AOT binary targets a baseline instruction set; choosing `IlcInstructionSet` for players is a pending decision. Parallel systems over small sets cost more than they save (Spinners' `Spin` over 600 entities: 0.10 ms parallel, 0.025 ms serial), and workers burn about 15× the wall time of a parallel loop in batches; the job scheduler needs a minimum batch size and cheaper wake-ups. The hierarchy panel walks every entity each frame (0.37 ms at 606 entities).

## Editor

The editor is the engine plus editor services and a shared AnsiDotNet cell interface. The desktop host draws glyph cells on the engine renderer and composites full-resolution Scene/Game textures into grid-aligned rectangles. The terminal host uses ANSI/VT output with headless Vulkan viewports converted to colored cells.

- **Panels:** Scene view, Game view, Hierarchy, Inspector, Project browser, Console and Tasks with dockable tabs. The layout is our own, drawn with lines rather than filled blocks: Hierarchy and Project stack as collapsible sections in an unboxed sidebar; the other regions are boxed panes with tabs in their top border; borders double as splitters; a single top line shows the scene, play controls and background work; Ctrl+P opens a command palette.
- **Never blocks:** the UI thread only handles input, layout and drawing.
    - Imports, asset builds, script compiles, bakes and player builds run as cancellable background jobs that report progress to a task panel.
    - Panels show placeholders or stale data until results land.
    - Jobs touch the edit world only through command buffers applied between frames.
    - Hot reload compiles off-thread. The only pause is swapping code at a frame boundary.
- **Inspector:** generated from serialized-field metadata, with attributes like `[Range]`, `[Tooltip]` and `[Header]`. Modules can register custom drawers.
- **Play mode:** snapshot the world, run it, then restore the snapshot on stop. This is fast because of contiguous storage. Multiplayer play mode can run host and clients side by side.
- **Hot reload:** game code recompiles into a new collectible `AssemblyLoadContext`, and world state survives through serialization.
- **Undo:** every edit goes through `SceneDocument`. Value edits (Inspector fields, gizmo drags) record only the changed component bytes of one entity; structural edits record whole-scene text splices. History is strictly LIFO and each entry verifies its target before applying.
- **Fault isolation:** game systems, extension panels, drawers and commands can throw without closing the editor. Play pauses on a system fault so its state stays inspectable.
- **Extensibility:** modules add panels, gizmos, importers, palette commands and Inspector drawers through the same registration API the built-in tools use.

## Web target

A web player is feasible. The engine already runs without reflection, so the remaining work is a WebGPU backend, a browser platform layer, and a few constraints we accept now.

- **Runtime:** .NET's browser-wasm target with AOT compilation works today (Blazor uses it). NativeAOT-to-wasm (via LLVM) is still experimental. Our code is AOT-clean, so either path works, and we'll pick whichever is mature when we get there.
- **Graphics:** a WebGPU RHI backend. Slang emits WGSL, so the shader pipeline already covers it.
- **Platform:** SDL3 supports Emscripten. Input and windows go through the same `IPlatform` interface; audio uses our own AudioWorklet backend (see Audio).

Constraints we accept now so the web target stays possible:

| Constraint | What it means for the engine |
| --- | --- |
| The browser owns the frame loop | The engine exposes `Tick()` and never runs a blocking loop |
| Threads need SharedArrayBuffer (COOP/COEP headers) and are limited | The job system must also run correctly on one thread |
| No synchronous file I/O | All asset loading is async |
| No raw UDP | Networking goes through `ITransport`. The web uses WebTransport datagrams (unreliable and unordered, over QUIC), falling back to WebRTC data channels, then WebSocket. See Multiplayer → Transport and the httpgrl gaps |
| WebGPU limits | The RHI's core features are a subset of what WebGPU offers; anything beyond is an optional capability |

## Modules and project layout

Each box below is its own assembly (`Gamegrl.Core`, `Gamegrl.Platform`, `Gamegrl.Rendering3D` and so on). Each one exposes a module that registers its systems, resources, importers and editor panels. Each layer depends only on the layers below it.

| Layer | Assemblies |
| --- | --- |
| Game and tools | Your game modules · Editor (JIT builds only) |
| Feature modules | Renderer 2D · Renderer 3D · Physics 2D · Physics 3D · Audio · Networking · UI · XR |
| Engine services | Render graph + RHI · Assets + importers · Serialization |
| Foundation | Core (entities, jobs, memory) · Platform (`IPlatform`) |
| Backends (swappable) | Vulkan · WebGPU · D3D12 · Metal · SDL3 (windows, input) · Android platform · OpenXR · audio (PipeWire/ALSA, AudioWorklet, WASAPI, CoreAudio, AAudio) · QUIC/TLS (httpgrl libraries) |
| Compile time, alongside every layer | Generators: Roslyn generators and analyzers that emit serializers, Inspector data, system loops, replication code and AOT-safety checks |

The editor and importers are left out of player builds. A headless server also drops rendering and audio.

```csharp
var app = EngineBuilder.Create()
    .AddModule<PlatformModule>()
    .AddModule<VulkanModule>()
    .AddModule<Renderer2DModule>()
    .AddModule<Renderer3DModule>()
    .AddModule<MyGameModule>()
    .Build();
```

Modules are composed at compile time, so whatever a game doesn't add is trimmed from its player build.

## Roadmap

We build in seven milestones. Each one ends with a working build. Later milestones are planned so they don't force rework of earlier ones, but when a later milestone shows a better design, we rework anyway, per the first principle.

| # | Milestone | Scope | Done when |
| --- | --- | --- | --- |
| 1 | Foundation | SDL3 window and input, Vulkan RHI clearing the screen, entity store, job scheduler. Source generator working end to end on `Spinner`; spinning cubes and a 2D sprite batch | The NativeAOT player renders 3D cubes and 2D sprites from generated systems |
| 2 | Data | Generated serialization, text scenes, nested prefabs. Asset database with `.meta` GUIDs, PNG and glTF importers, file watching and hot reload | A scene saved as text loads in the AOT player from packed assets |
| 3 | Editor shell | Immediate-mode UI; Hierarchy, Inspector, Scene view, Project browser, Console. Play mode with snapshot and restore, undo, game-code hot reload, never-blocking job model | A scene can be built, played and edited without touching a text file |
| 4 | Simulation and audio | `Gamegrl.Math` with both determinism modes; input recording, replay and per-tick world hashes; job scheduler fixes from profiling; fixed-tick loop with render interpolation; Physics 2D, then Physics 3D; snapshot and restore of physics state; streaming asset loading. Audio core: real-time mixer, sample-accurate scheduling and beat clock, object panning to any speaker layout, HRTF for headphones, distance, Doppler and occlusion | Replaying recorded inputs reproduces the same world state bit for bit (in `CrossPlatform` mode across JIT, NativeAOT and instruction-set levels), and a rhythm sample stays on the beat with measured latency compensation |
| 5 | Multiplayer | QUIC library extracted from httpgrl, `ITransport`, generated replication, client prediction and rollback. Headless server build; host plus clients in editor play mode with simulated lag | A sample game plays smoothly under simulated latency and packet loss |
| 6 | Reach | Player build pipeline. In order: web player (WebGPU backend, browser platform layer, WebTransport via httpgrl M3), then Windows (D3D12), then macOS (Metal). Advanced acoustics (propagation, real-time reverb from geometry) | One sample project builds for Linux, the browser, Windows and macOS |
| 7 | XR | `Gamegrl.XR` on OpenXR: PC VR on Linux (Monado, SteamVR) first, then Quest (Android platform layer, NativeAOT `linux-bionic-arm64`, APK packaging, AAudio, ASTC). Foveated rendering, hand tracking, simulated headset in the editor, spectator view | A sample scene runs at the headset's native refresh rate with no dropped frames, on PC VR and on a Quest |

Multiplayer, web and XR ship in milestones 5 to 7, but their constraints apply from milestone 1: a fixed tick, snapshots, a pluggable `Tick()` host, async I/O, N-view cameras, external render targets and action-based input. The web is the second platform, so WebGPU limits bound the RHI from the start. Early milestones are tested in a browser as soon as the WebGPU backend exists, rather than waiting for milestone 6.

## Status

**Milestone 4 (Simulation and audio): in progress.**

| Step | State |
| --- | --- |
| Determinism modes | Done. `<GamegrlDeterminism>` → `Gamegrl.Determinism.CrossPlatform` feature switch and a `GamegrlDeterminism` assembly attribute the editor applies when it loads game code (`Directory.Build.targets`, `Determinism.cs`) |
| `SimMath`, `SimRandom` | Done. Strict sin/cos/atan/atan2/asin/acos/exp/log/pow evaluated in double without fusing (within 2 ulps, tested over 200k inputs each); vector and quaternion helpers with fixed operation order; xoshiro128** random as a 16-byte value type (`src/Gamegrl.Core/Math`) |
| Replay and hashes | Done. `InputRecording` captures per-frame deltas and input events and replays them through `Input` (`GAMEGRL_RECORD` / `GAMEGRL_REPLAY` in the desktop host); `WorldHash` hashes entities and component bytes; `TickHashRecorder` keeps one hash per fixed step |
| Verification | Done. A swarm scenario (serial, parallel and command-buffer systems; varying frame times; scripted input) replays bit for bit, agrees with and without worker threads, and matches `tests/Gamegrl.Tests/Data/determinism.golden` under the JIT, NativeAOT and NativeAOT for x86-64-v3, all in CI. ARM64 and wasm join when those targets exist |
| Next | Determinism analyzer; job scheduler fixes (minimum batch size, cheaper wake-ups); render interpolation; Physics 2D; Physics 3D; audio core; streaming assets |

**Milestone 3 (Editor shell): complete, including the shared cell interface and the post-review fixes.**

The editor (`src/Gamegrl.Editor`) is a JIT host that takes a project root and a game `.csproj`. Its chrome is a shared AnsiDotNet cell interface (`CellUi`) painted into a `Surface`. Inside the Scene and Game views it draws with the engine's pixel UI (`Gamegrl.UI`), which also renders the desktop host's glyphs (see UI).

| Area | What exists |
| --- | --- |
| Hosts | Desktop: monospace glyph atlas, cell styles, full-resolution Scene/Game textures aligned to cells. Terminal (`--terminal` over a TTY, `--stdio` over streams): no SDL, font or display server; headless Vulkan renders two pixels per cell and nonblocking readbacks feed `▀` previews. A bounded latest-frame writer isolates slow terminals. `--colors` and `--ascii` select fallback profiles. Classic terminals deliver key pulses only (no releases); held-key gameplay needs the desktop host |
| Panels and docking | Hierarchy, generated-field Inspector, Scene and Game tabs, Project, Console (level filters with counts), Tasks. The sidebar stacks its panels as collapsible sections; the other regions are boxed panes with tabs and panel status in the top border and an accent border while focused. Tabs move and reorder among the four regions; borders are the splitters; Ctrl+B hides the sidebar. Layouts last for the session |
| Commands | One top line (scene, unsaved dot, play/pause, warnings, background work) and a Ctrl+P palette over every registered command, filtered by subsequence, with shortcut hints |
| Look | `CellTheme` tokens with a single pink accent; `[ bracket ]` buttons, status-dot toggles, foldout component headers; slim scrollbars on the border beside each scroll view (half-row thumb, draggable) |
| Scene editing | Create entities, cubes, cameras and sprites; add/remove components (defaults from generated `Reset`); assign assets from a picker; instantiate prefabs from Project. Orbit/pan/zoom camera, mesh picking, framing, world-axis move gizmo with parent-space conversion |
| Undo | `SceneDocument`: per-entity component-byte entries for value edits, whole-scene text splices for structural edits, dirty tracked by history position. Play snapshots the edit scene and time; Stop restores them and the selection |
| Inspector metadata | `[Header]`, `[Tooltip]` and numeric `[Range]` compile into `IFieldVisitor.Metadata` calls. Ranges constrain edits, never loading. GG0104 rejects invalid ranges |
| Files | New (Ctrl+N), Save (Ctrl+S), Save As (Ctrl+Shift+S); Save/Discard/Cancel on Open, New and Close. Writes go to a unique temporary file and are renamed atomically; paths are restricted to `.scene` files under `Assets/` |
| Jobs and builds | Imports, reads, saves and game builds run as cancellable worker jobs whose results apply between frames. Source changes trigger debounced builds into marked, leased session directories; outputs stay available for rollback and retire on background workers |
| Hot reload | Collectible game assemblies share the host's engine assemblies. Reload preserves serialized entity state and selection and resets undo across schema changes. A failed build or load rolls back to the previous assembly with history intact; if that also fails, the scene is held as text until code loads |
| Play | Startup runs only on entering play. Pause/Resume; a throwing system pauses play and logs its stack trace. Resources: unmanaged values checkpoint automatically; mutable ones need a clone delegate or fresh-play factory. Game-view input activates on click; `ActionMap` provides rebindable keyboard/mouse bindings |
| Extensions | `IEditorExtension` registers panels, palette commands and typed component drawers through `EditorRegistry`, the same path as built-ins. Extensions load with game code (`--extension path.dll`) and unregister in reverse order on reload. Failures render inline instead of closing the editor |
| In-view UI | Scene view (desktop): pixel move gizmo with arrowheads and axis labels, a name tag over the selection, clickable orientation axes, Grid and Frame buttons, an entity count. Game view: the game's own `UiModule` UI during play, with input in game coordinates |
| Recovery | On terminal EOF or hangup, dirty scenes are copied to `Library/recovery`; the "File: Recover disconnected scene" command reopens the newest copy |
| Efficiency | Steady-state frames allocate nothing (tested). Clicks and drags never serialize the scene |
| Profiler | A Profiler panel over the engine profiler (graph, game/engine/editor/wait/GPU split, merged call tree or flat list, pause on a frame, Perfetto export); benchmarks gate CI against per-machine baselines (see Profiling) |

**Validation:** 48 engine tests under JIT and NativeAOT (plus the determinism tests again for x86-64-v3), including the pixel UI (widgets, windows, sliders, formatting, zero-allocation frames, the headless module) and the profiler (call trees, worker threads, allocation-free profiled frames and reports, export); 34 JIT editor tests covering undo/redo (text and per-entity), dirty tracking, held scenes, play restore, malformed scenes, jobs, picking, collectible reload, UI input and layout, commit-before-click, splitter drags and highlights, border scrollbars, panel rewind, zero-allocation frames, file workflows and terminal output. The opt-in SDL/Vulkan smoke test (`--graphics`, `--terminal-smoke`) and `scripts/terminal-cli-smoke.py` drive the full workflow; all three passed on 2026-11-18 with the new layout, palette, Scene overlay and game UI, and window-only screenshots of the editor and the Spinners player confirmed the overlay and the debug window render. The smoke test also checks that game, editor and GPU time reach the profiler, the panel opens, and the export parses. The benchmarks (see Profiling) gate CI.

**Follow-ups:** persistent layouts, floating windows, automatic extension compilation, gamepad/touch/XR bindings, and migration policies for mutable resources across schema changes, a theme file with hot reload, an option to use the terminal's own background, and draggable sidebar section heights.

**Commits:** `2fc2768` shell, `227460b` file workflow, `7a8eb07` metadata, `9237df9` input, `6566f93` resource sessions, `1d2a919` docking and extensions, `961f6c5` build workspaces, `96e99d2` shared cell interface, `be108f6` terminal recovery. Review fixes: `389a892` fault isolation, `4b63143` commit before click, `a55a69e` splitter drags, `fd12f18` held scenes, `274f486` inverse cells, `623d04b` per-entity undo, `3490c21` component defaults, `bcd5772` allocation-free frames. Layout: `0b71a9d` line-drawn layout, command palette and theme. UI: `cf62f92` pixel UI as the game UI and the editor's in-view interface. Profiling: `d3339e6` profiler, GPU timestamps, Profiler panel, benchmarks and the fixes they found.

**Milestone 2 (Data): done, 2026-11-16.** The NativeAOT player loads `Assets/main.scene` from a memory-mapped `spinners.ggpak`. The scene holds three instances of `tower.prefab` (which nests `speck.prefab`) with per-instance overrides, a glTF mesh and a PNG texture, all decoded by our own importers. `Spinners.Build run` runs the same game from the live asset database with file watching.

| Area | What exists | Where |
| --- | --- | --- |
| Serialization | Generator-emitted field visitors per component, registered by module initializers that also register referenced assemblies. One visitor drives text, binary and later the Inspector. Supports primitives, vectors, quaternions, matrices, GUIDs, `FixedString64`, entity refs, `AssetRef<T>`, enums by name, nested structs. Diagnostics GG0101 to GG0104 | `src/Gamegrl.Generators/ComponentGenerator.cs`, `src/Gamegrl.Core/Serialization` |
| Scenes | Text format (deterministic, one property per line, entities depth first) and a binary player format; entity references remapped through scene ids | `Scene.cs`, `SceneText.cs` |
| Prefabs | Nested expansion, per-property overrides, added and removed components, deleted children, references remapped per instance; saving writes only differences | `SceneText.cs` |
| Hierarchy | `SetParent`, ordered children, recursive destroy; parallel transform propagation by root | `World.cs`, `Transforms.cs` |
| Assets (runtime) | Texture, mesh and scene artifact formats; `.ggpak` archive (memory-mapped, zero-copy reads); `IAssetSource` | `src/Gamegrl.Assets` |
| Asset pipeline | `.meta` GUIDs and settings; SHA-256-keyed `Library/artifacts` cache; importers for PNG (every color type, bit depth, Adam7, CRC-checked), glTF 2.0 (.gltf/.glb, node transforms, generated normals), scenes and prefabs; packing; file watching with reimport and change events | `src/Gamegrl.AssetPipeline` |
| Rendering | Meshes and sprites referenced by `AssetRef`, resolved by GUID on first use, re-uploaded on reimport; built-in cube and white texture | `src/Gamegrl.Rendering` |

**Milestone 1 (Foundation): done, 2026-11-16.** The NativeAOT player (`samples/Spinners`, a 2.2 MB binary) renders 576 instanced, lit, spinning cubes plus 40 alpha-blended 2D sprites, all driven by generated systems. It runs at the display's refresh rate on an RX 6750 XT (RADV). `scripts/ci.sh` runs the tests under the JIT and as a NativeAOT binary.

| Area | What exists | Where |
| --- | --- | --- |
| Entities | Generational handles; archetype storage (64-byte-aligned native columns, swap-back removal, cached add/remove edges); cached queries; command buffers; resources | `src/Gamegrl.Core/Entities` |
| Systems | `[System]` methods turned into pointer loops by the generator. Supports `ref`/`in` components, `[Res]` resources, `Entity`, `CommandBuffer`, `[With<T>]`/`[Without<T>]`, and `Parallel = true` (job structs with raw column pointers). Diagnostics GG0001 to GG0006 | `src/Gamegrl.Generators` |
| Jobs | Zero-allocation `ParallelFor` over a fixed worker pool, using a generic function-pointer trampoline | `src/Gamegrl.Core/Jobs` |
| App | `EngineBuilder` and modules; `App.Tick()` driven by a host; fixed-step accumulator in integer nanoseconds (no drift); phases from First to Last | `src/Gamegrl.Core/Hosting` |
| Platform | `IPlatform`/`IWindow`/`Input` (HID-numbered keys); SDL3 backend via `LibraryImport`; `DesktopHost` | `src/Gamegrl.Platform*` |
| RHI | WebGPU-shaped interface (bind groups, dynamic offsets, render passes, reverse-Z defaults) | `src/Gamegrl.Rhi` |
| Vulkan | Vulkan 1.3 (dynamic rendering, sync2), hand-written bindings with layouts tested against `vulkan_core.h`, deferred destruction per frame in flight, swapchain recreation, negative-viewport y-up clip space | `src/Gamegrl.Rhi.Vulkan` |
| Rendering | Extract systems, then an instanced mesh renderer and an instanced sprite batcher (quads from `gl_VertexIndex`), a per-frame upload ring, reverse-Z infinite projection | `src/Gamegrl.Rendering` |

**Known gaps and temporary choices, to fix in later milestones:**
- Shaders compile with `glslc` (GLSL) because Slang isn't installed yet. Slang remains the plan, for WGSL and the other targets.
- Keyboard/mouse action maps are available; gamepad, touch and XR bindings remain future platform work.
- `IBehaviour` (batched per-entity behaviours) isn't generated yet.
- Rendering is a single assembly; the split into 2D and 3D assemblies on a render graph comes later.
- Vulkan memory is one allocation per resource; a sub-allocator is needed.
- Vulkan validation layers aren't installed on the dev machine (`sudo apt install vulkan-validationlayers`). They're enabled automatically in Debug builds when present.
- Command buffers are single-threaded, and the job system has no task graph yet: parallelism is within systems only.
- Assets load synchronously on first use; streaming (async, budgeted) is milestone 4. Runtime hot reload of meshes and textures is wired to asset change events but only exercised by tests, not yet by hand.
- Any prefab change invalidates every cached scene and prefab artifact (coarse dependency tracking).
- glTF: one merged mesh per file, no materials, skinning, morph targets or sparse accessors. No JPEG or texture compression yet (BC and ASTC are needed for desktop and Quest).
- Runtime-instantiated binary prefabs keep their source scene ids, so two instances share ids (fine at runtime; the editor will reassign them).
- `Spinners.Build author` remains a sample fixture generator; interactive scene editing now runs in `Gamegrl.Editor`.

**httpgrl (2026-11-16):** M0 is committed (884640a). M1 (client endpoint, pluggable TLS) is done and green but uncommitted, awaiting the owner's go-ahead in that repo. Its public API: `QuicClientEndpoint`, an abstract `QuicTlsContext` provider (built-in OpenSSL `Server`/`Client`/`ClientWithoutVerification`), `QuicOpenSsl.UseLibraries`, `QuicClock`, `IQuicDispatcher`. Console note: ChaCha20 header protection still uses libcrypto, so a non-OpenSSL TLS provider should offer only AES-GCM suites.

## Open questions

| Question | Notes |
| --- | --- |
| What logged the user out at about 05:46 on 2026-11-16? | It happened after the desktop and headless smoke tests passed. Coredumps show KDE portal and Plasma crashes, with an X11 I/O failure in Plasma's stack. Kernel GPU and OOM logs aren't readable from this account, so the cause is unconfirmed. On 2026-11-18 all three smoke tests, the editor and the player ran again without a recurrence, so the tests are no longer paused. If it happens again, check `journalctl -k` and `dmesg` as root |

## Resolved questions

| Question | Answer (2026-11-16) |
| --- | --- |
| How should in-scene and in-view debug interfaces be drawn? (2026-11-17) | With the pixel UI (`Gamegrl.UI`), the same library games ship; the cell UI is only for editor chrome. See UI |
| Engine name | gamegrl, with `Gamegrl.*` assemblies and namespaces |
| Component storage | Archetypes, plus opt-in sparse sets |
| Scene text format | Our own line-oriented format |
| Audio | First-class and hand-rolled; see Audio |
| Scripting beyond C# | No; C# only |
| Console toolchains | Deferred; keep code friendly to a future C#-to-C++ step and decide with devkit access |
| Platform order after Linux | Web, then Windows, then macOS |
| Audio output | Our own low-latency backends per platform, not SDL3 audio |
| Audio determinism | Only the beat clock must be deterministic; the mix itself need not be bit-reproducible |
| Where VR fits | Optional `Gamegrl.XR` module on OpenXR, with engine-wide constraints from milestone 1; ships as milestone 7 |
| VR targets | PC VR and Meta Quest |
| OpenXR loader | The Khronos loader on every platform, behind our own interface |
| Quest bring-up spike | Yes, right after milestone 1, once a headset is available: a NativeAOT `.so` in a NativeActivity APK, clearing the screen through OpenXR |
| Where the QUIC library lives | In the httpgrl repo as public libraries, consumed by project reference |
