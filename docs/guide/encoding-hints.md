# Encoding hints

Steer the compressor per column, and measure whether it helped.

The writer chooses an encoding per column and per block by pricing the candidates on the block it is
holding. A hint overrides that choice for one column:

```csharp
VortexWriteOptions options = new VortexWriteOptions
{
    EncodingHints = new Dictionary<string, VortexEncodingHint> { ["celsius"] = VortexEncodingHint.Zstd },
};
```

The hints are `Auto`, `Canonical`, `RunEnd`, `Dictionary`, `BitPacked`, `Fsst`, `Alp`, `Sequence`
and `Zstd`. `report.Columns` says what each column actually got, one entry per chunk.

## What they do, measured

The same 200 000 rows of a text column and a floating-point column, written once per hint. The
figures are the whole file, with the other column left on `Auto`.

| `city` as | bytes | encoded |
|---|---|---|
| `Auto` | 396 025 | Dict |
| `Dictionary` | 396 025 | Dict |
| `RunEnd` | 396 025 | Dict |
| `Zstd` | **320 657** | Zstd |
| `Fsst` | 1 553 921 | Fsst |
| `Canonical` | 2 086 033 | none |

| `celsius` as | bytes | encoded |
|---|---|---|
| `Auto` | 396 025 | Dict, then Alp |
| `BitPacked` | 396 025 | Alp |
| `Sequence` | 396 025 | Alp |
| `Zstd` | **116 369** | Zstd |
| `Alp` | 781 585 | Alp |
| `Canonical` | 1 703 993 | none |

Three things are worth reading off that.

**A hint that does not fit is ignored.** `RunEnd` on five names in rotation, `BitPacked` on
floating-point values, `Sequence` on a column that is not one: each falls back to what `Auto` would
have chosen. A hint is a preference, not an instruction, and the report is where you find out.

**`Auto` is not the smallest.** Zstd beats it on both columns here, by 19 % on the text and by a
factor of three on the numbers. `Auto` prices decoding as well as size, and a Zstd block must be
inflated whole before a single value can be read, which a scan pays for on every block it touches.
Take the hint when the file is written once and read rarely, or when size is what you are paying
for; leave `Auto` when it is scanned often.

**`Canonical` is the measurement you want when comparing.** It stores the values as they are, so it
is the honest denominator for everything else: 2 086 033 and 1 703 993 bytes here.

**A hint is the same choice for every chunk, and `Auto` is not.** This file is two chunks, and the
report gives one encoding per chunk: `Auto` took a dictionary for the large one — 3 001 distinct
temperatures over 196 608 rows is what a dictionary is for — and `Alp` for the small tail. Pinning
`Alp` applied it to both and cost 385 560 bytes.

## Watch out

* The key is the column's path, dotted for a nested field. A path that names nothing is not an
  error and does nothing.
* A hint applies to every block of that column. There is no per-block override.
* `Compress = false` overrides every hint: it writes canonical everywhere.
* Measure on your data. Every figure above is a property of these 200 000 rows, and a text column of
  long distinct values would reorder the whole table.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- encoding-hints
```
