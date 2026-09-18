#!/usr/bin/env bash
# Everything that can say no, before you push (BENCH-AUDIT.md C7).
#
# v2 §1.6 is this ritual in prose, which means it is done from memory or not at all. Here it is a
# command with an exit code, so a hook can run it:
#
#   ln -s ../../bench/gate.sh .git/hooks/pre-push
#
# WHAT IT RUNS, and why in this order: the cheap deterministic things first, so a broken build or a
# blown allocation ceiling costs four seconds rather than forty.
#
#   1. the nine ratchet tests    counts and ceilings, deterministic, ~10 s; the ninth is the
#                                dataset's counting matrix (docs/13 §9.2), which step 40 promised
#   2. --ffi-check               ours and the reference agree on row counts, < 1 s
#   3. --ratio-check             every ratio against the reference, key order included, ~45 s
#
# WHAT IT DOES NOT RUN, and these are decisions rather than omissions:
#
# * `--throughput <touched families>`, which C7 proposed. B8 measured that narrow form at +29% on
#   `fsst` against the full run -- a stable bias, not dispersion -- which puts it OVER its ceiling
#   on a healthy repository. A gate that cries wolf gets disabled. `gate.sh --throughput` runs the
#   FULL axis (54 s) for when the corpus is present and you want it.
# * The BenchmarkDotNet classes. They are a direction, not a gate (§4.2), and 59 seconds of them
#   would double this script for a number nobody can fail on.
# * `--throughput --write --check`, about 90 s with its second-process confirmation (B19); its
#   reference table exists since B14, and the exit chain of IMPL-PLAN.md §3 runs it apart.
# * The Rust cross-check, which needs cargo and a minute: `gate.sh --crosscheck` adds it, and
#   `bench/crosscheck.sh` runs it alone.
#
# A RED RATIO IS NOT BELIEVED THE FIRST TIME while the axes are this close to their margins: the
# script says so and tells you to replay. Two reds out of three is a regression; one is noise, and
# either way it is data (B2).
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$root/bench/Vorticity.Benchmarks"
throughput=0
crosscheck=0
for flag in "$@"; do
    [ "$flag" = "--throughput" ] && throughput=1
    [ "$flag" = "--crosscheck" ] && crosscheck=1
done

ratchets=(PathAllocationTests ScanAllocationTests WriteAllocationTests WrittenSizeTests
          FlatLayoutDecodeCountTests RoundTripCountTests LiveMemoryTests CorpusCoverageTests
          DatasetBudgetTests)

failed=()
step() {
    local name="$1"; shift
    local start elapsed
    start=$SECONDS
    if "$@" > "/tmp/gate-$$.log" 2>&1; then
        elapsed=$((SECONDS - start))
        printf '  ok   %-28s %3ds\n' "$name" "$elapsed"
    else
        elapsed=$((SECONDS - start))
        printf '  FAIL %-28s %3ds\n' "$name" "$elapsed"
        sed 's/^/       /' "/tmp/gate-$$.log" | tail -25
        failed+=("$name")
    fi
    rm -f "/tmp/gate-$$.log"
}

echo "gate: $(git -C "$root" rev-parse --short HEAD)$(git -C "$root" diff --quiet || echo ' (dirty)')"

# One `--filter-class` per class: the flag repeats, and a comma-separated list matches nothing at
# all (exit 5, "zero tests ran") rather than erroring, which is exactly how a gate silently stops
# gating.
filter=()
for class in "${ratchets[@]}"; do filter+=(--filter-class "*$class"); done
step "ratchets (${#ratchets[@]} classes)" \
    dotnet test "$root/Vorticity.slnx" -c Release "${filter[@]}"
step "--ffi-check" dotnet run -c Release --project "$project" -- --ffi-check
step "--ratio-check" dotnet run -c Release --project "$project" -- --ratio-check
[ "$throughput" = 1 ] && step "--throughput --check" \
    dotnet run -c Release --project "$project" -- --throughput --check
[ "$crosscheck" = 1 ] && step "cross-check (cargo)" "$root/bench/crosscheck.sh"

if [ ${#failed[@]} -eq 0 ]; then
    echo "gate: green in ${SECONDS}s."
    exit 0
fi

echo "gate: ${#failed[@]} red (${failed[*]}) in ${SECONDS}s."
case " ${failed[*]} " in
    *--ratio-check*|*--throughput*)
        echo "A ratio gate is red. Replay it twice before believing it: two of three is a" \
             "regression, one of three is noise -- and either way it is data for BENCH-AUDIT.md B2." ;;
esac
exit 1
