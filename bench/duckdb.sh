#!/usr/bin/env bash
# Our group bys against DuckDB's, on the same files, in alternated pairs: DuckDB through its vortex
# extension and on a native table already in memory, our side the Native AOT runner, at the same threads.
#
#   bench/duckdb.sh [--set hc|db|all] [--only <rows>] [--rows N] [--db-rows N] [--threads N[,M…]]
#                   [--runner <commit>|<dir>] [--bases both|warm|cold] [--target P] [--most N] [--rest S]
#                   [--no-canary]
#   bench/duckdb.sh --publish <session> <session> [<session>]
#
#   bench/duckdb.sh                                   PR #43's table: 40M rows, 1 and 14 threads
#   bench/duckdb.sh --set db --db-rows 10000000       db-benchmark's group by at 10^7 rows
#   bench/duckdb.sh --set all --only db-q4,total-k7 --threads 14
#                                                     the rows a change touches, and no others: the
#                                                     runner's scenario names without "group-" (total-k3,
#                                                     range-k6, strided-k7, db-q10…)
#   bench/duckdb.sh --publish bench/.runs/duckdb-20261010-0900 bench/.runs/duckdb-20261010-1500
#                                                     the page's table, from two sessions at different hours
#
# THE SAME WORK ON BOTH SIDES. Each query is a group by whose result is aggregated once more: DuckDB's outer
# query sums every column the group by aggregates, and the runner sums the same columns as it reads the
# groups (GroupScenarios.Checksum). The two sums are the same answer, and the table says when they are
# not.
#
# TWO BASES. Warm: our runner with its file kept mapped (VORTICITY_RUNNER_WARM=1) against DuckDB's native
# table, loaded before anything is timed. Cold: our runner mapping its file anew each round against
# DuckDB's vortex reader. A comparison of a table in memory with a file mapped anew measured page faults,
# not engines.
#
# PAIRS, NOT ONE SIDE THEN THE OTHER. One DuckDB process a file, kept open and driven through a FIFO, its
# table loaded once. For each row, degree and base, a block of DuckDB's and a process of our runner in
# turn, ABBA (DuckDB then us, us then DuckDB…), which cancels a drift of the machine: run all of one side
# then all of the other, a perturbation biased one side unseen (the published q10 at fourteen threads,
# 257 ms for DuckDB where it took 102 the same day). A block is 10 rounds, the first 3 dropped, or 5, the
# first 2 dropped, once a row's first block passes 100 ms. A pair's speedup is DuckDB's median over ours;
# a row's, the median of its pairs', 3 pairs and then one more while the 95 % interval of that median (a
# bootstrap of the pairs) is wider than ±5 % (--target), 8 at most (--most). DuckDB's time is its JSON
# profile's latency, to the nanosecond, a profile a round (its timer counts whole milliseconds); its CPU,
# the profile's cpu_time. Ours, the runner's work_us and cpu_us.
#
# THE MACHINE, as bench/runners.sh watches it, all of it as a user: before and after each row and base,
# two canaries of our runner with their files kept mapped (a scan of one lane, a count and sum of 10^3
# keys on 14 lanes), the first values of the session their standard; the busiest other process, which
# holds a row back while it takes more than half a core (30 s at most); the swap, the free memory and the
# power source. A row framed by a canary more than 5 % off whose pairs did not hold the target, or during
# which the swap grew, is run again once. THE FILES are read once before anything is timed, so that both
# engines find them in the page cache.
#
# A SESSION keeps everything it ran under bench/.runs/duckdb-<date>/ (ignored by git): runs.tsv, each
# block, canary and control with its time of day; session.tsv, each row's result; DuckDB's output and
# profiles. --publish takes two sessions run at different hours and prints the page's table: a row is
# published when the two agree within 10 %, a third session deciding otherwise, beside its pairs' range
# and count, then the sessions' canaries and controls.
#
# THE FILES are the bench's own, written by its generators (bench/Vorticity.Benchmarks.Queries --fixture),
# canonical, at edition core2025.10, the one the extension reads (it does not know vortex.zoned); both
# sides read the same file.
#   hc: the high-cardinality rows (spread-random-N, spread-strided-N): a count and a sum by keys of 10^3,
#       10^6 and 10^7 values, a count, the least and the largest by 10^6, and the strided file's keys,
#       hashed, at 10^6 and 10^7.
#   db: db-benchmark's group by (groupby-N-100: the laws of its groupby-datagen.R, drawn from the bench's
#       stream, not R's): the queries our native aggregates cover, q1 to q5, q7 and q10. q6 (a median),
#       q8 (the two largest by group) and q9 (a correlation) are not covered, and not run.
#
# DuckDB is a workstation dependency, as cargo is for the comparison with Rust: duckdb on the PATH (or
# DUCKDB), its vortex extension installed (INSTALL vortex). Never in CI. These figures inform; they gate
# nothing (the plan's steps are judged on their ratchets). A figure for publication is taken on a machine
# at rest, twice: docs/guide/benchmarks-duckdb.md.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
duckdb="${DUCKDB:-$(command -v duckdb || echo /opt/homebrew/bin/duckdb)}"
corpus="${VORTICITY_QUERIES_CORPUS:-$HOME/.cache/vorticity/queries}"
set_name="hc"
only=""
rows=40000000
db_rows=10000000
threads="1,14"
runner="HEAD"
bases="both"
target=5
most=8
rest=0
canary=1
publish=()

usage() {
    echo "usage: bench/duckdb.sh [--set hc|db|all] [--only <rows>] [--rows N] [--db-rows N] [--threads N[,M…]] [--runner <commit>|<dir>] [--bases both|warm|cold] [--target P] [--most N] [--rest S] [--no-canary]" >&2
    echo "       bench/duckdb.sh --publish <session> <session> [<session>]" >&2
    exit 2
}

while [ $# -gt 0 ]; do
    case "$1" in
        --set) set_name="$2"; shift 2 ;;
        --only) only=",$2,"; shift 2 ;;
        --rows) rows="$2"; shift 2 ;;
        --db-rows) db_rows="$2"; shift 2 ;;
        --threads) threads="$2"; shift 2 ;;
        --runner) runner="$2"; shift 2 ;;
        --bases) bases="$2"; shift 2 ;;
        --target) target="$2"; shift 2 ;;
        --most) most="$2"; shift 2 ;;
        --rest) rest="$2"; shift 2 ;;
        --no-canary) canary=0; shift ;;
        --publish) shift; while [ $# -gt 0 ]; do publish+=("$1"); shift; done ;;
        *) usage ;;
    esac
done

# The awk functions the decisions and the tables share: a median, and the 95 % interval of a median by a
# bootstrap of 2 000 draws, seeded so that the same ratios give the same interval (as bench/runners.sh).
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

# --publish: the page's table from two or three sessions' session.tsv. A row of a base is published when
# two sessions agree within 10 %: the mean of their speedups, the widest of their 95 % intervals, their
# pairs summed; none agreeing, it is marked and its speedups printed.
if [ ${#publish[@]} -gt 0 ]; then
    [ ${#publish[@]} -ge 2 ] || usage
    files=()
    for session in "${publish[@]}"; do
        [ -f "$session/session.tsv" ] || { echo "no session.tsv in $session" >&2; exit 2; }
        files+=("$session/session.tsv")
    done

    echo "# Our group bys against DuckDB's"
    echo
    for session in "${publish[@]}"; do
        echo "- session $(basename "$session"): $(head -1 "$session/about.txt" 2> /dev/null)"
    done

    echo "- a speedup is DuckDB's time over ours, above 1.00x we are faster: the mean of the two sessions that agreed within 10 %, in brackets the widest of their 95 % intervals and their pairs"
    echo
    echo "| query | rows | threads | ours warm (ms) | DuckDB native table (ms) | speedup | ours cold (ms) | DuckDB Vortex reader (ms) | speedup | answer |"
    echo "|---|---:|---:|---:|---:|---:|---:|---:|---:|---|"
    awk -F '\t' '
        FNR == 1 { s++ }
        {
            key = $1 SUBSEP $2 SUBSEP $3
            if (!(key in seen)) { seen[key] = 1; order[++n] = key; label[key] = $1; rows[key] = $2; degree[key] = $3 }
            k = key SUBSEP $4; c = count[k]++
            ours[k, c] = $5; duck[k, c] = $6; sp[k, c] = $7; lo[k, c] = $8; hi[k, c] = $9; pairs[k, c] = $10
            if ($12 != "same") answer[key] = "DIFFERENT"
        }
        function cell(k,    i, j, best, bi, bj, d, m) {
            best = -1
            for (i = 0; i < count[k]; i++)
                for (j = i + 1; j < count[k]; j++) {
                    d = sp[k, i] / sp[k, j] - 1; if (d < 0) d = -d
                    if (d <= 0.10 && (best < 0 || d < best)) { best = d; bi = i; bj = j }
                }
            if (count[k] == 0) return "| | |"
            if (best < 0) {
                m = ""
                for (i = 0; i < count[k]; i++) m = m sprintf("%s%.2fx", (i ? ", " : ""), sp[k, i])
                return sprintf("%.1f | %.1f | unsettled: %s", ours[k, 0], duck[k, 0], m)
            }
            return sprintf("%.1f | %.1f | %.2fx [%.2f–%.2f, %d pairs]", (ours[k, bi] + ours[k, bj]) / 2, (duck[k, bi] + duck[k, bj]) / 2,
                (sp[k, bi] + sp[k, bj]) / 2, (lo[k, bi] < lo[k, bj] ? lo[k, bi] : lo[k, bj]), (hi[k, bi] > hi[k, bj] ? hi[k, bi] : hi[k, bj]), pairs[k, bi] + pairs[k, bj])
        }
        END {
            for (i = 1; i <= n; i++) {
                key = order[i]
                printf "| %s | %s | %s | %s | %s | %s |\n", label[key], rows[key], degree[key], cell(key SUBSEP "warm"), cell(key SUBSEP "cold"), (key in answer) ? answer[key] : "same"
            }
        }' "${files[@]}"

    echo
    echo "The machine during each session (its canaries' spread around their standard, the controls' worst):"
    echo
    for session in "${publish[@]}"; do
        awk -F '\t' -v name="$(basename "$session")" '
            $1 == "canary" { scan = $7 + 0; lanes = $8 + 0; split($9, st, "/")
                             d = scan / st[1] - 1; if (d < 0) d = -d; if (d > worst) worst = d
                             d = lanes / st[2] - 1; if (d < 0) d = -d; if (d > worst) worst = d; canaries++ }
            $1 == "control" { if ($8 + 0 > swap) swap = $8 + 0; if ($5 == "before") waited += $6; if ($10 != "ac") battery = 1 }
            $1 == "replay" { replays++ }
            END { printf "- %s: %d canaries, at most %.1f %% off their standard; %d rows replayed; %d s waited on another process; swap at most %s MB; %s\n",
                  name, canaries, 100 * worst, replays, waited, swap, battery ? "on battery at times" : "on mains power" }' "$session/runs.tsv"
    done

    exit 0
fi

[ -x "$duckdb" ] || { echo "no duckdb at '$duckdb': install it, or set DUCKDB" >&2; exit 2; }
"$duckdb" -c "LOAD vortex;" > /dev/null 2>&1 || { echo "duckdb cannot load its vortex extension: run INSTALL vortex in it" >&2; exit 2; }
case "$bases" in both) base_list="warm cold" ;; warm|cold) base_list="$bases" ;; *) usage ;; esac

# The runner: a directory, or a commit's, kept by bench/runners.sh.
if [ -d "$runner" ]; then
    dir="$(cd "$runner" && pwd)"
else
    dir="$("$root/bench/runners.sh" keep "$runner" | tail -1)"
fi
binary="$dir/Vorticity.Benchmarks.Runner"
[ -x "$binary" ] || { echo "no runner in '$dir'" >&2; exit 2; }

# A fixture's path, written first when it is not yet.
fixture() {
    dotnet run -c Release --project "$root/bench/Vorticity.Benchmarks.Queries" -- --fixture "$1" 2> /dev/null | tail -1
}

# The rows of the comparison: a label, the runner's scenario, the fixture, and DuckDB's query over `x`.
queries=()
if [ "$set_name" = "hc" ] || [ "$set_name" = "all" ]; then
    random="spread-random-$rows-core2025.10"
    strided="spread-strided-$rows-core2025.10"
    for k in 3 6 7; do
        queries+=("10^$k keys|group-total-k$k|$random|SELECT sum(c) + sum(s) FROM (SELECT K$k, count(*) AS c, sum(Value) AS s FROM x GROUP BY K$k)")
    done
    queries+=("count, min, max, 10^6 keys|group-range-k6|$random|SELECT sum(c) + sum(lo) + sum(hi) FROM (SELECT K6, count(*) AS c, min(Value) AS lo, max(Value) AS hi FROM x GROUP BY K6)")
    for k in 6 7; do
        queries+=("10^$k hashed keys|group-strided-k$k|$strided|SELECT sum(c) + sum(s) FROM (SELECT K$k, count(*) AS c, sum(Value) AS s FROM x GROUP BY K$k)")
    done
fi

if [ "$set_name" = "db" ] || [ "$set_name" = "all" ]; then
    db="groupby-$db_rows-100-core2025.10"
    queries+=("db q1: sum v1 by id1|group-db-q1|$db|SELECT sum(v1) FROM (SELECT Id1, sum(V1) AS v1 FROM x GROUP BY Id1)")
    queries+=("db q2: sum v1 by id1, id2|group-db-q2|$db|SELECT sum(v1) FROM (SELECT Id1, Id2, sum(V1) AS v1 FROM x GROUP BY Id1, Id2)")
    queries+=("db q3: sum v1, mean v3 by id3|group-db-q3|$db|SELECT sum(v1) + sum(v3) FROM (SELECT Id3, sum(V1) AS v1, avg(V3) AS v3 FROM x GROUP BY Id3)")
    queries+=("db q4: mean v1:v3 by id4|group-db-q4|$db|SELECT sum(v1) + sum(v2) + sum(v3) FROM (SELECT Id4, avg(V1) AS v1, avg(V2) AS v2, avg(V3) AS v3 FROM x GROUP BY Id4)")
    queries+=("db q5: sum v1:v3 by id6|group-db-q5|$db|SELECT sum(v1) + sum(v2) + sum(v3) FROM (SELECT Id6, sum(V1) AS v1, sum(V2) AS v2, sum(V3) AS v3 FROM x GROUP BY Id6)")
    queries+=("db q7: max v1 - min v2 by id3|group-db-q7|$db|SELECT sum(r) FROM (SELECT Id3, max(V1) - min(V2) AS r FROM x GROUP BY Id3)")
    queries+=("db q10: sum v3, count by id1:id6|group-db-q10|$db|SELECT sum(v3) + sum(c) FROM (SELECT Id1, Id2, Id3, Id4, Id5, Id6, sum(V3) AS v3, count(*) AS c FROM x GROUP BY Id1, Id2, Id3, Id4, Id5, Id6)")
fi

# --only keeps the rows it names, by their scenario without "group-".
if [ -n "$only" ]; then
    kept_queries=()
    for entry in "${queries[@]}"; do
        IFS='|' read -r _ scenario _ _ <<< "$entry"
        case "$only" in *",${scenario#group-},"*) kept_queries+=("$entry") ;; esac
    done

    queries=(${kept_queries[@]+"${kept_queries[@]}"})
fi

[ ${#queries[@]} -gt 0 ] || usage
IFS=',' read -r -a degrees <<< "$threads"

# Every file written before anything is timed: its name and its path, side by side (bash 3.2, no maps).
names=()
paths=()
path_of() {
    local i
    for i in "${!names[@]}"; do
        [ "${names[$i]}" = "$1" ] && { echo "${paths[$i]}"; return 0; }
    done

    return 1
}

for entry in "${queries[@]}"; do
    IFS='|' read -r _ _ name _ <<< "$entry"
    path_of "$name" > /dev/null && continue
    path="$(fixture "$name")"
    [ -f "$path" ] || { echo "the fixture $name was not written" >&2; exit 1; }
    names+=("$name")
    paths+=("$path")
done

out="$root/bench/.runs/duckdb-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out/duck"
tsv="$out/runs.tsv"
about="$(date -u +%Y-%m-%dT%H:%M:%SZ), $(sysctl -n machdep.cpu.brand_string 2> /dev/null || uname -m); ours: $(head -1 "$dir/commit.txt" 2> /dev/null || echo "$dir"); DuckDB $("$duckdb" --version | awk '{ print $1, $3 }'), its vortex extension $("$duckdb" -csv -noheader -c "SELECT extension_version FROM duckdb_extensions() WHERE extension_name = 'vortex'")"
echo "$about" > "$out/about.txt"
echo "session ${out#"$root"/}: $about"
echo "pairs: 3 to $most, ±$target % at 95 %; bases: $base_list"

now() { date +%H:%M:%S; }
load() { sysctl -n vm.loadavg 2> /dev/null | awk '{ print $2 }'; }

# Both engines find the files in the page cache.
for path in "${paths[@]}"; do
    cat "$path" > /dev/null
done

# The controls, read as a user: the swap in use (MB), the busiest process but the two engines ("<percent
# of a core>\t<those above 20 %>"), the free memory, the power source.
swap_used() { sysctl -n vm.swapusage 2> /dev/null | sed -E 's/.*used = ([0-9.]+)M.*/\1/'; }
free_memory() { memory_pressure -Q 2> /dev/null | awk -F ': ' '/free percentage/ { print $2 }'; }
power() { if pmset -g batt 2> /dev/null | head -1 | grep -q "AC Power"; then echo ac; else echo battery; fi; }
busiest() {
    ps -Ao pcpu=,comm= | awk '
        { cpu = $1 + 0; name = $0; sub(/^ *[0-9.]+ +/, "", name); sub(/.*\//, "", name)
          if (name ~ /Vorticity\.Benchmarks\.Runner/ || name == "duckdb" || name == "ps" || name == "awk") next
          if (cpu > top) top = cpu
          if (cpu > 20) busy = busy sprintf("%s %.0f%%; ", name, cpu) }
        END { printf "%.0f\t%s\n", top, busy }'
}

# Holds a row back while another process takes more than half a core, 30 s at most; prints the seconds waited.
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
    local where="$1" key="$2" waited="$3" top busy
    IFS=$'\t' read -r top busy <<< "$(busiest)"
    printf 'control\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$key" "$attempt" "$where" "$waited" "$(free_memory)" "$(swap_used)" "$(load)" "$(power)" "$busy" >> "$tsv"
}

# A block's figures from its rounds' times (µs) and CPU (µs), one "<time> <cpu>" a line, the first
# <skip> dropped: "<median µs>\t<rounds kept>\t<rounds past 1.5 times the median>\t<median CPU µs>\t<fastest µs>".
block_stats() {
    awk -v skip="$1" "$stats"'
        NR > skip { t[n + 0] = $1; c[n + 0] = $2; n++ }
        END {
            m = median(t, n); fastest = t[0]; past = 0
            for (i = 0; i < n; i++) { if (t[i] + 0 < fastest + 0) fastest = t[i]; if (t[i] > 1.5 * m) past++ }
            printf "%.0f\t%d\t%d\t%.0f\t%.0f\n", m, n, past, median(c, n), fastest
        }'
}

# Our side: a process of the runner, <rounds> rounds, its file kept mapped when warm.
ours_block() {
    local scenario="$1" path="$2" degree="$3" rounds="$4" skip="$5" warm="$6" log="$7"
    [ "$rest" -gt 0 ] && sleep "$rest"
    VORTICITY_RUNNER_WARM="$warm" "$binary" --scenario "$scenario" "$path" 0 --repeat "$rounds" --threads "$degree" > "$log" 2>&1 || {
        echo "the runner failed on $scenario at $degree:" >&2; tail -5 "$log" >&2; return 1; }
    printf '%s\t%s\n' "$(sed -nE 's/^round=[0-9]+ .*work_us=([0-9]+) .*cpu_us=([0-9]+).*/\1 \2/p' "$log" | block_stats "$skip")" "$(grep -o 'checksum=[^ ]*' "$log" | cut -d= -f2)"
}

# DuckDB's side: its process for a file, kept open, driven through a FIFO; each statement's output read
# as it comes, up to a sentinel, no file polled while it runs.
duck_open() {
    local path="$1"
    rm -f "$out/duck.in" "$out/duck.out"
    mkfifo "$out/duck.in" "$out/duck.out"
    "$duckdb" -csv -noheader < "$out/duck.in" > "$out/duck.out" 2>&1 &
    duck_pid=$!
    exec 3> "$out/duck.in"
    exec 4< "$out/duck.out"
    {
        echo "LOAD vortex;"
        echo "CREATE VIEW reader AS SELECT * FROM read_vortex('$path');"
        echo "CREATE TABLE native AS SELECT * FROM read_vortex('$path');"
    } >&3
    if ! duck_sync loaded > "$out/duck/loaded.out" || grep -qi "error" "$out/duck/loaded.out"; then
        echo "DuckDB did not load $path:" >&2
        cat "$out/duck/loaded.out" >&2
        return 1
    fi
}

duck_close() {
    exec 3>&-
    wait "$duck_pid" 2> /dev/null
    exec 4<&-
    rm -f "$out/duck.in" "$out/duck.out"
}

# Sends a sentinel and prints every line DuckDB wrote before it; fails when the process ended first.
duck_sync() {
    echo "SELECT 'fin-$1';" >&3
    local line
    while IFS= read -r line <&4; do
        [ "$line" = "fin-$1" ] && return 0
        printf '%s\n' "$line"
    done

    return 1
}

# A block of DuckDB's: <rounds> runs of the query over <side> at <degree> threads. A row under 20 ms
# (duck_profiled) is timed by a JSON profile a run, its latency to the nanosecond and its cpu_time, read
# once the block is done; a longer one by DuckDB's timer, real and user + sys, to the millisecond, which
# is enough there: on q10 at fourteen threads, the profile cost 2 % (median 107 ms against 105).
duck_block() {
    local side="$1" degree="$2" query="$3" rounds="$4" skip="$5" tag="$6" k
    {
        echo "SET threads = $degree;"
        if [ "$duck_profiled" -eq 1 ]; then
            echo "PRAGMA enable_profiling = 'json';"
            for k in $(seq "$rounds"); do
                echo "PRAGMA profiling_output = '$out/duck/$tag-$k.json';"
                echo "${query//FROM x /FROM $side };"
            done

            echo "PRAGMA profiling_output = '$out/duck/idle.json';"
            echo "PRAGMA disable_profiling;"
        else
            echo ".timer on"
            for k in $(seq "$rounds"); do
                echo "${query//FROM x /FROM $side };"
            done

            echo ".timer off"
        fi
    } >&3
    duck_sync "$tag" > "$out/duck/$tag.out" || { echo "DuckDB stopped in $tag:" >&2; tail -5 "$out/duck/$tag.out" >&2; return 1; }
    if [ "$duck_profiled" -eq 1 ]; then
        printf '%s\t' "$(for k in $(seq "$rounds"); do echo "$out/duck/$tag-$k.json"; done |
            python3 -c 'import json, sys
for name in sys.stdin.read().split():
    profile = json.load(open(name))
    print(round(profile["latency"] * 1e6), round(profile["cpu_time"] * 1e6))' | block_stats "$skip")"
    else
        printf '%s\t' "$(awk '/^Run Time/ { printf "%.0f %.0f\n", $5 * 1e6, ($7 + $9) * 1e6 }' "$out/duck/$tag.out" | block_stats "$skip")"
    fi

    grep -vE '^(fin-|Run Time|$)' "$out/duck/$tag.out" | head -1
}

# Two runs of DuckDB's, before a row's pairs: whether it takes under 20 ms, which a JSON profile times.
duck_probe() {
    local side="$1" degree="$2" query="$3" tag="$4" median
    duck_profiled=1
    median="$(duck_block "$side" "$degree" "$query" 2 0 "$tag")" || return 1
    median="${median%%$'\t'*}"
    [ "$median" -lt 20000 ] || duck_profiled=0
    printf 'probe\t%s\t%s\t%s\t%s\n' "$(now)" "$key" "$median" "$duck_profiled" >> "$tsv"
}

# The canaries, fixed runs of our runner with their files kept mapped: a scan of one lane and a count and
# sum of 10^3 keys on 14 lanes. Their first values are the session's standard; fails when either is more
# than 5 % off it.
canary_scan="$corpus/spread-strided-20000000.vortex"
[ -e "$canary_scan" ] || canary_scan="$corpus/spread-strided-4000000.vortex"
canary_lanes="$corpus/spread-random-40000000-core2025.10.vortex"
[ -e "$canary_lanes" ] || canary_lanes="$corpus/spread-random-20000000.vortex"
[ -e "$canary_lanes" ] || canary_lanes="$corpus/spread-random-4000000.vortex"
standard_scan=""
standard_lanes=""
canaries() {
    local where="$1" key="$2" scan lanes
    scan="$(ours_block group-stridedfloor-k7 "$canary_scan" 1 10 1 1 "$out/canary.log")" || return 2
    lanes="$(ours_block group-total-k3 "$canary_lanes" 14 10 1 1 "$out/canary.log")" || return 2
    scan="${scan%%$'\t'*}"
    lanes="${lanes%%$'\t'*}"
    [ -n "$standard_scan" ] || { standard_scan="$scan"; standard_lanes="$lanes"; }
    printf 'canary\t%s\t%s\t%s\t%s\t-\t%s\t%s\t%s\n' "$(now)" "$key" "$attempt" "$where" "$scan" "$lanes" "$standard_scan/$standard_lanes" >> "$tsv"
    awk -v a="$scan" -v b="$lanes" -v sa="$standard_scan" -v sb="$standard_lanes" \
        'BEGIN { da = a / sa - 1; db = b / sb - 1; if (da < 0) da = -da; if (db < 0) db = -db; exit (da > 0.05 || db > 0.05) ? 1 : 0 }'
}

# One side's block in a pair, recorded; run again once when more than one round in five passes 1.5 times
# its median. Prints its median.
side_block() {
    local side="$1" key="$2" pair="$3" result again=0 kept past
    shift 3
    result="$("$@")" || return 1
    IFS=$'\t' read -r _ kept past _ _ _ <<< "$result"
    if [ $((past * 5)) -gt "$kept" ]; then
        again=1
        result="$("$@")" || return 1
    fi

    printf 'run\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$key" "$attempt" "$side" "$pair" "$again" "$result" >> "$tsv"
    echo "${result%%$'\t'*}"
}

block=0
duck_profiled=1
for i in "${!names[@]}"; do
    name="${names[$i]}"
    path="${paths[$i]}"
    duck_open "$path" || exit 1
    for degree in "${degrees[@]}"; do
        for entry in "${queries[@]}"; do
            IFS='|' read -r label scenario file query <<< "$entry"
            [ "$file" = "$name" ] || continue
            file_rows="$(sed -E 's/^[a-z-]+-([0-9]+).*/\1/' <<< "$name")"
            for base in $base_list; do
                if [ "$base" = "warm" ]; then side="native"; warm=1; else side="reader"; warm=0; fi
                key="$label|$file_rows|$degree|$base"
                attempt=0
                while :; do
                    attempt=$((attempt + 1))
                    control before "$key" "$(quiet)"
                    swap_before="$(swap_used)"
                    off=0
                    if [ "$canary" -eq 1 ]; then
                        canaries before "$key" || off=1
                    fi

                    block=$((block + 1))
                    duck_probe "$side" "$degree" "$query" "b$block" || exit 1

                    # Pairs, ABBA, until the interval of the median speedup is within the target. A row whose
                    # first block passes 100 ms takes 5 rounds a block, the first 2 dropped.
                    rounds=10
                    skip=3
                    speedups=()
                    pair=0
                    held=0
                    while :; do
                        pair=$((pair + 1))
                        block=$((block + 1))
                        tag="b$block"
                        log="$out/$scenario-$degree-$base-$attempt-$pair.log"
                        if ((pair % 2 == 1)); then
                            d="$(side_block duck "$key" "$pair" duck_block "$side" "$degree" "$query" "$rounds" "$skip" "$tag")" || exit 1
                            o="$(side_block ours "$key" "$pair" ours_block "$scenario" "$path" "$degree" "$rounds" "$skip" "$warm" "$log")" || exit 1
                        else
                            o="$(side_block ours "$key" "$pair" ours_block "$scenario" "$path" "$degree" "$rounds" "$skip" "$warm" "$log")" || exit 1
                            d="$(side_block duck "$key" "$pair" duck_block "$side" "$degree" "$query" "$rounds" "$skip" "$tag")" || exit 1
                        fi

                        if [ "$pair" -eq 1 ] && awk -v a="$d" -v b="$o" 'BEGIN { exit (a > 100000 || b > 100000) ? 0 : 1 }'; then
                            rounds=5
                            skip=2
                        fi

                        speedups+=("$(awk -v d="$d" -v o="$o" 'BEGIN { printf "%.5f", d / o }')")
                        [ "$pair" -ge 3 ] || continue
                        if awk -v target="$target" "$stats"' BEGIN {
                            n = ARGC - 1; for (i = 0; i < n; i++) r[i] = ARGV[i + 1] + 0
                            interval(r, n); m = median(r, n)
                            exit (100 * (high - low) / 2 / m <= target) ? 0 : 1 }' "${speedups[@]}"; then
                            held=1
                            break
                        fi

                        [ "$pair" -ge "$most" ] && break
                    done

                    if [ "$canary" -eq 1 ]; then
                        canaries after "$key" || off=1
                    fi

                    control after "$key" 0
                    grew=0
                    awk -v a="${swap_before:-0}" -v b="$(swap_used)" 'BEGIN { exit (b > a + 1) ? 0 : 1 }' && grew=1
                    if { { [ "$off" -eq 1 ] && [ "$held" -eq 0 ]; } || [ "$grew" -eq 1 ]; } && [ "$attempt" -eq 1 ]; then
                        printf 'replay\t%s\t%s\t%s\tcanary off %s, swap grew %s\n' "$(now)" "$key" "$attempt" "$off" "$grew" >> "$tsv"
                        continue
                    fi

                    printf 'row\t%s\t%s\t%s\t%s\t%s\n' "$(now)" "$key" "$attempt" "$off" "$grew" >> "$tsv"
                    break
                done
            done
        done
    done

    duck_close
done

# Each row's result, from its last attempt: the medians of each side's blocks, the median of the pairs'
# speedups with its interval, the CPU of each side, the notes; and the answers, which must agree block
# after block and side against side, to a part in 10^9: a float's sum depends on its order.
awk -F '\t' -v target="$target" "$stats"'
    function near(a, b,    d, m) { d = a - b; if (d < 0) d = -d; m = (a < 0 ? -a : a); if (m < 1) m = 1; return a != "" && b != "" && d / m < 1e-9 }
    $1 == "row" { last[$3] = $4; off[$3] = $5; grew[$3] = $6; next }
    $1 == "control" && $5 == "before" { waited[$3, $4] = $6; busy[$3, $4] = $11; next }
    $1 == "run" {
        key = $3
        if (!(key in seen)) { seen[key] = 1; order[++n] = key }
        k = key SUBSEP $4 SUBSEP $5
        at = count[k]++
        time[k, at] = $8; pairof[k, at] = $6; cpu[k, at] = $11; fast[k, at] = $12; ans[k, at] = $13
        if ($7 == 1) again[key, $4]++
    }
    END {
        for (i = 1; i <= n; i++) {
            key = order[i]; t = last[key]; d = key SUBSEP t SUBSEP "duck"; o = key SUBSEP t SUBSEP "ours"
            nd = count[d]; no = count[o]; np = 0; fd = 1e18; fo = 1e18; differs = 0
            for (j = 0; j < nd; j++) { ld[j] = time[d, j]; cd[j] = cpu[d, j]; if (fast[d, j] + 0 < fd) fd = fast[d, j] + 0; td[pairof[d, j]] = time[d, j]; if (!near(ans[d, j], ans[d, 0])) differs = 1 }
            for (j = 0; j < no; j++) { lo[j] = time[o, j]; co[j] = cpu[o, j]; if (fast[o, j] + 0 < fo) fo = fast[o, j] + 0; to[pairof[o, j]] = time[o, j]; if (!near(ans[o, j], ans[o, 0])) differs = 1 }
            for (p = 1; p <= nd && p <= no; p++) r[np++] = td[p] / to[p]
            speedup = median(r, np); interval(r, np)
            md = median(ld, nd); mo = median(lo, no)
            notes = ""
            gap = (fd / fo) / speedup - 1; if (gap < 0) gap = -gap
            if (gap > 0.10) notes = notes "noisy; "
            if (100 * (high - low) / 2 / speedup > target) notes = notes "wide; "
            if (off[key]) notes = notes "canary off; "
            if (grew[key]) notes = notes "swap grew; "
            if (t > 1) notes = notes "replayed; "
            if (waited[key, t] > 0) notes = notes "waited " waited[key, t] " s; "
            if (again[key, t] > 0) notes = notes again[key, t] " blocks again; "
            if (busy[key, t] != "") notes = notes "busy: " busy[key, t]
            split(key, f, "|")
            answer = (!differs && near(ans[d, 0], ans[o, 0])) ? "same" : "DIFFERENT"
            printf "%s\t%s\t%s\t%s\t%.2f\t%.2f\t%.3f\t%.3f\t%.3f\t%d\t%.2f\t%s\t%s\n", f[1], f[2], f[3], f[4], mo / 1000, md / 1000, speedup, low, high, np,
                median(cd, nd) / median(co, no), answer, notes
            delete td; delete to
        }
    }' "$tsv" > "$out/session.tsv"

echo
echo "| query | rows | threads | base | ours (ms) | DuckDB (ms) | speedup | 95 % | pairs | CPU DuckDB/ours | answer | notes |"
echo "|---|---:|---:|---|---:|---:|---:|---|---:|---:|---|---|"
awk -F '\t' '{ printf "| %s | %s | %s | %s | %.2f | %.2f | %.2fx | %.2f–%.2f | %d | %.2f | %s | %s |\n", $1, $2, $3, ($4 == "warm" ? "warm, native table" : "cold, Vortex reader"), $5, $6, $7, $8, $9, $10, $11, $12, $13 }' "$out/session.tsv"
echo
echo "kept in ${out#"$root"/}: runs.tsv, session.tsv; the page's table from two sessions: bench/duckdb.sh --publish <session> <session>"
