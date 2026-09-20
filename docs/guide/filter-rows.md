# Filter rows

Push a predicate down so that whole blocks are never read.

```csharp
await using VortexFile file = await VortexFile.OpenAsync(path);
VortexExpr recent = Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(900)));

long rows = 0;
await foreach (RecordBatch batch in file.Scan().Where(recent).ExecuteAsync())
{
    using (batch)
    {
        rows += batch.RowCount;
    }
}
```

**The filter is exact.** What comes back is the matching rows and nothing else — 100 000 of the
million, and the earliest `day` among them is 900. It is not a hint you have to re-check.

## Writing a predicate

`Expr` builds them and `FilterLiteral.From` types the constants:

| | |
|---|---|
| comparison | `Expr.Eq`, `Ne`, `Lt`, `Le`, `Gt`, `Ge` |
| logic | `Expr.And`, `Or`, `Not` |
| membership | `Expr.In(field, [a, b, c])`, `Expr.ListContains(field, value)` |
| nulls | `Expr.IsNull(field)`, `Expr.IsNotNull(field)` |
| text | `Expr.StartsWith`, `Expr.Contains`, `Expr.Like(field, pattern, escape)` |

```csharp
VortexExpr both = Expr.And(recent, Expr.Gt(Expr.Field("celsius"), Expr.Literal(FilterLiteral.From(45.0))));
VortexExpr firstDays = Expr.In(
    Expr.Field("day"), [FilterLiteral.From(1), FilterLiteral.From(2), FilterLiteral.From(3)]);
```

A field is named by its path, dotted for a nested one.

## What pruning does, and when it does nothing

```csharp
ScanPlan plan = await file.Scan().Where(recent).ExplainAsync();
Console.WriteLine($"{plan.LiveBlocks} of {plan.Blocks} blocks survive");
foreach (PruningStep step in plan.Pruning)
{
    Console.WriteLine($"  {step.Structure} pruned {step.BlocksPruned}, reading {step.BytesRead} bytes to decide");
}
```

Two predicates of the same shape, on the same file, one on the column the rows are ordered by and
one on a column whose values are scattered:

```
day >= 900:    14 of 123 blocks survive, count exact: True
  zone map pruned 109 of them, reading 2140 bytes to decide
celsius > 45: 123 of 123 blocks survive, count exact: False
  zone map pruned 0 of them, reading 3124 bytes to decide
```

and what that is worth, measured against the file:

| | rounds of reading | bytes | rows |
|---|---|---|---|
| no filter | 124 | 184 725 367 | 1 000 000 |
| `day >= 900` | 18 | 19 751 747 | 100 000 |
| `celsius > 45` | 125 | 184 728 491 | 124 969 |

Pruning works on what a block's minimum and maximum can settle. The rows are in `day` order, so
109 blocks of 123 are provably outside the range and are never read: a ninth of the reading for a
tenth of the rows. `celsius` walks its whole range inside every block, so no block can be excluded
and the predicate costs a little more than no filter at all — the 3 124 bytes of zone maps read to
find that out, and then a comparison per value.

**This is a property of the data, not of the predicate.** If you filter on a column, write the rows
in that column's order, or at least clustered by it.

## Answers without rows

```csharp
await file.Scan().Where(recent).AnyAsync();    // True
await file.Scan().Where(recent).CountAsync();  // 100000
await file.Scan().MinAsync("celsius");         // 10
await file.Scan().MaxAsync("celsius");         // 50
file.MayMatch(recent);                         // True
```

`CountAsync` with a filter is answered from the block statistics wherever they suffice: on the
predicate above, 109 splits pruned, 13 proven whole, and **one** decoded. `MayMatch` is the cheapest
question of all — it compares the predicate against the file's own statistics and answers whether
any row could match, which for `day >= 5000` on this file is `False`, with nothing read.

## Watch out

* A field that is not in the schema throws `ArgumentException` when the scan starts, and so does a
  literal of a kind the column cannot be compared against — `day > "900"` on an `i32` column names
  both in the message. Integers and floats compare against each other, so `day > 900.0` is fine;
  text and booleans do not compare against numbers.
* **Nulls answer `unknown`, and only `true` returns a row.** A row whose `celsius` is null comes
  back neither for `celsius > 45` nor for its negation. `IsNull` and `IsNotNull` are the two that
  never answer unknown.
* The filter may read a column the projection leaves out — that column's segments are read to
  decide, and not delivered.
* `ExplainAsync` reads the statistics and the zone maps, never the data. It is the honest way to ask
  what a query is about to cost.

## Run it

```
dotnet run --project samples/Vorticity.Samples -- filter-rows
```
