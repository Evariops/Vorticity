#!/usr/bin/env bash
# One process, one clock, two builds of the library (BENCH-AUDIT.md C1).
#
#   bench/ab.sh <before-commit> [--after <commit>] <file> [scenario…]
#
# Builds the OTHER side in a git worktree of <commit>, then runs both against one clock with the
# rounds interleaved and the ratio taken per round, exactly as --ratio-check does against Rust.
#
#   bench/ab.sh 5baa92e --after 4a31e40 tests/.../utf8_nonnull_r8193.vortex fullscan
#
# Without --after the other side is the working build, which answers "how much faster are we now" --
# every commit since, not the one being judged. A perf change is judged against its own parent, so
# --after is what the criterion in BENCH-AUDIT.md's roadmap actually asks for.
#
# THE SCENARIO PROJECT IS COPIED INTO THE WORKTREE, and that is the load-bearing trick: a commit
# from before it existed has no such assembly, and asking every past commit to have carried one
# would make the tool useless on exactly the history it is for. Today's scenarios, that commit's
# library. If a scenario stops compiling against an old library, that is the answer: the API moved,
# and the two builds are not measuring the same thing anyway.
#
# The worktree lands under bench/.ab/ (git-ignored) and is removed on exit.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ $# -ge 2 ] || {
    echo "usage: bench/ab.sh <before-commit> [--after <commit>] <file> [scenario…]" >&2; exit 2; }
before_commit="$1"; shift
after_commit=""
if [ "${1:-}" = "--after" ]; then after_commit="$2"; shift 2; fi
[ $# -ge 1 ] || { echo "usage: bench/ab.sh <before-commit> [--after <commit>] <file> …" >&2; exit 2; }
file="$1"; shift

project="Vorticity.Benchmarks.Scenarios"
trees=()
# A worktree stays REGISTERED after its directory is deleted, so `rm -rf` alone makes the next run
# fail with "already exists" and leaves the entry `prunable` forever. Remove then prune, both here
# and before creating one.
cleanup() {
    for t in "${trees[@]:-}"; do
        [ -n "$t" ] && git -C "$root" worktree remove --force "$t" > /dev/null 2>&1
    done
    git -C "$root" worktree prune > /dev/null 2>&1
}
trap cleanup EXIT

# Builds <commit> in a throwaway worktree with TODAY'S scenario project copied in, and sets
# `side_dir` to the output directory.
#
# IT SETS A VARIABLE RATHER THAN ECHOING ONE, because `$(build_side …)` would run it in a subshell
# and the list of worktrees to remove would die with that subshell -- which is how the first version
# of this script left three of them registered behind it.
side_dir=""
build_side() {
    local commit="$1" label="$2" sha tree
    sha="$(git -C "$root" rev-parse --short "$commit")" || return 1
    tree="$root/bench/.ab/$label-$sha"
    echo "$label: $sha $(git -C "$root" log -1 --format=%s "$sha")"
    git -C "$root" worktree remove --force "$tree" > /dev/null 2>&1
    rm -rf "$tree"
    git -C "$root" worktree prune > /dev/null 2>&1
    git -C "$root" worktree add --detach "$tree" "$sha" > /dev/null 2>&1 || {
        echo "could not create a worktree at $tree" >&2; return 1; }
    trees+=("$tree")
    cp -R "$root/bench/$project" "$tree/bench/$project"
    rm -rf "$tree/bench/$project/bin" "$tree/bench/$project/obj"
    dotnet build "$tree/bench/$project" -c Release -v q --nologo > "$tree/build.log" 2>&1 || {
        echo "the scenario project does not build against $sha:" >&2
        tail -20 "$tree/build.log" >&2
        return 1; }
    side_dir="$tree/bench/$project/bin/Release/net11.0"
}

echo "building the other side(s)..."
build_side "$before_commit" before || exit 2
before_dir="$side_dir"
after_args=()
if [ -n "$after_commit" ]; then
    build_side "$after_commit" after || exit 2
    after_args=(--after "$side_dir")
fi

dotnet run -c Release --project "$root/bench/Vorticity.Benchmarks" -- \
    --ab "$before_dir" "$root/$file" "$@" "${after_args[@]}"
