#!/usr/bin/env bash
# A/B between two Native AOT builds of the perf tool, for changes that cannot be switched at run
# time (a table format, an assembly attribute). Builds HEAD (or the ref given) into a worktree as
# "before", the working tree as "after", then runs them alternately and compares their medians.
#
#   bench/zstd-ab-aot.sh [--ref <git-ref>] [--rounds N] [--frames a,b] [--compress | --micro <name>]
#
# --compress times compression (Vorticity.Zstd.Perf --compress) instead of decompression; --micro times a
# micro-benchmark (Vorticity.Zstd.Perf --micro <name>, e.g. dcorpus with --frames github-dict-L3).
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ref=HEAD; rounds=8; frames=reference,text-L3,json-L19; mode=(); micro=
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) ref="$2"; shift 2 ;;
    --rounds) rounds="$2"; shift 2 ;;
    --frames) frames="$2"; shift 2 ;;
    --compress) mode=(--compress); shift ;;
    --micro) micro="$2"; shift 2 ;;
    *) echo "unknown $1" >&2; exit 2 ;;
  esac
done
work="$root/artifacts/ab"
mkdir -p "$work"
rm -rf "$work/tree"
git -C "$root" worktree add --detach "$work/tree" "$ref" > /dev/null 2>&1 || { git -C "$root" worktree prune; git -C "$root" worktree add --detach "$work/tree" "$ref" > /dev/null; }
# The worktree has neither the native reference nor the data git does not keep (the test data, the
# benchmark corpora): point it at the main tree's.
ln -sfn "$root/tools/native-ref/out" "$work/tree/tools/native-ref/out"
[[ -e "$work/tree/tests/Vorticity.Zstd.Tests/testdata" ]] || ln -s "$root/tests/Vorticity.Zstd.Tests/testdata" "$work/tree/tests/Vorticity.Zstd.Tests/testdata"
dotnet publish "$work/tree/bench/Vorticity.Zstd.Perf/Vorticity.Zstd.Perf.csproj" -c Release -o "$work/before" > /dev/null
dotnet publish "$root/bench/Vorticity.Zstd.Perf/Vorticity.Zstd.Perf.csproj" -c Release -o "$work/after" > /dev/null
git -C "$root" worktree remove --force "$work/tree"
for frame in ${frames//,/ }; do
  b=(); a=()
  for ((i = 0; i < rounds; i++)); do
    if [[ -n "$micro" ]]; then
      b+=("$("$work/before/Vorticity.Zstd.Perf" --micro "$micro" --frames "$frame" | sed -nE 's/.*: +([0-9.]+) ns.*/\1/p')")
      a+=("$("$work/after/Vorticity.Zstd.Perf" --micro "$micro" --frames "$frame" | sed -nE 's/.*: +([0-9.]+) ns.*/\1/p')")
    else
      b+=("$("$work/before/Vorticity.Zstd.Perf" ${mode[@]+"${mode[@]}"} --frames "$frame" --only zstd --passes 1 --reps 300 | awk '/^  zstd/ {print $4}')")
      a+=("$("$work/after/Vorticity.Zstd.Perf" ${mode[@]+"${mode[@]}"} --frames "$frame" --only zstd --passes 1 --reps 300 | awk '/^  zstd/ {print $4}')")
    fi
  done
  python3 - "$frame" "${b[*]}" "${a[*]}" <<'PY'
import sys, statistics
frame, b, a = sys.argv[1], list(map(float, sys.argv[2].split())), list(map(float, sys.argv[3].split()))
mb, ma = statistics.median(b), statistics.median(a)
pairs = " ".join(f"{x / y:.3f}" for x, y in zip(b, a))
print(f"{frame:16} before {mb:9.1f}  after {ma:9.1f}  speedup {mb / ma:.3f}x  (per round: {pairs})")
PY
done
