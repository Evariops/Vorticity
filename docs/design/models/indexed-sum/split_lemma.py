"""The extraction of an indexed sum, proved for every double by Z3's floating-point theory.

IndexedSum.SplitOf splits a value into parts in three bins, from the bin its magnitude calls for, its top, down. Bin
b's unit is u = 2^(27b - 1074), its extractor s = 1.5 * 2^52 * u. One step adds the extractor, rounding to nearest,
reads the part off the rounded bits, and carries the remainder down:

    y = s + v                  (rounded to nearest)
    part = bits(y) - bits(s)
    r = v - (y - s)            (each rounded to nearest)

For a bin b and every finite double v with |v| <= 2^26 u, what the bin above leaves and what a value of that top is
under, the script proves:

    binade      y is positive, in the extractor's binade: same sign and exponent bits. So y - s is the bits'
                difference times the binade's spacing, u: the part is a whole number of units, read exactly.
    part        |part| <= 2^26, so 2^36 parts of one bin stay within a long.
    difference  y - s rounds to itself under every rounding mode: it is exact.
    remainder   v - (y - s) is exact too, and at most u / 2, which is 2^26 units of the bin below.

and for a top t and every finite double whose top it is (below limit(t) = 2^26 units of bin t, and not below
limit(t - 1) unless t is the lowest top):

    whole       the value is a whole number of units of bin t - 2: its last bit is at or above that unit.

Together they make the split exact. The parts of bins t and t - 1, and the value, are whole numbers of bin t - 2's
unit, so the remainder after two steps is one too, and it is at most half of bin t - 1's unit, 2^26 units of bin
t - 2: the third step's remainder is then a whole number of units at most half a unit, so zero, and the value is
the exact sum of its three parts.

Run: python3 -I split_lemma.py [bins...]   (every bin from 0 to 75 when none is named)
Exits 0 when every lemma is proved.
"""

import sys
import time

from z3 import (
    And,
    BitVecVal,
    Extract,
    FP,
    FPVal,
    Float64,
    Implies,
    Not,
    Or,
    RNE,
    RTN,
    RTP,
    RTZ,
    Solver,
    UGE,
    fpAbs,
    fpAdd,
    fpEQ,
    fpIsInf,
    fpIsNaN,
    fpLEQ,
    fpLT,
    fpSub,
    fpToIEEEBV,
    unsat,
)

WIDTH = 27
LOWEST = -1074
MIN_TOP = 2
HUGE_TOP = 76
D = Float64()


def unit_exponent(b):
    return WIDTH * b + LOWEST


def unit(b):
    return FPVal(2.0 ** unit_exponent(b), D)


def extractor(b):
    return FPVal(1.5 * 2.0 ** (52 + unit_exponent(b)), D)


def finite(v):
    return And(Not(fpIsNaN(v)), Not(fpIsInf(v)))


def exact(op, a, b):
    """op(a, b) is exact when every rounding mode gives what rounding to nearest gives."""
    return And(*[fpEQ(op(RNE(), a, b), op(mode, a, b)) for mode in (RTZ(), RTP(), RTN())])


def prove(name, claim, seconds=3600):
    solver = Solver()
    solver.set("timeout", seconds * 1000)
    solver.add(Not(claim))
    started = time.time()
    verdict = solver.check()
    elapsed = time.time() - started
    if verdict == unsat:
        print(f"PROVED {name} ({elapsed:.1f} s)", flush=True)
        return True
    print(f"NOT PROVED {name}: {verdict} ({elapsed:.1f} s)", flush=True)
    if str(verdict) == "sat":
        print(f"  counterexample: {solver.model()}", flush=True)
    return False


def step_lemmas(b):
    v = FP("v", D)
    s = extractor(b)
    bound = FPVal(2.0 ** 26 * 2.0 ** unit_exponent(b), D)
    pre = And(finite(v), fpLEQ(fpAbs(v), bound))
    y = fpAdd(RNE(), s, v)
    yb = fpToIEEEBV(y)
    sb = fpToIEEEBV(s)
    part = yb - sb
    d = fpSub(RNE(), y, s)
    r = fpSub(RNE(), v, d)
    half = 2.0 ** (unit_exponent(b) - 1)
    proved = prove(f"bin {b} binade", Implies(pre, And(Extract(63, 63, yb) == 0, Extract(62, 52, yb) == Extract(62, 52, sb))))
    proved &= prove(
        f"bin {b} part",
        Implies(pre, And(UGE(part + BitVecVal(2 ** 26, 64), BitVecVal(0, 64)), UGE(BitVecVal(2 ** 27, 64), part + BitVecVal(2 ** 26, 64)))))
    proved &= prove(f"bin {b} difference", Implies(pre, exact(fpSub, y, s)))
    proved &= prove(f"bin {b} remainder exact", Implies(pre, exact(fpSub, v, d)))
    if half >= 2.0 ** -1074:
        proved &= prove(f"bin {b} remainder within half a unit", Implies(pre, fpLEQ(fpAbs(r), FPVal(half, D))))
    else:
        proved &= prove(f"bin {b} remainder nothing", Implies(pre, fpEQ(fpAbs(r), FPVal(0.0, D))))
    return proved


def whole_lemma(t):
    v = FP("v", D)
    limit = FPVal(2.0 ** (unit_exponent(t) + WIDTH - 1), D)
    pre = And(finite(v), fpLT(fpAbs(v), limit))
    if t > MIN_TOP:
        pre = And(pre, Not(fpLT(fpAbs(v), FPVal(2.0 ** (unit_exponent(t - 1) + WIDTH - 1), D))))
    exponent = Extract(62, 52, fpToIEEEBV(v))
    # The last bit of a normal double of biased exponent E weighs 2^(E - 1075), of a subnormal 2^-1074.
    lowest = unit_exponent(t - 2)
    normal_floor = lowest + 1075
    claim = Or(
        And(exponent == 0, lowest == LOWEST),
        UGE(exponent, BitVecVal(max(normal_floor, 1), 11)),
    )
    return prove(f"top {t} whole", Implies(pre, claim))


def main():
    bins = [int(b) for b in sys.argv[1:]] or list(range(0, HUGE_TOP))
    proved = True
    for b in bins:
        proved &= step_lemmas(b)
        if MIN_TOP <= b < HUGE_TOP:
            proved &= whole_lemma(b)
    print("ALL PROVED" if proved else "SOME NOT PROVED", flush=True)
    sys.exit(0 if proved else 1)


if __name__ == "__main__":
    main()
