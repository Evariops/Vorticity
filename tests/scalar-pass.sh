#!/usr/bin/env bash
# Runs what CI runs with intrinsics disabled: the classes of Vorticity.Tests that tests/scalar-pass.txt
# names, which reach every scalar fallback the whole suite does, and the conformance corpus, through
# the scalar decoders. Arguments go to each dotnet test, --no-build say.
#
#   bash tests/scalar-pass.sh
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
filters=()
while IFS= read -r filter; do
  filters+=(--filter-class "$filter")
done < <(grep -v -E '^[[:space:]]*(#|$)' "$root/tests/scalar-pass.txt")

export DOTNET_EnableHWIntrinsic=0
status=0
dotnet test --project "$root/tests/Vorticity.Tests" -c Release "${filters[@]}" "$@" || status=1
dotnet test --project "$root/tests/Vorticity.Conformance" -c Release "$@" || status=1
exit $status
