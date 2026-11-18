#!/usr/bin/env python3
"""Folds an `xctrace export` of Instruments' CPU Profiler on a JIT process, its frames named by the JIT's perf map.

Instruments samples each core at its high frequency, a sample every 25 to 30 µs of a running thread weighed in cycles,
from the kernel: a thread that waits is never sampled, so every sample is a core at work, unlike EventPipe's sampler,
which stacks every thread every millisecond, running or not, and charges a blocked thread to its last frame. Instruments
cannot name a frame the JIT compiled; DOTNET_PerfMapEnabled=1 makes the runtime write each method's address range to
/tmp/perf-<pid>.map, and a frame's address is looked up there (a return address one byte back, so that a call at the end
of a method is not charged to the next one).

What comes out, in the output directory:

* summary.md: the cores busy on average and how often k cores were, millisecond by millisecond; each role's share of the
  cycles and the cores it kept busy; the top functions by self and inclusive cycles; the busiest threads; who spins;
* timeline.tsv: per millisecond, the threads that ran and the cores each role kept busy, for a plot or a closer look at
  the gaps;
* folded.txt: one line per distinct stack, root first and frames separated by semicolons, with its cycles, which
  speedscope and the flame graph scripts read.

A sample counts toward one role, that of its innermost frame matching a role, so roles add up to the whole: a compression
inside a line's item is a compression.

usage: jitcycles.py <cpu-profile.xml> <perf.map> <output directory> [--from S] [--to S] [--bucket MS] [--top N] [--ledger]

--from and --to keep the samples between two seconds of the recording; --ledger prints one line, the cores busy on
average then each role's share of the cycles, for bench/speed.sh's ledger, in place of the summary's head.
"""

import bisect
import collections
import os
import re
import sys
import xml.etree.ElementTree as ET

ROLES = [
    ("gc", r"gc_heap|WKS::|SVR::|GCHeap|GCToEEInterface"),
    ("jit", r"libclrjit|JIT_CountProfile|JIT_ClassProfile|CallCountingStub|jitNativeCode|CILJit"),
    ("pool_spin", r"ThreadNative_SpinWait|LowLevelSpinWaiter|PortableThreadPool\+WorkerThread::Wait|SpinWait::SpinOnce"),
    ("lock_spin", r"Lock::(Enter|Spin|TryEnter)|EnterAndGetCurrentThreadId|AwareLock|Monitor::"),
    ("compress", r"ZstdCompressor|DoubleFastMatchFinder|Zstd\w*Compress|Huf\w*Compress|Fse\w*Compress|VortexFileWriter::Compress\b"),
    ("decompress", r"ZstdDecompressor|Zstd\w*Decompress|Decoders\."),
    ("shred_take", r"Shredding::Take|VariantShredder"),
    ("census", r"PathCensus"),
    ("keys", r"IndexWriter::(FeedKeys|ReadKeys|FeedVariant)|VariantFieldExpr::(ReadKeys|WriteKey|ApplyKeys)|KeyEncoder|RowEncoder"),
    ("builders", r"GranuleStatsBuilder|ColumnSheetBuilder|LocateBuilder|ColumnCountsBuilder|BloomBuilder|QuantileSketch"),
    ("stats", r"IngestPart|ColumnWriter::(Accumulate|Ingest|Land)|BlockStats"),
    ("choose", r"ColumnCompressor::|RowComparer"),
    ("emission", r"VortexFileWriter::(CompressColumn|WriteFramed|WriteColumn|Graft|LandEmission|EmitChunk|Gather)|VortexFileWriter\+(LeafWork|FrameWork|ColumnEmission)|ChunkColumns"),
    ("staging", r"VortexFileWriter::(Stage|Own|Assemble|Gathered|Windowed)|CanonicalArena::|CompactVarBinView|CanonicalFilter"),
    ("line", r"FlightLine|VortexFileWriter::(RunLineItem|Prologue|Epilogue|LayItems|OwnStates)"),
    ("merge", r"KeyOrderedMerge|SlicedMerge|MergeAsync|RunRegister|BlockMap"),
    ("decode", r"BatchAsyncEnumerator|ShreddedVariant::|DecodeAhead|ScanSource|VortexFileReader"),
    ("memory", r"QueryMemoryBudget::|BlockPool::|BudgetAccount::|KeepPoolWithin|CountedAccount|JobMemory"),
    ("store", r"ObjectStream|ObjectStore|FileStream|RandomAccess|SafeFileHandle"),
    ("pump", r"FlowCore|CoreBatch|CoreUnit|LaneWorkers|HeldLane"),
    ("runtime", r"libcoreclr|libSystem|libsystem_|System\.Threading\.|System\.Runtime\.CompilerServices\."),
]
COMPILED = [(name, re.compile(rx)) for name, rx in ROLES]
LIBRARY = re.compile(r"Vorticity\.")
SPIN = re.compile(ROLES[2][1] + "|" + ROLES[3][1])


class PerfMap:
    def __init__(self, path):
        rows = []
        for line in open(path, errors="replace"):
            parts = line.rstrip("\n").split(" ", 2)
            if len(parts) < 3:
                continue
            try:
                rows.append((int(parts[0], 16), int(parts[1], 16), parts[2]))
            except ValueError:
                continue
        rows.sort()
        self.starts = [r[0] for r in rows]
        self.ends = [r[0] + r[1] for r in rows]
        self.names = [self.tidy(r[2]) for r in rows]

    @staticmethod
    def tidy(name):
        tier = ""
        m = re.search(r"\[(\w+)\]\s*$", name)
        if m and m.group(1) not in ("OptimizedTier1", "Optimized"):
            tier = " [" + m.group(1) + "]"
        name = re.sub(r"\[\w+\]\s*$", "", name)
        name = re.sub(r"^\[\w+\]\s*", "", name)
        name = re.sub(r"\(.*", "", name)
        return name.strip() + tier

    def find(self, address):
        k = bisect.bisect_right(self.starts, address) - 1
        if k >= 0 and address < self.ends[k]:
            return self.names[k]
        return None


def load(export, perfmap):
    """The samples, as (time ns, thread, weight, frames innermost first)."""
    cache = {}
    samples = []

    def value(element, parse):
        ref = element.get("ref")
        if ref is not None:
            return cache[ref]
        parsed = parse(element)
        ident = element.get("id")
        if ident is not None:
            cache[ident] = parsed
        return parsed

    def parse_binary(element):
        return element.get("name")

    def parse_frame(element, leaf):
        binary = None
        for child in element:
            if child.tag == "binary":
                binary = value(child, parse_binary)
        name = element.get("name", "?")
        if binary is None:
            address = int(element.get("addr", name), 16) if (element.get("addr") or name).startswith("0x") else None
            if address is not None:
                found = perfmap.find(address if leaf else address - 1)
                return found if found is not None else "??? jit"
            return name
        return name + " [" + binary + "]"

    frame_cache = {}

    def parse_backtrace(element):
        frames = []
        for index, child in enumerate(element):
            if child.tag != "frame":
                continue
            ref = child.get("ref")
            key = (ref or child.get("id"), index == 0)
            if ref is not None and key in frame_cache:
                frames.append(frame_cache[key])
                continue
            if ref is not None:
                # A frame seen before at another depth: its element is gone, its raw name is in the cache.
                raw = cache.get(ref)
                frames.append(raw)
                continue
            named = parse_frame(child, index == 0)
            cache[child.get("id")] = named
            frame_cache[key] = named
            frames.append(named)
        return tuple(frames)

    def parse_text(element):
        return element.text

    def parse_thread(element):
        return element.get("fmt", "?")

    for _, element in ET.iterparse(export, events=("end",)):
        if element.tag != "row":
            continue
        time = 0
        weight = 0
        thread = "?"
        frames = ()
        for child in element:
            tag = child.tag
            if tag == "sample-time":
                time = int(value(child, parse_text))
            elif tag == "thread":
                thread = value(child, parse_thread)
            elif tag == "cycle-weight":
                weight = int(value(child, parse_text))
            elif tag in ("tagged-backtrace", "backtrace"):
                frames = value(child, parse_backtrace)
        samples.append((time, thread, weight, frames))
        element.clear()
    return samples


def role_of(frames):
    for frame in frames:
        for name, rx in COMPILED:
            if rx.search(frame):
                return name
    return "other"


def main(argv):
    args = argv[1:]
    if len(args) < 3:
        sys.stderr.write(__doc__)
        return 2
    export, mapfile, directory = args[:3]
    options = {"--from": None, "--to": None, "--bucket": "1", "--top": "40", "--ledger": None}
    rest = args[3:]
    while rest:
        if rest[0] == "--ledger":
            options["--ledger"] = "1"
            rest = rest[1:]
            continue
        options[rest[0]] = rest[1]
        rest = rest[2:]
    bucket_ns = int(float(options["--bucket"]) * 1e6)
    top = int(options["--top"])

    samples = load(export, PerfMap(mapfile))
    samples.sort(key=lambda s: s[0])
    if not samples:
        print("no samples")
        return 1
    origin = samples[0][0]
    low = origin + int(float(options["--from"]) * 1e9) if options["--from"] else None
    high = origin + int(float(options["--to"]) * 1e9) if options["--to"] else None
    samples = [s for s in samples if (low is None or s[0] >= low) and (high is None or s[0] < high)]
    start, end = samples[0][0], samples[-1][0]
    wall_ns = max(end - start, 1)

    total = 0
    by_role = collections.Counter()
    self_function = collections.Counter()
    inclusive = collections.Counter()
    ours = collections.Counter()
    threads = collections.Counter()
    spinners = collections.Counter()
    folded = collections.Counter()
    buckets = collections.defaultdict(lambda: (set(), collections.Counter()))
    for time, thread, weight, frames in samples:
        weight = weight or 1
        total += weight
        role = role_of(frames)
        by_role[role] += weight
        threads[thread] += weight
        if frames:
            self_function[frames[0]] += weight
            for name in set(frames):
                inclusive[name] += weight
            mine = next((f for f in frames if LIBRARY.search(f)), None)
            ours[mine or "(no frame of ours) " + frames[0]] += weight
            if SPIN.search(frames[0]) or (len(frames) > 1 and SPIN.search(frames[1])):
                managed = [f for f in frames if LIBRARY.search(f)]
                spinners[" <- ".join(managed[:3]) or "(an idle pool worker)"] += weight
            folded[";".join(reversed(frames))] += weight
        running, roles = buckets[(time - start) // bucket_ns]
        running.add(thread)
        roles[role] += weight

    # Cores busy per bucket: the threads seen running in it; each role's cores, its share of the bucket's cycles times them.
    count = wall_ns // bucket_ns + 1
    histogram = collections.Counter()
    role_cores = collections.Counter()
    rows = []
    for index in range(count):
        running, roles = buckets.get(index, (set(), collections.Counter()))
        histogram[len(running)] += 1
        cycles = sum(roles.values()) or 1
        cells = {name: len(running) * roles[name] / cycles for name in roles}
        for name, cores in cells.items():
            role_cores[name] += cores
        rows.append((index, len(running), cells))
    mean = sum(k * n for k, n in histogram.items()) / max(count, 1)

    out = []
    pct = lambda part, whole: 100.0 * part / whole if whole else 0.0
    out.append("# CPU cycles: %s" % os.path.basename(os.path.dirname(os.path.abspath(export))))
    out.append("")
    out.append("%d samples over %.3f s of wall, %.1f Gcycles; %.2f cores busy on average (threads seen running in each %s ms)." % (
        len(samples), wall_ns / 1e9, total / 1e9, mean, options["--bucket"]))
    out.append("")
    out.append("## How many cores ran, bucket by bucket")
    out.append("")
    out.append("| cores | share of the wall |")
    out.append("|---:|---:|")
    for k in sorted(histogram):
        out.append("| %d | %.1f %% |" % (k, pct(histogram[k], count)))
    out.append("")
    out.append("## Roles (each sample counts toward its innermost role)")
    out.append("")
    out.append("| role | share of the cycles | cores on average |")
    out.append("|---|---:|---:|")
    for name, cycles in by_role.most_common():
        out.append("| %s | %.1f %% | %.2f |" % (name, pct(cycles, total), role_cores[name] / max(count, 1)))

    def table(title, counter):
        out.append("")
        out.append("## " + title)
        out.append("")
        out.append("| % | function |")
        out.append("|---:|---|")
        for key, cycles in counter.most_common(top):
            out.append("| %.2f | `%s` |" % (pct(cycles, total), key[:160]))

    table("Self, by function", self_function)
    table("Inclusive, by function", inclusive)
    table("Charged to the innermost frame of this repository", ours)
    table("Busiest threads", threads)
    table("Who spins", spinners)

    os.makedirs(directory, exist_ok=True)
    with open(os.path.join(directory, "summary.md"), "w") as handle:
        handle.write("\n".join(out) + "\n")
    names = [name for name, _ in ROLES] + ["other"]
    with open(os.path.join(directory, "timeline.tsv"), "w") as handle:
        handle.write("ms\tcores\t" + "\t".join(names) + "\n")
        for index, running, cells in rows:
            handle.write("%s\t%d\t%s\n" % (
                index * bucket_ns / 1e6, running, "\t".join("%.2f" % cells.get(name, 0.0) for name in names)))
    with open(os.path.join(directory, "folded.txt"), "w") as handle:
        for stack, cycles in folded.most_common():
            handle.write("%s %d\n" % (stack.replace(" ", "_"), cycles))
    if options["--ledger"]:
        print("%.2f\t%s" % (mean, " ".join("%s=%.1f" % (name, pct(cycles, total)) for name, cycles in by_role.most_common())))
    else:
        print("\n".join(out[:3]))
        print("written: summary.md, timeline.tsv, folded.txt in " + directory)
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
