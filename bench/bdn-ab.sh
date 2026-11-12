#!/usr/bin/env bash
# One BenchmarkDotNet class against an earlier commit: the gain a bench's comment claims of a change, measured again.
#
#   bench/bdn-ab.sh <before-commit> '<filter>' [rounds] [--copy <bench file>]... [-- <bench args>...]
#
#   bench/bdn-ab.sh HEAD~1 '*AuditDeletedCounts*' 2
#   bench/bdn-ab.sh HEAD~1 '*AuditTieredMoves*' 2 -- --case "Moves=true,RowsPerAppend=4096,Scale=1,Alternate=false"
#   bench/bdn-ab.sh 0142b197~1 '*AuditFileStoreReads*' 2 --copy bench/Vorticity.Benchmarks/Audit/AuditFileStoreReadsBenchmarks.cs
#
# A bench measures the code as it is, and most carry no baseline of their own. This builds <before-commit> in a worktree
# under bench/.ab/ (git-ignored, removed on exit), then runs the bench there and in the working tree in turn, ROUNDS
# times (2 by default), each side its own BenchmarkDotNet process in --explore mode, and prints each side's rows of the
# summary: interleaved, so that a drift of the machine falls on both sides. Judge a change against its own parent.
#
# A bench added or changed since <before-commit> is copied into the worktree with --copy, a path from the repository's
# root, before it builds: today's bench, that commit's library. One that does not build there says the API moved.
#
# Run nothing else meanwhile: a test suite or another bench beside it moves both sides by more than most changes do.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
usage="usage: bench/bdn-ab.sh <before-commit> '<filter>' [rounds] [--copy <bench file>]... [-- <bench args>...]"
[ $# -ge 2 ] || { echo "$usage" >&2; exit 2; }
before_commit="$1"
filter="$2"
shift 2
rounds=2
if [ $# -ge 1 ] && [[ "$1" =~ ^[0-9]+$ ]]; then
    rounds="$1"
    shift
fi

copies=()
while [ $# -ge 1 ] && [ "$1" = "--copy" ]; do
    [ $# -ge 2 ] || { echo "$usage" >&2; exit 2; }
    copies+=("$2")
    shift 2
done

if [ $# -ge 1 ] && [ "$1" = "--" ]; then
    shift
fi

project="bench/Vorticity.Benchmarks"
tree=""

# A worktree stays registered after its directory goes: removed, then pruned, as bench/ab.sh does.
cleanup() {
    if [ -n "$tree" ]; then
        git -C "$root" worktree remove --force "$tree" > /dev/null 2>&1
    fi

    git -C "$root" worktree prune > /dev/null 2>&1
}
trap cleanup EXIT

sha="$(git -C "$root" rev-parse --short "$before_commit")" || exit 2
tree="$root/bench/.ab/bdn-before-$sha"
echo "before: $sha $(git -C "$root" log -1 --format=%s "$sha")"
echo "after:  the working tree, $(git -C "$root" rev-parse --short HEAD) $(git -C "$root" log -1 --format=%s HEAD)"
git -C "$root" worktree remove --force "$tree" > /dev/null 2>&1
rm -rf "$tree"
git -C "$root" worktree prune > /dev/null 2>&1
git -C "$root" worktree add --detach "$tree" "$sha" > /dev/null 2>&1 || {
    echo "could not create a worktree at $tree" >&2; exit 2; }

for copy in ${copies[@]+"${copies[@]}"}; do
    mkdir -p "$(dirname "$tree/$copy")"
    cp "$root/$copy" "$tree/$copy" || exit 2
done

echo "building both sides..."
log="$root/bench/.ab/bdn-build.log"
for side in "$tree" "$root"; do
    dotnet build "$side/$project" -c Release -v q --nologo > "$log" 2>&1 || {
        echo "the benchmarks do not build in $side:" >&2
        tail -20 "$log" >&2
        exit 2; }
done

header=""
for round in $(seq 1 "$rounds"); do
    for side in before after; do
        dir="$tree"
        [ "$side" = after ] && dir="$root"
        rows="$(cd "$dir" && dotnet run -c Release --no-build --project "$project" -- --explore --filter "$filter" "$@" 2>&1 \
            | grep -E '^\| ' | grep -vE '^\|-')"
        if [ -z "$header" ]; then
            header="$(printf '%s\n' "$rows" | grep -E '^\| Method' | head -1)"
            [ -n "$header" ] && printf '%s\n' "$header"
        fi

        echo "== round $round $side"
        printf '%s\n' "$rows" | grep -vE '^\| Method'
    done
done
