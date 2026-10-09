#!/usr/bin/env bash
# Our group bys against DuckDB's, on the same files: DuckDB through its vortex extension and on a native
# table already in memory, our side the Native AOT runner, at the same threads.
#
#   bench/duckdb.sh [--set hc|db|all] [--only <rows>] [--rows N] [--db-rows N] [--threads N[,M…]]
#                   [--runner <commit>|<dir>] [--rounds N] [--rest S]
#
#   bench/duckdb.sh                                   PR #43's table: 40M rows, 1 and 14 threads
#   bench/duckdb.sh --set db --db-rows 10000000       db-benchmark's group by at 10^7 rows
#   bench/duckdb.sh --rows 4000000 --threads 1        a quick look
#   bench/duckdb.sh --set all --only db-q4,total-k7 --threads 14
#                                                     the rows a change touches, and no others: the
#                                                     runner's scenario names without "group-" (total-k3,
#                                                     range-k6, strided-k7, db-q10…)
#
# THE SAME WORK ON BOTH SIDES. Each query is a group by whose result is aggregated once more: DuckDB's outer
# query sums every column the group by aggregates, and the runner sums the same columns as it reads the
# groups (GroupScenarios.Checksum). The two sums are the same answer, and the table says when they are
# not. Each figure is the median of the last 7 of 10 runs (--rounds), each side's process started once a
# query; DuckDB's timer counts whole milliseconds. The native table's time excludes its loading.
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
# at rest: docs/guide/benchmarks-duckdb.md.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
duckdb="${DUCKDB:-$(command -v duckdb || echo /opt/homebrew/bin/duckdb)}"
set_name="hc"
only=""
rows=40000000
db_rows=10000000
threads="1,14"
runner="HEAD"
rounds=10
rest=0

usage() {
    echo "usage: bench/duckdb.sh [--set hc|db|all] [--only <rows>] [--rows N] [--db-rows N] [--threads N[,M…]] [--runner <commit>|<dir>] [--rounds N] [--rest S]" >&2
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
        --rounds) rounds="$2"; shift 2 ;;
        --rest) rest="$2"; shift 2 ;;
        *) usage ;;
    esac
done

[ -x "$duckdb" ] || { echo "no duckdb at '$duckdb': install it, or set DUCKDB" >&2; exit 2; }
"$duckdb" -c "LOAD vortex;" > /dev/null 2>&1 || { echo "duckdb cannot load its vortex extension: run INSTALL vortex in it" >&2; exit 2; }
[ "$rounds" -ge 7 ] || { echo "--rounds must be 7 or more: the median is of the last 7" >&2; exit 2; }

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

# The median of the last 7 of the numbers on stdin, one a line.
median7() { tail -7 | sort -n | sed -n 4p; }

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

# DuckDB's side, a process a file: its view over the extension and its native table, then each query at
# each degree, `rounds` times on each, each block marked. Prints "<label>|<degree>|<side>|<ms>|<answer>".
duck() {
    local name="$1" path="$2" script entry label query degree side
    script="$(mktemp)"
    {
        echo "LOAD vortex;"
        echo "CREATE VIEW reader AS SELECT * FROM read_vortex('$path');"
        echo "CREATE TABLE native AS SELECT * FROM read_vortex('$path');"
        for degree in "${degrees[@]}"; do
            echo "SET threads = $degree;"
            for entry in "${queries[@]}"; do
                IFS='|' read -r label _ file query <<< "$entry"
                [ "$file" = "$name" ] || continue
                for side in reader native; do
                    echo "SELECT 'mark|$label|$degree|$side';"
                    echo ".timer on"
                    for _ in $(seq "$rounds"); do
                        echo "${query//FROM x /FROM $side };"
                    done

                    echo ".timer off"
                done
            done
        done
    } > "$script"

    "$duckdb" -csv -noheader < "$script" | awk -v rounds="$rounds" '
        { sub(/^"/, ""); sub(/"$/, "") }
        /^mark\|/ { split($0, f, "|"); key = f[2] "|" f[3] "|" f[4]; keys[++n] = key; count[key] = 0; next }
        /^Run Time/ { times[key, count[key]++] = $5 * 1000; next }
        { if (!((key) in answer)) answer[key] = $0 }
        END {
            for (i = 1; i <= n; i++) {
                key = keys[i]; m = 0
                for (j = count[key] - 7; j < count[key]; j++) { v[++m] = times[key, j] }
                for (a = 2; a <= m; a++) { x = v[a]; b = a - 1; while (b > 0 && v[b] > x) { v[b + 1] = v[b]; b-- } v[b + 1] = x }
                printf "%s|%.0f|%s\n", key, v[4], answer[key]
            }
        }'
    rm -f "$script"
}

# Our side: the runner, a process a query and degree. Prints "<ms>|<answer>".
ours() {
    local scenario="$1" path="$2" degree="$3" out ms answer
    [ "$rest" -gt 0 ] && sleep "$rest"
    out="$("$binary" --scenario "$scenario" "$path" 0 --repeat "$rounds" --threads "$degree" 2>&1)"
    ms="$(grep '^round=' <<< "$out" | sed 's/.*work_us=\([0-9]*\).*/\1/' | median7 | awk '{ printf "%.1f", $1 / 1000 }')"
    answer="$(grep -o 'checksum=[^ ]*' <<< "$out" | cut -d= -f2)"
    echo "$ms|$answer"
}

# The two answers, the same to a part in 10^9: a float's sum depends on its order.
same() {
    awk -v a="$1" -v b="$2" 'BEGIN { d = a - b; if (d < 0) d = -d; m = (a < 0 ? -a : a); if (m < 1) m = 1; print (a != "" && b != "" && d / m < 1e-9) ? "same" : "DIFFERENT" }'
}

echo "# Our group bys against DuckDB's"
echo
echo "- $(date -u +%Y-%m-%dT%H:%M:%SZ), $(sysctl -n machdep.cpu.brand_string 2> /dev/null || uname -m), load $(sysctl -n vm.loadavg 2> /dev/null | awk '{ print $2 }')"
echo "- ours: the Native AOT runner, $(head -1 "$dir/commit.txt" 2> /dev/null || echo "$dir")"
echo "- DuckDB $("$duckdb" --version | awk '{ print $1, $3 }'), its vortex extension $("$duckdb" -csv -noheader -c "SELECT extension_version FROM duckdb_extensions() WHERE extension_name = 'vortex'")"
echo "- the median of the last 7 of $rounds runs; speedup is DuckDB's time over ours, above 1.00x we are faster"
echo
echo "| query | rows | threads | ours (ms) | DuckDB, Vortex reader (ms) | DuckDB, native table (ms) | speedup vs Vortex reader | speedup vs native table | answer |"
echo "|---|---:|---:|---:|---:|---:|---:|---:|---|"

duck_rows="$(mktemp)"
trap 'rm -f "$duck_rows"' EXIT
for i in "${!names[@]}"; do
    duck "${names[$i]}" "${paths[$i]}" >> "$duck_rows"
done

# DuckDB's figure and answer for a query, a degree and a side: "<ms>|<answer>".
duck_row() { grep -F "$1|$2|$3|" "$duck_rows" | head -1 | cut -d'|' -f4-; }

for degree in "${degrees[@]}"; do
    for entry in "${queries[@]}"; do
        IFS='|' read -r label scenario name _ <<< "$entry"
        IFS='|' read -r mine mine_answer <<< "$(ours "$scenario" "$(path_of "$name")" "$degree")"
        IFS='|' read -r reader reader_answer <<< "$(duck_row "$label" "$degree" reader)"
        IFS='|' read -r native native_answer <<< "$(duck_row "$label" "$degree" native)"
        file_rows="$(sed -E 's/^[a-z-]+-([0-9]+).*/\1/' <<< "$name")"
        answers="$(same "$mine_answer" "$reader_answer")"
        [ "$(same "$mine_answer" "$native_answer")" = "same" ] || answers="DIFFERENT"
        awk -v label="$label" -v rows="$file_rows" -v degree="$degree" -v ours="$mine" -v reader="$reader" -v native="$native" -v answers="$answers" 'BEGIN {
            printf "| %s | %s | %s | %.1f | %s | %s | %.2fx | %.2fx | %s |\n", label, rows, degree, ours, reader, native, reader / ours, native / ours, answers }'
    done
done
