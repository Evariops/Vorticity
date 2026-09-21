using System;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Keys;
using Vorticity.Scanning;

namespace Vorticity.Samples;

internal static class KeysInOrder
{
    internal static async Task RunAsync()
    {
        string path = await Demo.ReadingsAsync();
        await using VortexFile file = await VortexFile.OpenAsync(path);

        // What the cursor would walk, and what it turned down.
        KeyPlan plan = await file.Keys("day").ExplainAsync();
        Console.WriteLine($"day: source {plan.Source}, {plan.Runs} runs, {plan.EntryCount} entries, " +
            $"rows reachable: {plan.HasRows}");
        foreach (KeySourceRejection rejected in plan.Rejected)
        {
            Console.WriteLine($"  turned down {rejected.Source}: {rejected.Reason}");
        }

        await using (KeyCursor cursor = await file.Keys("day").OpenAsync())
        {
            await cursor.SeekFirstAsync();
            Console.WriteLine($"first key {cursor.Key.SignedValue} at row {cursor.Row}, " +
                $"format {cursor.KeyFormat}, distinct {cursor.IsDistinct}");

            await cursor.NextKeyAsync();
            Console.WriteLine($"next distinct key {cursor.Key.SignedValue} at row {cursor.Row}");

            await cursor.SeekAsync(FilterLiteral.From(900), SeekOp.AtOrAfter);
            Console.WriteLine($"seek to 900 landed on {cursor.Key.SignedValue} at row {cursor.Row}");

            Console.WriteLine($"rank of 900: {await cursor.RankAsync(FilterLiteral.From(900))}");
            Console.WriteLine($"{await cursor.KeyCountAsync()} keys in all");

            await cursor.SeekLastAsync();
            Console.WriteLine($"last key {cursor.Key.SignedValue} at row {cursor.Row}");

            await cursor.PrevKeyAsync();
            Console.WriteLine($"one key back: {cursor.Key.SignedValue}");

            bool found = await cursor.SeekAsync(FilterLiteral.From(5_000), SeekOp.Exact);
            Console.WriteLine($"an exact seek to a key that is not there: {found}, cursor valid: {cursor.IsValid}");
        }

        // Distinct keys only: one stop per value rather than one per row.
        await using (KeyCursor distinct = await file.Keys("day").Distinct().OpenAsync())
        {
            int seen = 0;
            await distinct.SeekFirstAsync();
            while (distinct.IsValid && seen < 3)
            {
                Console.WriteLine($"  distinct key {distinct.Key.SignedValue}, rows reachable: {distinct.HasRows}");
                seen++;
                if (!await distinct.NextAsync())
                {
                    break;
                }
            }
        }

        // A scan delivered in key order rather than in file order.
        OrderPlan? order = (await file.ScanBuilder().InKeyOrder("day").ExplainAsync()).Order;
        Console.WriteLine($"in key order: source {order?.Source}, {order?.Runs} runs, " +
            $"{order?.EntriesInRange} entries in range, descending {order?.Descending}");

        long rows = 0;
        await foreach (RecordBatch batch in file.ScanBuilder().InKeyOrder("day", true).ExecuteAsync())
        {
            using (batch)
            {
                if (rows == 0)
                {
                    Console.WriteLine("descending, the first row delivered has day " +
                        $"{batch.Column("day"u8).AsPrimitive<int>()[0]}");
                }

                rows += batch.RowCount;
            }
        }

        Console.WriteLine($"{rows} rows delivered in key order");

        try
        {
            file.ScanBuilder().InKeyOrder("day").Rows(new RowRange(0, 20_000));
        }
        catch (InvalidOperationException e)
        {
            Console.WriteLine($"a key-ordered scan with a row range: {e.Message}");
        }

        // A column with no order to walk.
        KeyPlan none = await file.Keys("celsius").ExplainAsync();
        Console.WriteLine($"celsius: source {none.Source}");
        foreach (KeySourceRejection rejected in none.Rejected)
        {
            Console.WriteLine($"  turned down {rejected.Source}: {rejected.Reason}");
        }
    }
}
