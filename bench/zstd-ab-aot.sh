#!/usr/bin/env bash
# A/B between two Native AOT builds of the perf tool, for changes that cannot be switched at run
# time (a table format, an assembly attribute, a hot loop's code). Builds HEAD (or the ref given)
# into a worktree as "before", the working tree as "after", then runs them alternately and
# compares their medians.
#
#   bench/zstd-ab-aot.sh [--ref <git-ref>] [--rounds N] [--frames a,b] [--compress | --micro <name>] [--core N]
#                        [--no-build | --before <build> --after <build>]
#
# --compress times compression (Vorticity.Zstd.Perf --compress) instead of decompression; --micro times a
# micro-benchmark (Vorticity.Zstd.Perf --micro <name>, e.g. dcorpus with --frames github-dict-L3).
# --core N keeps the timed thread on logical processor N (Windows; see Program.cs). --no-build
# compares the two builds the last run left, for more rounds or other frames; --before X and
# --after Y compare two builds kept under artifacts/ab (implying --no-build).
#
# Both builds are published under artifacts/ab, in this tree: the tool finds the native reference
# and the data git does not keep (tools/native-ref/out, the test data) by walking up from itself.
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ref=HEAD; rounds=8; frames=reference,text-L3,json-L19; mode=(); micro=; pin=(); build=1; before=before; after=after
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) ref="$2"; shift 2 ;;
    --rounds) rounds="$2"; shift 2 ;;
    --frames) frames="$2"; shift 2 ;;
    --compress) mode=(--compress); shift ;;
    --micro) micro="$2"; shift 2 ;;
    --core) pin=(--core "$2"); shift 2 ;;
    --no-build) build=0; shift ;;
    --before) before="$2"; build=0; shift 2 ;;
    --after) after="$2"; build=0; shift 2 ;;
    *) echo "unknown $1" >&2; exit 2 ;;
  esac
done
work="$root/artifacts/ab"
mkdir -p "$work"
if [[ $build == 1 ]]; then
  rm -rf "$work/tree"
  git -C "$root" worktree add --detach "$work/tree" "$ref" > /dev/null 2>&1 || { git -C "$root" worktree prune; git -C "$root" worktree add --detach "$work/tree" "$ref" > /dev/null; }
  dotnet publish "$work/tree/bench/Vorticity.Zstd.Perf/Vorticity.Zstd.Perf.csproj" -c Release -o "$work/before" > /dev/null
  dotnet publish "$root/bench/Vorticity.Zstd.Perf/Vorticity.Zstd.Perf.csproj" -c Release -o "$work/after" > /dev/null
  git -C "$root" worktree remove --force "$work/tree"
fi

run() {
  local side="$1" frame="$2"
  if [[ -n "$micro" ]]; then
    "$work/$side/Vorticity.Zstd.Perf" ${pin[@]+"${pin[@]}"} --micro "$micro" --frames "$frame" | sed -nE 's/.*: +([0-9.]+) ns.*/\1/p'
  else
    "$work/$side/Vorticity.Zstd.Perf" ${pin[@]+"${pin[@]}"} ${mode[@]+"${mode[@]}"} --frames "$frame" --only zstd --passes 1 --reps 300 | awk '/^  zstd/ {print $4}'
  fi
}

for frame in ${frames//,/ }; do
  b=(); a=()
  for ((i = 0; i < rounds; i++)); do
    b+=("$(run "$before" "$frame")")
    a+=("$(run "$after" "$frame")")
  done
  awk -v frame="$frame" -v b="${b[*]}" -v a="${a[*]}" '
    function median(v, n,   i, j, t, s) {
      for (i = 1; i <= n; i++) s[i] = v[i]
      for (i = 2; i <= n; i++) for (j = i; j > 1 && s[j - 1] > s[j]; j--) { t = s[j]; s[j] = s[j - 1]; s[j - 1] = t }
      return n % 2 ? s[(n + 1) / 2] : (s[n / 2] + s[n / 2 + 1]) / 2
    }
    BEGIN {
      n = split(b, before, " "); split(a, after, " ")
      for (i = 1; i <= n; i++) pairs = pairs sprintf(" %.3f", before[i] / after[i])
      mb = median(before, n); ma = median(after, n)
      printf "%-16s before %9.1f  after %9.1f  speedup %.3fx  (per round:%s)\n", frame, mb, ma, mb / ma, pairs
    }'
done
