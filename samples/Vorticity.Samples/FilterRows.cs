using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scan;

namespace Vorticity.Samples;

internal static class FilterRows
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();

        await using VortexFile file = await VortexFile.OpenAsync(path);
        VortexExpr recent = Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(900)));
        VortexExpr hot = Expr.Gt(Expr.Field("celsius"), Expr.Literal(FilterLiteral.From(45.0)));

        long rows = 0;
        int smallest = int.MaxValue;
        await foreach (RecordBatch batch in file.Scan().Where(recent).ExecuteAsync())
        {
            using (batch)
            {
                rows += batch.RowCount;
                smallest = Math.Min(smallest, SmallestDay(batch));
            }
        }

        // Only the matching rows come back: the filter is exact, not a hint.
        Console.WriteLine($"{rows} rows, the earliest day among them {smallest}");

        await Explain("day >= 900", recent);
        await Explain("celsius > 45", hot);

        (long clusteredRequests, long clusteredBytes, long kept) =
            await Demo.MeasureAsync(path, f => f.Scan().Where(recent));
        (long scatteredRequests, long scatteredBytes, long hotRows) =
            await Demo.MeasureAsync(path, f => f.Scan().Where(hot));
        (long wholeRequests, long wholeBytes, long _) = await Demo.MeasureAsync(path, f => f.Scan());
        Console.WriteLine($"measured whole:      {wholeRequests} rounds, {wholeBytes} bytes");
        Console.WriteLine($"measured day >= 900: {clusteredRequests} rounds, {clusteredBytes} bytes, {kept} rows");
        Console.WriteLine($"measured celsius:    {scatteredRequests} rounds, {scatteredBytes} bytes, {hotRows} rows");

        Console.WriteLine($"any: {await file.Scan().Where(recent).AnyAsync()}");
        Console.WriteLine($"count: {await file.Scan().Where(recent).CountAsync()}");
        Console.WriteLine($"the file as a whole may match: {file.MayMatch(recent)}");
        Console.WriteLine("and for a day past its last: " +
            $"{file.MayMatch(Expr.Ge(Expr.Field("day"), Expr.Literal(FilterLiteral.From(5000))))}");

        VortexExpr firstDays = Expr.In(
            Expr.Field("day"), [FilterLiteral.From(1), FilterLiteral.From(2), FilterLiteral.From(3)]);
        await Count("day >= 900 and celsius > 45", Expr.And(recent, hot));
        await Count("day in (1, 2, 3)", firstDays);
        await Count("not(day >= 900)", Expr.Not(recent));

        // A literal the column cannot be compared against is refused here, before anything is
        // read, rather than yielding the empty result that reads like an empty file.
        try
        {
            file.Scan().Where(Expr.Gt(Expr.Field("day"), Expr.Literal(FilterLiteral.From("900"))));
        }
        catch (ArgumentException error)
        {
            Console.WriteLine($"day > \"900\": {error.Message}");
        }

        async Task Explain(string what, VortexExpr predicate)
        {
            ScanPlan plan = await file.Scan().Where(predicate).ExplainAsync();
            Console.WriteLine($"{what}: {plan.LiveBlocks} of {plan.Blocks} blocks survive, " +
                $"count exact: {plan.Count?.ExactCover}");
            foreach (PruningStep step in plan.Pruning)
            {
                Console.WriteLine($"  {step.Structure} pruned {step.BlocksPruned} of them, " +
                    $"reading {step.BytesRead} bytes to decide");
            }
        }

        async Task Count(string what, VortexExpr predicate) =>
            Console.WriteLine($"{what}: {await file.Scan().Where(predicate).CountAsync()} rows");
    }

    private static int SmallestDay(RecordBatch batch)
    {
        ReadOnlySpan<int> days = batch.Column("day"u8).AsPrimitive<int>().Values;
        int smallest = int.MaxValue;
        foreach (int day in days)
        {
            smallest = Math.Min(smallest, day);
        }

        return smallest;
    }
}
