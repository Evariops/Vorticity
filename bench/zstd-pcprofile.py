#!/usr/bin/env python3
"""Resolves the samples of Vorticity.Zstd.Perf --pcprofile against the binary's dSYM.

  bench/zstd-pcprofile.py <binary> <samples> [symbol substring] [--top N]

Prints the functions by samples; with a symbol substring, the disassembly of the first function
that matches, each instruction with its samples and its share of the function's.
"""
import bisect
import collections
import os
import subprocess
import sys


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    top = 25
    if '--top' in sys.argv:
        top = int(sys.argv[sys.argv.index('--top') + 1])
        args = [a for a in args if a != str(top)]
    binary, samples_path = args[0], args[1]
    wanted = args[2] if len(args) > 2 else None

    dwarf = os.path.join(binary + '.dSYM', 'Contents', 'Resources', 'DWARF', os.path.basename(binary))
    symbols = []
    for line in subprocess.run(['nm', '-n', dwarf], capture_output=True, text=True).stdout.splitlines():
        parts = line.split()
        if len(parts) == 3 and parts[1] in 'sStT':
            symbols.append((int(parts[0], 16), parts[2]))
    symbols.sort()
    starts = [address for address, _ in symbols]

    samples = collections.Counter()
    with open(samples_path) as f:
        for line in f:
            pc, count = line.split()
            samples[int(pc, 16)] += int(count)
    total = sum(samples.values())

    by_symbol = collections.Counter()
    for pc, count in samples.items():
        i = bisect.bisect_right(starts, pc) - 1
        by_symbol[symbols[i][1] if i >= 0 else '?'] += count

    print(f'{total} samples')
    for name, count in by_symbol.most_common(top):
        print(f'{count:8d} {100.0 * count / total:5.1f}%  {name}')

    if wanted is None:
        return
    matches = [i for i, (_, name) in enumerate(symbols) if wanted in name]
    if not matches:
        print(f'no symbol contains {wanted}')
        return
    i = max(matches, key=lambda k: by_symbol[symbols[k][1]])
    start, name = symbols[i]
    end = symbols[i + 1][0] if i + 1 < len(symbols) else start + 0x10000
    own = by_symbol[name]
    print(f'\n{name}: {own} samples')
    listing = subprocess.run(
        ['objdump', '-d', '--no-show-raw-insn', f'--start-address={start:#x}', f'--stop-address={end:#x}', binary],
        capture_output=True, text=True).stdout.splitlines()
    for line in listing:
        head = line.strip().split(':', 1)
        try:
            address = int(head[0], 16)
        except ValueError:
            continue
        count = samples.get(address, 0)
        share = f'{100.0 * count / own:5.1f}%' if own else ''
        mark = f'{count:6d} {share}' if count else ' ' * 13
        print(f'{mark}  {address:x}: {head[1].strip()}')


if __name__ == '__main__':
    main()
