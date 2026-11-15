#!/usr/bin/env bash
# Who holds the memory of a test run, when it grows. Runs the unit tests, or the test command after --, with the blocks
# counted by test and site (MemoryWatch.cs, VORTICITY_MEMORY_WATCH), and follows the process's resident memory: each time it
# passes another --dump step, a dump of the process is taken (dotnet-dump, a few seconds whatever the heap), and once the run
# ends each dump's managed heap is reported by type, the largest first; past --kill, the run is stopped, so that a run that
# runs away does not take the machine. A walk of the heap of a busy process (dotnet-gcdump) ran ten minutes and did not end.
#
#   tests/memory-watch.sh                                           the unit tests, a block report every 1 GiB, a dump every 8 GiB, stopped past 24 GiB
#   tests/memory-watch.sh --blocks 512 --dump 4096 --kill 12288     tighter steps
#   tests/memory-watch.sh -- -namespace Vorticity.Tests.Dataset     the runner's own filters, after --
#
# Everything lands in --out (memory-watch-<date> under the current directory): blocks-NNN.txt, the tests, sites and pairs of
# both that held the most blocks at each step; heap-NNN.txt, the managed heap by type at each dump step (the dumps, which
# weigh what the process held, are removed once read unless --keep-dumps); rss.tsv, the resident memory every second. A stack
# is taken at each block rent counted: the run is slower, its bytes the same.
set -euo pipefail

blocks=1024
dump=8192
kill=24576
keep=0
out="memory-watch-$(date +%Y%m%d-%H%M%S)"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --blocks) blocks="$2"; shift 2 ;;
    --dump) dump="$2"; shift 2 ;;
    --kill) kill="$2"; shift 2 ;;
    --keep-dumps) keep=1; shift ;;
    --out) out="$2"; shift 2 ;;
    --) shift; break ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

root="$(cd "$(dirname "$0")/.." && pwd)"
dump_tool="$(command -v dotnet-dump || echo "$HOME/.dotnet/tools/dotnet-dump")"
if [[ ! -x "$dump_tool" ]]; then
  echo "dotnet-dump is not installed: dotnet tool install -g dotnet-dump" >&2
  exit 2
fi

mkdir -p "$out"
out="$(cd "$out" && pwd)"
dotnet build "$root/tests/Vorticity.Tests" -c Release -v quiet -nologo >/dev/null

VORTICITY_MEMORY_WATCH="$blocks" VORTICITY_MEMORY_WATCH_DIR="$out" \
  dotnet "$root/tests/Vorticity.Tests/bin/Release/net11.0/Vorticity.Tests.dll" "$@" > "$out/run.log" 2>&1 &
pid=$!

next=$dump
dumps=0
peak=0
started=$SECONDS
printf 'seconds\tresident_mib\n' > "$out/rss.tsv"
while kill -0 "$pid" 2>/dev/null; do
  rss=$(( $(ps -o rss= -p "$pid" | tr -d ' ' || echo 0) / 1024 ))
  (( rss > peak )) && peak=$rss
  printf '%s\t%s\n' "$((SECONDS - started))" "$rss" >> "$out/rss.tsv"
  if (( rss >= next )); then
    name=$(printf 'heap-%03d' "$dumps")
    "$dump_tool" collect -p "$pid" --type Heap -o "$out/$name.dmp" >/dev/null 2>&1 || true
    echo "resident $rss MiB at $((SECONDS - started)) s: $out/$name.dmp"
    dumps=$((dumps + 1))
    next=$((rss + dump))
  fi

  if (( rss >= kill )); then
    echo "resident $rss MiB past $kill MiB: the run is stopped" >&2
    kill "$pid" 2>/dev/null || true
    break
  fi

  sleep 1
done

wait "$pid" 2>/dev/null || true

# Each dump's managed heap by type, the largest first, once the run no longer competes for the machine; a dump taken in the
# middle of a collection is walked all the same. The dumps weigh what the process held, and go once read, unless kept.
for dmp in "$out"/heap-*.dmp; do
  [[ -e "$dmp" ]] || continue
  "$dump_tool" analyze "$dmp" -c "dumpheap -stat -ignoreGCState" -c "exit" 2>&1 | tail -40 > "${dmp%.dmp}.txt" || true
  (( keep == 1 )) || rm -f "$dmp"
done

echo "peak resident $peak MiB over $((SECONDS - started)) s; reports in $out"
grep -E "Total:" "$out/run.log" | tail -1 || true
