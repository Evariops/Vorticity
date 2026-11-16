#!/usr/bin/env bash
# bench/speed.sh: the short benches of the throughput work, each run once per source tree and kept in a ledger.
#
#   bench/speed.sh [--only B,B…] [--degree N,N…] [--profile] [--again] [--base REF|TREE] [--digest] [--note TEXT]
#
# Benches (default: logs,metrics):
#   logs     one tier merge of 5 job outputs of log records, 7.6 M rows, 2 GiB        (~15 s a round)
#   metrics  one consolidation of 74 appends of metric records, 51.84 M rows, 2 GiB    (~20 s a round)
#   append   24 appends of 65 536 metric records into a dataset with variants          (seconds)
#   scan, filter, write   the report's scenarios on its 10 485 760-row file, in the framework-dependent runner, 12 rounds
#
# What a run brings back, all at once (the ledger's columns): the cold and the warm time (two rounds in one process, the
# second compared), the passes, the processor and write-lane seconds, the occupancy (processor over wall over degree),
# the rows, the inputs, the output and scratch sizes, a digest of the bytes written (decrypted, the same bytes at any
# degree and on any run), the resident and block peaks, the full collections and the machine's load. --digest adds the
# rows' digest, which takes four to five times the job and is for a change of what is written. --profile is a run of its
# own (mode p10): `sample` at 10 ms on the last round only, with the JIT's perf map, summarised by bench/profile/samples.py
# into roles, top functions, threads and spinners; never compared with plain runs, whose timing it distorts.
#
# A run is keyed by the hash of the src/ tree it compiled, the working tree's changes included: a commit of the bench, the
# docs or the tests alone keeps the key, and a tree measured is not measured again unless --again. The base it is compared
# with is HEAD's src/ tree when the working tree changes it, else the first commit before HEAD whose src/ differs; the
# base's figures come from the ledger and are never run again. The ledger and the kept stores live in
# ~/.cache/vorticity/speed (VORTICITY_SPEED to move them).
set -u
repo=$(cd "$(dirname "$0")/.." && pwd)
home=${VORTICITY_SPEED:-$HOME/.cache/vorticity/speed}
ledger=$home/ledger.tsv
mkdir -p "$home/logs"
only="logs,metrics"; degrees="14"; profile=0; again=0; base=""; digest=0; note=""
while [ $# -gt 0 ]; do
  case $1 in
    --only) only=$2; shift ;;
    --degree) degrees=$2; shift ;;
    --profile) profile=1 ;;
    --again) again=1 ;;
    --base) base=$2; shift ;;
    --digest) digest=1 ;;
    --note) note=$2; shift ;;
    *) echo "unknown argument $1" >&2; exit 2 ;;
  esac
  shift
done

header="date	src	commit	bench	degree	mode	cold_s	job_s	write_s	plan_s	given_up_s	cpu_s	lanes_s	rows	inputs	out_MB	scratch_MiB	bytes	digest	resident_MiB	blocks_MiB	gc2	occupancy	load	cores	roles	log	note"
[ -f "$ledger" ] || echo "$header" > "$ledger"

index=$(mktemp); cp "$repo/.git/index" "$index"
tree=$(GIT_INDEX_FILE=$index git -C "$repo" add -A src >/dev/null 2>&1; GIT_INDEX_FILE=$index git -C "$repo" write-tree --prefix=src/)
rm -f "$index"
head_src=$(git -C "$repo" rev-parse HEAD:src)
if [ -z "$base" ]; then
  if [ "$tree" != "$head_src" ]; then base=$head_src; else
    for c in $(git -C "$repo" rev-list --max-count=80 HEAD~1); do t=$(git -C "$repo" rev-parse "$c:src"); [ "$t" != "$tree" ] && { base=$t; break; }; done
  fi
else
  base=$(git -C "$repo" rev-parse "$base:src" 2>/dev/null || echo "$base")
fi
commit=$(git -C "$repo" rev-parse --short HEAD); [ "$tree" != "$head_src" ] && commit="$commit+"
mode=$([ $profile = 1 ] && echo p10 || echo plain)
echo "tree ${tree:0:10} ($commit), base ${base:0:10}, mode $mode"

queries=$repo/bench/Vorticity.Benchmarks.Queries/bin/Release/net11.0
runner=$repo/bench/Vorticity.Benchmarks.Runner/bin/Release/net11.0/Vorticity.Benchmarks.Runner.dll
fixture=$home/mixed-10485760.vortex
built_queries=0; built_runner=0
build() {
  if (cd "$repo" && dotnet build "$1" -c Release 2>&1 | grep -E ' error |rr(o|eu)r\(s\)' | sort -u | grep -v ' 0 Err'); then echo "build of $1 failed" >&2; exit 1; fi
}

measured() { awk -F'\t' -v t="$tree" -v b="$1" -v d="$2" -v m="$mode" '$2==t && $4==b && $5==d && $6==m {f=1} END {exit !f}' "$ledger"; }

# Everything the runs need is built before the first one, from the tree whose hash keys them: a build between two runs
# would compile whatever the working tree became meanwhile.
for bench in ${only//,/ }; do
  case $bench in
    logs|metrics|append) [ $built_queries = 0 ] && { build bench/Vorticity.Benchmarks.Queries; built_queries=1; } ;;
    scan|filter|write)
      [ $built_runner = 0 ] && { build bench/Vorticity.Benchmarks.Runner; built_runner=1; }
      if [ ! -f "$fixture" ]; then
        build bench/Vorticity.Benchmarks
        (cd "$repo" && dotnet bench/Vorticity.Benchmarks/bin/Release/net11.0/Vorticity.Benchmarks.dll --fixture 10485760 "$fixture") || exit 1
      fi ;;
  esac
done

for bench in ${only//,/ }; do
  for degree in ${degrees//,/ }; do
    if [ $again = 0 ] && measured "$bench" "$degree"; then echo "$bench at $degree: tree already measured, not run again"; continue; fi
    stamp=${tree:0:10}-$bench-d$degree-$(date +%H%M%S)
    log=$home/logs/$stamp.log
    kind=compact; cores="-"; roles="-"
    case $bench in
      logs) args=(--micro backlog --store "$home/logtier" --logs --jobs 1 --compact-only --memory-limit 2g --degree "$degree" --repeat 2 --bytes-digest) ;;
      metrics) args=(--micro backlog --store "$home/metrictier" --jobs 1 --compact-only --memory-limit 2g --degree "$degree" --repeat 2 --bytes-digest) ;;
      append) kind=append; args=(--micro backlog --store "$home/append" --build-only --appends 24 --rows 65536 --memory-limit 2g --degree "$degree") ;;
      scan) kind=runner; args=(--scenario scan "$fixture" 10485760 --repeat 12 --threads "$degree") ;;
      filter) kind=runner; args=(--scenario filter-wide "$fixture" 10485760 --repeat 12 --threads "$degree") ;;
      write) kind=runner; args=(--scenario write "$fixture" 10485760 --repeat 12 --threads "$degree") ;;
      *) echo "no bench named $bench" >&2; exit 2 ;;
    esac
    [ $digest = 1 ] && [ $kind = compact ] && args+=(--digest)
    exe=("$queries/vortex-queries.dll"); [ $kind = runner ] && exe=("$runner")
    if [ $profile = 1 ]; then
      [ $kind = compact ] && args+=(--quick-exit)
      sample_file=$home/logs/$stamp.sample; map=$home/logs/$stamp.map; summary=$home/logs/$stamp.profile.txt
      (cd "$repo" && DOTNET_PerfMapEnabled=1 exec dotnet "${exe[@]}" "${args[@]}" > "$log" 2>&1) &
      pid=$!
      # The last round alone: a compaction waits a second and a half after saying so, a runner is sampled from its start.
      if [ $kind = compact ]; then
        for _ in $(seq 1 600); do grep -q '^round 2 starts' "$log" 2>/dev/null && break; kill -0 $pid 2>/dev/null || break; sleep 0.2; done
      else
        sleep 0.5
      fi
      sample $pid 600 10 -mayDie -file "$sample_file" > /dev/null 2>&1
      cp "/tmp/perf-$pid.map" "$map" 2>/dev/null
      wait $pid
      line=$(python3 -I "$repo/bench/profile/samples.py" "$sample_file" "$map" "$summary" 2>/dev/null || printf -- '-\t-')
      cores=${line%%	*}; roles=${line#*	}
      gzip -f "$sample_file"
    else
      (cd "$repo" && dotnet "${exe[@]}" "${args[@]}" > "$log" 2>&1) || { echo "$bench at $degree failed, see $log" >&2; tail -5 "$log" >&2; continue; }
    fi
    fields=$(python3 -I "$repo/bench/profile/speed_line.py" $kind "$log")
    load=$(uptime | sed -E 's/.*averages?: ([0-9.,]+).*/\1/' | tr -d ',')
    occupancy=$(echo "$fields" | awk -F'\t' -v d="$degree" '{ if ($2 != "-" && $6 != "-" && $2 > 0) printf "%.3f", $6 / $2 / d; else print "-" }')
    printf '%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(date '+%m-%d %H:%M')" "$tree" "$commit" "$bench" "$degree" "$mode" \
      "$fields" "$occupancy" "$load" "$cores" "$roles" "$(basename "$log")" "$note" >> "$ledger"
    sleep 2
  done
done

python3 -I "$repo/bench/profile/speed_compare.py" "$ledger" "$tree" "$base" "$mode" "$only" "$degrees"
