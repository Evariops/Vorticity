#!/usr/bin/env bash
# Profiles a scenario of the published report in the Native AOT runner, on macOS.
#
#   bench/profile.sh <cycles|allocations|trace> <scenario> <file.vortex> <rows> [rounds]
#                    [--threads <n>|all] [--out <dir>]
#
# cycles       Instruments' CPU Profiler at its high frequency, a sample every 25 to 30 µs weighed in
#              cycles, folded per function and per source line by bench/profile/cycles.py. Without
#              rounds, enough of them for about two seconds of work.
# allocations  every allocation of every round, stopped on under lldb, with its type, its size and
#              its stack, by bench/profile/allocations.py: the managed ones, whose totals per round
#              are the runner's own, and the native ones, the C allocator and mmap. Nothing is
#              sampled. A stop costs about 5 ms: three rounds by default.
# trace        Processor Trace, every branch the rounds take, for Instruments to open. It needs an
#              M4 or later and Processor Trace allowed in System Settings, Privacy & Security,
#              Developer Tools.
#
# The scenario is one of the report's: open, scan, project, filter-narrow, filter-wide, take, write,
# append. The rows size the bands, the take and the append, as they do in the report. The report
# leaves its fixtures in $TMPDIR/vorticity-report; any other file works, a file of the throughput
# corpus for one encoding at a time.
#
# --threads gives the scans n lanes, or one per processor with `all`, as the report's all-core rows
# do. The cycles report then comes with bench/profile/threads.py's view by thread: who ran in each
# millisecond, and what the caller's thread and the pool's workers spent their cycles on.
#
# The reports land in the output directory, by default under $TMPDIR/vorticity-profile.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
usage() {
    echo "usage: bench/profile.sh <cycles|allocations|trace> <scenario> <file.vortex> <rows> [rounds] [--threads <n>|all] [--out <dir>]" >&2
    exit 2
}
[ $# -ge 4 ] || usage
mode="$1" scenario="$2" file="$3" rows="$4"
shift 4
rounds=""
out=""
threads=""
while [ $# -gt 0 ]; do
    case "$1" in
        --out) out="$2"; shift 2 ;;
        --threads) threads="$2"; shift 2 ;;
        *) rounds="$1"; shift ;;
    esac
done
lanes=()
[ -z "$threads" ] || lanes=(--threads "$threads")
case "$mode" in cycles|allocations|trace) ;; *) usage ;; esac
[ -f "$file" ] || { echo "no such file: $file" >&2; exit 2; }
file="$(cd "$(dirname "$file")" && pwd)/$(basename "$file")"

xctrace="${XCTRACE:-$(xcrun --find xctrace 2>/dev/null || echo /Applications/Xcode.app/Contents/Developer/usr/bin/xctrace)}"
if [ "$mode" != allocations ] && [ ! -x "$xctrace" ]; then
    echo "xctrace not found: install Xcode, or point XCTRACE at it" >&2
    exit 2
fi

echo "publishing the runner"
dotnet publish -c Release "$root/bench/Vorticity.Benchmarks.Runner" -v q --nologo > /dev/null
publish="$root/bench/Vorticity.Benchmarks.Runner/bin/Release/net11.0/osx-arm64/publish"
runner="$publish/Vorticity.Benchmarks.Runner"
[ -d "$runner.dSYM" ] || { echo "no dSYM beside $runner: the source lines need it" >&2; exit 1; }

out="${out:-${TMPDIR:-/tmp}/vorticity-profile/$mode-$scenario-$(basename "$file" .vortex)${threads:+-$threads}}"
rm -rf "$out"
mkdir -p "$out"

if [ -z "$rounds" ]; then
    case "$mode" in
        cycles)
            # Two seconds of work at the rate of a warm round, measured on three.
            warm="$("$runner" --scenario "$scenario" "$file" "$rows" --repeat 3 ${lanes[@]+"${lanes[@]}"} |
                awk -F'work_us=' '/^round=2 /{split($2, a, " "); print a[1]}')"
            rounds=$(( 2000000 / (warm > 0 ? warm : 1) ))
            [ "$rounds" -ge 3 ] || rounds=3
            [ "$rounds" -le 20000 ] || rounds=20000
            ;;
        *) rounds=3 ;;
    esac
fi
echo "$mode of $scenario on $(basename "$file"), $rounds rounds, into $out"

case "$mode" in
    cycles)
        "$xctrace" record --template 'CPU Profiler' --show-recording-options 2> /dev/null |
            sed 's/"highFrequency" : false/"highFrequency" : true/' > "$out/options.json"
        "$xctrace" record --template 'CPU Profiler' --recording-options "$out/options.json" --no-prompt \
            --output "$out/cycles.trace" --target-stdout "$out/runner.txt" \
            --launch -- "$runner" --scenario "$scenario" "$file" "$rows" --repeat "$rounds" ${lanes[@]+"${lanes[@]}"} > /dev/null
        "$xctrace" export --input "$out/cycles.trace" \
            --xpath '/trace-toc/run[@number="1"]/data/table[@schema="cpu-profile"]' \
            --output "$out/cpu-profile.xml" > /dev/null
        python3 "$root/bench/profile/cycles.py" "$out/cpu-profile.xml" "$root" "$out"
        echo "report: $out/cycles.md"
        if [ -n "$threads" ]; then
            python3 "$root/bench/profile/threads.py" "$out/cpu-profile.xml" "$out"
            echo "report: $out/threads.md"
        fi
        ;;
    allocations)
        lldb --batch \
            -o "target symbols add '$runner.dSYM'" \
            -o "command script import '$root/bench/profile/allocations.py'" \
            -o "allocations '$out' '$root' 48 --native" \
            -- "$runner" --scenario "$scenario" "$file" "$rows" --repeat "$rounds" ${lanes[@]+"${lanes[@]}"} |
            grep -E '^(round=|rows=|[0-9]+ allocations|error)' | tee "$out/runner.txt"
        echo "report: $out/allocations.md"
        ;;
    trace)
        "$xctrace" record --template 'Processor Trace' --no-prompt \
            --output "$out/processor.trace" --target-stdout "$out/runner.txt" \
            --launch -- "$runner" --scenario "$scenario" "$file" "$rows" --repeat "$rounds" ${lanes[@]+"${lanes[@]}"}
        echo "open it with: open '$out/processor.trace'"
        ;;
esac
