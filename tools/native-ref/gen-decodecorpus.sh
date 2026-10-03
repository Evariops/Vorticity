#!/usr/bin/env bash
# Regenerates tests/Vorticity.Zstd.Tests/testdata/decodecorpus/ with zstd's decodecorpus (built by build.sh): random VALID frames
# that exercise what an encoder rarely emits - RLE and repeat sequence tables, treeless literals,
# direct Huffman weights, raw and RLE blocks between compressed ones, dictionary references.
#
# Only the frames are kept, with a manifest giving each original's size and SHA-256: the originals
# themselves would weigh tens of megabytes. The seeds below make the output reproducible.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
gen="$here/out/decodecorpus"
dest="$here/../../tests/Vorticity.Zstd.Tests/testdata/decodecorpus"
[[ -x "$gen" ]] || { echo "run build.sh first" >&2; exit 1; }

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

rm -rf "$dest"
for set in small plain large dict; do
  mkdir -p "$dest/$set"
  manifest="$dest/$set/manifest.txt"
  : > "$manifest"
  for frame in "$work/$set"/*.zst; do
    name="$(basename "$frame" .zst)"
    original="$work/$set/$name"
    cp "$frame" "$dest/$set/"
    size=$(wc -c < "$original" | tr -d ' ')
    hash=$(shasum -a 256 "$original" | cut -d' ' -f1)
    echo "$name.zst $size $hash" >> "$manifest"
  done
  [[ -f "$work/$set/dictionary" ]] && cp "$work/$set/dictionary" "$dest/$set/dictionary"
done

du -sh "$dest"
