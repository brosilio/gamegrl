#!/usr/bin/env bash
# Build everything, run the tests under the JIT and as a NativeAOT binary, run the benchmarks, and publish the sample as NativeAOT.
# "It builds" is not "it works under AOT": the AOT test run is the one that counts.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== build"
dotnet build gamegrl.slnx -c Release -nologo -v q -m:1

echo "== tests (JIT)"
dotnet run --project tests/Gamegrl.Tests -c Release --no-build
dotnet run --project tests/Gamegrl.Editor.Tests -c Release --no-build

if [[ "${GAMEGRL_TERMINAL_SMOKE:-0}" == "1" ]]; then
    echo "== terminal editor (headless Vulkan)"
    dotnet run --project tests/Gamegrl.Editor.Tests -c Release --no-build -- --terminal-smoke
fi

echo "== determinism analyzer (the fixture must fail with exactly its 7 violations)"
if dotnet build tests/Gamegrl.DeterminismFixture -c Release -nologo -v q > artifacts/analyzer-fixture.log 2>&1; then
    echo "the determinism fixture built, so the analyzer reported nothing"; exit 1
fi
found=$(grep -oE "Violations.cs\([0-9]+,[0-9]+\): error GG020[12]" artifacts/analyzer-fixture.log | sort -u | wc -l)
[[ "$found" == "7" ]] || { echo "expected 7 determinism diagnostics, found $found"; cat artifacts/analyzer-fixture.log; exit 1; }

echo "== tests (NativeAOT)"
dotnet publish tests/Gamegrl.Tests -c Release -o artifacts/tests-aot -nologo -v q -m:1
./artifacts/tests-aot/Gamegrl.Tests

# CrossPlatform determinism: the golden hashes must also hold for code compiled for another instruction set (AVX2),
# which is where fused multiply-add and wider SIMD would show up.
echo "== determinism (NativeAOT, x86-64-v3)"
dotnet publish tests/Gamegrl.Tests -c Release -o artifacts/tests-aot-v3 -p:IlcInstructionSet=x86-64-v3 -nologo -v q -m:1
./artifacts/tests-aot-v3/Gamegrl.Tests determinism
./artifacts/tests-aot-v3/Gamegrl.Tests math
./artifacts/tests-aot-v3/Gamegrl.Tests physics2d

# Audio under NativeAOT: the ALSA interop must compile; render the smoke program offline and check its beats land
# exactly one beat apart. GAMEGRL_AUDIO_DEVICE=1 also plays it on the default ALSA device (fails on underruns).
echo "== audio (NativeAOT smoke)"
dotnet publish tests/Gamegrl.AudioSmoke -c Release -o artifacts/audio-smoke -nologo -v q -m:1
./artifacts/audio-smoke/Gamegrl.AudioSmoke --wav artifacts/audio-smoke/smoke.wav --layout 5.1 --seconds 2 > /dev/null
./artifacts/audio-smoke/Gamegrl.AudioSmoke --analyze artifacts/audio-smoke/smoke.wav --expect-spacing 24000 | tail -1
if [[ "${GAMEGRL_AUDIO_DEVICE:-0}" == "1" ]]; then
    ./artifacts/audio-smoke/Gamegrl.AudioSmoke --seconds 2
fi

# Performance: scenarios under the profiler, gated against perf/baselines/<machine>.json (allocations exactly, times
# within a tolerance). No baseline for this machine only warns. GAMEGRL_BENCH=0 skips this on a loaded machine.
if [[ "${GAMEGRL_BENCH:-1}" == "1" ]]; then
    echo "== bench (NativeAOT engine scenarios)"
    dotnet publish tests/Gamegrl.Bench -c Release -o artifacts/bench-aot -nologo -v q -m:1
    ./artifacts/bench-aot/Gamegrl.Bench
    echo "== bench (editor, terminal host)"
    dotnet run --project tests/Gamegrl.Editor.Tests -c Release --no-build -- --bench
fi

echo "== sample: NativeAOT player + packed assets"
dotnet publish samples/Spinners/Player -c Release -o artifacts/spinners -nologo -v q -m:1
dotnet run --project samples/Spinners/Build -c Release --no-build -- pack artifacts/spinners/spinners.ggpak
ls -la artifacts/spinners/Spinners artifacts/spinners/spinners.ggpak
echo "ok"
