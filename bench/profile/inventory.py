"""Folds allocation traces into one inventory of allocation sites, with the source of each.

Every trace directory is one `bench/profile.sh allocations` run (its `allocations.tsv`). A site is
the innermost line of the library that led to the allocation (a line under src/, or under bench/
when the library is not on the stack), the line that allocated, and the type allocated. For each
site the inventory gives, per trace, the objects of the cold round and of a warm round, and the
text of the library line with the member it is in.

When the same scenario was traced at two sizes, the directory names ending in the row count
(`scan-1048576`, `scan-10485760`), a site is said to grow when its warm count grows with the rows:
one that grows is paid per batch, block or chunk; one that does not is paid per scan, file or
writer; one with no warm count is paid once per process.

Usage: python3 inventory.py <repository root> <output.md> <trace directory>...
"""

import collections
import os
import re
import sys

MEMBER = re.compile(
    r'^\s*(?:\[.*\]\s*)*(?:(?:public|private|internal|protected|static|readonly|override|virtual|'
    r'sealed|async|unsafe|partial|new|extern|ref)\s+)+[^;=]*?\b([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^>]*>)?\s*\(')


def frames(stack):
    for part in stack.split(' | '):
        part = part.strip()
        if part:
            line, _, function = part.partition(' ')
            yield line, function


def library_line(stack):
    harness = None
    for line, _ in frames(stack):
        if line.startswith('src/'):
            return line
        if harness is None and line.startswith('bench/'):
            harness = line
    return harness or '(runtime)'


class Sources:
    def __init__(self, root):
        self.root = root
        self.files = {}

    def describe(self, where):
        path, _, number = where.rpartition(':')
        if not number.isdigit() or not path:
            return '', ''
        if path not in self.files:
            try:
                with open(os.path.join(self.root, path)) as handle:
                    self.files[path] = handle.read().split('\n')
            except OSError:
                self.files[path] = []
        lines = self.files[path]
        n = int(number)
        if not 0 < n <= len(lines):
            return '', ''
        member = ''
        for i in range(n - 1, -1, -1):
            match = MEMBER.match(lines[i])
            if match:
                member = match.group(1)
                break
        return member, lines[n - 1].strip()


def load(directory):
    rounds = set()
    rows = []
    with open(os.path.join(directory, 'allocations.tsv')) as handle:
        next(handle)
        for line in handle:
            parts = line.rstrip('\n').split('\t')
            if len(parts) < 6:
                continue
            index, kind, size, site, _, stack = parts[:6]
            rows.append((int(index), kind, int(size), site, stack))
            rounds.add(int(index))
    return rows, max(len([r for r in rounds if r > 0]), 1)


def main(argv):
    if len(argv) < 4:
        sys.stderr.write(__doc__)
        return 2
    root, output, directories = argv[1], argv[2], argv[3:]
    sources = Sources(root)
    # site -> label -> [cold objects, cold bytes, warm objects, warm bytes] (warm: per round)
    sites = collections.defaultdict(lambda: collections.defaultdict(lambda: [0.0, 0.0, 0.0, 0.0]))
    totals = {}
    for directory in directories:
        if not os.path.exists(os.path.join(directory, 'allocations.tsv')):
            continue
        label = os.path.basename(directory.rstrip('/'))
        rows, warm = load(directory)
        total = [0, 0, 0, 0, 0, 0, 0, 0]
        for index, kind, size, site, stack in rows:
            cell = sites[(library_line(stack), site, kind)][label]
            native = kind.startswith('native ')
            if index == 0:
                cell[0] += 1
                cell[1] += size
                total[4 if native else 0] += 1
                total[5 if native else 1] += size
            else:
                cell[2] += 1.0 / warm
                cell[3] += size / warm
                total[6 if native else 2] += 1.0 / warm
                total[7 if native else 3] += size / warm
        totals[label] = total

    # Pairs of one scenario at two sizes, for the growth of each site.
    by_scenario = collections.defaultdict(list)
    for label in totals:
        match = re.match(r'^(.*)-(\d+)$', label)
        if match:
            by_scenario[match.group(1)].append((int(match.group(2)), label))
    pairs = [(sorted(v)[0][1], sorted(v)[-1][1]) for v in by_scenario.values() if len(v) >= 2]

    def growth(per):
        verdicts = []
        for small, large in pairs:
            a = per[small][2] if small in per else 0.0
            b = per[large][2] if large in per else 0.0
            if a > 0 and b >= 4 * a:
                verdicts.append('grows')
            elif a > 0 or b > 0:
                verdicts.append('fixed')
        if 'grows' in verdicts:
            return 'grows'
        if verdicts:
            return 'fixed'
        return 'warm' if any(c[2] > 0 for c in per.values()) else 'once'

    out = ['# Allocation inventory', '']
    out.append('%d traces, %d sites. Warm figures are per round.' % (len(totals), len(sites)))
    out.append('')
    out.append('| trace | cold objects | cold bytes | warm objects | warm bytes | cold native calls | cold native bytes | warm native calls | warm native bytes |')
    out.append('|---|---:|---:|---:|---:|---:|---:|---:|---:|')
    for label in sorted(totals):
        out.append('| %s | %s |' % (label, ' | '.join('%.0f' % v for v in totals[label])))

    ranked = sorted(
        sites.items(),
        key=lambda item: (-sum(c[2] for c in item[1].values()), -sum(c[0] for c in item[1].values())))
    out.append('')
    out.append('## Sites')
    out.append('')
    out.append('`grows`: its warm count grows with the rows. `fixed`: paid per scan, file or writer. '
               '`once`: only in a cold round. Traces: warm objects per round, then cold objects.')
    out.append('')
    out.append('| warm objects | warm bytes | cold objects | growth | library line | member | source | allocating line | type | traces |')
    out.append('|---:|---:|---:|---|---|---|---|---|---|---|')
    for (library, site, kind), per in ranked:
        member, text = sources.describe(library)
        top = sorted(per.items(), key=lambda kv: -(kv[1][2] + kv[1][0]))[:4]
        detail = ', '.join('%s %s/%d' % (
            label, ('%.1f' % c[2]).rstrip('0').rstrip('.'), c[0]) for label, c in top)
        out.append('| %.1f | %.0f | %d | %s | `%s` | `%s` | `%s` | `%s` | `%s` | %s |' % (
            sum(c[2] for c in per.values()), sum(c[3] for c in per.values()),
            sum(c[0] for c in per.values()), growth(per), library, member,
            text.replace('|', '\\|')[:120], site, kind, detail))

    with open(output, 'w') as handle:
        handle.write('\n'.join(out) + '\n')
    print('%d sites over %d traces: %s' % (len(sites), len(totals), output))
    return 0


if __name__ == '__main__':
    sys.exit(main(sys.argv))
