#!/usr/bin/env bash
# Runs what CI's runner for the tests that run alone runs: the classes of Vorticity.Tests that carry
# the trait Category=RunsAlone, in three processes side by side. Such a class reads what its whole
# process does (allocations, peaks, finalizers, a shared pool), so it needs a process to itself, not a
# machine: what another process does moves none of it. GroupSpillTests takes half of their time, so
# its methods go to two processes, the methods that share an expected answer or a peak kept together,
# and the third runs every other class. The second and the third take what the one before them does
# not name, so a method or a class added later is never left out.
# Each line of output carries the name of its process. Arguments go to each dotnet test, --no-build
# say. With --report-xunit-xml, each process writes its own report, named after it.
#
#   bash tests/runs-alone.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
spill=Vorticity.Tests.Aggregation.GroupSpillTests

# The catalogue, which shares the count's peak with the state refused past the budget, and the two
# keys of six columns, which share their expected counts: about half of the class's time.
first=(
  "$spill.EveryAggregateOfTheCatalogueSpillsAndEndsAsItWould"
  "$spill.AStateWithReferencesIsRefusedTypedPastTheBudget"
  "$spill.AKeyOfSixPartsSpillsAndEndsExact"
  "$spill.AKeyOfSixValuesSpillsAsItsTuplesAndEndsExact"
)

group_spill_1=(--filter-class "$spill")
group_spill_2=(--filter-class "$spill")
for method in "${first[@]}"; do
  group_spill_1+=(--filter-method "$method")
  group_spill_2+=(--filter-not-method "$method")
done
others=(--filter-trait Category=RunsAlone --filter-not-class "$spill")

build=true
report=false
for arg in "$@"; do
  case "$arg" in
    --no-build) build=false ;;
    --report-xunit-xml) report=true ;;
  esac
done

# The three start at once and would each build the same project: the build comes first, once.
if $build; then
  dotnet build "$root/tests/Vorticity.Tests" -c Release
  set -- --no-build "$@"
fi

# One process, its output prefixed with its name. With pipefail, a failing dotnet test fails the run.
run() {
  local name=$1
  shift
  local named=()
  if $report; then
    named=(--report-xunit-xml-filename "runs-alone-$name.xunit.xml")
  fi
  dotnet test --project "$root/tests/Vorticity.Tests" -c Release "$@" ${named[@]+"${named[@]}"} 2>&1 |
    awk -v prefix="[$name] " '{ print prefix $0; fflush() }'
}

run group-spill-1 "${group_spill_1[@]}" "$@" &
first_pid=$!
run group-spill-2 "${group_spill_2[@]}" "$@" &
second_pid=$!
run others "${others[@]}" "$@" &
third_pid=$!

status=0
wait "$first_pid" || status=1
wait "$second_pid" || status=1
wait "$third_pid" || status=1
exit $status
