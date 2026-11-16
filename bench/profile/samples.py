#!/usr/bin/env python3
"""One pass over a macOS `sample` file and the JIT's perf map (DOTNET_PerfMapEnabled=1): the cores busy on average, each
role's inclusive share of the busy samples, the top functions by self time, the busiest threads, and who calls the waits
that burn a core (the runtime's spin of an idle pool worker, a lock's spin).

usage: samples.py SAMPLE MAP SUMMARY_OUT  ->  prints 'cores<TAB>role=pct ...' on one line, for bench/speed.sh's ledger.

A sample counts toward a role when any frame of its stack matches the role, so roles may overlap: a compression that runs
on a fan's item counts toward both. A sample whose innermost frame is a kernel wait is not busy and counts toward nothing.
"""
import bisect
import re
import sys
from collections import Counter

path, mpath, out = sys.argv[1:4]
ROLES = [
    ("compress", r"VortexFileWriter::Compress\b|VortexFileWriter::Compress$|ZstdCompressor|DoubleFastMatchFinder"),
    ("shred_take", r"Shredding::Take"),
    ("keys", r"IndexWriter::(FeedKeys|ReadKeys|FeedVariant)|VariantFieldExpr::(ReadKeys|WriteKey|ApplyKeys)"),
    ("stats", r"IngestPart|ColumnWriter::Accumulate|BlockStats"),
    ("builders", r"GranuleStatsBuilder|ColumnSheetBuilder|LocateBuilder|ColumnCountsBuilder|BloomBuilder"),
    ("choose", r"ColumnCompressor::(Choose|TryRuns|PlainBinarySize)|RowComparer"),
    ("merge", r"KeyOrderedMerge"),
    ("decode", r"BatchAsyncEnumerator::CompleteBatch|ShreddedVariant::|ZstdDecompressor|Decoders\."),
    ("census", r"PathCensus"),
    ("staging", r"VortexFileWriter::Stage|CanonicalArena::CopyFrom|CompactVarBinView"),
    ("memory", r"QueryMemoryBudget::|BlockPool::|BudgetAccount::|KeepPoolWithin"),
    ("gc", r"gc_heap|WKS::|SVR::|GCHeap"),
    ("jit", r"libclrjit|JIT_CountProfile|JIT_ClassProfile|CallCountingStub"),
    ("pool_spin", r"ThreadNative_SpinWait"),
    ("lock_spin", r"Lock::(Enter|Spin)|EnterAndGetCurrentThreadId|AwareLock"),
]
compiled = [(name, re.compile(rx)) for name, rx in ROLES]
WAIT = re.compile(r"__psynch_cvwait|__ulock_wait|mach_msg|kevent|__semwait_signal|__workq_kernreturn|__psynch_mutexwait|__select|poll|__wait4|read \[libsystem_kernel|__open|pread|__accept|__read_nocancel")
SPIN = re.compile(r"ThreadNative_SpinWait|EnterAndGetCurrentThreadId")

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
            n = re.sub(r"\[[A-Za-z0-9.]+\] ", "", names[k])
            return re.sub(r"\(.*", "", n)[-90:]
        return "??? jit"
    m = re.match(r"(.*?)\s+\(in ([^)]+)\)", text)
    if m:
        return f"{m.group(1)[:80]} [{m.group(2)}]"
    return text[:80]


node_re = re.compile(r"^(\s*[+!:| ]*)(\d+) (.*\S)\s*$")
stack = []
selfc = Counter()
roles = Counter()
threads = Counter()
spinners = Counter()
busy = 0
thread = "?"
wall = 0


def record(frames, s):
    global busy
    if WAIT.search(frames[-1]):
        return
    busy += s
    selfc[frames[-1]] += s
    threads[thread] += s
    text = "\n".join(frames)
    for name, rx in compiled:
        if rx.search(text):
            roles[name] += s
    if SPIN.search(frames[-1]):
        managed = [f for f in frames[:-1] if "[" not in f and f not in ("THREAD", "??? jit")]
        spinners[" <- ".join(reversed(managed[-3:])) or "(no managed caller: an idle pool worker)"] += s


def pop_to(depth):
    while stack and stack[-1][0] >= depth:
        d, c, n, cs = stack.pop()
        if c - cs > 0:
            record([x[2] for x in stack] + [n], c - cs)


in_graph = False
for line in open(path, errors="replace"):
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
    depth = len(m.group(1))
    count = int(m.group(2))
    text = m.group(3)
    if re.match(r"Thread_\d+", text):
        pop_to(0)
        thread = text.split()[0]
        wall = max(wall, count)
        stack.append((depth, count, "THREAD", 0))
        continue
    pop_to(depth)
    if stack:
        d, c, n, cs = stack[-1]
        stack[-1] = (d, c, n, cs + count)
    stack.append((depth, count, resolve(text), 0))
pop_to(0)

cores = busy / max(wall, 1)
with open(out, "w") as f:
    f.write(f"busy {busy} samples over a wall of {wall}: {cores:.2f} cores\n\nroles (inclusive, may overlap):\n")
    for name, _ in ROLES:
        f.write(f"  {name:12} {100 * roles[name] / max(busy, 1):5.1f} %\n")
    f.write("\ntop self functions:\n")
    for k, v in selfc.most_common(50):
        f.write(f"  {v:7d} {100 * v / max(busy, 1):5.1f} %  {k}\n")
    f.write("\nbusiest threads (share of the wall):\n")
    for k, v in threads.most_common(16):
        f.write(f"  {v:7d} {100 * v / max(wall, 1):5.1f} %  {k}\n")
    f.write("\nwho spins (self samples of a spin, by their managed callers):\n")
    for k, v in spinners.most_common(10):
        f.write(f"  {v:7d} {100 * v / max(busy, 1):5.1f} %  {k}\n")
print(f"{cores:.2f}\t" + " ".join(f"{name}={100 * roles[name] / max(busy, 1):.1f}" for name, _ in ROLES))
