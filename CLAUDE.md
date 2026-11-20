# gamegrl — ground rules

Read `docs/DESIGN.md` first; it is the source of truth for decisions. Record new decisions in its decision log.

- **NativeAOT always works.** Every project builds with `IsAotCompatible` and `TreatWarningsAsErrors`. No reflection,
  `Reflection.Emit`, `dynamic` or runtime loading in engine or game code; the source generator does that work.
  `scripts/ci.sh` runs the tests as a NativeAOT binary: "it builds" is not "it works under AOT".
- **Few dependencies.** BCL only, plus SDL3 (behind `IPlatform`), the platform audio APIs (ALSA's `libasound` on Linux), OpenSSL (QUIC, via httpgrl), the Khronos OpenXR
  loader (XR module only), an editor-only shader compiler, and the sibling `../ansidotnet` libraries (editor only). Tests are dependency-free (`tests/Gamegrl.Tests/Harness.cs`).
- **Best implementation over backwards compatibility.** Nothing is released: change any API when it makes the code
  clearly better, and update its callers.
- **Efficiency is the default path.** Hot paths are written like C: spans, pointers into component columns, pooled or
  ring-allocated memory, no LINQ and no per-frame allocations. Components are unmanaged structs.
- **Structural changes never happen during iteration**; use the world's `CommandBuffer`.
- **Native interop** uses `LibraryImport` or unmanaged function pointers. Vulkan struct layouts are checked against
  `vulkan_core.h` by `VkLayoutTests`; regenerate the expected sizes when adding structs.
- **Screenshots for verification** capture only the game window (`xwininfo -name ...` then `import -window`), never
  the whole desktop.
- **Commits** use `brosilio` and the GitHub noreply address; commit only when the user asks.
