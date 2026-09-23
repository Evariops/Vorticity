"""Folds an `xctrace export` of a cpu-profile table by thread: who ran, when, and on what.

The view a scan on several lanes needs and `cycles.py` does not give: every sample carries its
thread and its time, so the samples say how many threads were on a core in each millisecond, how
the work split between the caller's thread and the pool's, and what each side spent its cycles on.

Three views come out of it:

* each thread: its samples, its share of the cycles, how much of them ran on efficiency cores, and
  in how many milliseconds of the recording it ran at all;
* concurrency: how many threads ran in each millisecond, as a histogram;
* per group of threads, the main thread and the pool's workers: the hottest functions by self and
  by inclusive cycles.

The profiler samples fewer of a thread's cycles the more threads are running: on fourteen busy
threads it recorded about a third of what getrusage counted. Shares within a recording hold; the
processor time a process spent is the runner's `cpu_ms`, not the sum of these cycles.

Usage: python3 threads.py <export.xml> <output directory> [top]
"""

import collections
import os
import sys
import xml.etree.ElementTree as ET

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from cycles import readable


def load(export):
    """The samples of the export, as (time in ns, thread name, core type, weight, frame names)."""
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

    def parse_backtrace(element):
        return tuple(value(child, lambda frame: readable(frame.get('name', '?')))
                     for child in element if child.tag == 'frame')

    for _, element in ET.iterparse(export, events=('end',)):
        if element.tag != 'row':
            continue
        time = None
        thread = '?'
        core = '?'
        weight = 0
        frames = ()
        for child in element:
            if child.tag == 'sample-time':
                time = int(value(child, lambda e: e.text))
            elif child.tag == 'thread':
                thread = value(child, lambda e: e.get('fmt', '?').rsplit(' (', 1)[0])
            elif child.tag == 'core':
                core = value(child, lambda e: 'E' if '(E Core)' in e.get('fmt', '') else 'P')
            elif child.tag == 'cycle-weight':
                weight = int(value(child, lambda e: e.text))
            elif child.tag in ('tagged-backtrace', 'backtrace'):
                frames = value(child, parse_backtrace)
        if time is not None:
            samples.append((time, thread, core, weight, frames))
        element.clear()
    samples.sort(key=lambda sample: sample[0])
    return samples


def group(thread):
    if thread.startswith('Main Thread'):
        return 'main thread'
    if '.NET TP Worker' in thread:
        return 'pool workers'
    return 'other threads'


def pct(part, whole):
    return 100.0 * part / whole if whole else 0.0


def main(argv):
    if len(argv) < 3:
        sys.stderr.write(__doc__)
        return 2
    export, directory = argv[1], argv[2]
    top = int(argv[3]) if len(argv) > 3 else 30
    samples = load(export)
    if not samples:
        sys.stderr.write('no samples in %s\n' % export)
        return 1

    start = samples[0][0]
    millis = int((samples[-1][0] - start) // 1_000_000) + 1
    total = sum(sample[3] for sample in samples)

    threads = collections.defaultdict(lambda: [0, 0, 0, set()])
    running = collections.defaultdict(set)
    for time, thread, core, weight, _ in samples:
        entry = threads[thread]
        entry[0] += 1
        entry[1] += weight
        if core == 'E':
            entry[2] += weight
        bin_ = int((time - start) // 1_000_000)
        entry[3].add(bin_)
        running[bin_].add(thread)

    out = ['# Threads: %s' % os.path.basename(export), '']
    out.append('%d samples over %d ms, %.1f Gcycles.' % (len(samples), millis, total / 1e9))
    out.append('')
    out.append('## By thread')
    out.append('')
    out.append('| thread | samples | %% cycles | on E cores | ms it ran in, of %d |' % millis)
    out.append('|---|---:|---:|---:|---:|')
    for thread, (count, cycles, efficiency, bins) in sorted(threads.items(), key=lambda item: -item[1][1]):
        out.append('| %s | %d | %.1f | %.0f %% | %d |' % (
            thread, count, pct(cycles, total), pct(efficiency, cycles), len(bins)))

    histogram = collections.Counter(len(names) for names in running.values())
    histogram[0] += millis - len(running)
    mean = sum(count * bins for count, bins in histogram.items()) / millis
    out.append('')
    out.append('## Threads running per millisecond, mean %.2f' % mean)
    out.append('')
    out.append('| threads | ms | % |')
    out.append('|---:|---:|---:|')
    for count in sorted(histogram):
        out.append('| %d | %d | %.1f |' % (count, histogram[count], pct(histogram[count], millis)))

    for name in ('main thread', 'pool workers', 'other threads'):
        chosen = [sample for sample in samples if group(sample[1]) == name]
        cycles = sum(sample[3] for sample in chosen)
        if not cycles:
            continue
        own = collections.Counter()
        inclusive = collections.Counter()
        for _, _, _, weight, frames in chosen:
            if not frames:
                own['(no backtrace)'] += weight
                continue
            own[frames[0]] += weight
            for frame in set(frames):
                inclusive[frame] += weight
        for title, counter in (('self', own), ('inclusive', inclusive)):
            out.append('')
            out.append('## %s, %.1f %% of the cycles: %s' % (name, pct(cycles, total), title))
            out.append('')
            out.append('| % of the group | function |')
            out.append('|---:|---|')
            for function, weight in counter.most_common(top):
                out.append('| %.1f | `%s` |' % (pct(weight, cycles), function))

    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, 'threads.md'), 'w') as handle:
        handle.write('\n'.join(out) + '\n')
    print('\n'.join(out[:3]))
    print('written: threads.md in the output directory')
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
