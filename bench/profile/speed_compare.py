#!/usr/bin/env python3
"""What the ledger of bench/speed.sh holds of a source tree and of its base, side by side, bench by bench and degree by
degree, with the change of each time. Never runs anything.

usage: speed_compare.py LEDGER TREE BASE MODE BENCHES DEGREES
"""
import csv
import sys

ledger, tree, base, mode, benches, degrees = sys.argv[1:7]
rows = list(csv.DictReader(open(ledger), delimiter="\t"))


def num(r, k):
    try:
        return float(r.get(k) or "")
    except ValueError:
        return None


for bench in benches.split(","):
    for degree in degrees.split(","):
        sides = {}
        for name, key in (("base", base), ("tree", tree)):
            runs = [r for r in rows if r["src"] == key and r["bench"] == bench and r["degree"] == degree and r["mode"] == mode]
            sides[name] = runs
            for r in runs[-3:]:
                print(f"  {bench:7} d{degree:<2} {name:4} {r['date']} {r['commit']:10} job {r['job_s']:>6} s (cold {r['cold_s']:>5})  "
                      f"write {r['write_s']:>5}  plan {r['plan_s']:>5}  given up {r['given_up_s']:>4}  cpu {r['cpu_s']:>6}  "
                      f"occ {r['occupancy']:>5}  {r['inputs']:>3} in  {r['out_MB']:>6} MB  res {r['resident_MiB']:>5}  "
                      f"blocks {r['blocks_MiB']:>4}  bytes {r['bytes'][:8]}  load {r['load']}  {r['note']}")
            if mode == "p10":
                for r in runs[-1:]:
                    print(f"  {bench:7} d{degree:<2} {name:4} cores {r['cores']}  {r['roles']}  ({r['log'].replace('.log', '.profile.txt')})")
        if sides["base"] and sides["tree"]:
            for k in ("job_s", "write_s", "plan_s", "cpu_s"):
                b = [x for x in (num(r, k) for r in sides["base"]) if x is not None]
                t = [x for x in (num(r, k) for r in sides["tree"]) if x is not None]
                if b and t and sum(b):
                    mb, mt = sum(b) / len(b), sum(t) / len(t)
                    print(f"  {bench:7} d{degree:<2} {k:8} {mb:8.3f} -> {mt:8.3f}  {100 * (mt - mb) / mb:+.1f} %")
            same = {r["bytes"] for r in sides["base"]} == {r["bytes"] for r in sides["tree"]}
            print(f"  {bench:7} d{degree:<2} bytes {'the same' if same else 'differ'}")
        elif not sides["base"]:
            print(f"  {bench:7} d{degree:<2} base {base[:10]} not in the ledger")
