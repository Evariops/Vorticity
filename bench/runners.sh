#!/usr/bin/env bash
# The kept runners: the Native AOT runner of a commit, published once and kept, and two kept runners
# alternated on a few scenarios of the high-cardinality bench.
#
#   bench/runners.sh keep [<commit>] [--force]
#   bench/runners.sh list
#   bench/runners.sh alt <before> <after> [--alternations N] [--most N] [--target P] [--repeat N] [--rest S]
#                        [--threads N[,M…]] [--warm] [--no-canary] <scenario>[@<file>] …
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
# alt runs each scenario at each degree in the two runners, in turns, each run a process of its own that
# runs the scenario <repeat> times (8 by default). A run's time is the median of its rounds after the
# first, which pays the page faults and the pools' first rents. A pair is a run of each side, the order
# alternating from one pair to the next (ABBA), which cancels a drift of the machine; its ratio is
# after over before. A side's time is the median of its runs, the scenario's ratio the median of its
# pairs' ratios. A commit not kept yet is kept first; a directory is taken as a runner kept by hand. A
# scenario's file is under ~/.cache/vorticity/queries unless it holds a slash; without one, each family
# has its file, the one bench/Vorticity.Benchmarks.Runner/GroupScenarios.cs names (vortex-queries
# --matrix small and full write them).
#
# THE NOISE IS OTHER PROCESSES: a Mac always runs some beside the bench. So:
# - pairs until the answer holds: <alternations> pairs first (3), then one more while the 95 % interval
#   of the median ratio (a bootstrap of the pairs) is wider than ±<target> percent (3), <most> at most
#   (8). The pairs and the interval are printed: a clear answer stops at three;
# - canaries: before and after each scenario, two fixed runs of the before runner, the file kept mapped
#   (a scan of one lane, group-stridedfloor-k7, and a count and sum of 10^3 keys on 14 lanes). Their
#   first values of the session are the standard; a scenario framed by a canary more than 5 % off is
#   run again once, and marked if it still is;
# - controls, user-level, never sudo: another process above 50 % of a core holds a scenario back (30 s
#   at most), one above 20 % is noted; swap that grows during a scenario has it run again once; the
#   free memory, the power source and the load are noted;
# - a run whose rounds pass 1.5 times its median more than one time in five is run again once;
# - the cross-check: the ratio of the two sides' fastest runs beside the median of the pairs' ratios, a
#   gap of more than 10 % marking the scenario noisy;
# - the cycles: the runner prints the processor's cycles of each round (macOS), and the table their
#   ratio beside the time's. A time that moves while the cycles do not was moved by the machine.
# --warm runs both sides with their file kept mapped (VORTICITY_RUNNER_WARM=1): the engine without the
# page faults of a new mapping, which other processes make dearer at fourteen lanes. --rest S rests S
# seconds before every run (1 is the most this machine needs). Every run, canary and control is kept,
# with its time of day, in runs.tsv under bench/.ab/runners-<date>/.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
store="${VORTICITY_RUNNERS:-$HOME/.cache/vorticity/runners}"
corpus="${VORTICITY_QUERIES_CORPUS:-$HOME/.cache/vorticity/queries}"
usage() {
    echo "usage: bench/runners.sh keep [<commit>] [--force]" >&2
    echo "       bench/runners.sh list" >&2
    echo "       bench/runners.sh alt <before> <after> [--alternations N] [--most N] [--target P] [--repeat N] [--rest S] [--threads N[,M…]] [--warm] [--no-canary] <scenario>[@<file>] …" >&2
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
        most=8
        target=3
        repeat=8
        rest=0
        degrees="1"
        canary=1
        warm=0
        scenarios=()
        while [ $# -gt 0 ]; do
            case "$1" in
                --alternations) alternations="$2"; shift 2 ;;
                --most) most="$2"; shift 2 ;;
                --target) target="$2"; shift 2 ;;
                --repeat) repeat="$2"; shift 2 ;;
                --rest) rest="$2"; shift 2 ;;
                --threads) degrees="$2"; shift 2 ;;
                --warm) warm=1; shift ;;
                --no-canary) canary=0; shift ;;
                --*) usage ;;
                *) scenarios+=("$1"); shift ;;
            esac
        done

        [ ${#scenarios[@]} -gt 0 ] && [ "$repeat" -ge 2 ] && [ "$alternations" -ge 1 ] && [ "$most" -ge "$alternations" ] || usage
        runner_of "$before" || exit 1
        before_bin="$kept/Vorticity.Benchmarks.Runner"
        before_name="$(basename "$kept")"
        runner_of "$after" || exit 1
        after_bin="$kept/Vorticity.Benchmarks.Runner"
        after_name="$(basename "$kept")"

        out="$root/bench/.ab/runners-$(date +%Y%m%d-%H%M%S)"
        mkdir -p "$out"
        tsv="$out/runs.tsv"
        mode="cold"
        [ "$warm" -eq 1 ] && mode="warm"
        echo "before $before_name, after $after_name: $alternations to $most pairs of $repeat rounds, ±$target % at 95 %, $mode; runs kept in ${out#"$root"/}"

        now() { date +%H:%M:%S; }

        # The awk functions the decisions and the table share: a median, and the 95 % interval of a
        # median by a bootstrap of 2 000 draws, seeded so that the same ratios give the same interval.
        stats='
            function sortn(a, n,    gap, i, j, t) {
                for (gap = int(n / 2); gap > 0; gap = int(gap / 2))
                    for (i = gap; i < n; i++) { t = a[i]; for (j = i; j >= gap && a[j - gap] + 0 > t + 0; j -= gap) a[j] = a[j - gap]; a[j] = t }
            }
            function median(a, n,    c, i) {
                for (i = 0; i < n; i++) c[i] = a[i]
                sortn(c, n)
                return n % 2 ? c[int(n / 2)] : (c[n / 2 - 1] + c[n / 2]) / 2
            }
            function interval(r, n,    b, i, s, m) {
                srand(7)
                for (b = 0; b < 2000; b++) { for (i = 0; i < n; i++) s[i] = r[int(rand() * n)]; m[b] = median(s, n) }
                sortn(m, 2000)
                low = m[49]; high = m[1950]
            }'

        # One run of a side: "<median µs>\t<rows>\t<rounds>\t<rounds past 1.5 times the median>\t<median cycles>\t<fastest µs>"
        # of its rounds after the first.
        measure() {
            local bin="$1" name="$2" file="$3" threads="$4" log="$5" keep="$6" rounds="${7:-$repeat}"
            [ "$rest" -gt 0 ] && sleep "$rest"
            VORTICITY_RUNNER_WARM="$keep" "$bin" --scenario "$name" "$file" 0 --repeat "$rounds" --threads "$threads" > "$log" 2>&1 || {
                echo "the runner failed on $name at $threads:" >&2; tail -5 "$log" >&2; return 1; }
            awk "$stats"'
                /^round=/ {
                    delete v
                    split($0, f, " ")
                    for (i in f) { split(f[i], kv, "="); v[kv[1]] = kv[2] }
                    if (v["round"] > 0) { times[n + 0] = v["work_us"]; cycles[n + 0] = ("cycles" in v) ? v["cycles"] : 0; n++ }
                    rows = v["rows"]
                }
                END {
                    m = median(times, n); c = median(cycles, n); fastest = times[0]; past = 0
                    for (i = 0; i < n; i++) { if (times[i] + 0 < fastest + 0) fastest = times[i]; if (times[i] > 1.5 * m) past++ }
                    printf "%.0f\t%s\t%d\t%d\t%.0f\t%s\n", m, rows, n, past, c, fastest
                }' "$log"
        }

        # A side's run in a pair, recorded; run again once when more than one round in five passes 1.5
        # times its median. Prints the run's median.
        run() {
            local side="$1" bin="$2" name="$3" file="$4" threads="$5" log="$6" label="$7" pair="$8" result again=0 rounds past
            result="$(measure "$bin" "$name" "$file" "$threads" "$log" "$warm")" || return 1
            IFS=$'\t' read -r _ _ rounds past _ _ <<< "$result"
            if [ $((past * 5)) -gt "$rounds" ]; then
                again=1
                result="$(measure "$bin" "$name" "$file" "$threads" "$log" "$warm")" || return 1
            fi

            printf 'run\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$label" "$threads" "$attempt" "$side" "$pair" "$again" "$result" >> "$tsv"
            echo "${result%%$'\t'*}"
        }

        # The canaries, fixed runs of the before runner with their files kept mapped: a scan of one lane
        # and a count and sum of 10^3 keys on 14 lanes. Their first values are the session's standard;
        # fails when either is more than 5 % off it.
        # Each takes ten rounds of 7 to 11 ms: shorter, it would move more than the 5 % it judges.
        canary_scan="$corpus/spread-strided-20000000.vortex"
        [ -e "$canary_scan" ] || canary_scan="$corpus/spread-strided-4000000.vortex"
        canary_lanes="$corpus/spread-random-40000000-core2025.10.vortex"
        [ -e "$canary_lanes" ] || canary_lanes="$corpus/spread-random-20000000.vortex"
        [ -e "$canary_lanes" ] || canary_lanes="$corpus/spread-random-4000000.vortex"
        standard_scan=""
        standard_lanes=""
        canaries() {
            local where="$1" label="$2" threads="$3" scan lanes
            scan="$(measure "$before_bin" group-stridedfloor-k7 "$canary_scan" 1 "$out/canary.log" 1 10)" || return 2
            lanes="$(measure "$before_bin" group-total-k3 "$canary_lanes" 14 "$out/canary.log" 1 10)" || return 2
            scan="${scan%%$'\t'*}"
            lanes="${lanes%%$'\t'*}"
            [ -n "$standard_scan" ] || { standard_scan="$scan"; standard_lanes="$lanes"; }
            printf 'canary\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$label" "$threads" "$attempt" "$where" "$scan" "$lanes" "$standard_scan/$standard_lanes" >> "$tsv"
            awk -v a="$scan" -v b="$lanes" -v sa="$standard_scan" -v sb="$standard_lanes" \
                'BEGIN { da = a / sa - 1; db = b / sb - 1; if (da < 0) da = -da; if (db < 0) db = -db; exit (da > 0.05 || db > 0.05) ? 1 : 0 }'
        }

        # The controls, read as a user: the swap in use (MB), the busiest process but the runners
        # ("<percent of a core>\t<those above 20 %>"), the free memory, the power source.
        swap_used() { sysctl -n vm.swapusage 2> /dev/null | sed -E 's/.*used = ([0-9.]+)M.*/\1/'; }
        free_memory() { memory_pressure -Q 2> /dev/null | awk -F ': ' '/free percentage/ { print $2 }'; }
        power() { if pmset -g batt 2> /dev/null | head -1 | grep -q "AC Power"; then echo ac; else echo battery; fi; }
        busiest() {
            ps -Ao pcpu=,comm= | awk '
                { cpu = $1 + 0; name = $0; sub(/^ *[0-9.]+ +/, "", name); sub(/.*\//, "", name)
                  if (name ~ /Vorticity\.Benchmarks\.Runner/ || name == "ps" || name == "awk") next
                  if (cpu > top) top = cpu
                  if (cpu > 20) busy = busy sprintf("%s %.0f%%; ", name, cpu) }
                END { printf "%.0f\t%s\n", top, busy }'
        }

        # Holds a scenario back while another process takes more than half a core, 30 s at most; prints the seconds waited.
        quiet() {
            local waited=0 top
            while :; do
                IFS=$'\t' read -r top _ <<< "$(busiest)"
                if [ "${top:-0}" -le 50 ] || [ "$waited" -ge 30 ]; then
                    break
                fi

                sleep 2
                waited=$((waited + 2))
            done

            echo "$waited"
        }

        control() {
            local where="$1" label="$2" threads="$3" waited="$4" top busy
            IFS=$'\t' read -r top busy <<< "$(busiest)"
            printf 'control\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$label" "$threads" "$attempt" "$where" \
                "$waited" "$(swap_used)" "$(free_memory)" "$(power)" "$(load)" "$busy" >> "$tsv"
        }

        for spec in "${scenarios[@]}"; do
            name="${spec%%@*}"
            file="${spec#*@}"
            [ "$file" = "$spec" ] && file="$(file_of "$name")"
            case "$file" in */*) ;; *) file="$corpus/$file" ;; esac
            [ -e "$file" ] || { echo "no file $file for $name: vortex-queries --matrix small or full, or --fixture, writes it" >&2; exit 2; }
            label="$name@$(basename "$file")"
            for threads in ${degrees//,/ }; do
                attempt=0
                while :; do
                    attempt=$((attempt + 1))
                    control before "$label" "$threads" "$(quiet)"
                    swap_before="$(swap_used)"
                    off=0
                    if [ "$canary" -eq 1 ]; then
                        canaries before "$label" "$threads" || off=1
                    fi

                    # Pairs, ABBA, until the interval of the median ratio is within the target.
                    ratios=()
                    pair=0
                    while :; do
                        pair=$((pair + 1))
                        log="$out/$name-$(basename "$file")-$threads-$attempt-$pair"
                        if ((pair % 2 == 1)); then
                            b="$(run before "$before_bin" "$name" "$file" "$threads" "$log-before.log" "$label" "$pair")" || exit 1
                            a="$(run after "$after_bin" "$name" "$file" "$threads" "$log-after.log" "$label" "$pair")" || exit 1
                        else
                            a="$(run after "$after_bin" "$name" "$file" "$threads" "$log-after.log" "$label" "$pair")" || exit 1
                            b="$(run before "$before_bin" "$name" "$file" "$threads" "$log-before.log" "$label" "$pair")" || exit 1
                        fi

                        ratios+=("$(awk -v a="$a" -v b="$b" 'BEGIN { printf "%.5f", a / b }')")
                        [ "$pair" -ge "$alternations" ] || continue
                        [ "$pair" -ge "$most" ] && break
                        awk -v target="$target" "$stats"' BEGIN {
                            n = ARGC - 1; for (i = 0; i < n; i++) r[i] = ARGV[i + 1] + 0
                            interval(r, n); m = median(r, n)
                            exit (100 * (high - low) / 2 / m <= target) ? 0 : 1 }' "${ratios[@]}" && break
                    done

                    if [ "$canary" -eq 1 ]; then
                        canaries after "$label" "$threads" || off=1
                    fi

                    control after "$label" "$threads" 0
                    grew=0
                    awk -v a="${swap_before:-0}" -v b="$(swap_used)" 'BEGIN { exit (b > a + 1) ? 0 : 1 }' && grew=1
                    if { [ "$off" -eq 1 ] || [ "$grew" -eq 1 ]; } && [ "$attempt" -eq 1 ]; then
                        printf 'replay\t%s\t%s\t%s\t%s\tcanary off %s, swap grew %s\n' "$(now)" "$label" "$threads" "$attempt" "$off" "$grew" >> "$tsv"
                        continue
                    fi

                    printf 'scenario\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$label" "$threads" "$attempt" "$off" "$grew" >> "$tsv"
                    break
                done
            done
        done

        # The table, from the last attempt of each scenario. A side's time is the median of its runs; the
        # ratio, the median of the pairs' ratios, with its 95 % interval; the fastest, the ratio of the two
        # sides' fastest rounds, which a gap of more than 10 % from the median marks noisy; the cycles, the
        # ratio of the medians of the rounds' cycles. The results must agree, run after run and side
        # against side: else the two did not do the same work.
        echo
        awk -F '\t' "$stats"'
            $1 == "scenario" { key = $3 " (" $4 ")"; last[key] = $5; off[key] = $6; grew[key] = $7; next }
            $1 == "control" && $6 == "before" { key = $3 " (" $4 ")"; waited[key, $5] = $7; loadb[key, $5] = $11; busy[key, $5] = $12; next }
            $1 == "control" && $6 == "after" { key = $3 " (" $4 ")"; loada[key, $5] = $11; next }
            $1 == "run" {
                key = $3 " (" $4 ")"
                if (!(key in seen)) { seen[key] = 1; order[++n] = key }
                k = key SUBSEP $5 SUBSEP $6
                at = count[k]++
                time[k, at] = $9; pairof[k, at] = $7; cyc[k, at] = $13; fast[k, at] = $14
                if ($8 == 1) again[key, $5]++
                if ((key SUBSEP $5) in result && result[key, $5] != $10) differs[key] = 1
                result[key, $5] = $10
            }
            END {
                printf "%-56s %9s %9s %7s %15s %5s %8s %8s  %s\n", "scenario@file (degree)", "before ms", "after ms", "ratio", "95 %", "pairs", "fastest", "cycles", "notes"
                for (i = 1; i <= n; i++) {
                    key = order[i]; t = last[key]; b = key SUBSEP t SUBSEP "before"; a = key SUBSEP t SUBSEP "after"
                    nb = count[b]; na = count[a]; np = 0; fb = 1e18; fa = 1e18
                    for (j = 0; j < nb; j++) { lb[j] = time[b, j]; cb[j] = cyc[b, j]; if (fast[b, j] + 0 < fb) fb = fast[b, j] + 0; tb[pairof[b, j]] = time[b, j] }
                    for (j = 0; j < na; j++) { la[j] = time[a, j]; ca[j] = cyc[a, j]; if (fast[a, j] + 0 < fa) fa = fast[a, j] + 0; ta[pairof[a, j]] = time[a, j] }
                    for (p = 1; p <= nb && p <= na; p++) r[np++] = ta[p] / tb[p]
                    ratio = median(r, np); interval(r, np)
                    mb = median(lb, nb); ma = median(la, na); mcb = median(cb, nb); mca = median(ca, na)
                    fastest = fa / fb; cycles = mcb > 0 ? sprintf("%8.3f", mca / mcb) : sprintf("%8s", "-")
                    notes = ""
                    gap = fastest / ratio - 1; if (gap < 0) gap = -gap
                    if (gap > 0.10) notes = notes "noisy; "
                    if (100 * (high - low) / 2 / ratio > target) notes = notes "wide; "
                    if (off[key]) notes = notes "canary off; "
                    if (grew[key]) notes = notes "swap grew; "
                    if (t > 1) notes = notes "replayed; "
                    if (waited[key, t] > 0) notes = notes "waited " waited[key, t] " s; "
                    if (again[key, t] > 0) notes = notes again[key, t] " runs again; "
                    if (key in differs) notes = notes "RESULT DIFFERS; "
                    if (busy[key, t] != "") notes = notes "busy: " busy[key, t]
                    printf "%-56s %9.2f %9.2f %7.3f %7.3f-%-7.3f %5d %8.3f %s  load %s -> %s  %s\n", key, mb / 1000, ma / 1000, ratio, low, high, np, fastest, cycles, loadb[key, t], loada[key, t], notes
                    delete tb; delete ta
                }
            }' target="$target" "$tsv"
        ;;

    *) usage ;;
esac
