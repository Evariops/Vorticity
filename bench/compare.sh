#!/usr/bin/env bash
# Two benchmark runs, compared case by case (BENCH-AUDIT.md C2).
#
# A run is a directory of BenchmarkDotNet artifacts. Produce one per commit you want to judge,
# named so you can tell them apart later:
#
#   bench/compare.sh --record before FastLanesKernel
#   ... change something, rebuild ...
#   bench/compare.sh --record after FastLanesKernel
#   bench/compare.sh before after
#
# The comparison is Mann-Whitney on every recorded iteration, exact at the five the fast profile
# takes, with a 5% threshold and a 0.5 ns floor: the same rule as the in-run `MannWhitney(5%)`
# column, so one run and two runs cannot disagree about what counts.
#
# A name resolves under bench/.runs/; a path is used as given.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
runs="$root/bench/.runs"
project="$root/bench/Vorticity.Benchmarks"

resolve() {
    if [ -e "$1" ]; then printf '%s' "$1"; else printf '%s' "$runs/$1"; fi
}

if [ "${1:-}" = "--record" ]; then
    shift
    [ $# -ge 1 ] || { echo "usage: bench/compare.sh --record <name> [benchmark filter…]" >&2; exit 2; }
    name="$1"; shift
    out="$runs/$name"
    rm -rf "$out"
    echo "recording $name at $(git -C "$root" rev-parse --short HEAD)$(git -C "$root" diff --quiet || echo ' (dirty)')"
    dotnet run -c Release --project "$project" -- "$@" --artifacts "$out"
    exit $?
fi

[ $# -eq 2 ] || { echo "usage: bench/compare.sh [--record <name> …] <base> <diff>" >&2; exit 2; }
exec dotnet run -c Release --project "$project" -- --compare "$(resolve "$1")" "$(resolve "$2")"
