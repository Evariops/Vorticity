#!/usr/bin/env bash
# bench/host.sh: the library inside a host that answers for a while (vortex-queries --micro host), once under each collector.
#
#   bench/host.sh [--modes workstation,server,fixed] [--limit SIZE] [--log DIR] [-- options of --micro host]
#
# Modes (default: all three):
#   workstation  one heap, the default of a console app            DOTNET_gcServer=0
#   server       a heap a core, their number adapted to the load    DOTNET_gcServer=1, dynamic adaptation on, as an ASP.NET
#                Core app runs by default
#   fixed        two heaps, no adaptation                           DOTNET_gcServer=1 DOTNET_GCHeapCount=2
#                DOTNET_GCDynamicAdaptationMode=0
#
# Each run is held to --limit (3g), as a container's limit would hold it, and prints a line every --sample seconds, then the
# free space of the pinned heap in each half of the run: a free space that grows from the first half to the second is what a
# host sees over hours. The pinned heap is the collector's, shared with the host's own pinned blocks (a web server's pool of
# 4 KiB), which the scenario makes too. Logs go to --log (the scratch of the bench stores), one per mode.
set -euo pipefail

repo=$(cd "$(dirname "$0")/.." && pwd)
modes=workstation,server,fixed
limit=3g
log=
while [ $# -gt 0 ]; do
  case $1 in
    --modes) modes=$2; shift 2 ;;
    --limit) limit=$2; shift 2 ;;
    --log) log=$2; shift 2 ;;
    --) shift; break ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
done

out=$(cd "$repo" && dotnet build bench/Vorticity.Benchmarks.Queries -c Release 2>&1) || { echo "$out" | grep -E ' error ' | sort -u >&2; exit 1; }
exe=$repo/bench/Vorticity.Benchmarks.Queries/bin/Release/net11.0/vortex-queries.dll
log=${log:-${TMPDIR:-/tmp}/vortex-host}
mkdir -p "$log"
stamp=$(date +%Y%m%d-%H%M%S)

IFS=, read -r -a chosen <<<"$modes"
for mode in "${chosen[@]}"; do
  case $mode in
    workstation) env=(DOTNET_gcServer=0) ;;
    server) env=(DOTNET_gcServer=1 DOTNET_GCDynamicAdaptationMode=1) ;;
    fixed) env=(DOTNET_gcServer=1 DOTNET_GCHeapCount=2 DOTNET_GCDynamicAdaptationMode=0) ;;
    *) echo "unknown mode $mode" >&2; exit 2 ;;
  esac
  file=$log/host-$stamp-$mode.log
  echo "== $mode -> $file"
  env "${env[@]}" dotnet "$exe" --micro host --memory-limit "$limit" "$@" 2>&1 | tee "$file"
done
