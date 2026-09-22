"""Folds an `xctrace export` of a cpu-profile table into cycles per function and per source line.

Each row of the table is one sample: a weight in cycles and a backtrace, innermost frame first,
in which Instruments has already expanded inlining (an inlined frame carries `inlined="true"`)
and resolved every frame of a binary with a dSYM to its source line. So the innermost frame of a
sample is the line that was executing, even inside a method the compiler folded into its caller.

Four views come out of it:

* self, per function and per line: where the cycles were spent;
* inclusive, per function: what a frame and everything under it cost;
* ours: every sample charged to the innermost frame whose source is in this repository, so that
  a memset or a GC pause lands on the line of our code that asked for it;
* callers: for the hottest functions outside this repository, which line of ours they came from.

Usage: python3 cycles.py <export.xml> <repository root> <output directory> [top]
"""

import collections
import os
import sys
import xml.etree.ElementTree as ET


# The assembly prefixes Native AOT puts in front of every symbol, and again inside generic arguments.
PREFIXES = (
    'Vorticity_Benchmarks_Scenarios_Vorticity_Bench_Scenarios_',
    'Vorticity_Benchmarks_Runner_Vorticity_Bench_Runner_',
    'Vorticity_Vorticity_',
    'System_IO_Compression_System_IO_Compression_',
    'S_P_CoreLib_',
)


def readable(name):
    for prefix in PREFIXES:
        name = name.replace(prefix, '')
    return name


class Frame:
    __slots__ = ('name', 'binary', 'inlined', 'path', 'line', 'mine')

    def __init__(self, name, binary, inlined, path, line, mine):
        self.name = name
        self.binary = binary
        self.inlined = inlined
        self.path = path
        self.line = line
        self.mine = mine


def shorten(path, root):
    """A source path without the machine it was built on."""
    if path is None:
        return None
    if root and path.startswith(root):
        return path[len(root):].lstrip('/')
    for marker, label in (
            ('/src/libraries/System.Private.CoreLib/src/', 'CoreLib/'),
            ('/src/libraries/', 'libraries/'),
            ('/src/coreclr/nativeaot/', 'nativeaot/'),
            ('/src/coreclr/', 'coreclr/'),
            ('/src/native/', 'native/')):
        at = path.find(marker)
        if at >= 0:
            return label + path[at + len(marker):]
    return os.path.basename(path)


def load(export, root):
    """The samples of the export, as (weight, core type, frames) with frames innermost first."""
    cache = {}
    samples = []

    def value(element, parse):
        ref = element.get('ref')
        if ref is not None:
            return cache[ref]
        parsed = parse(element)
        ident = element.get('id')
        if ident is not None:
            cache[ident] = parsed
        return parsed

    def parse_binary(element):
        return element.get('name')

    def parse_path(element):
        return shorten(element.text, root), element.text.startswith(root)

    def parse_frame(element):
        binary = None
        path = None
        line = None
        mine = False
        for child in element:
            if child.tag == 'binary':
                binary = value(child, parse_binary)
            elif child.tag == 'source':
                line = int(child.get('line', '0'))
                for part in child:
                    if part.tag == 'path':
                        path, mine = value(part, parse_path)
        return Frame(
            readable(element.get('name', '?')), binary, element.get('inlined') == 'true', path, line, mine)

    def parse_backtrace(element):
        return tuple(value(child, parse_frame) for child in element if child.tag == 'frame')

    def parse_text(element):
        return element.text

    def parse_core(element):
        text = element.get('fmt', '')
        return 'E' if '(E Core)' in text else 'P' if '(P Core)' in text else '?'

    for _, element in ET.iterparse(export, events=('end',)):
        if element.tag != 'row':
            continue
        weight = 0
        core = '?'
        frames = ()
        for child in element:
            if child.tag == 'cycle-weight':
                weight = int(value(child, parse_text))
            elif child.tag == 'core':
                core = value(child, parse_core)
            elif child.tag in ('tagged-backtrace', 'backtrace'):
                frames = value(child, parse_backtrace)
        samples.append((weight, core, frames))
        element.clear()
    return samples


def category(frame):
    """What kind of code a frame is: ours, the runtime's, a native library's, the system's."""
    name = frame.name
    path = frame.path or ''
    if frame.binary is None:
        return 'unknown'
    if frame.mine:
        return 'this repository'
    if name.startswith(('WKS::', 'SVR::')) or 'gc_heap' in name:
        return 'garbage collector'
    if path.startswith('native/external/'):
        return 'native: ' + path.split('/')[2]
    if path.startswith(('CoreLib/', 'libraries/')):
        return 'base class library'
    if path.startswith(('nativeaot/', 'coreclr/', 'native/')) or name.startswith('Rh'):
        return 'runtime'
    return frame.binary


def where(frame):
    if frame.path is None:
        return frame.name
    return '%s:%d' % (frame.path, frame.line or 0)


def pct(part, whole):
    return 100.0 * part / whole if whole else 0.0


def table(out, title, rows, total, top, heads):
    out.append('')
    out.append('## ' + title)
    out.append('')
    out.append('| % | Mcycles | ' + ' | '.join(heads) + ' |')
    out.append('|---:|---:|' + '---|' * len(heads))
    for key, cycles in rows.most_common(top):
        cells = key if isinstance(key, tuple) else (key,)
        out.append('| %.2f | %.1f | %s |' % (
            pct(cycles, total), cycles / 1e6, ' | '.join('`%s`' % c for c in cells)))


def main(argv):
    if len(argv) < 4:
        sys.stderr.write(__doc__)
        return 2
    export, root, directory = argv[1], argv[2].rstrip('/') + '/', argv[3]
    top = int(argv[4]) if len(argv) > 4 else 60
    samples = load(export, root)
    total = sum(weight for weight, _, _ in samples)

    self_function = collections.Counter()
    self_line = collections.Counter()
    inclusive = collections.Counter()
    charged = collections.Counter()
    categories = collections.Counter()
    cores = collections.Counter()
    callers = collections.defaultdict(collections.Counter)
    in_library = 0

    for weight, core, frames in samples:
        cores[core] += weight
        if not frames:
            categories['no backtrace'] += weight
            continue
        leaf = frames[0]
        self_function[leaf.name] += weight
        self_line[(where(leaf), leaf.name)] += weight
        categories[category(leaf)] += weight
        seen = set()
        for frame in frames:
            if frame.name not in seen:
                seen.add(frame.name)
                inclusive[frame.name] += weight
        mine = next((frame for frame in frames if frame.mine), None)
        if mine is not None:
            in_library += weight
            charged[(where(mine), mine.name)] += weight
            if not leaf.mine:
                callers[leaf.name][where(mine)] += weight
        else:
            charged[('(no frame of ours)', category(leaf))] += weight

    out = []
    out.append('# Cycles: %s' % os.path.basename(export))
    out.append('')
    out.append('%d samples, %.1f Mcycles; %.1f%% of them with a frame of this repository on the stack.' % (
        len(samples), total / 1e6, pct(in_library, total)))
    out.append('Cores: ' + ', '.join('%s %.1f%%' % (k, pct(v, total)) for k, v in cores.most_common()))

    out.append('')
    out.append('## Self, by kind of code')
    out.append('')
    out.append('| % | Mcycles | kind |')
    out.append('|---:|---:|---|')
    for key, cycles in categories.most_common():
        out.append('| %.2f | %.1f | %s |' % (pct(cycles, total), cycles / 1e6, key))

    table(out, 'Self, by function (innermost, inlinees included)', self_function, total, top, ['function'])
    table(out, 'Self, by source line', self_line, total, top, ['line', 'function'])
    table(out, 'Charged to the innermost line of this repository', charged, total, top, ['line', 'function'])
    table(out, 'Inclusive, by function', inclusive, total, top, ['function'])

    out.append('')
    out.append('## Outside this repository, and the line of ours that called it')
    for name, cycles in self_function.most_common(top):
        if name not in callers:
            continue
        out.append('')
        out.append('`%s`: %.2f%%' % (name, pct(cycles, total)))
        out.append('')
        for line, weight in callers[name].most_common(8):
            out.append('* %.2f%% `%s`' % (pct(weight, total), line))

    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, 'cycles.md'), 'w') as handle:
        handle.write('\n'.join(out) + '\n')
    with open(os.path.join(directory, 'cycles-lines.tsv'), 'w') as handle:
        handle.write('cycles\tline\tfunction\n')
        for (line, name), cycles in self_line.most_common():
            handle.write('%d\t%s\t%s\n' % (cycles, line, name))
    with open(os.path.join(directory, 'cycles-ours.tsv'), 'w') as handle:
        handle.write('cycles\tline\tfunction\n')
        for (line, name), cycles in charged.most_common():
            handle.write('%d\t%s\t%s\n' % (cycles, line, name))
    print('\n'.join(out[:4]))
    print('written: cycles.md, cycles-lines.tsv, cycles-ours.tsv in the output directory')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
