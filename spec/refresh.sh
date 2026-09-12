#!/usr/bin/env bash
# Re-vendor the upstream Vortex schema files and edition manifests.
#
#   ./refresh.sh              # the pinned version (see PINNED_REF below)
#   ./refresh.sh develop      # upstream tip, to check for drift
#   ./refresh.sh 0.85.0       # any tag
#
# The default is pinned rather than tracking a branch on purpose: a run of this script must
# reproduce exactly the bytes that are checked in, or "git diff -- spec/" stops meaning
# "upstream changed" and starts meaning "upstream moved since you last looked".
set -euo pipefail

# Matches the vortex pin in tools/conformance-gen/Cargo.toml. Moving one means moving both.
PINNED_REF="0.86.1"

ref="${1:-$PINNED_REF}"
base="https://raw.githubusercontent.com/vortex-data/vortex/${ref}"
cd "$(dirname "$0")"
mkdir -p flatbuffers proto editions

# Upstream relocated the schemas after 0.86.1: they used to live in vortex-flatbuffers/ and
# vortex-proto/, and on develop they sit under vortex-array/ and vortex-layout/ and vortex-file/.
# The file CONTENTS are identical across the move (verified 2026-09-12), so we try the pinned
# layout first and fall back to the newer one. Add a third layout here if upstream moves again.
try_fetch() {
  local url="$1" dest="$2" code
  code=$(curl -sSL --max-time 30 -w '%{http_code}' -o "${dest}.tmp" "$url" || true)
  if [ "$code" = "200" ]; then mv "${dest}.tmp" "$dest"; return 0; fi
  rm -f "${dest}.tmp"; return 1
}

fetch_either() {
  local pinned_path="$1" develop_path="$2" dest="$3"
  if try_fetch "${base}/${pinned_path}" "$dest"; then echo "  ${dest}"; return; fi
  if try_fetch "${base}/${develop_path}" "$dest"; then echo "  ${dest}  (post-0.86.1 path)"; return; fi
  echo "FAILED: neither ${pinned_path} nor ${develop_path} at ref ${ref}" >&2
  exit 1
}

echo "Vendoring Vortex schemas from ${ref}:"
fetch_either vortex-flatbuffers/flatbuffers/vortex-dtype/dtype.fbs \
             vortex-array/flatbuffers/vortex-dtype/dtype.fbs        flatbuffers/dtype.fbs
fetch_either vortex-flatbuffers/flatbuffers/vortex-array/array.fbs \
             vortex-array/flatbuffers/vortex-array/array.fbs        flatbuffers/array.fbs
fetch_either vortex-flatbuffers/flatbuffers/vortex-layout/layout.fbs \
             vortex-layout/flatbuffers/vortex-layout/layout.fbs     flatbuffers/layout.fbs
fetch_either vortex-flatbuffers/flatbuffers/vortex-file/footer.fbs \
             vortex-file/flatbuffers/vortex-file/footer.fbs         flatbuffers/footer.fbs
fetch_either vortex-proto/proto/dtype.proto  vortex-array/proto/dtype.proto   proto/dtype.proto
fetch_either vortex-proto/proto/scalar.proto vortex-array/proto/scalar.proto  proto/scalar.proto
fetch_either vortex-proto/proto/expr.proto   vortex-array/proto/expr.proto    proto/expr.proto

echo "Editions (frozen, so a diff here means a NEW edition, never an edited one):"
for e in family core2025.05.0 core2025.06.0 core2025.10.0 \
         core2026.08.0 core2026.08.1 core2026.08.2 core2026.08.3; do
  fetch_either "vortex/editions/core/${e}.toml" "vortex/editions/core/${e}.toml" "editions/${e}.toml"
done

echo
echo "Done. 'git diff -- spec/' now shows drift against ${ref}, if any."
