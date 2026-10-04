#!/usr/bin/env bash
# Builds the native reference side of the comparisons, into tools/native-ref/out/:
#
#   libzstd_ref.dylib   zstd 1.5.7 compiled with today's compiler at -O3: the libzstd a well-built
#                       C program would ship, and the speed this project aims for.
#   zd                  the timing harness (zd.c): compresses the reference frame with the first
#                       library, writes it out, and times ZSTD_decompressDCtx in each library given.
#   libzstd_mt.dylib    the same library with ZSTD_MULTITHREAD: zstdmt, what libzstd writes with
#                       ZSTD_c_nbWorkers >= 1, the oracle of the parallel compressor. `build.sh mt`
#                       builds it alone.
#
# The sources are the zstd 1.5.7 release tarball (zstd-source.sh). macOS: the libraries are dylibs,
# and zd times with mach_absolute_time. The test data comes from testdata.sh, which runs anywhere.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$here/zstd-source.sh"

lib="$src/lib"
cc="${CC:-cc}"
echo "compiler: $($cc --version | head -1)"

# libzstd_mt.dylib: compression with worker threads (zstdmt), decompression for the round trips.
build_mt() {
  local objs="$out/obj-mt"
  rm -rf "$objs" && mkdir -p "$objs"
  (
    cd "$objs"
    $cc -O3 -DZSTD_MULTITHREAD -pthread -c -I"$lib" -I"$lib/common" \
      "$lib"/common/*.c "$lib"/compress/*.c \
      "$lib/decompress/zstd_ddict.c" "$lib/decompress/zstd_decompress.c" \
      "$lib/decompress/zstd_decompress_block.c" "$lib/decompress/huf_decompress.c"
    $cc -dynamiclib -pthread -o "$out/libzstd_mt.dylib" ./*.o
  )
  echo "built: $out/libzstd_mt.dylib"
}

if [[ "${1:-}" == mt ]]; then
  build_mt
  exit 0
fi

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

build_mt
echo "built: $out/libzstd_ref.dylib $out/zd"
