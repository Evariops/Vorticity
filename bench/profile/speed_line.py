#!/usr/bin/env python3
"""The ledger fields of one bench/speed.sh run, from the log the run left.

usage: speed_line.py KIND LOG  ->  one TSV line, in the order of FIELDS below.

KIND is `compact` (a `--micro backlog --compact-only` log, one job a round, `--repeat` rounds: the first round is the cold
one, the last the warm one the ledger compares), `append` (a `--micro backlog --build-only` log) or `runner` (the output of
a Vorticity.Benchmarks.Runner `--repeat` run: the median of the rounds after the second).
"""
import re
import statistics
import sys

FIELDS = ["cold_s", "job_s", "write_s", "plan_s", "given_up_s", "cpu_s", "lanes_s", "rows", "inputs", "out_MB", "scratch_MiB",
          "bytes", "digest", "resident_MiB", "blocks_MiB", "gc2"]
kind, path = sys.argv[1:3]
v = {f: "-" for f in FIELDS}
lines = open(path, errors="replace").read().splitlines()

if kind == "compact":
    jobs = [l for l in lines if re.match(r"(round \d+ )?job \d+:", l)]
    def job_of(line):
        m = re.search(r"job \d+: \w+ L\d->L\d, (\d+) -> .*?, ([\d,]+) rows, [\d.]+ -> ([\d.]+) MB, .*?scratch (\d+) MiB, ([\d.]+) s, ([\d.]+) s of processor, ([\d.]+) s of (?:write )?lanes", line)
        if not m:
            return None
        d = {"inputs": m.group(1), "rows": m.group(2).replace(",", ""), "out_MB": m.group(3), "scratch_MiB": m.group(4),
             "job_s": m.group(5), "cpu_s": m.group(6), "lanes_s": m.group(7)}
        b = re.search(r"blocks held (\d+) MiB", line)
        d["blocks_MiB"] = b.group(1) if b else "-"
        g = re.search(r"(\d+) full collections", line)
        d["gc2"] = g.group(1) if g else "-"
        r = re.search(r"peaks: resident (\d+) MiB", line)
        d["resident_MiB"] = r.group(1) if r else "-"
        write = plan = given = 0.0
        p = re.search(r"passes: ([^;]*);", line)
        if p:
            for name, secs in re.findall(r"(\b[a-z][a-z ]*?(?:\([^)]*\))?) ([\d.]+) s", p.group(1)):
                name = name.strip()
                if name == "write":
                    write += float(secs)
                elif name == "plan":
                    plan += float(secs)
                elif name.startswith("given up"):
                    given += float(secs)
        d["write_s"], d["plan_s"], d["given_up_s"] = f"{write:.2f}", f"{plan:.2f}", f"{given:.2f}"
        return d
    parsed = [x for x in (job_of(l) for l in jobs) if x]
    if parsed:
        v.update(parsed[-1])
        v["cold_s"] = parsed[0]["job_s"] if len(parsed) > 1 else "-"
    for l in lines:
        if l.startswith("bytes:"):
            m = re.search(r"([\d,]+) bytes, sum ([0-9a-f]+)", l)
            if m:
                v["bytes"] = m.group(2)
        elif l.startswith("digest"):
            m = re.search(r"sum ([0-9a-f]+)", l)
            if m:
                v["digest"] = m.group(1)
elif kind == "append":
    for l in lines:
        m = re.search(r"backlog of (\d+) appends of (\d+) rows, ([\d.]+) MB, built in ([\d.]+) s, ([\d.]+) s of processor, ([\d.]+) s of (?:write )?lanes", l)
        if m:
            v.update({"rows": str(int(m.group(1)) * int(m.group(2))), "out_MB": m.group(3), "job_s": m.group(4), "cpu_s": m.group(5),
                      "lanes_s": m.group(6)})
elif kind == "runner":
    rounds = []
    for l in lines:
        m = re.match(r"round=(\d+) rows=(\d+) work_us=(\d+) .*?cpu_us=(\d+)", l)
        if m:
            rounds.append((int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4))))
    warm = [r for r in rounds if r[0] >= 2] or rounds
    if warm:
        v["job_s"] = f"{statistics.median(r[2] for r in warm) / 1e6:.4f}"
        v["cpu_s"] = f"{statistics.median(r[3] for r in warm) / 1e6:.4f}"
        v["cold_s"] = f"{rounds[0][2] / 1e6:.4f}"
        v["rows"] = str(warm[-1][1])
print("\t".join(v[f] for f in FIELDS))
