#!/usr/bin/env bash
# Two commits of the library, the same queries, run in turns: what a change does to the query bench.
#
#   bench/queries-ab.sh <before-commit> [--after <commit>] [--pairs N] [bench args…]
#
#   bench/queries-ab.sh HEAD~1 --after HEAD --degrees 1,14 skewed medium
#
# BOTH SIDES ARE BUILT THE SAME WAY, in git worktrees under bench/.ab/ (git-ignored), each with
# TODAY'S queries bench copied in, so that they run the same scenarios and print the same lines.
# Without --after, the working tree's tracked changes are that side, as a dangling commit
# (`git stash create`), built like the other: a side built in place against one built in a worktree
# would differ by more than the change. Files git does not track yet are not in that commit; add
# them first.
#
# THE TWO BINARIES RUN IN TURNS, N pairs (4 by default), the order of a pair alternating. On a loaded
# machine a burst of load lands on a turn, not on a side. Each turn keeps the best of its rounds; the
# table keeps the best turn of each side and prints after / before. A query whose result differs
# between the sides is flagged: the two did not do the same work.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
[ $# -ge 1 ] || { echo "usage: bench/queries-ab.sh <before-commit> [--after <commit>] [--pairs N] [bench args…]" >&2; exit 2; }
before_commit="$1"; shift
after_commit=""
pairs=4
while [ $# -gt 0 ]; do
    case "$1" in
        --after) after_commit="$2"; shift 2 ;;
        --pairs) pairs="$2"; shift 2 ;;
        *) break ;;
    esac
done

if [ -z "$after_commit" ]; then
    after_commit="$(git -C "$root" stash create "queries-ab working tree")"
    [ -n "$after_commit" ] || after_commit="HEAD"
fi

project="Vorticity.Benchmarks.Queries"
trees=()
cleanup() {
    for t in "${trees[@]:-}"; do
        [ -n "$t" ] && git -C "$root" worktree remove --force "$t" > /dev/null 2>&1
    done
    git -C "$root" worktree prune > /dev/null 2>&1
}
trap cleanup EXIT

# Builds <commit> in a throwaway worktree with today's queries bench, and sets `side_bin` to its
# binary. It sets a variable rather than echoing one: a subshell would lose the worktree to remove.
side_bin=""
build_side() {
    local commit="$1" label="$2" sha tree
    sha="$(git -C "$root" rev-parse --short "$commit")" || return 1
    tree="$root/bench/.ab/queries-$label-$sha"
    echo "$label: $sha $(git -C "$root" log -1 --format=%s "$sha")"
    git -C "$root" worktree remove --force "$tree" > /dev/null 2>&1
    rm -rf "$tree"
    git -C "$root" worktree prune > /dev/null 2>&1
    git -C "$root" worktree add --detach "$tree" "$sha" > /dev/null 2>&1 || {
        echo "could not create a worktree at $tree" >&2; return 1; }
    trees+=("$tree")
    # Replaced, not merged: `cp -R src dst` copies INTO dst when it exists (see bench/ab.sh).
    rm -rf "$tree/bench/$project"
    cp -R "$root/bench/$project" "$tree/bench/$project"
    rm -rf "$tree/bench/$project/bin" "$tree/bench/$project/obj"
    dotnet build "$tree/bench/$project" -c Release -v q --nologo > "$tree/build.log" 2>&1 || {
        echo "the queries bench does not build against $sha:" >&2
        tail -20 "$tree/build.log" >&2
        return 1; }
    side_bin="$tree/bench/$project/bin/Release/net11.0/vortex-queries"
}

echo "building both sides..."
build_side "$before_commit" before || exit 2
before_bin="$side_bin"
build_side "$after_commit" after || exit 2
after_bin="$side_bin"

out="$root/bench/.ab/queries-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
turn() {
    local side="$1" bin="$2" i="$3"
    echo "pair $i of $pairs: $side"
    "$bin" "${@:4}" --tsv "$out/$side.tsv" > "$out/$side-$i.log" 2>&1 || {
        echo "the $side side failed:" >&2; tail -20 "$out/$side-$i.log" >&2; exit 1; }
}

# THE ORDER ALTERNATES, before-after then after-before: a null control (the same commit on both
# sides) read the second process of every pair 3 to 10 % faster, so a fixed order would credit that
# to whichever side always ran second.
for ((i = 1; i <= pairs; i++)); do
    if ((i % 2 == 1)); then
        turn before "$before_bin" "$i" "$@"
        turn after "$after_bin" "$i" "$@"
    else
        turn after "$after_bin" "$i" "$@"
        turn before "$before_bin" "$i" "$@"
    fi
done

# The best turn of each side by query and degree, its first answer, memory and lanes taken from
# that turn; the result must be the same on both sides and in every turn.
echo
echo "runs kept in ${out#"$root"/}"
awk -F '\t' '
    FNR == 1 { side = (FILENAME ~ /before\.tsv$/) ? "b" : "a" }
    {
        key = $1 " (" $2 ")"
        if (!(key in seen)) { seen[key] = 1; order[++n] = key }
        k = side SUBSEP key
        if (!(k in ms) || $3 + 0 < ms[k] + 0) {
            ms[k] = $3; first[k] = $4; alloc[k] = $5; live[k] = $6; gen2[k] = $8
            spread[k] = (NF >= 14 && $13 + 0 > 0) ? sprintf("%.2f", $12 / $13) : "-"
            busiest[k] = (NF >= 14) ? sprintf("%.2f", $12) : "-"
            merge[k] = (NF >= 14) ? sprintf("%.2f", $14) : "-"
        }
        if (key in result && result[key] != $7) differs[key] = 1
        result[key] = $7
    }
    END {
        printf "%-72s %9s %9s %7s %17s %17s %17s %11s %13s %11s %13s\n", "query (degree)", "before", "after", "ratio", "first ms b/a", "alloc MiB b/a", "live MiB b/a", "gen2 b/a", "lane ms b/a", "max/mean", "merge ms b/a"
        for (i = 1; i <= n; i++) {
            key = order[i]; b = "b" SUBSEP key; a = "a" SUBSEP key
            if (!(b in ms) || !(a in ms)) continue
            printf "%-72s %9.2f %9.2f %7.3f %8.2f/%-8.2f %8.1f/%-8.1f %8.1f/%-8.1f %5.1f/%-5.1f %6s/%-6s %5s/%-5s %6s/%-6s%s\n", key, ms[b], ms[a], ms[a] / ms[b], first[b], first[a], alloc[b], alloc[a], live[b], live[a], gen2[b], gen2[a], busiest[b], busiest[a], spread[b], spread[a], merge[b], merge[a], (key in differs) ? "  RESULT DIFFERS" : ""
        }
    }' "$out/before.tsv" "$out/after.tsv"
