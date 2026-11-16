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

echo "== sample (NativeAOT)"
dotnet publish samples/Spinners -c Release -o artifacts/spinners -nologo -v q
ls -la artifacts/spinners/Spinners
echo "ok"
