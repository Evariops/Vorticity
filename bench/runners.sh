#!/usr/bin/env bash
# The kept runners: the Native AOT runner of a commit, published once and kept, and two kept runners
# alternated on a few scenarios of the high-cardinality bench.
#
#   bench/runners.sh keep [<commit>] [--force]
#   bench/runners.sh list
#   bench/runners.sh alt <before> <after> [--alternations N] [--repeat N] [--rest S] [--threads N[,M…]] <scenario>[@<file>] …
#
#   bench/runners.sh keep main
#   bench/runners.sh alt main HEAD --threads 1,14 group-pages-url group-users-day@visits-20000000.vortex
#
# A CHANGE TO THE ENGINE IS TIMED AGAINST THE RUNNER THE LAST CHANGE TO THE SAME PART LEFT, not against
# a number of milliseconds taken another day: on a machine that runs other work, the load moves a
# number from one day to the next, and it moves two binaries run in turns alike. The ratio of the two
# is what holds; the binary it was taken against is kept, so that the next change runs against it.
#
# keep publishes <commit>'s runner, the working tree's tracked changes when no commit is given (a
# dangling commit, as bench/queries-ab.sh makes one), into ~/.cache/vorticity/runners/<sha>
# (VORTICITY_RUNNERS overrides it), and prints that directory. TODAY'S RUNNER AND SCENARIO PROJECTS are
# built against that commit's library, in a worktree under bench/.ab/, so that every kept runner names
# the same scenarios; when they do not build against it, the commit's own are, and the runner says so
# in its commit.txt. A runner kept already is kept as it is, unless --force.
#
# alt runs each scenario at each degree in the two runners, in turns: <alternations> pairs (3 by
# default), the order of a pair alternating, each run a process of its own that runs the scenario
# <repeat> times (8 by default). A run's time is the median of its rounds after the first, which pays
# the page faults and the pools' first rents; a side's, the median of its runs. A commit not kept yet is
# kept first; a directory is taken as a runner kept by hand. A scenario's file is under
# ~/.cache/vorticity/queries unless it holds a slash; without one, each family has its file, the one
# bench/Vorticity.Benchmarks.Runner/GroupScenarios.cs names (vortex-queries --matrix small and full write
# them). The table prints the machine's load before and after each scenario: on a machine whose load
# passes its cores, a degree-14 ratio is to be replayed before it is believed. With --rest S, S
# seconds of rest before every run: fourteen lanes heat a laptop, and on 2026-10-08 a group by of 10^7
# keys run back to back read 133 ms in its first process and 165 to 245 in the next, where five
# seconds of rest before each kept every process within 5 % of the others.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
store="${VORTICITY_RUNNERS:-$HOME/.cache/vorticity/runners}"
corpus="${VORTICITY_QUERIES_CORPUS:-$HOME/.cache/vorticity/queries}"
usage() {
    echo "usage: bench/runners.sh keep [<commit>] [--force]" >&2
    echo "       bench/runners.sh list" >&2
    echo "       bench/runners.sh alt <before> <after> [--alternations N] [--repeat N] [--rest S] [--threads N[,M…]] <scenario>[@<file>] …" >&2
    exit 2
}
[ $# -ge 1 ] || usage

trees=()
cleanup() {
    for t in "${trees[@]:-}"; do
        [ -n "$t" ] && git -C "$root" worktree remove --force "$t" > /dev/null 2>&1
    done
    git -C "$root" worktree prune > /dev/null 2>&1
}
trap cleanup EXIT

# Publishes the runner of <commit> into the store, unless it is there already, and sets `kept` to its
# directory. It sets a variable rather than echoing one: a subshell would lose the worktree to remove.
kept=""
keep() {
    local commit="$1" force="$2" sha dir tree source publish
    sha="$(git -C "$root" rev-parse --short=12 "$commit^{commit}" 2> /dev/null)" || {
        echo "no commit named '$commit'" >&2; return 1; }
    dir="$store/$sha"
    if [ -x "$dir/Vorticity.Benchmarks.Runner" ] && [ "$force" -eq 0 ]; then
        kept="$dir"
        return 0
    fi

    echo "keeping the runner of $sha $(git -C "$root" log -1 --format=%s "$sha")" >&2
    tree="$root/bench/.ab/runner-$sha"
    git -C "$root" worktree remove --force "$tree" > /dev/null 2>&1
    rm -rf "$tree"
    git -C "$root" worktree prune > /dev/null 2>&1
    git -C "$root" worktree add --detach "$tree" "$sha" > /dev/null 2>&1 || {
        echo "could not create a worktree at $tree" >&2; return 1; }
    trees+=("$tree")

    # Today's projects first; the commit's own when today's do not build against its library. Replaced,
    # not merged: `cp -R src dst` copies into dst when it exists.
    source="today's runner, $(git -C "$root" rev-parse --short=12 HEAD)"
    for project in Vorticity.Benchmarks.Runner Vorticity.Benchmarks.Scenarios; do
        rm -rf "$tree/bench/$project"
        cp -R "$root/bench/$project" "$tree/bench/$project"
        rm -rf "$tree/bench/$project/bin" "$tree/bench/$project/obj"
    done

    if ! dotnet publish -c Release "$tree/bench/Vorticity.Benchmarks.Runner" -v q --nologo > "$tree/publish.log" 2>&1; then
        echo "today's runner does not build against $sha; its own instead" >&2
        source="its own runner"
        for project in Vorticity.Benchmarks.Runner Vorticity.Benchmarks.Scenarios; do
            rm -rf "$tree/bench/$project"
            git -C "$tree" checkout -- "bench/$project" > /dev/null 2>&1
        done

        dotnet publish -c Release "$tree/bench/Vorticity.Benchmarks.Runner" -v q --nologo > "$tree/publish.log" 2>&1 || {
            echo "the runner does not publish at $sha:" >&2
            tail -20 "$tree/publish.log" >&2
            return 1; }
    fi

    publish="$(find "$tree/bench/Vorticity.Benchmarks.Runner/bin/Release" -type d -name publish | head -1)"
    [ -x "$publish/Vorticity.Benchmarks.Runner" ] || { echo "no runner published under $tree" >&2; return 1; }
    rm -rf "$dir"
    mkdir -p "$dir"
    cp -R "$publish/." "$dir/"
    {
        echo "commit: $sha $(git -C "$root" log -1 --format='%s (%cI)' "$sha")"
        echo "runner: $source"
        echo "kept: $(date -u +%Y-%m-%dT%H:%M:%SZ) on $(sysctl -n machdep.cpu.brand_string 2> /dev/null || uname -m)"
    } > "$dir/commit.txt"
    kept="$dir"
}

# The runner <side> names: a directory, or a commit kept first.
runner_of() {
    if [ -d "$1" ]; then
        kept="$(cd "$1" && pwd)"
        [ -x "$kept/Vorticity.Benchmarks.Runner" ] || { echo "no runner in $1" >&2; return 1; }
        return 0
    fi

    keep "$1" 0
}

# The file a scenario runs over when its name gives none: its family's.
file_of() {
    case "$1" in
        group-pages-*) echo "pages-4000000.vortex" ;;
        group-names-*) echo "names-2000000.vortex" ;;
        group-users-*) echo "visits-20000000.vortex" ;;
        group-pairs-*) echo "draws-2000000.vortex" ;;
        group-db-*) echo "groupby-10000000-100-core2025.10.vortex" ;;
        group-strided-*) echo "spread-strided-4000000.vortex" ;;
        *) echo "spread-random-4000000.vortex" ;;
    esac
}

load() { sysctl -n vm.loadavg 2> /dev/null | awk '{ print $2 }'; }

case "$1" in
    keep)
        shift
        commit=""
        force=0
        while [ $# -gt 0 ]; do
            case "$1" in
                --force) force=1; shift ;;
                *) commit="$1"; shift ;;
            esac
        done

        if [ -z "$commit" ]; then
            # The working tree's tracked changes, as a dangling commit; HEAD when there are none.
            commit="$(git -C "$root" stash create "runners working tree")" || { echo "git stash create failed" >&2; exit 2; }
            [ -n "$commit" ] || commit="HEAD"
        fi

        keep "$commit" "$force" || exit 1
        echo "$kept"
        ;;

    list)
        for dir in "$store"/*/; do
            [ -f "$dir/commit.txt" ] && { echo "${dir%/}"; sed 's/^/    /' "$dir/commit.txt"; }
        done
        ;;

    alt)
        shift
        [ $# -ge 3 ] || usage
        before="$1" after="$2"
        shift 2
        alternations=3
        repeat=8
        rest=0
        degrees="1"
        scenarios=()
        while [ $# -gt 0 ]; do
            case "$1" in
                --alternations) alternations="$2"; shift 2 ;;
                --repeat) repeat="$2"; shift 2 ;;
                --rest) rest="$2"; shift 2 ;;
                --threads) degrees="$2"; shift 2 ;;
                --*) usage ;;
                *) scenarios+=("$1"); shift ;;
            esac
        done

        [ ${#scenarios[@]} -gt 0 ] && [ "$repeat" -ge 2 ] || usage
        runner_of "$before" || exit 1
        before_bin="$kept/Vorticity.Benchmarks.Runner"
        before_name="$(basename "$kept")"
        runner_of "$after" || exit 1
        after_bin="$kept/Vorticity.Benchmarks.Runner"
        after_name="$(basename "$kept")"

        out="$root/bench/.ab/runners-$(date +%Y%m%d-%H%M%S)"
        mkdir -p "$out"
        tsv="$out/runs.tsv"
        echo "before $before_name, after $after_name: $alternations pairs of $repeat rounds; runs kept in ${out#"$root"/}"

        # One run: the median of its rounds after the first, in microseconds, and the result it gave.
        run() {
            local side="$1" bin="$2" name="$3" file="$4" threads="$5" log="$6" label="$7"
            [ "$rest" -gt 0 ] && sleep "$rest"
            "$bin" --scenario "$name" "$file" 0 --repeat "$repeat" --threads "$threads" > "$log" 2>&1 || {
                echo "the $side runner failed on $name at $threads:" >&2; tail -5 "$log" >&2; return 1; }
            awk -v side="$side" -v name="$label" -v threads="$threads" '
                /^round=/ {
                    split($0, f, " ")
                    for (i in f) { split(f[i], kv, "="); v[kv[1]] = kv[2] }
                    if (v["round"] > 0) times[n++] = v["work_us"]
                    rows = v["rows"]
                }
                END {
                    for (i = 0; i < n; i++) for (j = i + 1; j < n; j++) if (times[j] + 0 < times[i] + 0) { t = times[i]; times[i] = times[j]; times[j] = t }
                    median = n % 2 ? times[int(n / 2)] : (times[n / 2 - 1] + times[n / 2]) / 2
                    printf "%s\t%s\t%s\t%.0f\t%s\n", name, threads, side, median, rows
                }' "$log"
        }

        for spec in "${scenarios[@]}"; do
            name="${spec%%@*}"
            file="${spec#*@}"
            [ "$file" = "$spec" ] && file="$(file_of "$name")"
            case "$file" in */*) ;; *) file="$corpus/$file" ;; esac
            [ -e "$file" ] || { echo "no file $file for $name: vortex-queries --matrix small or full, or --fixture, writes it" >&2; exit 2; }
            label="$name@$(basename "$file")"
            for threads in ${degrees//,/ }; do
                started="$(load)"
                for ((i = 1; i <= alternations; i++)); do
                    log="$out/$name-$(basename "$file")-$threads-$i"
                    if ((i % 2 == 1)); then
                        run before "$before_bin" "$name" "$file" "$threads" "$log-before.log" "$label" >> "$tsv" || exit 1
                        run after "$after_bin" "$name" "$file" "$threads" "$log-after.log" "$label" >> "$tsv" || exit 1
                    else
                        run after "$after_bin" "$name" "$file" "$threads" "$log-after.log" "$label" >> "$tsv" || exit 1
                        run before "$before_bin" "$name" "$file" "$threads" "$log-before.log" "$label" >> "$tsv" || exit 1
                    fi
                done

                printf '%s\t%s\tload\t%s\t%s\n' "$label" "$threads" "$started" "$(load)" >> "$tsv"
            done
        done

        # A side's time is the median of its runs; the pairs' ratios give the spread. The results must
        # agree, run after run and side against side: else the two did not do the same work.
        echo
        awk -F '\t' '
            function median(list, count,    i, j, t) {
                for (i = 0; i < count; i++) for (j = i + 1; j < count; j++) if (list[j] + 0 < list[i] + 0) { t = list[i]; list[i] = list[j]; list[j] = t }
                return count % 2 ? list[int(count / 2)] : (list[count / 2 - 1] + list[count / 2]) / 2
            }
            {
                key = $1 " (" $2 ")"
                if (!(key in seen)) { seen[key] = 1; order[++n] = key }
                if ($3 == "load") { loads[key] = $4 " -> " $5; next }
                k = key SUBSEP $3
                at = count[k]++
                times[k, at] = $4
                if (key in result && result[key] != $5) differs[key] = 1
                result[key] = $5
            }
            END {
                printf "%-64s %10s %10s %7s %15s %13s\n", "scenario@file (degree)", "before ms", "after ms", "ratio", "pairs min-max", "load"
                for (i = 1; i <= n; i++) {
                    key = order[i]; b = key SUBSEP "before"; a = key SUBSEP "after"
                    nb = count[b]; na = count[a]
                    for (j = 0; j < nb; j++) lb[j] = times[b, j]
                    for (j = 0; j < na; j++) la[j] = times[a, j]
                    lo = 1e9; hi = 0
                    for (j = 0; j < nb && j < na; j++) { r = times[a, j] / times[b, j]; lo = r < lo ? r : lo; hi = r > hi ? r : hi }
                    mb = median(lb, nb); ma = median(la, na)
                    printf "%-64s %10.2f %10.2f %7.3f %7.3f-%-7.3f %13s%s\n", key, mb / 1000, ma / 1000, ma / mb, lo, hi, loads[key], (key in differs) ? "  RESULT DIFFERS" : ""
                }
            }' "$tsv"
        ;;

    *) usage ;;
esac
