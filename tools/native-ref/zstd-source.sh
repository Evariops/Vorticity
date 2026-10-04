# Sourced by build.sh and testdata.sh: the zstd 1.5.7 release, downloaded once into
# tools/native-ref/out/ and checked against its SHA-256, or ZSTD_SRC=<dir> for an extracted tree.
# Sets `out` and `src`.

out="$here/out"
mkdir -p "$out"

zstd_version=1.5.7
zstd_sha256=eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3

# The SHA-256 of a file: sha256sum where it exists (Linux), shasum elsewhere (macOS).
sha256_of() {
  if command -v sha256sum > /dev/null; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

if [[ -n "${ZSTD_SRC:-}" ]]; then
  src="$ZSTD_SRC"
else
  src="$out/zstd-$zstd_version"
  if [[ ! -d "$src" ]]; then
    tarball="$out/zstd-$zstd_version.tar.gz"
    if [[ ! -f "$tarball" ]]; then
      curl -fsSL -o "$tarball" "https://github.com/facebook/zstd/releases/download/v$zstd_version/zstd-$zstd_version.tar.gz"
    fi
    if [[ "$(sha256_of "$tarball")" != "$zstd_sha256" ]]; then
      echo "$tarball does not have the SHA-256 of zstd $zstd_version" >&2
      exit 1
    fi
    tar -xzf "$tarball" -C "$out"
  fi
fi
