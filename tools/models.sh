#!/usr/bin/env bash
# Model-checks the TLA+ models of docs/design/models with the pinned TLA+ tools.
#
# Every model lives in a directory of its own, docs/design/models/<model>/, with its modules, its
# configurations (.cfg) and a README that records its claims and the evidence TLC gave. Each
# configuration runs on its own, against the module its directory holds, with the other model
# directories on TLC's library path so that a model can INSTANCE another's module.
#
# docs/design/models/expected-exit-codes.txt records the exit code each configuration must give:
# 0 when nothing is violated, 12 for an invariant violated, 13 for a temporal property violated.
# The configurations named Broken* and Reachability* exist to be violated: a mutation that must be
# caught, and a race the model must be able to reach. A listed configuration that gives another code,
# or runs out of time, fails the check; an unlisted one passes on any verdict TLC reaches (0, 10 to
# 14) and fails on anything else, a parse error above all.
#
# Usage: tools/models.sh [<model directory>...]     every model when none is named
# Environment:
#   JAVA             the java to run, 11 or later (default: java on the PATH)
#   TLA2TOOLS        a tla2tools.jar to use as it is (default: the pinned one, downloaded and checked)
#   MODELS_TIMEOUT   seconds a configuration may run (default: 600)
set -euo pipefail

TLA_VERSION=1.7.4
TLA_URL="https://github.com/tlaplus/tlaplus/releases/download/v${TLA_VERSION}/tla2tools.jar"
TLA_SHA256=936a262061c914694dfd669a543be24573c45d5aa0ff20a8b96b23d01e050e88

ROOT=$(cd "$(dirname "$0")/.." && pwd -P)
MODELS="$ROOT/docs/design/models"
MANIFEST="$MODELS/expected-exit-codes.txt"
JAVA=${JAVA:-java}
TIMEOUT=${MODELS_TIMEOUT:-600}

sha256() {
  if command -v sha256sum > /dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi
}

# The pinned jar, downloaded once into the user's cache and checked on every run, cached or not.
tools_jar() {
  if [ -n "${TLA2TOOLS:-}" ]; then
    echo "$TLA2TOOLS"
    return
  fi
  local cache="${XDG_CACHE_HOME:-$HOME/.cache}/vorticity"
  local jar="$cache/tla2tools-$TLA_VERSION.jar"
  if [ ! -f "$jar" ]; then
    mkdir -p "$cache"
    curl -fsSL --retry 3 -o "$jar.part" "$TLA_URL"
    mv "$jar.part" "$jar"
  fi
  local actual
  actual=$(sha256 "$jar")
  if [ "$actual" != "$TLA_SHA256" ]; then
    echo "tla2tools.jar at $jar has SHA-256 $actual, not the pinned $TLA_SHA256" >&2
    exit 1
  fi
  echo "$jar"
}

# The exit code the manifest records for a configuration, or nothing when it is unlisted.
expected_code() {
  local relative="$1"
  [ -f "$MANIFEST" ] || return 0
  grep -E "^${relative//./\\.}=[0-9]+$" "$MANIFEST" | head -1 | cut -d= -f2
}

# The module a configuration runs against: the only .tla of its directory, or the one named like it.
module_of() {
  local dir="$1" cfg="$2"
  local name
  name=$(basename "$cfg" .cfg)
  local modules=("$dir"/*.tla)
  if [ "${#modules[@]}" -eq 1 ]; then
    basename "${modules[0]}" .tla
  elif [ -f "$dir/$name.tla" ]; then
    echo "$name"
  else
    echo "no module for $cfg: $dir holds several and none is named $name.tla" >&2
    return 1
  fi
}

# Runs a command into a file, killed past $TIMEOUT seconds: prints its exit code, or "timeout".
run_bounded() {
  local out="$1"
  shift
  "$@" > "$out" 2>&1 &
  local pid=$! waited=0
  while kill -0 "$pid" 2> /dev/null; do
    if [ "$waited" -ge "$((TIMEOUT * 10))" ]; then
      kill "$pid" 2> /dev/null || true
      wait "$pid" 2> /dev/null || true
      echo timeout
      return
    fi
    sleep 0.1
    waited=$((waited + 1))
  done
  local code=0
  wait "$pid" || code=$?
  echo "$code"
}

main() {
  local jar
  jar=$(tools_jar)
  "$JAVA" -version > /dev/null 2>&1 || { echo "no java at '$JAVA': set JAVA to a Java 11 or later" >&2; exit 1; }

  local dirs=()
  if [ "$#" -gt 0 ]; then
    for dir in "$@"; do dirs+=("$(cd "$dir" && pwd -P)"); done
  else
    for dir in "$MODELS"/*/; do
      compgen -G "$dir*.tla" > /dev/null && dirs+=("$(cd "$dir" && pwd -P)")
    done
  fi

  # Every model directory is on the library path, so that one model can INSTANCE another's module.
  local library=""
  for dir in "$MODELS"/*/; do library="${library:+$library:}${dir%/}"; done

  local scratch failures=0
  scratch=$(mktemp -d)
  # shellcheck disable=SC2064 # the directory is known now, and the variable is gone at exit
  trap "rm -rf '$scratch'" EXIT

  for dir in "${dirs[@]}"; do
    for cfg in "$dir"/*.cfg; do
      [ -f "$cfg" ] || continue
      local relative="${cfg#"$ROOT"/}"
      local module expected code verdict started elapsed out
      module=$(module_of "$dir" "$cfg") || { failures=$((failures + 1)); continue; }
      expected=$(expected_code "$relative")
      out="$scratch/$(basename "$dir")-$(basename "$cfg" .cfg).out"
      started=$(date +%s)
      code=$(cd "$dir" && run_bounded "$out" "$JAVA" -XX:+UseParallelGC "-DTLA-Library=$library" -cp "$jar" tlc2.TLC \
        -workers 1 -seed 1 -fp 1 -cleanup -metadir "$scratch/states-$(basename "$dir")-$(basename "$cfg" .cfg)" \
        -config "$(basename "$cfg")" "$module.tla")
      elapsed=$(($(date +%s) - started))
      local states depth
      states=$({ grep -E 'distinct states found' "$out" || true; } | tail -1 | sed -E 's/.* ([0-9]+) distinct states found.*/\1 distinct states/')
      depth=$({ grep -E 'depth of the complete state graph search is' "$out" || true; } | tail -1 | sed -E 's/.* is ([0-9]+)\./, depth \1/')
      states="$states$depth"
      if [ -n "$expected" ]; then
        if [ "$code" = "$expected" ]; then verdict=PASS; else verdict=FAIL; fi
      else
        case "$code" in
          0 | 10 | 11 | 12 | 13 | 14) verdict=PASS ;;
          timeout) verdict=WARN ;;
          *) verdict=FAIL ;;
        esac
      fi
      echo "$verdict $relative: exit $code${expected:+ (expected $expected)}, ${states:-no states}, ${elapsed}s"
      if [ "$verdict" = FAIL ]; then
        failures=$((failures + 1))
        { grep -E 'Error|violated|Exception|Parse' "$out" || true; } | head -10 | sed 's/^/    /'
      fi
    done
  done

  if [ "$failures" -gt 0 ]; then
    echo "$failures configuration(s) did not give their expected verdict" >&2
    exit 1
  fi
}

main "$@"
