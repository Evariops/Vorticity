#!/usr/bin/env bash
# Writes the data Vorticity.Zstd's tests read into tests/Vorticity.Zstd.Tests/testdata/, which git
# ignores: binary files, generated rather than kept. From the zstd 1.5.7 release (zstd-source.sh):
#
#   golden-*        zstd's own golden files (its tests/golden-*): frames that must decode, frames that
#                   must be refused, inputs whose compression once broke a decoder, and a dictionary.
#   decodecorpus/   random VALID frames from zstd's decodecorpus, which exercise what an encoder rarely
#                   emits: RLE and repeat sequence tables, treeless literals, direct Huffman weights, raw
#                   and RLE blocks between compressed ones, dictionary references. Only the frames are
#                   kept, with a manifest of each original's size and SHA-256: the originals would weigh
#                   tens of megabytes. The seeds below make them the same bytes on every machine.
#
# Needs curl, tar and a C compiler (CC, or cc); runs on macOS and Linux, and on Windows from Git Bash
# with a GNU-compatible clang as CC (llvm-mingw). CI runs it before the tests,
# which fail there without the data, and skip elsewhere.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$here/zstd-source.sh"
dest="$here/../../tests/Vorticity.Zstd.Tests/testdata"
lib="$src/lib"
cc="${CC:-cc}"

# decodecorpus, as zstd's tests/Makefile builds it: the whole library plus the dictionary builder,
# without the x86-64 assembly of the Huffman decoder, a .S file which the frames do not depend on.
gen="$out/decodecorpus"
$cc -O2 -DZSTD_MULTITHREAD=0 -DZSTD_DISABLE_ASM \
  -I"$lib" -I"$lib/common" -I"$lib/compress" -I"$lib/dictBuilder" -I"$lib/decompress" -I"$src/programs" \
  -o "$gen" \
  "$src/tests/decodecorpus.c" "$src/programs/util.c" "$src/programs/timefn.c" \
  "$lib"/common/*.c "$lib"/compress/zstdmt_compress.c "$lib"/compress/hist.c \
  "$lib"/compress/huf_compress.c "$lib"/compress/fse_compress.c \
  "$lib"/compress/zstd_compress_literals.c "$lib"/compress/zstd_compress_sequences.c \
  "$lib"/compress/zstd_compress_superblock.c "$lib"/compress/zstd_double_fast.c \
  "$lib"/compress/zstd_fast.c "$lib"/compress/zstd_lazy.c "$lib"/compress/zstd_ldm.c \
  "$lib"/compress/zstd_opt.c "$lib"/compress/zstd_preSplit.c \
  "$lib"/decompress/*.c "$lib"/dictBuilder/*.c -lm -lpthread

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

#        set    count seed  extra arguments
generate() {
  local set=$1 count=$2 seed=$3; shift 3
  mkdir -p "$work/$set"
  "$gen" -n"$count" -p"$work/$set" -o"$work/$set" -s"$seed" "$@" > /dev/null
}

generate small 300 101 --max-content-size-log=12
generate plain 200 102 --max-content-size-log=15
generate large  20 103 --max-content-size-log=18
generate dict  100 104 --use-dict=32KB --max-content-size-log=15

rm -rf "$dest/decodecorpus"
for set in small plain large dict; do
  mkdir -p "$dest/decodecorpus/$set"
  manifest="$dest/decodecorpus/$set/manifest.txt"
  : > "$manifest"
  for frame in "$work/$set"/*.zst; do
    name="$(basename "$frame" .zst)"
    original="$work/$set/$name"
    cp "$frame" "$dest/decodecorpus/$set/"
    echo "$name.zst $(wc -c < "$original" | tr -d ' ') $(sha256_of "$original")" >> "$manifest"
  done
  if [[ -f "$work/$set/dictionary" ]]; then
    cp "$work/$set/dictionary" "$dest/decodecorpus/$set/dictionary"
  fi
done

# The golden files, without the .gitignore one of their directories carries.
for golden in golden-compression golden-decompression golden-decompression-errors golden-dictionaries; do
  rm -rf "$dest/$golden"
  mkdir -p "$dest/$golden"
  find "$src/tests/$golden" -maxdepth 1 -type f ! -name '.*' -exec cp {} "$dest/$golden/" \;
done

echo "wrote $(find "$dest" -type f ! -path '*/corpus/*' | wc -l | tr -d ' ') files into $dest"
