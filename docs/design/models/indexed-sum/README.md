# The split of an indexed sum, proved for every double

`split_lemma.py` proves, with Z3's theory of floating point, that `IndexedSum.SplitOf` splits every
finite double exactly ([sums](../../16-queries.md#51-sums-exact-and-the-same-bits-whatever-the-cut)).
A value goes into three bins, from the bin its magnitude calls for, its top, down. Bin `b`'s unit is
`u = 2^(27b − 1074)` and its extractor `s = 1.5 · 2^52 · u`. One step adds the extractor, rounding to
nearest, reads the part off the rounded bits, and carries the remainder down:

```
y    = s + v                (rounded to nearest)
part = bits(y) − bits(s)
r    = v − (y − s)          (each rounded to nearest)
```

## The lemmas

For each bin `b` from 0 to 75 and every finite double `v` with `|v| ≤ 2^26 · u`, what the bin above
leaves and what a value of that top is under:

| lemma | claim |
|---|---|
| binade | `y` is positive and has the extractor's exponent bits: `y − s` is the bits' difference times `u`, so the part is a whole number of units, read exactly |
| part | `|part| ≤ 2^26`, so the 2^36 values a narrow sum takes keep each bin within a long: 2^36 · 2^26 = 2^62 |
| difference | `y − s` rounds to itself under every rounding mode: it is exact |
| remainder | `v − (y − s)` is exact, and at most `u / 2`, which is 2^26 units of the bin below |

For each top `t` from 2 to 75 and every finite double whose top it is (below `2^26` units of bin `t`,
and not below that of bin `t − 1` unless `t` is the lowest top):

| lemma | claim |
|---|---|
| whole | the value is a whole number of units of bin `t − 2`: its last bit weighs that unit or more |

Together they make the split exact. The parts of bins `t` and `t − 1`, and the value, are whole numbers
of bin `t − 2`'s unit, so the remainder after two steps is one too, and it is at most half of bin
`t − 1`'s unit, which is `2^26` units of bin `t − 2`. The third step's remainder is then a whole number
of units at most half a unit, so zero: the value is the exact sum of its three parts, and a bin is the
exact sum of the parts it took. What is left to the code, and to `IndexedSumTests` and the merge laws,
is that the bins add and drop the same way whatever the order: integer additions, and a top that only
moves up.

## What it does not claim

Values past the extractors, from top 76 on, are split scaled, and NaN and the infinities are not split:
neither is proved here. The wide sum's settled high parts are not either. Z3 proves the floating-point
steps as the script writes them, which mirror `IndexedSum.SplitOf` line for line (`IndexedSum.cs:233-254`)
and `Deposit`, which repeats them.

## Running it

```sh
python3 -m pip install z3-solver        # or brew install z3, which carries its Python binding
python3 -I docs/design/models/indexed-sum/split_lemma.py          # every bin, about six minutes
python3 -I docs/design/models/indexed-sum/split_lemma.py 0 40 75  # some bins
```

It prints each lemma proved and exits 0 when all are, 1 otherwise, with a counterexample when Z3 finds
one. It is not in CI, which installs no Z3: it reads the code's constants, which change rarely, and its
proof stands until they do.

## Evidence

Z3 5.3.3, its Python binding, Python 3.15.2, macOS arm64, 2026-11-07: 455 lemmas proved, 76 bins of
five and 74 tops of one, in 5 minutes 37 seconds on one core, the longest 5.5 seconds. A first
formulation with a division and a conversion to an integer took more than ten minutes for one bin: on
the bits, each lemma takes seconds.
