#!/usr/bin/env bash
# Build everything, run the tests under the JIT and as a NativeAOT binary, and publish the sample as NativeAOT.
# "It builds" is not "it works under AOT": the AOT test run is the one that counts.
set -euo pipefail
cd "$(dirname "$0")/.."

echo "== build"
dotnet build gamegrl.slnx -c Release -nologo -v q

echo "== tests (JIT)"
dotnet run --project tests/Gamegrl.Tests -c Release --no-build

echo "== tests (NativeAOT)"
dotnet publish tests/Gamegrl.Tests -c Release -o artifacts/tests-aot -nologo -v q
./artifacts/tests-aot/Gamegrl.Tests

echo "== sample: NativeAOT player + packed assets"
dotnet publish samples/Spinners/Player -c Release -o artifacts/spinners -nologo -v q
dotnet run --project samples/Spinners/Build -c Release --no-build -- pack artifacts/spinners/spinners.ggpak
ls -la artifacts/spinners/Spinners artifacts/spinners/spinners.ggpak
echo "ok"
