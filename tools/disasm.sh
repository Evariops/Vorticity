#!/usr/bin/env bash
# Disassembles one method of a Native AOT binary: tools/disasm.sh <binary> <Type__Method>
# The binary is stripped; the symbol comes from its .dSYM, the code from the binary itself.
set -eu
binary="$1"; method="$2"
dwarf="$binary.dSYM/Contents/Resources/DWARF/$(basename "$binary")"
symbols="$(mktemp)"
trap 'rm -f "$symbols"' EXIT
nm -n "$dwarf" | grep -E '^[0-9a-f]+ [sStT] ' > "$symbols"
start="$(grep -m1 -E "_${method}\$" "$symbols" | awk '{print $1}')"
[[ -n "$start" ]] || { echo "no symbol ending in _$method" >&2; exit 1; }
next="$(awk -v s="$start" '$1 > s { print $1; exit }' "$symbols")"
objdump -d --no-show-raw-insn --start-address="0x$start" --stop-address="0x$next" "$binary" | tail -n +7
