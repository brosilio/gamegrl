#!/usr/bin/env bash
# Runs Box2D v3's benchmark and gamegrl's port of it (tests/Gamegrl.PhysicsBench) on this machine, interleaved, and
# prints a table of best times. Box2D is fetched at a pinned commit and built once into the cache directory.
#
#   scripts/box2d-compare.sh [scene...] [--workers "1 4"] [--repeats 3]
#
# Box2D runs its 4-wide SSE2 path (--simd-width=4), the same width as gamegrl's solver. Close other busy programs
# first: the multithreaded runs are sensitive to load.
set -euo pipefail
cd "$(dirname "$0")/.."

BOX2D_COMMIT=2b14ae3feebce864131e24799e3c1c5484b431bc
CACHE="${XDG_CACHE_HOME:-$HOME/.cache}/gamegrl/box2d"
WORKERS="1 4"
REPEATS=3
SCENES=()
while [[ $# -gt 0 ]]; do
    case "$1" in
        --workers) WORKERS="$2"; shift 2 ;;
        --repeats) REPEATS="$2"; shift 2 ;;
        *) SCENES+=("$1"); shift ;;
    esac
done

BOX2D="${BOX2D_DIR:-$CACHE}"
if [[ ! -x "$BOX2D/build/bin/benchmark" ]]; then
    echo "== building Box2D $BOX2D_COMMIT in $BOX2D"
    if [[ ! -d "$BOX2D/.git" ]]; then
        mkdir -p "$BOX2D"
        git clone -q https://github.com/erincatto/box2d.git "$BOX2D"
    fi
    git -C "$BOX2D" checkout -q "$BOX2D_COMMIT"
    cmake -S "$BOX2D" -B "$BOX2D/build" -DCMAKE_BUILD_TYPE=Release -DBOX2D_SAMPLES=OFF -DBOX2D_UNIT_TESTS=OFF -DBOX2D_BENCHMARKS=ON > /dev/null
    cmake --build "$BOX2D/build" --target benchmark -j > /dev/null
fi

echo "== publishing gamegrl's benchmark (NativeAOT)"
dotnet build-server shutdown > /dev/null 2>&1 || true
dotnet publish tests/Gamegrl.PhysicsBench -c Release -o artifacts/physics-bench -nologo -v q -m:1 > /dev/null
dotnet build-server shutdown > /dev/null 2>&1 || true
OURS=artifacts/physics-bench/Gamegrl.PhysicsBench
[[ ${#SCENES[@]} -eq 0 ]] && mapfile -t SCENES < <("$OURS" -l)

# Box2D writes step logs into its working directory: run it from a scratch one.
SCRATCH=$(mktemp -d)
trap 'rm -rf "$SCRATCH"' EXIT
best() { awk '/^run [0-9]+ :/ { v = $4 + 0; if (min == "" || v < min) min = v } END { print min }'; }

printf "%-14s %7s %12s %12s %8s\n" scene workers "box2d ms" "gamegrl ms" ratio
for scene in "${SCENES[@]}"; do
    for w in $WORKERS; do
        theirs=$( (cd "$SCRATCH" && "$BOX2D/build/bin/benchmark" --benchmark="$scene" --workers="$w" --repeats="$REPEATS" --simd-width=4) | best)
        ours=$("$OURS" -b="$scene" -w="$w" -r="$REPEATS" | best)
        ratio=$(awk -v a="$ours" -v b="$theirs" 'BEGIN { if (b > 0) printf "%.2f", a / b; else print "-" }')
        printf "%-14s %7s %12s %12s %8s\n" "$scene" "$w" "$theirs" "$ours" "$ratio"
    done
done
