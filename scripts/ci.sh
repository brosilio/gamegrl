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

echo "== tests (NativeAOT)"
dotnet publish tests/Gamegrl.Tests -c Release -o artifacts/tests-aot -nologo -v q -m:1
./artifacts/tests-aot/Gamegrl.Tests

# CrossPlatform determinism: the golden hashes must also hold for code compiled for another instruction set (AVX2),
# which is where fused multiply-add and wider SIMD would show up.
echo "== determinism (NativeAOT, x86-64-v3)"
dotnet publish tests/Gamegrl.Tests -c Release -o artifacts/tests-aot-v3 -p:IlcInstructionSet=x86-64-v3 -nologo -v q -m:1
./artifacts/tests-aot-v3/Gamegrl.Tests determinism
./artifacts/tests-aot-v3/Gamegrl.Tests math

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
