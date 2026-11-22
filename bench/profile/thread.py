#!/usr/bin/env python3
"""One thread of a macOS `sample` file, the one whose stacks most often hold PATTERN: what it spends its samples on, by
self function and by the frames under the frame that matches PATTERN, and how much of the wall it is busy.

usage: thread.py SAMPLE[.gz] MAP PATTERN [DEPTH]

DEPTH (2 by default) is how many frames under the matching one name a line of the breakdown. Waits in the kernel are
counted apart: a thread that waits for another shows them as such.
"""
import bisect
import gzip
import re
import sys
from collections import Counter

path, mpath, pattern = sys.argv[1:4]
depth = int(sys.argv[4]) if len(sys.argv) > 4 else 2
rx = re.compile(pattern)
starts, ends, names = [], [], []
for line in open(mpath, errors="replace"):
    parts = line.rstrip("\n").split(" ", 2)
    if len(parts) < 3:
        continue
    try:
        s = int(parts[0], 16)
        n = int(parts[1], 16)
    except ValueError:
        continue
    starts.append(s)
    ends.append(s + n)
    names.append(parts[2])
order = sorted(range(len(starts)), key=lambda i: starts[i])
starts = [starts[i] for i in order]
ends = [ends[i] for i in order]
names = [names[i] for i in order]


def resolve(text):
    m = re.match(r"\?\?\?\s+\(in <unknown binary>\)\s+\[0x([0-9a-f]+)\]", text)
    if m:
        a = int(m.group(1), 16)
        k = bisect.bisect_right(starts, a) - 1
        if k >= 0 and a < ends[k]:
            return re.sub(r"\(.*", "", re.sub(r"\[[A-Za-z0-9.]+\] ", "", names[k]))[-80:]
        return "??? jit"
    m = re.match(r"(.*?)\s+\(in ([^)]+)\)", text)
    return f"{m.group(1)[:70]} [{m.group(2)}]" if m else text[:70]


WAIT = re.compile(r"__psynch_cvwait|__ulock_wait|mach_msg|kevent|__semwait_signal|__workq_kernreturn|__psynch_mutexwait|__select|poll|__wait4|__read_nocancel")
node_re = re.compile(r"^(\s*[+!:| ]*)(\d+) (.*\S)\s*$")
opener = gzip.open if path.endswith(".gz") else open
threads = {}
stack = []
thread = "?"


def pop_to(d):
    while stack and stack[-1][0] >= d:
        dd, c, n, cs = stack.pop()
        own = c - cs
        if own > 0:
            frames = [x[2] for x in stack] + [n]
            threads.setdefault(thread, []).append((frames, own))


in_graph = False
wall = 0
for line in opener(path, "rt", errors="replace"):
    if line.startswith("Call graph:"):
        in_graph = True
        continue
    if not in_graph:
        continue
    if line.startswith("Total number in stack") or line.startswith("Sort by top of stack"):
        break
    m = node_re.match(line)
    if not m:
        continue
    d = len(m.group(1))
    c = int(m.group(2))
    t = m.group(3)
    if re.match(r"Thread_\d+", t):
        pop_to(0)
        thread = t.split()[0]
        wall = max(wall, c)
        stack.append((d, c, "THREAD", 0))
        continue
    pop_to(d)
    if stack:
        a, b, n, cs = stack[-1]
        stack[-1] = (a, b, n, cs + c)
    stack.append((d, c, resolve(t), 0))
pop_to(0)

if "--all" in sys.argv:
    # Every thread's samples whose stack holds the pattern: an async chain hops from one pool thread to another.
    best = "every thread, stacks under " + pattern
    samples = [(f, s) for t in threads for f, s in threads[t] if any(rx.search(x) for x in f)]
else:
    best = max(threads, key=lambda t: sum(s for f, s in threads[t] if any(rx.search(x) for x in f)))
    samples = threads[best]
busy = sum(s for f, s in samples if not WAIT.search(f[-1]))
waits = sum(s for f, s in samples if WAIT.search(f[-1]))
print(f"{best}: busy {busy} of a wall of {wall} ({100 * busy / max(wall, 1):.0f} %), waiting {waits}")
selfc = Counter()
under = Counter()
for frames, s in samples:
    if WAIT.search(frames[-1]):
        continue
    selfc[frames[-1]] += s
    for i, f in enumerate(frames):
        if rx.search(f):
            under[" > ".join(frames[i + 1:i + 1 + depth]) or "(self)"] += s
            break
print("\nself:")
for k, v in selfc.most_common(25):
    print(f"  {v:6d} {100 * v / max(busy, 1):5.1f} %  {k}")
print(f"\nunder {pattern}, {depth} frames:")
for k, v in under.most_common(30):
    print(f"  {v:6d} {100 * v / max(busy, 1):5.1f} %  {k[:220]}")
