#!/usr/bin/env bash
# Builds the native reference side of the comparisons, into tools/native-ref/out/:
#
#   libzstd_ref.dylib   zstd 1.5.7 compiled with today's compiler at -O3: the libzstd a well-built
#                       C program would ship, and the speed this project aims for.
#   zd                  the timing harness (zd.c): compresses the reference frame with the first
#                       library, writes it out, and times ZSTD_decompressDCtx in each library given.
#   decodecorpus        zstd's own generator of random VALID frames, which exercises the modes an
#                       encoder almost never emits (RLE and repeat sequence tables, treeless
#                       literals, direct Huffman weights...). Used to build the test corpus.
#
# The sources are the zstd 1.5.7 release tarball, downloaded once and checked against its SHA-256;
# ZSTD_SRC=<dir> points at an already extracted tree instead.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
out="$here/out"
mkdir -p "$out"

version=1.5.7
sha256=eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3

if [[ -n "${ZSTD_SRC:-}" ]]; then
  src="$ZSTD_SRC"
else
  src="$out/zstd-$version"
  if [[ ! -d "$src" ]]; then
    tarball="$out/zstd-$version.tar.gz"
    if [[ ! -f "$tarball" ]]; then
      curl -fsSL -o "$tarball" "https://github.com/facebook/zstd/releases/download/v$version/zstd-$version.tar.gz"
    fi
    echo "$sha256  $tarball" | shasum -a 256 -c - >/dev/null
    tar -xzf "$tarball" -C "$out"
  fi
fi

lib="$src/lib"
cc="${CC:-cc}"
echo "compiler: $($cc --version | head -1)"

# libzstd_ref.dylib: the reference library, with the flags of the measurement it reproduces.
objs="$out/obj-ref"
rm -rf "$objs" && mkdir -p "$objs"
(
  cd "$objs"
  $cc -O3 -c -I"$lib" -I"$lib/common" \
    "$lib"/common/*.c "$lib"/compress/*.c \
    "$lib/decompress/zstd_ddict.c" "$lib/decompress/zstd_decompress.c" \
    "$lib/decompress/zstd_decompress_block.c" "$lib/decompress/huf_decompress.c"
  $cc -dynamiclib -o "$out/libzstd_ref.dylib" ./*.o
)

# zd: the timing harness.
$cc -O2 -o "$out/zd" "$here/zd.c"

# decodecorpus, as zstd's tests/Makefile builds it: the whole library plus the dictionary builder.
$cc -O2 -DZSTD_MULTITHREAD=0 \
  -I"$lib" -I"$lib/common" -I"$lib/compress" -I"$lib/dictBuilder" -I"$lib/decompress" -I"$src/programs" \
  -o "$out/decodecorpus" \
  "$src/tests/decodecorpus.c" "$src/programs/util.c" "$src/programs/timefn.c" \
  "$lib"/common/*.c "$lib"/compress/zstdmt_compress.c "$lib"/compress/hist.c \
  "$lib"/compress/huf_compress.c "$lib"/compress/fse_compress.c \
  "$lib"/compress/zstd_compress_literals.c "$lib"/compress/zstd_compress_sequences.c \
  "$lib"/compress/zstd_compress_superblock.c "$lib"/compress/zstd_double_fast.c \
  "$lib"/compress/zstd_fast.c "$lib"/compress/zstd_lazy.c "$lib"/compress/zstd_ldm.c \
  "$lib"/compress/zstd_opt.c "$lib"/compress/zstd_preSplit.c \
  "$lib"/decompress/*.c "$lib"/dictBuilder/*.c -lm

echo "built: $out/libzstd_ref.dylib $out/zd $out/decodecorpus"
