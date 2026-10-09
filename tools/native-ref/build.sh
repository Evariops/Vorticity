#!/usr/bin/env bash
# Builds the native reference side of the comparisons, into tools/native-ref/out/:
#
#   libzstd_ref.<ext>   zstd 1.5.7 compiled with today's compiler at -O3: the libzstd a well-built
#                       C program would ship, and the speed this project aims for.
#   zd                  the timing harness (zd.c, macOS only): compresses the reference frame with the
#                       first library, writes it out, and times ZSTD_decompressDCtx in each library given.
#   libzstd_mt.<ext>    the same library with ZSTD_MULTITHREAD: zstdmt, what libzstd writes with
#                       ZSTD_c_nbWorkers >= 1, the oracle of the parallel compressor. `build.sh mt`
#                       builds it alone.
#
# The sources are the zstd 1.5.7 release tarball (zstd-source.sh). The libraries are dylibs on macOS,
# where zd times with mach_absolute_time; DLLs on Windows, from Git Bash with a GNU-compatible clang
# (CC=<llvm-mingw>/bin/clang: MSVC builds neither the assembly nor the BMI2 dispatch libzstd ships
# with); shared objects elsewhere. On x86-64 the library takes zstd's assembly Huffman decoder, as
# libzstd built by a GNU-compatible compiler does there. The test data comes from testdata.sh, which
# runs anywhere.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$here/zstd-source.sh"

lib="$src/lib"
cc="${CC:-cc}"
echo "compiler: $($cc --version | head -1)"

pic=()
pdb=
threads=(-pthread)
case "$(uname -s)" in
  Darwin) shared=(-dynamiclib); ext=dylib ;;
  # Windows threads for zstdmt (lib/common/threading.c), not winpthreads, which would be one more DLL.
  # A PDB beside each DLL, for profilers: debug information leaves the code as it is.
  MINGW* | MSYS* | CYGWIN*) shared=(-shared); ext=dll; threads=(); pdb=1 ;;
  *) shared=(-shared); ext=so; pic=(-fPIC) ;;
esac

asm=()
if [[ "$(uname -m)" == x86_64 ]]; then
  asm=("$lib/decompress/huf_decompress_amd64.S")
fi

sources=(
  "$lib"/common/*.c "$lib"/compress/*.c
  "$lib/decompress/zstd_ddict.c" "$lib/decompress/zstd_decompress.c"
  "$lib/decompress/zstd_decompress_block.c" "$lib/decompress/huf_decompress.c"
  ${asm[@]+"${asm[@]}"}
)

# libzstd_mt: compression with worker threads (zstdmt), decompression for the round trips.
build_mt() {
  local objs="$out/obj-mt"
  rm -rf "$objs" && mkdir -p "$objs"
  (
    cd "$objs"
    $cc -O3 ${pic[@]+"${pic[@]}"} -DZSTD_MULTITHREAD ${threads[@]+"${threads[@]}"} -c -I"$lib" -I"$lib/common" "${sources[@]}"
    $cc "${shared[@]}" ${threads[@]+"${threads[@]}"} -o "$out/libzstd_mt.$ext" ./*.o
  )
  echo "built: $out/libzstd_mt.$ext"
}

if [[ "${1:-}" == mt ]]; then
  build_mt
  exit 0
fi

# libzstd_ref: the reference library, with the flags of the measurement it reproduces.
objs="$out/obj-ref"
rm -rf "$objs" && mkdir -p "$objs"
(
  cd "$objs"
  $cc -O3 ${pic[@]+"${pic[@]}"} ${pdb:+-g -gcodeview} -c -I"$lib" -I"$lib/common" "${sources[@]}"
  $cc "${shared[@]}" ${pdb:+-Wl,--pdb=$out/libzstd_ref.pdb} -o "$out/libzstd_ref.$ext" ./*.o
)

# zd: the timing harness, on macOS.
if [[ "$ext" == dylib ]]; then
  $cc -O2 -o "$out/zd" "$here/zd.c"
fi

build_mt
echo "built: $out/libzstd_ref.$ext"
