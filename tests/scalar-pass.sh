#!/usr/bin/env bash
# Runs what CI runs with intrinsics disabled: the classes of Vorticity.Tests that tests/scalar-pass.txt
# names, which reach every scalar fallback the whole suite does, Parquet's tests among them, and the
# conformance corpus, through the scalar decoders.
# Arguments go to each dotnet test, --no-build say.
#
# PASS picks the tier the kernels run on: scalar (the default), intrinsics disabled; avx2, AVX-512
# disabled, for the 256-bit paths a machine with AVX-512 never takes; vector128, AVX2 disabled, for
# the 128-bit paths. The same classes reach every branch on the hardware at each tier.
#
#   bash tests/scalar-pass.sh
#   PASS=vector128 bash tests/scalar-pass.sh --no-build
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
filters=()
while IFS= read -r filter; do
  filters+=(--filter-class "$filter")
done < <(grep -v -E '^[[:space:]]*(#|$)' "$root/tests/scalar-pass.txt")

case "${PASS:-scalar}" in
  scalar) export DOTNET_EnableHWIntrinsic=0 ;;
  avx2) export DOTNET_EnableAVX512=0 ;;
  vector128) export DOTNET_EnableAVX2=0 ;;
  *) echo "PASS is scalar, avx2 or vector128, not ${PASS}" >&2; exit 2 ;;
esac

status=0
dotnet test --project "$root/tests/Vorticity.Tests" -c Release "${filters[@]}" "$@" || status=1
dotnet test --project "$root/tests/Vorticity.Conformance" -c Release "$@" || status=1
exit $status
