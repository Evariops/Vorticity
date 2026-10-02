#!/usr/bin/env bash
# Builds tools/pmu/out/libpmu.dylib, the hardware counters Vorticity.Zstd.Perf --pmu reads.
set -eu
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
mkdir -p "$here/out"
clang -O2 -dynamiclib -o "$here/out/libpmu.dylib" "$here/pmu.c"
echo "$here/out/libpmu.dylib"
