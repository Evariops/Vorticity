#!/usr/bin/env python3
"""Resolves the samples of Vorticity.Zstd.Perf --pcprofile against the binary's dSYM and the native libraries.

  bench/zstd-pcprofile.py <binary> <samples> [symbol substring] [--top N]

Prints the functions by samples; with a symbol substring, the disassembly of the function that
matches with the most samples, each instruction with its samples and its share of the function's.
The samples file starts with a header: the executable's slide, then each native library with a
symbol and where it was loaded (as many headers as profiled runs; the last ones win).
"""
import bisect
import collections
import os
import subprocess
import sys


def symbols_of(path):
    out = subprocess.run(['nm', '-n', path], capture_output=True, text=True).stdout
    table = []
    for line in out.splitlines():
        parts = line.split()
        if len(parts) == 3 and parts[1] in 'sStT':
            table.append((int(parts[0], 16), parts[2]))
    table.sort()
    return table


class Image:
    def __init__(self, path, symbols, base, disassemble_path):
        self.path = path
        self.symbols = symbols
        self.starts = [address for address, _ in symbols]
        self.base = base
        self.disassemble_path = disassemble_path
        self.end = (symbols[-1][0] + 0x10000) if symbols else 0

    def resolve(self, relative):
        i = bisect.bisect_right(self.starts, relative) - 1
        return (i, self.symbols[i][1]) if i >= 0 else (-1, '?')


def main():
    args = [a for a in sys.argv[1:] if not a.startswith('--')]
    top = 25
    if '--top' in sys.argv:
        top = int(sys.argv[sys.argv.index('--top') + 1])
        args = [a for a in args if a != str(top)]
    binary, samples_path = args[0], args[1]
    wanted = args[2] if len(args) > 2 else None

    dwarf = os.path.join(binary + '.dSYM', 'Contents', 'Resources', 'DWARF', os.path.basename(binary))
    slide = 0
    libraries = {}
    raw = collections.Counter()
    with open(samples_path) as f:
        for line in f:
            if line.startswith('# slide '):
                slide = int(line.split()[2], 16)
            elif line.startswith('# library '):
                _, _, path, symbol, address = line.split()
                libraries[path] = (symbol, int(address, 16))
            else:
                pc, count = line.split()
                raw[int(pc, 16)] += int(count)

    images = [Image(binary, symbols_of(dwarf), slide, binary)]
    for path, (symbol, address) in libraries.items():
        table = symbols_of(path)
        own = next((a for a, n in table if n == symbol), None)
        if own is not None:
            images.insert(0, Image(path, table, address - own, path))

    # Each sample to (image, relative address).
    samples = collections.Counter()
    for pc, count in raw.items():
        for image in images:
            relative = pc - image.base
            if image is images[-1] or 0 <= relative < image.end:
                samples[(image.path, relative)] += count
                break

    total = sum(samples.values())
    by_symbol = collections.Counter()
    for (path, relative), count in samples.items():
        image = next(i for i in images if i.path == path)
        by_symbol[(path, image.resolve(relative)[1])] += count

    print(f'{total} samples')
    for (path, name), count in by_symbol.most_common(top):
        where = '' if path == binary else f'  ({os.path.basename(path)})'
        print(f'{count:8d} {100.0 * count / total:5.1f}%  {name}{where}')

    if wanted is None:
        return
    best = None
    for image in images:
        for i, (_, name) in enumerate(image.symbols):
            if wanted in name:
                weight = by_symbol[(image.path, name)]
                if best is None or weight > best[0]:
                    best = (weight, image, i)
    if best is None:
        print(f'no symbol contains {wanted}')
        return
    own, image, i = best
    start, name = image.symbols[i]
    end = image.symbols[i + 1][0] if i + 1 < len(image.symbols) else start + 0x10000
    print(f'\n{name}: {own} samples')
    listing = subprocess.run(
        ['objdump', '-d', '--no-show-raw-insn', f'--start-address={start:#x}', f'--stop-address={end:#x}', image.disassemble_path],
        capture_output=True, text=True).stdout.splitlines()
    for line in listing:
        head = line.strip().split(':', 1)
        try:
            address = int(head[0], 16)
        except ValueError:
            continue
        count = samples.get((image.path, address), 0)
        share = f'{100.0 * count / own:5.1f}%' if own else ''
        mark = f'{count:6d} {share}' if count else ' ' * 13
        print(f'{mark}  {address:x}: {head[1].strip()}')


if __name__ == '__main__':
    main()
