#!/usr/bin/env bash
# Downloads the corpora of zstd's own benchmarks into tests/Vorticity.Zstd.Tests/testdata/corpus/ (not in git), each checked
# against its SHA-256, for Vorticity.Zstd.Perf --corpus:
#
#   silesia.zip      the Silesia compression corpus: 12 files, 211,938,580 bytes. The data of the
#                    benchmarks in zstd's README (lzbench on silesia.tar).
#   github.tar.zst   500 JSON records of GitHub users, some 800 bytes each, and github.dict.zst, the
#   github.dict.zst  110 KiB dictionary trained on them: the small data of zstd's regression tests
#                    (tests/regression/data.c, release "regression-data").
set -euo pipefail
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
out="$root/tests/Vorticity.Zstd.Tests/testdata/corpus"
mkdir -p "$out"

fetch() {
  local name="$1" url="$2" sha256="$3"
  if [[ ! -f "$out/$name" ]]; then
    curl -fsSL -o "$out/$name.part" "$url"
    mv "$out/$name.part" "$out/$name"
  fi
  echo "$sha256  $out/$name" | shasum -a 256 -c - > /dev/null
  echo "$out/$name"
}

regression=https://github.com/facebook/zstd/releases/download/regression-data
fetch silesia.zip https://sun.aei.polsl.pl/~sdeor/corpus/silesia.zip 0626e25f45c0ffb5dc801f13b7c82a3b75743ba07e3a71835a41e3d9f63c77af
fetch github.tar.zst "$regression/github.tar.zst" aa3a49084cfaff5b3ada174fef43bf57d2f480448598be53e7351177dea03162
fetch github.dict.zst "$regression/github.dict.zst" 1232682fc32888765e7e5e1d662466379d91e2ecbee610dabb6f683dbaa839d5
