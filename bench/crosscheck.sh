#!/usr/bin/env bash
# Every file we write, read back by Vortex Rust and compared scalar by scalar.
#
#   bench/crosscheck.sh
#
# Two steps that were only ever run by hand, which is how three documents came to claim a green
# cross-check while it was red: the .NET side writes the whole corpus out, the Rust side reads each
# file and compares it against the reference's own. Exit is non-zero when a file disagrees.
#
# A file the REFERENCE cannot read on its own side is skipped and named, not failed: the corpus
# deliberately carries one written with editions off, and the verifier deliberately pins editions.
#
# Then the type and encoding matrix: one table of every .NET type a record maps, written under every
# encoding hint, each file compared against its canonical twin. The corpus holds the reference's
# files, so it cannot show an encoding the reference never writes; the matrix does.
#
# Needs cargo. About seven minutes on a warm build, three of them writing the matrix and four the
# reference reading everything back, which is why it is not in bench/gate.sh by default --
# `bench/gate.sh --crosscheck` adds it when you want it.
set -uo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="${VORTICITY_WRITE_CORPUS:-${TMPDIR:-/tmp}/vorticity-crosscheck}"
matrix="${VORTICITY_WRITE_MATRIX:-${TMPDIR:-/tmp}/vorticity-crosscheck-matrix}"

echo "writing the corpus to $out and the matrix to $matrix"
rm -rf "$out" "$matrix"
VORTICITY_WRITE_CORPUS="$out" dotnet test "$root/Vorticity.slnx" -c Release \
    --filter-method '*WritesTheCorpusOutForTheRustCrossCheck*' > "${out}.log" 2>&1 || {
    echo "the corpus could not be written:" >&2; tail -20 "${out}.log" >&2; exit 1; }
VORTICITY_WRITE_MATRIX="$matrix" dotnet test "$root/Vorticity.slnx" -c Release \
    --filter-class '*TypeEncodingMatrixTests*' > "${matrix}.log" 2>&1 || {
    echo "the matrix could not be written:" >&2; tail -20 "${matrix}.log" >&2; exit 1; }

cd "$root/tools/conformance-gen"
cargo run --release --example verify_written -- "$root/tests/Vorticity.Conformance/corpus" "$out" || exit 1

status=0
for variant in "$matrix"/*/; do
    name="$(basename "$variant")"
    [ "$name" = reference ] && continue
    echo "matrix, $name:"
    cargo run --quiet --release --example verify_written -- "$matrix/reference" "$variant" || status=1
done

exit $status
